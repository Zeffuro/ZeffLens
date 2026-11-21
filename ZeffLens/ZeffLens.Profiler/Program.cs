using System.Diagnostics;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using ZeffLens.Diagnostics;
using ZeffLens.Profiler;

try
{
    var line = Console.ReadLine();
    var request = line is null ? null : JsonSerializer.Deserialize<ProfileRequest>(line);
    if (request is null || request.ProcessId <= 0 || request.Seconds is < 5 or > 20 ||
        (request.Targets?.Length ?? request.Assemblies.Length) == 0 || !Enum.IsDefined(request.Mode) ||
        (request.IsScan && request.Mode != ManagedProfileMode.Stacks))
        throw new InvalidDataException("Invalid profile request.");

    var scratch = Path.GetFullPath(args.Single());
    if (!Directory.Exists(scratch))
        throw new DirectoryNotFoundException("Missing profile work directory.");

    try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; }
    catch (System.ComponentModel.Win32Exception) { }

    var stop = Task.Run(Console.ReadLine);
    var tracePath = Path.Combine(scratch, "capture.nettrace");
    try
    {
        var seconds = await Capture.RunAsync(request, tracePath, stop,
            () => ReportStatus("recording", request.ReportProgress), () => ReportStatus("finishing", request.ReportProgress));
        if (stop.IsCompleted)
            return 2;

        var profile = TraceAnalysis.Read(request, tracePath, seconds);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(profile));
        return 0;
    }
    finally
    {
        RemoveTrace(tracePath);
        RemoveTrace(tracePath + ".etlx");
    }
}

catch (Exception error) when (error is ServerNotAvailableException or UnsupportedCommandException)
{
    await Console.Error.WriteLineAsync("The game's .NET diagnostics endpoint is unavailable.");
    return 1;
}
catch (Exception error)
{
    await Console.Error.WriteLineAsync(error.Message);
    return 1;
}

static void RemoveTrace(string path)
{
    try { File.Delete(path); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

static void ReportStatus(string status, bool requested)
{
    if (!requested)
        return;
    Console.Out.WriteLine(status);
    Console.Out.Flush();
}
