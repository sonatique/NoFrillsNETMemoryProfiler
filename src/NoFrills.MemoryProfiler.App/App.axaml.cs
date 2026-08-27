using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using NoFrills.MemoryProfiler.Core;

namespace NoFrills.MemoryProfiler.App;

public partial class App : Application
{
    private const string PaletteUri = "avares://NoFrills.MemoryProfiler.App/Theme/Palette.axaml";
    private const string SystemPaletteUri = "avares://NoFrills.MemoryProfiler.App/Theme/SystemPalette.axaml";

    /// <summary>
    /// Forces the look, ignoring the setting. Tests set it so they do not depend on what happens to
    /// be in the settings file of the machine they run on.
    /// </summary>
    public static bool? ForcedProfilerTheme { get; set; }

    /// <summary>Whether the profiler's own look is the one in use.</summary>
    public static bool UsesProfilerTheme { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // The look is chosen once, at startup: swapping the style sheet under a live window leaves
        // controls holding brushes from the sheet that just left, so the setting asks for a restart
        // rather than pretending it can change underneath.
        UsesProfilerTheme = ForcedProfilerTheme ?? ProfilerSettings.Load().UseProfilerTheme;

        Styles.Add(new StyleInclude(new Uri("avares://NoFrills.MemoryProfiler.App/"))
        {
            Source = new Uri(UsesProfilerTheme ? PaletteUri : SystemPaletteUri)
        });

        RequestedThemeVariant = UsesProfilerTheme ? ThemeVariant.Dark : ThemeVariant.Default;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
