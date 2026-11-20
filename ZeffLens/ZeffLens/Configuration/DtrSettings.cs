namespace ZeffLens.Configuration;

public sealed class DtrSettings
{
    public bool Enabled { get; set; }
    public string Text { get; set; } = "ZeffLens";
    public string Tooltip { get; set; } = "Click to toggle ZeffLens.";
    public bool ShowWhenLoggedOut { get; set; }
    public bool ClickToOpenConfig { get; set; } = true;
}
