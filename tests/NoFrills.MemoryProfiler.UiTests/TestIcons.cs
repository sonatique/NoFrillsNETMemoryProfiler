using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.NUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.UiTests;

/// <summary>
/// The icon set has two variants: a plain badge for the sizes a title bar and a task bar ask for,
/// and the detailed drawing above that. Both mistakes it guards against have happened: an icon
/// holding a single small image, which the shell then blows up everywhere, and the detailed
/// drawing used small, where it turns into a brown smudge.
/// </summary>
public sealed class TestIcons
{
    private static Stream Asset(string name) =>
        AssetLoader.Open(new Uri($"avares://NoFrills.MemoryProfiler.App/Assets/{name}"));

    [AvaloniaTest]
    public void TheExecutableIconHoldsEverySizeTheShellAsksFor()
    {
        using var stream = Asset("app.ico");
        using var memory = new MemoryStream();

        stream.CopyTo(memory);

        var bytes = memory.ToArray();
        var count = BitConverter.ToUInt16(bytes, 4);
        var sizes = new List<int>();

        for (var i = 0; i < count; ++i)
        {
            var entry = 6 + i * 16;
            sizes.Add(bytes[entry] == 0 ? 256 : bytes[entry]);
        }

        TestContext.Out.WriteLine($"app.ico holds {count} images: {string.Join(", ", sizes)}");

        Assert.That(sizes, Does.Contain(16).And.Contain(32).And.Contain(48).And.Contain(256),
            "an icon with one image in it is scaled by the shell to every other size, badly");
    }

    [AvaloniaTest]
    public void TheSmallIconIsTheBadgeAndTheLargeOneIsNot()
    {
        // The badge is a filled accent square, so the accent covers most of it. The detailed
        // drawing is mostly its own browns and its background, and is nothing like as green.
        var small = AccentShare("icon-32.png");
        var large = AccentShare("icon-256.png");

        TestContext.Out.WriteLine($"accent covers {small:P0} of icon-32 and {large:P0} of icon-256");

        Assert.That(small, Is.GreaterThan(0.5),
            "the small icon has to be the badge, which is what stays readable in a title bar");
        Assert.That(large, Is.LessThan(small / 2),
            "and the large one the detailed drawing, which is not a flat accent square");
    }

    [AvaloniaTest]
    public void TheWindowShowsAnIcon()
    {
        var window = new NoFrills.MemoryProfiler.App.MainWindow();
        window.Show();

        Assert.That(window.Icon, Is.Not.Null);
    }

    /// <summary>How much of the picture is close to the accent green of the palette.</summary>
    private static double AccentShare(string name)
    {
        using var stream = Asset(name);
        var bitmap = new Bitmap(stream);

        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        var pixels = new byte[width * height * 4];

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

        try
        {
            bitmap.CopyPixels(
                new PixelRect(0, 0, width, height),
                handle.AddrOfPinnedObject(),
                pixels.Length,
                width * 4);
        }
        finally
        {
            handle.Free();
        }

        var accent = 0;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            // The buffer is blue, green, red, alpha.
            var blue = pixels[i];
            var green = pixels[i + 1];
            var red = pixels[i + 2];

            if (Close(red, 0x4F) && Close(green, 0xAE) && Close(blue, 0x7A)) ++accent;
        }

        return (double)accent / (width * height);
    }

    private static bool Close(byte value, int expected) => Math.Abs(value - expected) <= 24;
}
