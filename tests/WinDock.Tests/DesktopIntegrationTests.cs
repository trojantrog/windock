using System.Text.Json;
using WinDock.Models;
using WinDock.Services;

namespace WinDock.Tests;

public class DesktopIntegrationTests
{
    const string Source = "a1111111-1111-1111-1111-111111111111";
    const string Target = "b2222222-2222-2222-2222-222222222222";
    const string Item = "c3333333-3333-3333-3333-333333333333";
    static FenceItem Selected => new(Item, "测试.txt", @"C:\Desktop\测试.txt", Source, "Inbox", false);

    sealed class Runner : IPecoCommandRunner
    {
        public List<string[]> Calls { get; } = [];
        public string SourceKind { get; set; } = "inbox";
        public string TargetKind { get; set; } = "virtual";
        public string CurrentFence { get; set; } = Source;
        public bool SnapshotFails { get; set; }
        public bool MoveTimesOut { get; set; }
        public Task<JsonElement> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellation)
        {
            Calls.Add(arguments.ToArray());
            if (arguments[0] == "snapshot" && SnapshotFails) throw new PecoFenceException("snapshot failed");
            if (arguments[0] == "item" && arguments[1] == "move" && MoveTimesOut) throw new PecoFenceException("timeout", true);
            object reply = arguments[0] == "fence" && arguments[1] == "list"
                ? new[] { new FenceInfo(Source, "Inbox", SourceKind, 1, null, false), new FenceInfo(Target, "Work", TargetKind, 0, null, false) }
                : arguments[0] == "item" && arguments[1] == "list" ? new[] { Selected with { Fence = CurrentFence } } : new { skipped = Array.Empty<string>() };
            return Task.FromResult(JsonSerializer.SerializeToElement(reply, Json.Options));
        }
    }

    [Theory]
    [InlineData("portal", "virtual", Source)]
    [InlineData("inbox", "portal", Source)]
    [InlineData("inbox", "portal", Target)]
    public async Task Moving_rejects_real_folder_portals_including_stale_selections(string source, string target, string current)
    {
        var runner = new Runner { SourceKind = source, TargetKind = target, CurrentFence = current };
        await Assert.ThrowsAsync<PecoFenceException>(() => new PecoFenceClient(runner).MoveAsync([Selected], Target));
        Assert.DoesNotContain(runner.Calls, a => a[0] == "snapshot" || a[1] == "move");
    }

    [Fact]
    public async Task A_virtual_move_is_preceded_by_a_snapshot_and_uses_item_ids()
    {
        var runner = new Runner();
        await new PecoFenceClient(runner).MoveAsync([Selected], Target);
        Assert.Equal("snapshot", runner.Calls[^2][0]);
        Assert.Equal(new[] { "item", "move", Item, "--to", Target }, runner.Calls[^1]);
        Assert.DoesNotContain(runner.Calls.SelectMany(a => a), a => a == Selected.Path);
    }

    [Fact]
    public async Task No_mutation_occurs_when_the_backup_fails()
    {
        var runner = new Runner { SnapshotFails = true };
        await Assert.ThrowsAsync<PecoFenceException>(() => new PecoFenceClient(runner).MoveAsync([Selected], Target));
        Assert.DoesNotContain(runner.Calls, a => a[0] == "item" && a[1] == "move");
    }

    [Fact]
    public async Task A_timed_out_mutation_is_not_retried()
    {
        var runner = new Runner { MoveTimesOut = true };
        var failure = await Assert.ThrowsAsync<PecoFenceException>(() => new PecoFenceClient(runner).MoveAsync([Selected], Target));
        Assert.True(failure.MayHaveCompleted);
        Assert.Single(runner.Calls, a => a[0] == "item" && a[1] == "move");
    }

    [Fact]
    public async Task Chinese_names_and_shell_metacharacters_remain_one_argument()
    {
        var runner = new Runner();
        const string name = "课程 & 个人 $(test)";
        await new PecoFenceClient(runner).RenameAsync(Target, name);
        Assert.Equal(new[] { "fence", "rename", Target, name }, runner.Calls[^1]);
    }

    [Fact]
    public void Search_ranks_exact_and_prefix_application_names_first()
    {
        Assert.True(DesktopSearch.MatchScore("课程", "课程笔记.txt") > DesktopSearch.MatchScore("课程", "我的课程.txt"));
    }

    [Fact]
    public async Task Everything_response_checks_lengths_and_decodes_Chinese_paths()
    {
        const string path = @"D:\课程\设计图.pdf";
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.Unicode);
        foreach (var value in new[] { 1, 1, 0, 4, 1, 0, 28, path.Length }) writer.Write(value);
        writer.Write(System.Text.Encoding.Unicode.GetBytes(path + "\0"));
        var bytes = stream.ToArray();
        Assert.Equal(path, Assert.Single(EverythingSearch.ParseResponse(bytes).Hits).Path);
        Assert.Throws<InvalidDataException>(() => EverythingSearch.ParseResponse(bytes[..^2]));
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 24);
        Assert.Throws<InvalidDataException>(() => EverythingSearch.ParseResponse(bytes));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EverythingSearch.QueryAsync("test", null, new CancellationToken(true)));
    }

    [Fact]
    public void Existing_settings_keep_pins_and_the_Everything_instance()
    {
        var parsed = Settings.Parse("""{"language":"zh-CN","alwaysPinned":[{"name":"Keep"}],"desktop":{"everythingInstance":"1.5a"},"dock":{"animationDuration":9999,"animationStrength":-10}}""");
        Assert.Single(parsed.AlwaysPinned);
        Assert.Equal("1.5a", parsed.Desktop.EverythingInstance);
        Assert.Equal(.2, parsed.Dock.AnimationStrength);
    }
}
