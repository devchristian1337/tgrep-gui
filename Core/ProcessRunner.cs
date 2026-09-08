using System.Diagnostics;
using System.Text;

namespace TgrepGui.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public static class ProcessRunner
{
    public static Process Start(string executable, IEnumerable<string> arguments, string folder)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = folder
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        // Do not pass the GUI's private WinAppSDK runtime location to child programs.
        info.Environment.Remove("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY");
        return Process.Start(info) ?? throw new IOException($"Unable to start {executable}.");
    }

    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        string folder, Func<string, ValueTask>? stdout, Action<string>? stderr, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = Start(executable, arguments, folder);
        using var registration = token.Register(() => Kill(process));
        var output = new StringBuilder();
        var error = new StringBuilder();
        var outTask = PumpAsync(process.StandardOutput, line =>
        {
            if (stdout != null) return stdout(line);
            AppendBounded(output, line);
            return ValueTask.CompletedTask;
        });
        var errTask = PumpAsync(process.StandardError, line =>
        {
            AppendBounded(error, line);
            stderr?.Invoke(line);
            return ValueTask.CompletedTask;
        });
        try
        {
            // A parsing/consumer error must also terminate the producer, otherwise a full
            // stdout pipe can deadlock WaitForExitAsync.
            var exit = process.WaitForExitAsync(token);
            var pending = new List<Task> { outTask, errTask, exit };
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                await finished.ConfigureAwait(false);
                pending.Remove(finished);
            }
            token.ThrowIfCancellationRequested();
            return new(process.ExitCode, output.ToString(), error.ToString());
        }
        catch
        {
            Kill(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
            try { await Task.WhenAll(outTask, errTask).ConfigureAwait(false); } catch { /* preserve original failure */ }
            token.ThrowIfCancellationRequested();
            throw;
        }
    }

    internal static async Task PumpAsync(StreamReader reader, Func<string, ValueTask> receive)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            await receive(line).ConfigureAwait(false);
    }

    internal static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }

    private static void AppendBounded(StringBuilder builder, string line)
    {
        builder.AppendLine(line);
        if (builder.Length > 32768) builder.Remove(0, builder.Length - 32768);
    }
}
