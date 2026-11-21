using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Parsers;
using ZeffLens.Diagnostics;

namespace ZeffLens.Profiler;

internal static class Capture
{
    private const int BufferMb = 128;
    private const long MaxTraceBytes = 128L * 1024 * 1024;
    private const ClrTraceEventParser.Keywords Rundown = ClrTraceEventParser.Keywords.Loader |
        ClrTraceEventParser.Keywords.Jit | ClrTraceEventParser.Keywords.StopEnumeration |
        ClrTraceEventParser.Keywords.OverrideAndSuppressNGenEvents;

    public static async Task<double> RunAsync(ProfileRequest request, string path, Task stop, Action startedCapture, Action stoppedCapture)
    {
        var client = new DiagnosticsClient(request.ProcessId);
        using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var options = new EventPipeSessionConfiguration(Providers(request.Mode), BufferMb, (long)Rundown, requestStackwalk: true);
        using var session = await client.StartEventPipeSessionAsync(options, connect.Token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);

        var started = Stopwatch.GetTimestamp();
        var overflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var copy = CopyBoundedAsync(session.EventStream, output, overflow);
        using var timer = new CancellationTokenSource();
        var duration = Task.Delay(TimeSpan.FromSeconds(request.Seconds), timer.Token);
        startedCapture();
        var ended = await Task.WhenAny(duration, stop, overflow.Task, copy);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        timer.Cancel();
        stoppedCapture();

        if (ended != copy)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await session.StopAsync(timeout.Token);
        }
        await copy.WaitAsync(TimeSpan.FromSeconds(12));
        if (overflow.Task.IsCompleted)
            throw new InvalidDataException("The trace reached its 128 MiB limit. Try a shorter run.");
        if (ended == copy && !stop.IsCompleted)
            throw new IOException("The diagnostics stream closed before the capture finished.");

        return elapsed;
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, TaskCompletionSource overflow)
    {
        var buffer = new byte[64 * 1024];
        long written = 0;
        while (await input.ReadAsync(buffer.AsMemory()) is var count && count != 0)
        {
            if (overflow.Task.IsCompleted)
                continue;
            if (written + count > MaxTraceBytes)
            {
                overflow.TrySetResult();
                continue;
            }

            await output.WriteAsync(buffer.AsMemory(0, count));
            written += count;
        }
    }

    private static EventPipeProvider[] Providers(ManagedProfileMode mode)
    {
        var keywords = ClrTraceEventParser.Keywords.Loader | ClrTraceEventParser.Keywords.Jit;
        if (mode == ManagedProfileMode.Allocations)
            return [new EventPipeProvider(ClrTraceEventParser.ProviderName, EventLevel.Verbose,
                (long)(keywords | ClrTraceEventParser.Keywords.GC))];

        return
        [
            new EventPipeProvider(ClrTraceEventParser.ProviderName, EventLevel.Informational, (long)keywords),
            new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
        ];
    }
}
