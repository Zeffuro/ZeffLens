using ZeffLens.Diagnostics;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace ZeffLens;

internal static class GameFrameProbe
{
    public static unsafe NativeFrameReading? Read()
    {
        if (GameFramework.Addresses.Instance.Value == 0)
            return null;

        var framework = GameFramework.Instance();
        if (framework is null || framework->IsDestroying || framework->IsExiting || framework->IsFreed)
            return null;

        var ms = framework->RealFrameDeltaTime * 1000;
        return float.IsFinite(ms) && ms > 0 ? new NativeFrameReading(framework->FrameCounter, ms) : null;
    }
}
