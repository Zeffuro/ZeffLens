using System;
using Dalamud.Configuration;

namespace ZeffLens.Configuration;

[Serializable]
public sealed class SystemConfiguration : IPluginConfiguration
{
    public const int CurrentVersion = 2;

    public int Version { get; set; }

    private GeneralSettings _general = new();
    private DtrSettings _dtr = new();
    private DiagnosticsSettings _diagnostics = new();

    public GeneralSettings General
    {
        get => _general;
        set => _general = value ?? new GeneralSettings();
    }

    public DtrSettings Dtr
    {
        get => _dtr;
        set => _dtr = value ?? new DtrSettings();
    }

    public DiagnosticsSettings Diagnostics
    {
        get => _diagnostics;
        set => _diagnostics = value ?? new DiagnosticsSettings();
    }

    public void EnsureInitialized()
    {
        _general ??= new GeneralSettings();
        _dtr ??= new DtrSettings();
        _diagnostics ??= new DiagnosticsSettings();

        if (Version < CurrentVersion)
        {
            Migrate();
        }
    }

    private void Migrate()
    {
        while (Version < CurrentVersion)
        {
            switch (Version)
            {
                case <= 0:
                    MigrateFromUnversionedConfig();
                    Version = 1;
                    break;
                case 1:
                    _diagnostics ??= new DiagnosticsSettings();
                    Version = 2;
                    break;
                default:
                    Version = CurrentVersion;
                    break;
            }
        }
    }

    private void MigrateFromUnversionedConfig()
    {
    }
}
