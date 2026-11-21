using System;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using ZeffLens.Diagnostics;
using static TerraFX.Interop.DirectX.DXGI_MEMORY_SEGMENT_GROUP;

namespace ZeffLens;

internal static class GraphicsMemoryProbe
{
    public static GraphicsMemorySnapshot? Capture() => Capture(Service.PluginInterface.UiBuilder.DeviceHandle);

    internal static unsafe GraphicsMemorySnapshot? Capture(nint borrowedDevice)
    {
        if (borrowedDevice == 0)
            return null;

        IDXGIDevice* device = null;
        IDXGIAdapter* adapter = null;
        IDXGIAdapter3* adapter3 = null;
        var deviceId = IID.IID_IDXGIDevice;
        var adapterId = IID.IID_IDXGIAdapter3;
        try
        {
            if (((IUnknown*)borrowedDevice)->QueryInterface(&deviceId, (void**)&device).FAILED || device is null ||
                device->GetAdapter(&adapter).FAILED || adapter is null ||
                adapter->QueryInterface(&adapterId, (void**)&adapter3).FAILED || adapter3 is null)
                return null;

            DXGI_ADAPTER_DESC description;
            if (adapter->GetDesc(&description).FAILED)
                return null;

            DXGI_QUERY_VIDEO_MEMORY_INFO local, nonLocal;
            var hasLocal = adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &local).SUCCEEDED;
            var hasNonLocal = adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, &nonLocal).SUCCEEDED;
            if (!hasLocal && !hasNonLocal)
                return null;

            ReadOnlySpan<char> name = description.Description;
            var length = name.IndexOf('\0');
            var luid = ((long)description.AdapterLuid.HighPart << 32) | description.AdapterLuid.LowPart;
            return new GraphicsMemorySnapshot((length < 0 ? name : name[..length]).ToString(), luid,
                hasLocal ? local.CurrentUsage : null, hasLocal ? local.Budget : null,
                hasNonLocal ? nonLocal.CurrentUsage : null, hasNonLocal ? nonLocal.Budget : null);
        }
        finally
        {
            if (adapter3 is not null)
                adapter3->Release();
            if (adapter is not null)
                adapter->Release();
            if (device is not null)
                device->Release();
        }
    }
}
