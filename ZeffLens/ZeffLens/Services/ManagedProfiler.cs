using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZeffLens.Diagnostics;

namespace ZeffLens;

internal sealed class ManagedProfiler(
    string helperPath,
    Func<string?, ProfileAssembly[]> getAssemblies,
    Func<int> getPluginRevision,
    Action<Exception> logError)
{
    private readonly object _gate = new();
    private volatile CaptureJob? _job;
    private ManagedProfile? _result;
    private ManagedProfile? _scan;
    private string? _error;
    private bool _shutDown;
    public bool IsRunning => _job is not null;

    public (ProfileProgress? Progress, bool Cancelling, ManagedProfile? Result, ManagedProfile? Scan, string? Error) Read()
    {
        lock (_gate)
        {
            if (_job is not { } job)
                return (null, false, _result, _scan, _error);

            var secondsLeft = job.Started == 0 ? (double?)null :
                Math.Max(0, job.Seconds - Stopwatch.GetElapsedTime(job.Started).TotalSeconds);
            return (new ProfileProgress(job.Plugin ?? "loaded plugins", secondsLeft, job.Finishing),
                job.Cancel.IsCancellationRequested, _result, _scan, _error);
        }
    }

    public bool Start(string? plugin, ManagedProfileMode mode, int seconds)
    {
        lock (_gate)
        {
            if (_job is not null || _shutDown)
                return false;

            var job = new CaptureJob(plugin, Math.Clamp(seconds, 5, 20));
            _job = job;
            _error = null;
            job.Work = Task.Run(() => CaptureAsync(job, mode));
            return true;
        }
    }

    public void Cancel()
    {
        lock (_gate)
            _job?.Cancel.Cancel();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _job?.Cancel.Cancel();
            _result = null;
            _scan = null;
            _error = null;
        }
    }

    public Task ShutdownAsync()
    {
        lock (_gate)
        {
            _shutDown = true;
            _job?.Cancel.Cancel();
            return _job?.Work ?? Task.CompletedTask;
        }
    }

    private async Task CaptureAsync(CaptureJob job, ManagedProfileMode mode)
    {
        ManagedProfile? result = null;
        string? error = null;
        try
        {
            job.Cancel.Token.ThrowIfCancellationRequested();
            if (!File.Exists(helperPath))
                throw new ProfileException("Profiler helper is missing. Keep the profiler folder beside ZeffLens.dll.");

            var revision = getPluginRevision();
            var assemblies = getAssemblies(job.Plugin);
            if (assemblies.Length == 0)
                throw new ProfileException("No managed assemblies found for this selection. Check that the plugin is loaded.");

            var request = new ProfileRequest(Environment.ProcessId, job.Plugin ?? "Loaded plugins", mode, job.Seconds,
                assemblies.Select(item => item.Identity).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                ReportProgress: true, Targets: assemblies, IsScan: job.Plugin is null);
            result = await RunHelperAsync(request, job).ConfigureAwait(false);
            if (result.IsScan != request.IsScan)
                throw new ProfileException("The profiler helper is out of date. Rebuild or update ZeffLens.");
            if (getPluginRevision() != revision)
                throw new ProfileException("The plugin list changed during profiling. Run it again with the same plugins loaded.");
        }
        catch (ProfileException ex)
        {
            error = ex.Message;
        }
        catch (OperationCanceledException) when (job.Cancel.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            error = "Profile timed out. Try a shorter run.";
        }
        catch (Exception ex)
        {
            logError(ex);
            error = "Profile failed. See the Dalamud log.";
        }
        finally
        {
            lock (_gate)
            {
                if (!job.Cancel.IsCancellationRequested)
                {
                    if (error is null && result is not null)
                    {
                        if (result.IsScan)
                            _scan = result;
                        else
                            _result = result;
                    }
                    _error = error;
                }

                _job = null;
                job.Cancel.Dispose();
            }
        }
    }

    private async Task<ManagedProfile> RunHelperAsync(ProfileRequest request, CaptureJob job)
    {
        var cancellationToken = job.Cancel.Token;
        var scratch = Directory.CreateTempSubdirectory("zefflens-profile-");
        try
        {
            var start = new ProcessStartInfo(helperPath)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(scratch.FullName);
            UseHostRuntime(start);

            cancellationToken.ThrowIfCancellationRequested();
            using var helper = Process.Start(start) ?? throw new IOException("Could not start the profiler helper.");
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(55));
                var stdout = ReadHelperOutputAsync(helper.StandardOutput, job, timeout.Token);
                var stderr = helper.StandardError.ReadToEndAsync(timeout.Token);
                await helper.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token).ConfigureAwait(false);
                await helper.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                await helper.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

                var output = await stdout.ConfigureAwait(false);
                var failure = await stderr.ConfigureAwait(false);
                if (helper.ExitCode != 0)
                {
                    var message = failure.Trim();
                    if (message.Contains("You must install or update .NET", StringComparison.OrdinalIgnoreCase))
                        throw new ProfileException("The profiler helper needs the x64 .NET 10 Runtime.");
                    throw new ProfileException(message.Length == 0 ? "Profiler helper failed." : message[..Math.Min(message.Length, 280)]);
                }

                return output ?? throw new InvalidDataException("Profiler helper returned no results.");
            }
            finally
            {
                await StopHelperAsync(helper).ConfigureAwait(false);
            }
        }
        finally
        {
            try { scratch.Delete(recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logError(ex); }
        }
    }

    private static void UseHostRuntime(ProcessStartInfo start)
    {
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        if (runtime.Parent is not { Name: "Microsoft.NETCore.App", Parent: { Name: "shared", Parent: { } root } })
            return;

        if (Directory.Exists(Path.Combine(root.FullName, "host", "fxr")))
            start.Environment["DOTNET_ROOT_X64"] = root.FullName;
    }

    private async Task<ManagedProfile?> ReadHelperOutputAsync(StreamReader reader, CaptureJob job, CancellationToken token)
    {
        ManagedProfile? result = null;
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith('{'))
                result = JsonSerializer.Deserialize<ManagedProfile>(line);
            else if (line is "recording" or "finishing")
            {
                lock (_gate)
                {
                    if (line == "recording")
                        job.Started = Stopwatch.GetTimestamp();
                    else
                        job.Finishing = true;
                }
            }
        }

        return result;
    }

    private static async Task StopHelperAsync(Process helper)
    {
        if (helper.HasExited)
            return;

        try { helper.StandardInput.Close(); }
        catch (IOException) { }
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await helper.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!helper.HasExited)
            {
                try { helper.Kill(); }
                catch (InvalidOperationException) when (helper.HasExited) { }
            }
            await helper.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    internal readonly record struct ProfileProgress(string Plugin, double? SecondsLeft, bool Finishing);

    private sealed class CaptureJob(string? plugin, int seconds)
    {
        public readonly string? Plugin = plugin;
        public readonly int Seconds = seconds;
        public readonly CancellationTokenSource Cancel = new();
        public Task Work = Task.CompletedTask;
        public long Started;
        public bool Finishing;
    }

    private sealed class ProfileException(string message) : Exception(message);
}
