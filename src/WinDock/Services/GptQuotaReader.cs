using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WinDock.Services;

public sealed class GptQuotaReader
{
    public static string InstalledClient => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenAI", "Codex", "bin", "codex-stable.exe");

    public static ProcessStartInfo StartInfo(string executable)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        info.ArgumentList.Add("app-server");
        return info;
    }

    public async Task<GptQuotaResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var executable = InstalledClient;
        if (!File.Exists(executable)) return new(GptQuotaStatus.NoClient);
        using var process = new Process { StartInfo = StartInfo(executable) };
        var started = false;
        try
        {
            started = process.Start();
            if (!started) return new(GptQuotaStatus.Unavailable);
            var drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            try
            {
                return await ReadProtocolAsync(process.StandardOutput, process.StandardInput, DateTimeOffset.UtcNow,
                    TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                process.StandardInput.Close();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    if (!process.HasExited) process.Kill(true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                try { await drain.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException) { }
            }
        }
        catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            return new(GptQuotaStatus.Unavailable);
        }
        finally
        {
            if (started)
                try { if (!process.HasExited) process.Kill(true); }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
        }
    }

    public static async Task<GptQuotaResult> ReadProtocolAsync(TextReader input, TextWriter output, DateTimeOffset fetchedAt,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        try
        {
            await Send(output, "{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"windock\",\"title\":\"WinDock\",\"version\":\"1\"}}}", token);
            using var initialized = await Response(input, 1, token);
            if (Error(initialized.RootElement) is { } initError) return new(initError);
            await Send(output, "{\"method\":\"initialized\",\"params\":{}}", token);
            await Send(output, "{\"method\":\"account/read\",\"id\":2,\"params\":{\"refreshToken\":false}}", token);
            using var account = await Response(input, 2, token);
            if (Error(account.RootElement) is { } accountError) return new(accountError);
            var auth = account.RootElement.GetProperty("result");
            if (!auth.TryGetProperty("account", out var identity) || identity.ValueKind == JsonValueKind.Null)
                return new(auth.TryGetProperty("requiresOpenaiAuth", out var required) && required.ValueKind == JsonValueKind.True
                    ? GptQuotaStatus.NoLogin : GptQuotaStatus.Unsupported);
            if (identity.ValueKind != JsonValueKind.Object || !identity.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String || type.GetString() is not ("chatgpt" or "chatgptAuthTokens"))
                return new(GptQuotaStatus.Unsupported);
            await Send(output, "{\"method\":\"account/rateLimits/read\",\"id\":3}", token);
            using var quota = await Response(input, 3, token);
            if (Error(quota.RootElement) is { } quotaError) return new(quotaError);
            var limits = GptQuotaLimits.Parse(quota.RootElement.GetProperty("result"), fetchedAt);
            return limits is null ? new(GptQuotaStatus.Unsupported) : new(GptQuotaStatus.Ready, limits);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(GptQuotaStatus.Timeout); }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or KeyNotFoundException)
        {
            return new(GptQuotaStatus.Unavailable);
        }
    }

    static GptQuotaStatus? Error(JsonElement message)
    {
        if (!message.TryGetProperty("error", out var error)) return null;
        return error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) && value == -32601
            ? GptQuotaStatus.Unsupported : GptQuotaStatus.Unavailable;
    }

    static async Task Send(TextWriter output, string json, CancellationToken token)
    {
        await output.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    static async Task<JsonDocument> Response(TextReader input, int request, CancellationToken token)
    {
        for (var count = 0; count < 128; count++)
        {
            var line = await input.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null || line.Length > 262144) throw new IOException();
            var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                && id.TryGetInt32(out var value) && value == request) return document;
            document.Dispose();
        }
        throw new IOException();
    }
}
