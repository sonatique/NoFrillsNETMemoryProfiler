using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using NoFrills.MemoryProfiler.App.ViewModels;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.UiTests;

/// <summary>
/// The palette and the icon, checked rather than looked at. A mistyped resource key resolves to
/// nothing at runtime and the control simply keeps the theme default, which is invisible until
/// someone notices the window is the wrong colour.
/// </summary>
public sealed class TestTheme
{
    private static readonly (string Key, string Hex)[] Palette =
    {
        ("BackgroundBrush", "#FF201B18"),
        ("SurfaceBrush", "#FF2E2622"),
        ("SurfaceElevatedBrush", "#FF3A322D"),
        ("BorderBrush", "#FF4A3F3A"),
        ("TextPrimaryBrush", "#FFECE6E1"),
        ("TextSecondaryBrush", "#FFA89A92"),
        ("BrandBrush", "#FF6B5C56"),
        ("AccentBrush", "#FF4FAE7A"),
        ("AccentHoverBrush", "#FF63C48F"),
        ("AccentPressedBrush", "#FF3D8F63"),
        ("SuccessBrush", "#FF4FAE7A"),
        ("WarningBrush", "#FFD9A441"),
        ("ErrorBrush", "#FFC1554A"),
        ("InfoBrush", "#FF5B8FAE"),
    };

    [AvaloniaTest]
    public void EveryColourOfThePaletteIsAvailableAndIsTheOneGiven()
    {
        var application = Application.Current!;

        foreach (var (key, hex) in Palette)
        {
            Assert.That(
                application.TryGetResource(key, ThemeVariant.Dark, out var found),
                Is.True,
                $"{key} must resolve; a key that does not is silently ignored at runtime");

            Assert.That(found, Is.InstanceOf<ISolidColorBrush>(), key);
            Assert.That(((ISolidColorBrush)found!).Color.ToString().ToUpperInvariant(), Is.EqualTo(hex), key);
        }
    }

    [AvaloniaTest]
    public void TheThemeIsTheDarkOneThePaletteWasDrawnFor()
    {
        Assert.That(Application.Current!.RequestedThemeVariant, Is.EqualTo(ThemeVariant.Dark),
            "the palette has no light counterpart, so following the system would break half the machines");
    }

    [AvaloniaTest]
    public void TheAccentOfTheThemeIsTheAccentOfThePalette()
    {
        Assert.That(
            Application.Current!.TryGetResource("SystemAccentColor", ThemeVariant.Dark, out var accent),
            Is.True);

        Assert.That(((Color)accent!).ToString().ToUpperInvariant(), Is.EqualTo("#FF4FAE7A"),
            "the selected row and the focus ring come from this, not from AccentBrush");
    }

    /// <summary>
    /// The palette records the official .NET purple and restricts it to the corner chip of the
    /// detailed icon: "icone uniquement, ne pas utiliser dans l'UI". It is written down, since it
    /// is part of the identity, but given no brush, so nothing can paint with it. Both halves of
    /// that are checked, because a colour with one permitted place drifts out of it otherwise.
    /// </summary>
    [AvaloniaTest]
    public void TheDotNetPurpleIsRecordedButNeverPaintedWith()
    {
        Assert.That(
            Application.Current!.TryGetResource("DotNetBadgeColor", ThemeVariant.Dark, out _),
            Is.True,
            "the palette records it, so it stays recorded");

        Assert.That(
            Application.Current!.TryGetResource("DotNetBadgeBrush", ThemeVariant.Dark, out _),
            Is.False,
            "and gives it no brush, which is what keeps it off the interface");

        var source = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "src"));

        if (!Directory.Exists(source)) Assert.Ignore($"sources not beside the tests: {source}");

        var users = Directory
            .EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".axaml") || file.EndsWith(".cs"))
            .Where(file => Path.GetFileName(file) != "Colors.axaml")
            .Where(file => File.ReadAllText(file).Contains("DotNetBadge", StringComparison.Ordinal)
                           || File.ReadAllText(file).Contains("512BD4", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.That(users, Is.Empty, "only the palette may mention it; the icon carries it, not the interface");
    }

    [AvaloniaTest]
    public void TheWindowWearsTheApplicationIcon()
    {
        var window = new NoFrills.MemoryProfiler.App.MainWindow();
        window.Show();

        Assert.That(window.Icon, Is.Not.Null, "the icon shows in the task bar and when switching windows");
    }

    [AvaloniaTest]
    public void TheWindowIsPaintedWithThePaletteRatherThanTheThemeDefault()
    {
        var window = new NoFrills.MemoryProfiler.App.MainWindow();
        window.Show();

        Assert.That(window.Background, Is.InstanceOf<ISolidColorBrush>());
        Assert.That(
            ((ISolidColorBrush)window.Background!).Color.ToString().ToUpperInvariant(),
            Is.EqualTo("#FF201B18"));
    }

    [AvaloniaTest]
    public async Task OnlyTheHoldersWorthLookingAtAreColoured()
    {
        var path = NoFrills.MemoryProfiler.Tests.ReferenceDumps.Find(
            NoFrills.MemoryProfiler.Tests.ReferenceDumps.PoolRetention);

        if (path == null) Assert.Ignore("reference dump not available");

        var model = new MainViewModel { ProcessSource = () => Array.Empty<NoFrills.MemoryProfiler.Core.LiveProcess>() };
        await model.LoadDumpAsync(path!, asBaseline: false);
        await model.AnalyzePoolBlocksCommand.ExecuteAsync(null);

        var pooled = model.RootPaths.First(row => row.CategoryDisplay == "free in pool");
        var suspect = model.RootPaths.First(row => row.CategoryDisplay == "investigate");

        Assert.That(pooled.IsSuspect, Is.False, "the pool holding its own free blocks is not a finding");
        Assert.That(suspect.IsSuspect, Is.True);
    }
}
