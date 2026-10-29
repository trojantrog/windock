using System.Diagnostics;
using System.Text.Json;
using WinDock.Models;
using WinDock.Services;
using WinDock.ViewModels;

namespace WinDock.Tests;

public class GptUsageTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 29, 5, 40, 30, TimeSpan.Zero);
    const string SevenDay = "{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":80,\"windowDurationMins\":10080,\"resetsAt\":1793784675},\"secondary\":null}";
    const string Both = "{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":80,\"windowDurationMins\":10080,\"resetsAt\":1793784675},\"secondary\":{\"usedPercent\":12.5,\"windowDurationMins\":300,\"resetsAt\":1793263230}}";

    static GptQuotaLimits? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return GptQuotaLimits.Parse(document.RootElement, Now);
    }

    static GptQuotaLimits Limits(string bucket = SevenDay) => Parse("{\"rateLimits\":" + bucket + "}")!;

    [Fact]
    public void Real_single_week_window_maps_by_duration_and_has_no_invented_five_hour_value()
    {
        var limits = Limits();
        Assert.Null(limits.FiveHour);
        Assert.Equal(80, limits.SevenDay!.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793784675), limits.SevenDay.ResetsAt);
        var face = GptUsageWidget.Present(new(GptQuotaStatus.Ready, limits, false), Now);
        Assert.Equal("80%", face.Value);
        Assert.Contains("GPT", face.Caption);
        Assert.Contains(Loc.T("gptSevenDay"), face.Caption);
        Assert.Null(face.SecondValue);
        Assert.Equal(.8, face.Progress);
        Assert.DoesNotContain(Loc.T("gptFiveHour"), face.Tip);
        Assert.Contains(limits.SevenDay.ResetsAt!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"), face.Tip);
    }

    [Fact]
    public void Codex_bucket_overrides_legacy_and_other_model_buckets()
    {
        var limits = Parse("{\"rateLimits\":" + Both + ",\"rateLimitsByLimitId\":{\"codex\":" + SevenDay
            + ",\"codex_other\":" + Both.Replace("80", "42") + "}}");
        Assert.NotNull(limits);
        Assert.Null(limits.FiveHour);
        Assert.Equal(80, limits.SevenDay!.UsedPercent);
        Assert.Null(Parse("{\"rateLimits\":" + SevenDay.Replace("\"codex\"", "\"codex_other\"") + "}"));
    }

    [Fact]
    public async Task Untagged_legacy_must_not_inherit_another_named_bucket()
    {
        const string input = """
            {"rateLimits":{"primary":{"usedPercent":42,"windowDurationMins":10080,"resetsAt":1793784675}},
             "rateLimitsByLimitId":{"codex_other":{"limitId":"codex_other","primary":{"usedPercent":42,"windowDurationMins":10080,"resetsAt":1793784675}}}}
            """;
        Assert.Null(Parse(input));
        var result = await GptQuotaReader.ReadProtocolAsync(new StringReader(Transcript("{\"type\":\"chatgpt\"}",
            "{\"id\":3,\"result\":" + input.ReplaceLineEndings("") + "}")), new StringWriter(), Now, TimeSpan.FromSeconds(1));
        Assert.Equal(GptQuotaStatus.Unsupported, result.Status);
        Assert.Null(result.Limits);
    }

    [Fact]
    public void Explicit_codex_legacy_and_untagged_legacy_without_other_buckets_remain_compatible()
    {
        var untagged = SevenDay.Replace("\"limitId\":\"codex\",", "");
        foreach (var map in new[] { "null", "{}" })
            Assert.Equal(80, Parse("{\"rateLimits\":" + untagged + ",\"rateLimitsByLimitId\":" + map + "}")!.SevenDay!.UsedPercent);
        Assert.Equal(80, Parse("{\"rateLimits\":" + SevenDay + ",\"rateLimitsByLimitId\":{\"codex_other\":{\"limitId\":\"codex_other\"}}}")!.SevenDay!.UsedPercent);
    }

    [Fact]
    public void Swapped_primary_and_secondary_windows_are_presented_in_their_actual_duration_order()
    {
        var limits = Limits(Both);
        Assert.Equal(12.5, limits.FiveHour!.UsedPercent);
        Assert.Equal(80, limits.SevenDay!.UsedPercent);
        var face = GptUsageWidget.Present(new(GptQuotaStatus.Ready, limits, false), Now);
        Assert.Equal(("12.5%", "80%", .125, .8), (face.Value, face.SecondValue, face.Progress, face.SecondProgress));
        Assert.Contains(Loc.T("gptFiveHour"), face.Caption);
        Assert.Contains(Loc.T("gptSevenDay"), face.SecondCaption);
        Assert.True(face.Dual);
        Assert.False(face.HasMarker);
    }

    [Theory]
    [InlineData("{\"rateLimits\":null}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":12,\"windowDurationMins\":60}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":-2,\"windowDurationMins\":300}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":\"80\",\"windowDurationMins\":10080}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"windowDurationMins\":10080}}}")]
    public void Unknown_or_invalid_windows_do_not_become_zero_usage(string json) => Assert.Null(Parse(json));

    [Fact]
    public void Missing_or_invalid_reset_is_unknown_and_does_not_discard_real_usage()
    {
        var limits = Limits(SevenDay.Replace("1793784675", "9223372036854775807"));
        Assert.Null(limits.SevenDay!.ResetsAt);
        var face = GptUsageWidget.Present(new(GptQuotaStatus.Ready, limits, false), Now);
        Assert.Equal("80%", face.Value);
        Assert.Contains(Loc.T("gptResetUnknown"), face.Tip);
    }

    [Fact]
    public async Task Protocol_uses_only_the_read_allowlist_and_no_quota_params()
    {
        var output = new StringWriter();
        var result = await GptQuotaReader.ReadProtocolAsync(new StringReader(Transcript("{\"type\":\"chatgpt\",\"email\":\"private@example.invalid\"}")),
            output, Now, TimeSpan.FromSeconds(1));
        Assert.Equal(GptQuotaStatus.Ready, result.Status);
        var sent = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal(new[] { "initialize", "initialized", "account/read", "account/rateLimits/read" },
                sent.Select(document => document.RootElement.GetProperty("method").GetString()));
            Assert.False(sent[2].RootElement.GetProperty("params").GetProperty("refreshToken").GetBoolean());
            Assert.False(sent[3].RootElement.TryGetProperty("params", out _));
            Assert.DoesNotContain("private@example.invalid", JsonSerializer.Serialize(result));
            Assert.DoesNotContain("private@example.invalid", output.ToString());
        }
        finally { foreach (var document in sent) document.Dispose(); }
    }

    [Theory]
    [InlineData("null", GptQuotaStatus.NoLogin)]
    [InlineData("{\"type\":\"apiKey\"}", GptQuotaStatus.Unsupported)]
    [InlineData("{\"type\":\"amazonBedrock\"}", GptQuotaStatus.Unsupported)]
    public async Task Account_mode_is_checked_before_requesting_chatgpt_quota(string account, GptQuotaStatus expected)
    {
        var output = new StringWriter();
        var result = await GptQuotaReader.ReadProtocolAsync(new StringReader(Transcript(account)), output, Now, TimeSpan.FromSeconds(1));
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Limits);
        Assert.DoesNotContain("account/rateLimits/read", output.ToString());
    }

    [Theory]
    [InlineData(-32600, GptQuotaStatus.Unavailable)]
    [InlineData(-32601, GptQuotaStatus.Unsupported)]
    public async Task Protocol_errors_do_not_invent_login_or_quota_state(int code, GptQuotaStatus expected)
    {
        var transcript = Transcript("{\"type\":\"chatgpt\"}", "{\"id\":3,\"error\":{\"code\":" + code + ",\"message\":\"private failure\"}}");
        var result = await GptQuotaReader.ReadProtocolAsync(new StringReader(transcript), new StringWriter(), Now, TimeSpan.FromSeconds(1));
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Limits);
        Assert.DoesNotContain("private failure", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task An_unresponsive_server_times_out_and_does_not_block_a_refresh_forever()
    {
        var watch = Stopwatch.StartNew();
        var result = await GptQuotaReader.ReadProtocolAsync(new SilentReader(), new StringWriter(), Now, TimeSpan.FromMilliseconds(50));
        Assert.Equal(GptQuotaStatus.Timeout, result.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Eof_and_malformed_protocol_data_fail_without_zero_usage()
    {
        foreach (var input in new[] { "", "not-json", "{\"id\":1,\"result\":{}}\n{\"id\":2,\"result\":true}" })
        {
            var result = await GptQuotaReader.ReadProtocolAsync(new StringReader(input), new StringWriter(), Now, TimeSpan.FromSeconds(1));
            Assert.Equal(GptQuotaStatus.Unavailable, result.Status);
            Assert.Null(result.Limits);
        }
    }

    [Fact]
    public void The_local_stdio_client_cannot_open_a_console_or_start_a_turn()
    {
        var info = GptQuotaReader.StartInfo(GptQuotaReader.InstalledClient);
        Assert.True(info.CreateNoWindow);
        Assert.False(info.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Hidden, info.WindowStyle);
        Assert.True(info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError);
        Assert.Equal(new[] { "app-server" }, info.ArgumentList);
        Assert.EndsWith(Path.Combine("OpenAI", "Codex", "bin", "codex-stable.exe"), info.FileName);
    }

    [Fact]
    public async Task Multiple_widgets_share_one_background_request_and_retry_no_sooner_than_90_seconds()
    {
        var release = new TaskCompletionSource<GptQuotaResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new GptQuotaService(_ => { Interlocked.Increment(ref calls); return release.Task; });
        var pending = service.RefreshAsync(Now);
        Assert.Same(pending, service.RefreshAsync(Now.AddSeconds(30)));
        Assert.False(pending.IsCompleted);
        release.SetResult(new(GptQuotaStatus.Ready, Limits()));
        await pending;
        await service.RefreshAsync(Now.AddSeconds(89));
        Assert.Equal(1, calls);
        await service.RefreshAsync(Now.AddSeconds(90));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Failed_refresh_retains_explicitly_stale_data_and_logout_clears_previous_account_data()
    {
        var responses = new Queue<GptQuotaResult>([new(GptQuotaStatus.Ready, Limits()), new(GptQuotaStatus.Timeout), new(GptQuotaStatus.NoLogin)]);
        var service = new GptQuotaService(_ => Task.FromResult(responses.Dequeue()));
        await service.RefreshAsync(Now);
        await service.RefreshAsync(Now.AddSeconds(90));
        var stale = GptUsageWidget.Present(service.Snapshot, Now.AddSeconds(90));
        Assert.Equal("80%", stale.Value);
        Assert.True(stale.Dim);
        Assert.Contains(Loc.T("gptStaleShort"), stale.Caption);
        Assert.Contains(Loc.T("gptTimeout"), stale.Tip);
        await service.RefreshAsync(Now.AddSeconds(180));
        Assert.Null(service.Snapshot.Limits);
        Assert.Equal("—", GptUsageWidget.Present(service.Snapshot, Now.AddSeconds(180)).Value);
    }

    [Fact]
    public void Aged_or_reset_data_remains_visible_but_is_never_recalculated_to_zero()
    {
        var aged = new GptQuotaSnapshot(GptQuotaStatus.Ready, Limits(), false);
        Assert.True(aged.Stale(Now.AddMinutes(5)));
        var reset = new GptQuotaLimits(null, new(80, 10080, Now), Now);
        var face = GptUsageWidget.Present(new(GptQuotaStatus.Ready, reset, false), Now);
        Assert.Equal("80%", face.Value);
        Assert.True(face.Dim);
        Assert.Contains(Loc.T("gptStale"), face.Tip);
    }

    [Fact]
    public void Factory_and_default_wide_size_reuse_the_readable_neutral_presentation()
    {
        Assert.Contains(WidgetSpec.GptUsage, WidgetSpec.Kinds);
        var widget = Assert.IsType<GptUsageWidget>(WidgetTile.Create("gpt", new WidgetSpec { Kind = WidgetSpec.GptUsage }));
        Assert.False(widget.Square(true));
        Assert.Equal(64, WidgetPresentation.ContentHeight([widget], 30, true));
        widget.Spec.Size = WidgetSpec.SquareSize;
        Assert.True(widget.Square(true));
        Assert.Equal(30, WidgetPresentation.ContentHeight([widget], 30, true));
        widget.RefreshFace();
        Assert.False(widget.Presentation!.HasMarker);
        Assert.Equal(widget.Readable.Value, widget.Presentation.Value);
        foreach (var key in Loc.Keys.Where(k => k.StartsWith("gpt") || k == "widget_gpt-usage"))
            Assert.True(Loc.HasChineseTranslation(key), key);
    }

    static string Transcript(string account, string? quota = null) =>
        "{\"id\":1,\"result\":{\"userAgent\":\"codex-test\"}}\n"
        + "{\"method\":\"account/updated\",\"params\":{}}\n"
        + "{\"id\":2,\"result\":{\"account\":" + account + ",\"requiresOpenaiAuth\":true}}\n"
        + (quota ?? "{\"id\":3,\"result\":{\"rateLimits\":" + SevenDay + "}}") + "\n";

    sealed class SilentReader : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
    }
}
