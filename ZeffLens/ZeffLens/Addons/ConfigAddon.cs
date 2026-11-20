using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using ZeffLens.Nodes;

namespace ZeffLens.Addons;

public sealed unsafe class ConfigAddon : NativeAddon
{
    private ConfigNode? configNode;
    private Vector2 lastContentPosition;
    private Vector2 lastContentSize;

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        base.OnSetup(addon, atkValueSpan);

        this.configNode = new ConfigNode
        {
            Position = this.ContentStartPosition,
            Size = this.ContentSize,
        };
        this.configNode.AttachNode(this);

        this.lastContentPosition = this.ContentStartPosition;
        this.lastContentSize = this.ContentSize;
    }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);

        if (this.configNode is null)
        {
            return;
        }

        if (this.ContentStartPosition == this.lastContentPosition && this.ContentSize == this.lastContentSize)
        {
            return;
        }

        this.configNode.Position = this.ContentStartPosition;
        this.configNode.Size = this.ContentSize;
        this.lastContentPosition = this.ContentStartPosition;
        this.lastContentSize = this.ContentSize;
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        this.configNode?.OnClose();
        base.OnFinalize(addon);

        this.configNode = null;
    }
}
