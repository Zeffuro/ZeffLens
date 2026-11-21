using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.ImGuiNotification;
using ZeffLens.Diagnostics;

namespace ZeffLens;

internal static class ReportExport
{
    private static int _writing;

    public static bool IsWriting => Volatile.Read(ref _writing) != 0;

    public static Task WriteAsync(MemoryDiagnosticsService diagnostics)
        => WriteAsync(diagnostics.ExportReport);

    public static Task WriteAsync(MemoryDiagnosticsService diagnostics, ManagedProfile profile)
        => WriteAsync(() => diagnostics.ExportReport(profile));

    public static Task WriteAsync(MemoryDiagnosticsService diagnostics, AddonTiming timing)
        => WriteAsync(() => diagnostics.ExportReport(timing));

    public static Task WriteAsync(MemoryDiagnosticsService diagnostics, FrameCapture? reference, FrameCapture? candidate)
        => WriteAsync(() => diagnostics.ExportReport(reference, candidate));

    public static Task WriteAsync(MemoryDiagnosticsService diagnostics, ReloadComparison comparison)
        => WriteAsync(() => diagnostics.ExportReport(comparison));

    public static Task WriteAsync(MemoryDiagnosticsService diagnostics, ReloadRun[] runs)
        => WriteAsync(() => diagnostics.ExportReport(runs));

    private static async Task WriteAsync(Func<string> export)
    {
        if (Interlocked.Exchange(ref _writing, 1) != 0)
            return;

        var notifications = Service.NotificationManager;
        var logger = Service.Logger;
        var notification = new Notification { Title = "ZeffLens" };
        try
        {
            var path = await Task.Run(export).ConfigureAwait(false);
            notification.Type = NotificationType.Success;
            notification.Content = $"Saved {Path.GetFileName(path)}. Copy the full path from the Memory tab.";
            logger.Information("Saved diagnostics report to {Path}", path);
        }
        catch (Exception ex)
        {
            notification.Type = NotificationType.Error;
            notification.Content = "Could not export JSON. See the Dalamud log.";
            logger.Warning(ex, "Diagnostics report export failed.");
        }
        finally
        {
            Volatile.Write(ref _writing, 0);
        }

        if (ReferenceEquals(Service.NotificationManager, notifications))
            notifications.AddNotification(notification);
    }
}
