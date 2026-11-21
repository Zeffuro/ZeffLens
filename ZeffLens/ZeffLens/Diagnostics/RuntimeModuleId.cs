using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ZeffLens.Diagnostics;

internal static class RuntimeModuleId
{
    private static readonly MethodInfo? ReadHandle = Environment.Version.Major == 10 && OperatingSystem.IsWindows() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64
        ? typeof(Module).Assembly.GetType("System.Reflection.RuntimeModule")?
            .GetMethod("GetUnderlyingNativeHandle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, Type.EmptyTypes)
        : null;

    public static long Read(Module module)
    {
        // CoreCLR 10 uses this handle as EventPipe's ModuleID.
        if (ReadHandle?.ReturnType != typeof(IntPtr) || module.GetType() != ReadHandle.DeclaringType)
            return 0;
        return ReadHandle.Invoke(module, null) is IntPtr handle ? handle.ToInt64() : 0;
    }
}
