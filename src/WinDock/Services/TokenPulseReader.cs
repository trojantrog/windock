using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinDock.Services;

public sealed class TokenPulseReader(string? databasePath = null)
{
    public const int MaximumSamples = 10000;
    public string DatabasePath { get; } = databasePath ?? DefaultPath;
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokenPulse", "history.sqlite");

    const string Query = """
        SELECT ended_at, valid,
          CASE WHEN json_valid(payload) THEN
            CASE WHEN json_type(payload, '$.output_tokens') = 'integer'
              AND json_type(payload, '$.started_at') IN ('integer', 'real')
              AND json_type(payload, '$.ended_at') IN ('integer', 'real')
              AND json_extract(payload, '$.token_scope') = 'total_output'
              AND (json_extract(payload, '$.issue') IS NULL OR json_extract(payload, '$.issue') = '')
            THEN 1 ELSE 0 END
          ELSE 0 END,
          CASE WHEN json_valid(payload) THEN json_extract(payload, '$.started_at') END,
          CASE WHEN json_valid(payload) THEN json_extract(payload, '$.ended_at') END,
          CASE WHEN json_valid(payload) THEN json_extract(payload, '$.output_tokens') END,
          CASE WHEN json_valid(payload) THEN
            CASE WHEN json_type(payload, '$.context.model') = 'text'
              THEN json_extract(payload, '$.context.model') END
          END
        FROM samples
        WHERE ended_at >= ?1 AND ended_at <= ?2
        ORDER BY ended_at DESC
        LIMIT 10001
        """;

    public TokenPulseSpeed Read(DateTimeOffset now)
    {
        nint database = 0, statement = 0;
        TokenPulseNative.Progress? progress = null;
        try
        {
            if ((File.GetAttributes(DatabasePath) & FileAttributes.Directory) != 0)
                return new(TokenPulseStatus.Unavailable, now);
            var result = TokenPulseNative.Open(DatabasePath, out database, 0x00000001 | 0x00040000, 0);
            if (result != 0) return Failure(result, now);
            if (TokenPulseNative.ReadOnly(database, "main") != 1) return new(TokenPulseStatus.Unavailable, now);
            TokenPulseNative.BusyTimeout(database, 100);
            TokenPulseNative.Limit(database, 0, 65536);
            var timer = Stopwatch.StartNew();
            progress = _ => timer.ElapsedMilliseconds >= 250 ? 1 : 0;
            TokenPulseNative.ProgressHandler(database, 1000, progress, 0);
            result = TokenPulseNative.Prepare(database, Query, -1, out statement, 0);
            if (result != 0) return Failure(result, now);
            if (TokenPulseNative.StatementReadOnly(statement) != 1) return new(TokenPulseStatus.Unavailable, now);
            var unix = now.ToUnixTimeMilliseconds() / 1000.0;
            if (TokenPulseNative.BindDouble(statement, 1, unix - TokenPulseSpeed.Window.TotalSeconds) != 0
                || TokenPulseNative.BindDouble(statement, 2, unix) != 0)
                return new(TokenPulseStatus.Unavailable, now);

            var samples = new List<TokenPulseSample>();
            var rows = 0;
            var invalid = false;
            while ((result = TokenPulseNative.Step(statement)) == 100)
            {
                if (++rows > MaximumSamples) break;
                var valid = TokenPulseNative.Integer(statement, 1);
                if (TokenPulseNative.Type(statement, 1) != 1 || valid is not (0 or 1))
                {
                    invalid = true;
                    continue;
                }
                if (valid == 0) continue;
                var sample = Sample(statement);
                if (sample is null) invalid = true;
                else samples.Add(sample);
            }
            if (result != 101 && rows <= MaximumSamples) return Failure(result, now);
            if (samples.Count == 0) return new(invalid ? TokenPulseStatus.InvalidData : TokenPulseStatus.NoSamples, now);
            var latest = samples[0];
            var model = samples.Where(s => s.Model == latest.Model).ToArray();
            var seconds = model.Sum(s => s.Seconds);
            double? average = double.IsFinite(seconds) ? model.Sum(s => (double)s.OutputTokens) / seconds : null;
            return new TokenPulseSpeed(TokenPulseStatus.Ready, now, latest, average, model.Length, rows > MaximumSamples).At(now);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(TokenPulseStatus.Missing, now);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new(TokenPulseStatus.Unsupported, now);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(TokenPulseStatus.Unavailable, now);
        }
        finally
        {
            if (statement != 0) TokenPulseNative.Finalize(statement);
            if (database != 0) TokenPulseNative.Close(database);
            GC.KeepAlive(progress);
        }
    }

    static TokenPulseSample? Sample(nint statement)
    {
        if (TokenPulseNative.Integer(statement, 2) != 1 || TokenPulseNative.Type(statement, 5) != 1) return null;
        var ended = TokenPulseNative.Double(statement, 0);
        var started = TokenPulseNative.Double(statement, 3);
        var payloadEnd = TokenPulseNative.Double(statement, 4);
        var tokens = TokenPulseNative.Integer(statement, 5);
        var seconds = ended - started;
        if (!double.IsFinite(ended) || !double.IsFinite(started) || !double.IsFinite(payloadEnd)
            || ended != payloadEnd || !double.IsFinite(seconds) || seconds <= 0 || tokens < 0
            || !double.IsFinite(tokens / seconds)) return null;
        var model = TokenPulseNative.Type(statement, 6) == 3 ? Marshal.PtrToStringUTF8(TokenPulseNative.Text(statement, 6)) : null;
        return new(ended, started, tokens, model);
    }

    static TokenPulseSpeed Failure(int code, DateTimeOffset now) => new((code & 0xFF) switch
    {
        5 or 6 => TokenPulseStatus.Busy,
        9 => TokenPulseStatus.TimedOut,
        11 or 18 or 26 => TokenPulseStatus.InvalidData,
        1 or 17 => TokenPulseStatus.Unsupported,
        _ => TokenPulseStatus.Unavailable
    }, now);
}
