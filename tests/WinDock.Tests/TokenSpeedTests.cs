using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using WinDock.Models;
using WinDock.Services;
using WinDock.ViewModels;

namespace WinDock.Tests;

public sealed class TokenSpeedTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    static double Unix => Now.ToUnixTimeSeconds();

    [Fact]
    public void Latest_completed_output_and_same_model_weighted_average_are_distinct()
    {
        using var database = new Database();
        database.Add("old-a", Unix - 30, 100, 10, "model-a");
        database.Add("other-model", Unix - 10, 999, 1, "model-b");
        database.Add("latest-a", Unix - 3, 900, 5, "model-a");
        database.Add("excluded", Unix - 1, 12345, 1, "model-a", 0);
        database.Add("future", Unix + 1, 20000, 1, "model-a");
        database.Add("outside-window", Unix - 1801, 20000, 1, "model-a");

        var speed = database.Read();
        Assert.Equal(TokenPulseStatus.Ready, speed.Status);
        Assert.Equal(180, speed.Latest!.TokensPerSecond);
        Assert.Equal("model-a", speed.Latest.Model);
        Assert.Equal(2, speed.ModelSamples);
        Assert.Equal(1000.0 / 15, speed.ModelAverage!.Value, 8);
    }

    [Fact]
    public void Zero_output_is_a_valid_measurement_and_not_an_error_placeholder()
    {
        using var database = new Database();
        database.Add("zero", Unix, 0, 1, null);
        var speed = database.Read();
        Assert.Equal(TokenPulseStatus.Ready, speed.Status);
        Assert.Equal(0, speed.Latest!.TokensPerSecond);
        Assert.Null(speed.Latest.Model);
    }

    [Fact]
    public void Model_grouping_uses_the_full_model_value()
    {
        using var database = new Database();
        var prefix = new string('m', 160);
        database.Add("other", Unix - 2, 9999, 1, prefix + "other");
        database.Add("latest", Unix - 1, 200, 2, prefix + "latest");
        var speed = database.Read();
        Assert.Equal(1, speed.ModelSamples);
        Assert.Equal(100, speed.ModelAverage);
    }

    [Fact]
    public void Completed_output_expires_after_sixty_seconds_even_without_a_new_read()
    {
        using var database = new Database();
        database.Add("edge", Unix - 60, 500, 5);
        var speed = database.Read();
        Assert.Equal(TokenPulseStatus.Ready, speed.Status);
        Assert.Equal(TokenPulseStatus.Stale, speed.At(Now.AddMilliseconds(1)).Status);
        Assert.Equal(TokenPulseStatus.Stale, database.Reader.Read(Now.AddSeconds(1)).Status);
    }

    [Fact]
    public void Empty_excluded_future_and_old_records_never_show_a_fake_zero()
    {
        using var database = new Database();
        Assert.Equal(TokenPulseStatus.NoSamples, database.Read().Status);
        database.Add("excluded", Unix, 123, 2, valid: 0);
        database.Add("future", Unix + 1, 123, 2);
        database.Add("outside", Unix - 1801, 123, 2);
        var speed = database.Read();
        Assert.Equal(TokenPulseStatus.NoSamples, speed.Status);
        Assert.Null(speed.Latest);
        Assert.Null(speed.ModelAverage);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"output_tokens\":-1,\"started_at\":1793275190,\"ended_at\":1793275200,\"token_scope\":\"total_output\"}")]
    [InlineData("{\"output_tokens\":1.5,\"started_at\":1793275190,\"ended_at\":1793275200,\"token_scope\":\"total_output\"}")]
    [InlineData("{\"output_tokens\":100,\"started_at\":1793275200,\"ended_at\":1793275200,\"token_scope\":\"total_output\"}")]
    [InlineData("{\"output_tokens\":100,\"started_at\":1793275190,\"ended_at\":1793275199,\"token_scope\":\"total_output\"}")]
    [InlineData("{\"output_tokens\":100,\"started_at\":1793275190,\"ended_at\":1793275200,\"token_scope\":\"visible_output\"}")]
    [InlineData("{\"output_tokens\":100,\"started_at\":1793275190,\"ended_at\":1793275200,\"token_scope\":\"total_output\",\"issue\":\"withdrawn\"}")]
    [InlineData("{\"output_tokens\":9223372036854775808,\"started_at\":1793275190,\"ended_at\":1793275200,\"token_scope\":\"total_output\"}")]
    public void Malformed_or_ineligible_metadata_is_explicitly_unavailable(string payload)
    {
        using var database = new Database();
        database.Put("bad", Unix, 1, payload);
        var speed = database.Read();
        Assert.Equal(TokenPulseStatus.InvalidData, speed.Status);
        Assert.Null(speed.Latest);
    }

    [Fact]
    public void One_broken_record_does_not_hide_another_valid_completed_output()
    {
        using var database = new Database();
        database.Add("good", Unix - 3, 200, 4);
        database.Put("broken", Unix, 1, "{");
        var speed = database.Read();
        Assert.Equal(TokenPulseStatus.Ready, speed.Status);
        Assert.Equal(50, speed.Latest!.TokensPerSecond);
    }

    [Fact]
    public void Online_wal_updates_corrections_and_withdrawals_are_seen_on_every_read()
    {
        using var database = new Database();
        database.Add("older", Unix - 10, 200, 2);
        database.Add("latest", Unix - 3, 100, 1);
        Assert.True(File.Exists(database.Path + "-wal"));
        Assert.Equal(100, database.Read().Latest!.TokensPerSecond);
        database.Add("latest", Unix - 3, 150, 1);
        Assert.Equal(150, database.Read().Latest!.TokensPerSecond);
        database.Add("latest", Unix - 3, 150, 1, valid: 0);
        Assert.Equal(Unix - 10, database.Read().Latest!.EndedAt);
        database.Execute("DELETE FROM samples");
        Assert.Equal(TokenPulseStatus.NoSamples, database.Read().Status);
        database.Add("backfill", Unix - 15, 600, 3);
        Assert.Equal(200, database.Read().Latest!.TokensPerSecond);
    }

    [Fact]
    public void Reads_leave_the_database_and_committed_wal_bytes_unchanged()
    {
        using var database = new Database();
        database.Add("sample", Unix, 100, 2);
        var main = Hash(database.Path);
        var wal = Hash(database.Path + "-wal");
        Assert.Equal(TokenPulseStatus.Ready, database.Read().Status);
        Assert.Equal(main, Hash(database.Path));
        Assert.Equal(wal, Hash(database.Path + "-wal"));
        database.Add("after-read", Unix, 200, 2);
        Assert.Equal(2, database.Read().ModelSamples);
    }

    [Fact]
    public void Missing_locked_unrecognized_and_corrupt_databases_have_clear_states()
    {
        using var database = new Database(wal: false);
        var missing = database.Path + ".missing";
        Assert.Equal(TokenPulseStatus.Missing, new TokenPulseReader(missing).Read(Now).Status);
        Assert.False(File.Exists(missing));
        database.Execute("BEGIN EXCLUSIVE");
        var timer = Stopwatch.StartNew();
        Assert.Equal(TokenPulseStatus.Busy, database.Read().Status);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
        database.Execute("ROLLBACK; DROP TABLE samples");
        Assert.Equal(TokenPulseStatus.Unsupported, database.Read().Status);
        var corrupt = database.Path + ".corrupt";
        File.WriteAllText(corrupt, "This is not a SQLite database.");
        Assert.Equal(TokenPulseStatus.InvalidData, new TokenPulseReader(corrupt).Read(Now).Status);
    }

    [Fact]
    public void An_unreadable_file_or_directory_is_not_reported_as_zero_output()
    {
        using var database = new Database(wal: false);
        database.CloseWriter();
        using var blocked = new FileStream(database.Path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(TokenPulseStatus.Unavailable, database.Read().Status);
        Assert.Equal(TokenPulseStatus.Unavailable, new TokenPulseReader(System.IO.Path.GetDirectoryName(database.Path)).Read(Now).Status);
    }

    [Fact]
    public void Excessive_query_work_times_out_and_releases_the_connection()
    {
        using var database = new Database();
        database.Execute(FormattableString.Invariant($"DROP TABLE samples; CREATE VIEW samples AS WITH RECURSIVE huge(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM huge WHERE n<100000000) SELECT n AS key, {Unix} AS ended_at, 1 AS valid, '{{}}' AS payload FROM huge"));
        var timer = Stopwatch.StartNew();
        Assert.Equal(TokenPulseStatus.TimedOut, database.Read().Status);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
        database.Execute("DROP VIEW samples");
    }

    [Fact]
    public void The_read_window_has_a_hard_row_limit_and_marks_partial_averages()
    {
        using var database = new Database();
        database.Execute("BEGIN");
        for (var i = 0; i <= TokenPulseReader.MaximumSamples; i++)
            database.Add(i.ToString(CultureInfo.InvariantCulture), Unix - i / 10.0, 10, 1);
        database.Execute("COMMIT");
        var speed = database.Read();
        Assert.Equal(TokenPulseStatus.Ready, speed.Status);
        Assert.True(speed.WindowLimited);
        Assert.Equal(TokenPulseReader.MaximumSamples, speed.ModelSamples);
        Assert.Equal(10, speed.ModelAverage);
    }

    [Fact]
    public async Task Sampling_runs_in_the_background_without_overlapping_or_overpolling()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var service = new TokenPulseService(now =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException();
            return new(TokenPulseStatus.NoSamples, now);
        });
        var pending = service.Refresh(Now);
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(3))));
            Assert.False(pending.IsCompleted);
            await service.Refresh(Now.AddSeconds(5));
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { release.Set(); }
        await pending;
        await service.Refresh(Now.AddSeconds(1));
        Assert.Equal(1, calls);
        await service.Refresh(Now.AddSeconds(2));
        Assert.Equal(2, calls);
        Assert.Equal(TokenPulseStatus.NoSamples, service.Snapshot.Status);
    }

    [Fact]
    public async Task Widget_uses_plain_numbers_units_and_explicit_idle_or_error_states()
    {
        var report = new TokenPulseSpeed(TokenPulseStatus.Ready, Now,
            new(Unix, Unix - 10, 824, "model-a"), 80, 2);
        var service = new TokenPulseService(_ => report);
        await service.Refresh(Now);
        var widget = new TokenSpeedWidget("speed", new WidgetSpec { Kind = WidgetSpec.TokenSpeed }, service);
        widget.Tick(Now.LocalDateTime);
        widget.RefreshFace();
        Assert.Equal("82.4", widget.Face!.Value);
        Assert.Equal(Loc.T("tokenSpeedUnit"), widget.Face.Caption);
        Assert.Contains("model-a", widget.Face.Tip);
        Assert.Contains("824", widget.Face.Tip);
        Assert.False(widget.Face.Dim);
        Assert.Equal(Now.LocalDateTime.AddSeconds(2), widget.Due(Now.LocalDateTime));
        widget.Tick(Now.AddSeconds(61).LocalDateTime);
        widget.RefreshFace();
        Assert.Equal("—", widget.Face.Value);
        Assert.Equal(Loc.T("tokenSpeedWaiting"), widget.Face.Caption);
        Assert.True(widget.Face.Dim);

        var busy = new TokenPulseService(_ => new(TokenPulseStatus.Busy, Now));
        await busy.Refresh(Now.AddSeconds(64));
        var unavailable = new TokenSpeedWidget("busy", new WidgetSpec { Kind = WidgetSpec.TokenSpeed }, busy);
        unavailable.Tick(Now.AddSeconds(64).LocalDateTime);
        unavailable.RefreshFace();
        Assert.Equal("—", unavailable.Face!.Value);
        Assert.Equal(Loc.T("tokenSpeedBusy"), unavailable.Face.Caption);
    }

    [Fact]
    public void Token_speed_can_be_selected_and_round_trips_through_the_preset()
    {
        Assert.Contains(WidgetSpec.TokenSpeed, WidgetSpec.Kinds);
        var spec = new WidgetSpec { Kind = WidgetSpec.TokenSpeed, Size = WidgetSpec.WideSize };
        var restored = JsonSerializer.Deserialize<WidgetSpec>(JsonSerializer.Serialize(spec))!;
        Assert.IsType<TokenSpeedWidget>(WidgetTile.Create("token", restored));
        Assert.Equal(WidgetSpec.WideSize, restored.Size);
        Assert.NotEqual("widget_token-speed", Loc.T("widget_token-speed"));
    }

    static byte[] Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return SHA256.HashData(stream);
    }

    sealed class Database : IDisposable
    {
        nint connection;
        readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "windock-token-speed-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }
        public TokenPulseReader Reader { get; }

        public Database(bool wal = true)
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "history.sqlite");
            Assert.Equal(0, Open(Path, out connection, 6, 0));
            if (wal) Execute("PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0");
            Execute("CREATE TABLE samples(key TEXT PRIMARY KEY, ended_at REAL NOT NULL, valid INTEGER NOT NULL, payload TEXT NOT NULL); CREATE INDEX samples_time ON samples(ended_at)");
            Reader = new(Path);
        }

        public TokenPulseSpeed Read() => Reader.Read(Now);

        public void Add(string key, double ended, long tokens, double seconds, string? model = "model", int valid = 1)
            => Put(key, ended, valid, JsonSerializer.Serialize(new
            {
                started_at = ended - seconds,
                ended_at = ended,
                output_tokens = tokens,
                token_scope = "total_output",
                issue = (string?)null,
                context = new { model }
            }));

        public void Put(string key, double ended, int valid, string payload)
            => Execute(FormattableString.Invariant($"INSERT INTO samples VALUES({Quote(key)}, {ended:R}, {valid}, {Quote(payload)}) ON CONFLICT(key) DO UPDATE SET ended_at=excluded.ended_at, valid=excluded.valid, payload=excluded.payload"));

        public void Execute(string sql) => Assert.Equal(0, Exec(connection, sql, 0, 0, 0));

        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

        public void CloseWriter()
        {
            if (connection != 0) Close(connection);
            connection = 0;
        }

        public void Dispose()
        {
            CloseWriter();
            Directory.Delete(directory, true);
        }

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint database, int flags, nint vfs);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int Exec(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint callback, nint state, nint error);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int Close(nint database);
    }
}
