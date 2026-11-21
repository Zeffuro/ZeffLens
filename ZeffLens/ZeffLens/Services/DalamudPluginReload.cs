using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Plugin;

namespace ZeffLens;

internal static class DalamudPluginReload
{
    private static readonly Type? ExposedPlugin = typeof(IExposedPlugin).Assembly.GetType("Dalamud.Plugin.ExposedPlugin");
    private static readonly Type? LocalPlugin = typeof(IExposedPlugin).Assembly.GetType("Dalamud.Plugin.Internal.Types.LocalPlugin");
    private static readonly FieldInfo? PluginField = ExposedPlugin?.GetField("<plugin>P", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? ReloadMethod = LocalPlugin?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .FirstOrDefault(method => method.Name == "ReloadAsync" && !method.IsGenericMethod &&
            method.ReturnType == typeof(Task) && method.GetParameters().Length == 0);

    internal static bool IsAvailable => PluginField is not null && PluginField.FieldType == LocalPlugin && ReloadMethod is not null;

    public static async Task RunAsync(IEnumerable<IExposedPlugin> installed, string name, string ownName)
    {
        if (name.Equals(ownName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ZeffLens must stay loaded to compare the two captures.");
        if (!IsAvailable)
            throw new InvalidOperationException("Reload from Dalamud on this version.");

        var matches = installed.Where(plugin => plugin.IsLoaded && plugin.InternalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Reload needs one loaded plugin with that name.");

        var localPlugin = PluginField!.GetValue(matches[0]);
        if (localPlugin is null || ReloadMethod!.Invoke(localPlugin, null) is not Task reload)
            throw new InvalidOperationException("Could not start the Dalamud reload.");
        await reload.ConfigureAwait(false);
        if (!matches[0].IsLoaded)
            throw new InvalidOperationException("Dalamud finished without loading the plugin.");
    }
}
