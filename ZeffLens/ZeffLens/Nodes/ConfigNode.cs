using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using ZeffLens.Configuration.Persistence;

namespace ZeffLens.Nodes;

public sealed class ConfigNode : ResNode
{
    private readonly ScrollingNode<VerticalListNode> listNode;

    public ConfigNode()
    {
        this.listNode = new ScrollingNode<VerticalListNode>
        {
            AutoHideScrollBar = true,
            ContentNode =
            {
                FitWidth = true,
                FitContents = true,
                ItemSpacing = 6.0f,
            },
        };
        this.listNode.AttachNode(this);

        this.listNode.ContentNode.AddNode(new TextNode
        {
            Height = 34.0f,
            FontSize = 20,
            AlignmentType = AlignmentType.Left,
            String = "Configuration",
        });

        this.listNode.ContentNode.AddNode(new CheckboxNode
        {
            Height = 28.0f,
            String = "Enabled",
            IsChecked = PluginState.Config.General.Enabled,
            OnClick = SetEnabled,
        });
        this.listNode.ContentNode.AddNode(new CheckboxNode
        {
            Height = 28.0f,
            String = "Debug enabled",
            IsChecked = PluginState.Config.General.DebugEnabled,
            OnClick = SetDebugEnabled,
        });


    }

    public void OnClose()
    {
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();

        this.listNode.Size = this.Size;
        this.listNode.RecalculateSizes();
    }

    private static void SetEnabled(bool enabled)
    {
        PluginState.Config.General.Enabled = enabled;
        ConfigRepository.Save(PluginState.Config);
    }

    private static void SetDebugEnabled(bool enabled)
    {
        PluginState.Config.General.DebugEnabled = enabled;
        ConfigRepository.Save(PluginState.Config);
    }

}
