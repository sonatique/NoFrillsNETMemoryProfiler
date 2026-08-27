using System.Text.Json;
using Avalonia;
using Avalonia.Headless.NUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using NoFrills.MemoryProfiler.App.ViewModels;
using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.UiTests;

/// <summary>
/// The look can be turned off in favour of the system colours. It is read once at startup, so what
/// is checked here is that the choice is remembered, that the user is told a restart applies it,
/// and that both style sheets are loadable - a mistyped one would only fail on the machine of
/// whoever picked that setting.
/// </summary>
public sealed class TestThemeSetting
{
    private const string PaletteUri = "avares://NoFrills.MemoryProfiler.App/Theme/Palette.axaml";
    private const string SystemPaletteUri = "avares://NoFrills.MemoryProfiler.App/Theme/SystemPalette.axaml";

    [AvaloniaTest]
    public void BothLooksAreLoadable()
    {
        foreach (var uri in new[] { PaletteUri, SystemPaletteUri })
        {
            var include = new StyleInclude(new Uri("avares://NoFrills.MemoryProfiler.App/")) { Source = new Uri(uri) };

            Assert.That(include.Loaded, Is.Not.Null, $"{uri} must be a style sheet that loads");
            Assert.That(((Styles)include.Loaded!), Is.Not.Empty, uri);
        }
    }

    [AvaloniaTest]
    public void TheLookInUseIsTheOneThatWasAskedFor()
    {
        Assert.That(NoFrills.MemoryProfiler.App.App.UsesProfilerTheme, Is.True,
            "the tests force the profiler look, so that is what must be in place");
        Assert.That(Application.Current!.RequestedThemeVariant, Is.EqualTo(ThemeVariant.Dark));
    }

    [AvaloniaTest]
    public void TurningItOffIsRememberedAndSaysWhenItApplies()
    {
        var settingsPath = ProfilerSettings.SettingsFilePath;
        var backup = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;

        try
        {
            var model = new MainViewModel { ProcessSource = () => Array.Empty<LiveProcess>() };

            Assert.That(model.UseProfilerTheme, Is.True, "the profiler's own look is the default");

            model.UseProfilerTheme = false;

            Assert.That(model.Status, Does.Contain("system colours").And.Contain("next time"),
                "a setting that needs a restart has to say so");

            var saved = JsonSerializer.Deserialize<ProfilerSettings>(File.ReadAllText(settingsPath))!;
            Assert.That(saved.UseProfilerTheme, Is.False, "and it has to survive the restart it asks for");

            model.UseProfilerTheme = true;

            saved = JsonSerializer.Deserialize<ProfilerSettings>(File.ReadAllText(settingsPath))!;
            Assert.That(saved.UseProfilerTheme, Is.True);
            Assert.That(model.Status, Does.Contain("profiler's own colours"));
        }
        finally
        {
            if (backup != null) File.WriteAllText(settingsPath, backup);
            else if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    [AvaloniaTest]
    public void TheSystemLookStillTellsTheStatesApart()
    {
        // Colour that carries meaning survives the look being switched off; colour that is only
        // decoration does not. Checked on the style sheet itself, since only one look is loaded.
        var include = new StyleInclude(new Uri("avares://NoFrills.MemoryProfiler.App/")) { Source = new Uri(SystemPaletteUri) };
        var styles = (Styles)include.Loaded!;

        var selectors = styles.OfType<Style>().Select(style => style.Selector?.ToString() ?? string.Empty).ToList();

        Assert.That(selectors, Has.Some.Contains("suspect"), "a holder to investigate must still stand out");
        Assert.That(selectors, Has.Some.Contains("warning"));
        Assert.That(selectors, Has.Some.Contains("expected"));
        Assert.That(selectors, Has.Some.Contains("panel"), "the separators need a colour in both looks");

        Assert.That(selectors, Has.None.Contains("Window"), "the system decides the window colour");
        Assert.That(selectors, Has.None.Contains("DataGridColumnHeader"), "and the colour of the tables");
    }
}
