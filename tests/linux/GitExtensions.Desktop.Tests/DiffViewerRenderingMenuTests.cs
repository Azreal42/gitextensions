using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class DiffViewerRenderingMenuTests
{
    [AvaloniaTest]
    public void RealMenuButtonsAndScopedXUseTheOriginalIndependentRenderingChoices()
    {
        using Fixture fixture = new(); int changes = 0; fixture.Controller.OptionsChanged += (_, _) => changes++;
        Assert.That(fixture.Menu.ItemsSource!.OfType<MenuItem>().Select(item => item.Name), Is.EqualTo(new[] { "RevisionDiffNonPrintingMenu", "RevisionDiffSyntaxHighlightingMenu" }));
        Assert.That(fixture.Controller.Options, Is.EqualTo(new DiffViewerRenderingOptions()));
        Click(fixture.Open("NonPrintingMenu")); Assert.That(fixture.Controller.Options.ShowNonPrintingChars, Is.True);
        Assert.That(fixture.NonPrinting.IsChecked == true && fixture.Item("NonPrintingMenu").IsChecked, Is.True);
        Click(fixture.NonPrinting); Assert.That(fixture.Controller.Options.ShowNonPrintingChars, Is.False);
        Click(fixture.Syntax); Assert.That(fixture.Controller.Options.ShowSyntaxHighlightingInDiff, Is.False);
        Assert.That(fixture.Syntax.IsChecked == true || fixture.Item("SyntaxHighlightingMenu").IsChecked, Is.False);
        Assert.That(fixture.Viewer.Focus(), Is.True); Press(fixture.Window, PhysicalKey.X, RawInputModifiers.None);
        Assert.That(fixture.Controller.Options.ShowSyntaxHighlightingInDiff, Is.True); Assert.That(changes, Is.EqualTo(4));
        Press(fixture.Window, PhysicalKey.X, RawInputModifiers.Control); Press(fixture.Window, PhysicalKey.X, RawInputModifiers.Shift);
        Press(fixture.Window, PhysicalKey.H, RawInputModifiers.None); Assert.That(changes, Is.EqualTo(4), "There is no invented nonprinting shortcut or modified-X gesture.");
        Assert.That(fixture.Outside.Focus(), Is.True); Press(fixture.Window, PhysicalKey.X, RawInputModifiers.None); Assert.That(changes, Is.EqualTo(4), "X is scoped to the reader, not the whole window.");
        Assert.That(fixture.Controller.HandleKeyDown(new KeyEventArgs { Key = Key.X, Handled = true }), Is.False);
        Assert.That(changes, Is.EqualTo(4));
    }

    [AvaloniaTest]
    public void PlainTextImageAndBusyCapabilitiesKeepTheHiddenSyntaxSettingAndDisableMutations()
    {
        using Fixture fixture = new(); int changes = 0; fixture.Controller.OptionsChanged += (_, _) => changes++;
        Assert.That(fixture.Controller.ToggleSyntaxHighlighting(), Is.True);
        fixture.Controller.SetContext(true, false); Pump(fixture.Window);
        Assert.That(fixture.Item("NonPrintingMenu").IsVisible && fixture.NonPrinting.IsVisible, Is.True);
        Assert.That(fixture.Item("SyntaxHighlightingMenu").IsVisible || fixture.Syntax.IsVisible, Is.False);
        fixture.Viewer.Focus(); Press(fixture.Window, PhysicalKey.X, RawInputModifiers.None);
        Assert.That(fixture.Controller.ToggleSyntaxHighlighting(), Is.False); Assert.That(fixture.Controller.Options.ShowSyntaxHighlightingInDiff, Is.False);
        Assert.That(fixture.Controller.ToggleNonPrinting(), Is.True); Assert.That(changes, Is.EqualTo(2));
        fixture.Controller.SetContext(false, false); Assert.That(fixture.Controller.ToggleNonPrinting(), Is.False);
        Assert.That(fixture.Item("NonPrintingMenu").IsEnabled || fixture.NonPrinting.IsEnabled, Is.False);
        fixture.Controller.SetContext(true, true, isBusy: true); fixture.Controller.SetHostBusy(true); fixture.Controller.SetHostBusy(false);
        Assert.That(fixture.Item("SyntaxHighlightingMenu").IsEnabled || fixture.Syntax.IsEnabled || fixture.NonPrinting.IsEnabled, Is.False, "Ending host busy must not end document loading.");
        fixture.Viewer.Focus(); Press(fixture.Window, PhysicalKey.X, RawInputModifiers.None); Assert.That(changes, Is.EqualTo(2));
        fixture.Controller.SetContext(true, true); fixture.Controller.SetHostBusy(true); fixture.Controller.SetContext(true, true);
        Assert.That(fixture.Controller.SetOptions(new(false, true)), Is.False);
        fixture.Controller.SetHostBusy(false); Assert.That(fixture.Syntax.IsEnabled && fixture.NonPrinting.IsEnabled, Is.True);
        Assert.That(fixture.Controller.Options, Is.EqualTo(new DiffViewerRenderingOptions(true, false)), "Capabilities never rewrite stored choices; full-text automatic grammar belongs to the host.");
        fixture.Controller.Dispose(); Assert.That(fixture.Controller.HandleKeyDown(new KeyEventArgs { Key = Key.X }), Is.False);
        Assert.That(((Image)fixture.NonPrinting.Content!).Source, Is.Null); Assert.That(((Image)fixture.Syntax.Content!).Source, Is.Null); Pump(fixture.Window);
    }

    [AvaloniaTest]
    public void OriginalFrenchRenderingGlyphsPreserveAlphaAndRenderBothThemesThroughRealSelectedContextMenus()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture; CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            using Fixture fixture = new();
            Assert.That(((AccessText)fixture.Item("NonPrintingMenu").Header!).Text, Is.EqualTo("Affic_her les caractères non-imprimables"));
            Assert.That(((AccessText)fixture.Item("SyntaxHighlightingMenu").Header!).Text, Is.EqualTo("Afficher la coloration synta_xique"));
            Assert.That(ToolTip.GetTip(fixture.NonPrinting), Is.EqualTo("Afficher les caractères non-imprimables"));
            Assert.That(ToolTip.GetTip(fixture.Syntax), Is.EqualTo("Afficher la coloration syntaxique"));
            Dictionary<string, byte[]> originals = new(StringComparer.Ordinal); Dictionary<string, IImage?> light = [], dark = [];
            foreach ((string suffix, string asset, int visible) in Glyphs)
            {
                using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + asset)); using Bitmap original = new(stream);
                byte[] pixels = ReadBitmap(original); originals[suffix] = pixels;
                Assert.That(pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha != 0), Is.EqualTo(visible));
                Assert.That(pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha is > 0 and < 255), Is.True, "The real assets have fractional alpha, not opaque-black-only glyphs.");
            }
            Click(fixture.Open("NonPrintingMenu")); Assert.That(fixture.Controller.Options.ShowNonPrintingChars, Is.True);
            int visit = 0;
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Light, ThemeVariant.Dark })
            {
                fixture.Menu.Close(); fixture.Window.RequestedThemeVariant = theme; Pump(fixture.Window);
                MenuItem first = fixture.Open("NonPrintingMenu"); TopLevel root = TopLevel.GetTopLevel(first)!;
                root.MouseMove(new Point(root.ClientSize.Width - 1, root.ClientSize.Height - 1)); Pump(root);
                Assert.That(Glyphs.Any(glyph => fixture.Item(glyph.Suffix).IsSelected), Is.False, "The unselected proof must come from a real pointer outside both menu rows.");
                foreach ((string suffix, _, _) in Glyphs)
                {
                    MenuItem item = fixture.Item(suffix); Image image = (Image)item.Icon!; Bitmap bitmap = (Bitmap)image.Source!;
                    Assert.That(image.ActualThemeVariant, Is.EqualTo(theme)); Assert.That(item.Bounds.Height, Is.EqualTo(22));
                    byte[] actual = ReadBitmap(bitmap), expected = originals[suffix];
                    for (int offset = 0; offset < expected.Length; offset += 4)
                    {
                        Assert.That(actual[offset + 3], Is.EqualTo(expected[offset + 3]), "AdaptLightness preserves every original alpha byte.");
                        if (theme == ThemeVariant.Light) { Assert.That(actual.AsSpan(offset, 4).ToArray(), Is.EqualTo(expected.AsSpan(offset, 4).ToArray()), "Default light uses the untouched original asset."); }
                        else if (expected[offset + 3] != 0) { Assert.That(actual.AsSpan(offset, 3).ToArray(), Is.EqualTo(new byte[] { 240, 240, 240 }), "Source gray0/1 follows the exact original Dark lightness correction."); }
                    }
                    Dictionary<string, IImage?> cache = theme == ThemeVariant.Light ? light : dark;
                    if (cache.TryGetValue(suffix, out IImage? old)) { Assert.That(image.Source, Is.SameAs(old)); } else { cache[suffix] = image.Source; }
                }
                foreach (ToggleButton button in new[] { fixture.NonPrinting, fixture.Syntax })
                { Assert.That(button.Bounds.Size, Is.EqualTo(new Size(24, 24))); Assert.That(((Image)button.Content!).Bounds.Size, Is.EqualTo(new Size(16, 16))); }
                Capture(root, "diff-rendering-menu-unselected-" + theme + "-" + visit + ".png", frame =>
                { foreach ((string suffix, _, int visible) in Glyphs) { AssertGlyphPixels(frame, root, fixture.Item(suffix), visible); } });
                foreach ((string suffix, _, int visible) in Glyphs)
                {
                    MenuItem item = fixture.Item(suffix); IImage? bitmap = ((Image)item.Icon!).Source;
                    Hover(item); Assert.That(item.IsSelected && item.IsChecked, Is.True); Assert.That(((Image)item.Icon!).Source, Is.SameAs(bitmap), "Selection never recolors the original adapted bitmap.");
                    Capture(root, "diff-rendering-menu-selected-" + suffix + "-" + theme + "-" + visit + ".png", frame => AssertGlyphPixels(frame, root, item, visible));
                }
                visit++;
            }
            Image[] held = Glyphs.Select(glyph => (Image)fixture.Item(glyph.Suffix).Icon!).ToArray();
            fixture.Menu.Close(); fixture.Controller.Dispose(); Assert.That(held.All(image => image.Source is null), Is.True);
            fixture.Window.RequestedThemeVariant = ThemeVariant.Light; Pump(fixture.Window);
            Assert.That(held.All(image => image.Source is null), Is.True, "Disposed theme handlers cannot resurrect released bitmap caches.");
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [AvaloniaTest]
    public void OriginalAchromaticLightnessCorrectionPreservesAllAlphaAndBothNearBlackAndOtherGrayLevels()
    {
        using WriteableBitmap original = new(new PixelSize(16, 16), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (ILockedFramebuffer buffer = original.Lock())
        {
            byte[] pixels = new byte[buffer.RowBytes * buffer.Size.Height];
            for (int index = 0; index < 256; index++)
            {
                int offset = index / 16 * buffer.RowBytes + index % 16 * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = (byte)index; pixels[offset + 3] = (byte)index;
            }
            Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        }
        byte[] before = ReadBitmap(original);
        using Bitmap adapted = DiffViewerRenderingBitmap.CreateDark(original, Color.Parse("#f0f0f0"), Color.Parse("#323232")); byte[] after = ReadBitmap(adapted);
        Assert.That(ReadBitmap(original), Is.EqualTo(before));
        for (int index = 0; index < 256; index++)
        {
            int offset = index * 4; Assert.That(after[offset + 3], Is.EqualTo(before[offset + 3]));
            int gray = (int)Math.Floor((240d / 255 + before[offset] / 255d * (50d / 255 - 240d / 255)) * 256);
            if (before[offset + 3] != 0)
            { Assert.That(after.AsSpan(offset, 3).ToArray(), Is.EqualTo(new[] { (byte)gray, (byte)gray, (byte)gray }), "Match the achromatic branch of original TransformL and HslColor.ToColor, not a black-only substitution."); }
        }
    }

    private static readonly (string Suffix, string Asset, int Visible)[] Glyphs = [("NonPrintingMenu", "ShowWhitespace.png", 53), ("SyntaxHighlightingMenu", "SyntaxHighlighting.png", 51)];
    private static byte[] ReadBitmap(Bitmap bitmap)
    {
        using WriteableBitmap copy = new(bitmap.PixelSize, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using ILockedFramebuffer buffer = copy.Lock(); bitmap.CopyPixels(buffer, AlphaFormat.Unpremul);
        byte[] padded = new byte[buffer.RowBytes * buffer.Size.Height], packed = new byte[buffer.Size.Width * buffer.Size.Height * 4]; Marshal.Copy(buffer.Address, padded, 0, padded.Length);
        for (int y = 0; y < buffer.Size.Height; y++) { Array.Copy(padded, y * buffer.RowBytes, packed, y * buffer.Size.Width * 4, buffer.Size.Width * 4); } return packed;
    }
    private static void AssertGlyphPixels(WriteableBitmap frame, TopLevel root, MenuItem item, int visible)
    {
        Image icon = (Image)item.Icon!; Assert.That(icon.Bounds.Size, Is.EqualTo(new Size(16, 16)));
        Point origin = icon.TranslatePoint(default, root)!.Value; Assert.That(origin.X, Is.EqualTo(Math.Round(origin.X))); Assert.That(origin.Y, Is.EqualTo(Math.Round(origin.Y)));
        Border row = item.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_LayoutRoot"); Color background = ((ISolidColorBrush)row.Background!).Color;
        byte[] glyph = ReadBitmap((Bitmap)icon.Source!); using ILockedFramebuffer buffer = frame.Lock(); byte[] rendered = new byte[buffer.RowBytes * buffer.Size.Height]; Marshal.Copy(buffer.Address, rendered, 0, rendered.Length);
        bool bgra = buffer.Format == PixelFormat.Bgra8888; int ink = 0, transparent = 0;
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int source = (y * 16 + x) * 4, offset = ((int)origin.Y + y) * buffer.RowBytes + ((int)origin.X + x) * 4;
                double alpha = glyph[source + 3] / 255d;
                byte[] pixel = [rendered[offset + (bgra ? 2 : 0)], rendered[offset + 1], rendered[offset + (bgra ? 0 : 2)]];
                double[] expected = [background.R * (1 - alpha) + glyph[source + 2] * alpha, background.G * (1 - alpha) + glyph[source + 1] * alpha, background.B * (1 - alpha) + glyph[source] * alpha];
                for (int channel = 0; channel < 3; channel++) { Assert.That(Math.Abs(pixel[channel] - expected[channel]), Is.LessThanOrEqualTo(2), "Every actual icon pixel must composite its original alpha over the real selected/unselected background."); }
                if (alpha > 0) { ink++; } else { transparent++; }
            }
        }
        Assert.That(ink, Is.EqualTo(visible)); Assert.That(transparent, Is.EqualTo(256 - visible));
    }
    private static void Pump(TopLevel root) { root.UpdateLayout(); Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); }
    private static void Press(TopLevel root, PhysicalKey key, RawInputModifiers modifiers) { root.KeyPressQwerty(key, modifiers); root.KeyReleaseQwerty(key, modifiers); Pump(root); }
    private static void Hover(Control control)
    { TopLevel root = TopLevel.GetTopLevel(control)!; root.MouseMove(control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value); Pump(root); }
    private static void Click(Control control)
    {
        TopLevel root = TopLevel.GetTopLevel(control)!; Pump(root); Assert.That(control.IsEffectivelyEnabled && control.IsEffectivelyVisible, Is.True);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point); root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump(root);
    }
    private static void Capture(TopLevel root, string name, Action<WriteableBitmap> verify)
    {
        Pump(root); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using WriteableBitmap frame = root.CaptureRenderedFrame() ?? throw new AssertionException("No original rendering menu frame."); verify(frame);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, name); frame.Save(path); TestContext.AddTestAttachment(path);
    }
    private sealed class Fixture : IDisposable
    {
        internal DiffViewerPreferencesTests.TemporaryDirectory Directory { get; } = new();
        internal DiffViewerRenderingMenu Controller { get; }
        internal ContextMenu Menu { get; }
        internal ToggleButton NonPrinting { get; }
        internal ToggleButton Syntax { get; }
        internal TextBox Viewer { get; } = new() { Text = "Spaces  and\ttabs\r\nRead-only reader", IsReadOnly = true, Height = 80 };
        internal TextBox Outside { get; } = new() { Text = "Outside reader", IsReadOnly = true, Height = 40 };
        internal Window Window { get; }
        internal Fixture()
        {
            Controller = new(new DiffViewerPreferencesStore(Directory.Path));
            Menu = new() { ItemsSource = Controller.CreateContextMenuItems("RevisionDiff") }; Viewer.ContextMenu = Menu;
            NonPrinting = (ToggleButton)Controller.CreateNonPrintingButton(); Syntax = (ToggleButton)Controller.CreateSyntaxHighlightingButton();
            Viewer.AddHandler(InputElement.KeyDownEvent, (_, args) => Controller.HandleKeyDown(args), Avalonia.Interactivity.RoutingStrategies.Tunnel);
            Window = new() { Width = 980, Height = 420, Content = new StackPanel { Margin = new Thickness(10), Spacing = 3, Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { NonPrinting, Syntax } }, Viewer, Outside } } };
            Window.Show(); Pump(Window);
        }
        internal MenuItem Item(string suffix) => Menu.ItemsSource!.OfType<MenuItem>().Single(item => item.Name == "RevisionDiff" + suffix);
        internal MenuItem Open(string suffix)
        {
            Menu.Close(); Point point = Viewer.TranslatePoint(new Point(8, 8), Window)!.Value;
            Window.MouseMove(point); Window.MouseDown(point, MouseButton.Right); Window.MouseUp(point, MouseButton.Right); Pump(Window);
            Assert.That(Menu.IsOpen, Is.True); MenuItem item = Item(suffix); Assert.That(item.IsEffectivelyVisible && item.Bounds.Height > 0, Is.True); return item;
        }
        public void Dispose() { Menu.Close(); Controller.Dispose(); Window.Close(); Dispatcher.UIThread.RunJobs(); Directory.Dispose(); }
    }
}
