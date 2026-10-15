using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
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
using GitCommands.Settings;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class DiffViewerComparisonMenuTests
{
    [AvaloniaTest]
    public void RealPointerWhitespaceChoicesUseOriginalCumulativeChecksAndExactModeToggles()
    {
        using Fixture fixture = new();
        Assert.That(fixture.Menu.ItemsSource!.OfType<MenuItem>().Any(item => item.Name?.EndsWith("Whitespace_None", StringComparison.Ordinal) == true), Is.False);
        Assert.That(fixture.Menu.ItemsSource!.OfType<MenuItem>().Any(item => item.Name?.Contains("Remember", StringComparison.Ordinal) == true), Is.False, "Remember preferences belong to DiffViewerSettingsPage, not the FileViewer context menu.");
        foreach ((string clicked, IgnoreWhitespaceKind expected, bool[] checks) in new[]
        {
            ("Whitespace_AllSpace", IgnoreWhitespaceKind.AllSpace, new[] { true, true, true }),
            ("Whitespace_Change", IgnoreWhitespaceKind.Change, new[] { true, true, false }),
            ("Whitespace_Eol", IgnoreWhitespaceKind.Eol, new[] { true, false, false }),
            ("Whitespace_Eol", IgnoreWhitespaceKind.None, new[] { false, false, false }),
            ("Whitespace_Change", IgnoreWhitespaceKind.Change, new[] { true, true, false }),
            ("Whitespace_Change", IgnoreWhitespaceKind.None, new[] { false, false, false })
        })
        {
            Click(fixture.Open(clicked)); Assert.That(fixture.Controller.Options.IgnoreWhitespace, Is.EqualTo(expected));
            Assert.That(new[] { fixture.Item("Whitespace_Eol").IsChecked, fixture.Item("Whitespace_Change").IsChecked, fixture.Item("Whitespace_AllSpace").IsChecked }, Is.EqualTo(checks));
            Assert.That(fixture.Item(clicked).ToggleType, Is.EqualTo(MenuItemToggleType.CheckBox));
        }
    }

    [AvaloniaTest]
    public void RealButtonsAndKeyboardLeaveContextUntouchedWhileWholeFileIsEnabled()
    {
        using Fixture fixture = new();
        int changed = 0; fixture.Controller.OptionsChanged += (_, _) => changed++;
        fixture.Viewer.Focus(); Press(fixture.Window, PhysicalKey.Equal, RawInputModifiers.Control);
        Assert.That(fixture.Controller.Options.ContextLines, Is.EqualTo(4));
        Click(fixture.Whole); Assert.That(fixture.Controller.Options.ShowEntireFile, Is.True);
        Assert.That(fixture.Whole, Is.InstanceOf<ToggleButton>()); Assert.That(((ToggleButton)fixture.Whole).IsChecked, Is.True);
        Assert.That(fixture.Increase.IsEnabled || fixture.Decrease.IsEnabled || fixture.Item("IncreaseContextMenu").IsEnabled || fixture.Item("DecreaseContextMenu").IsEnabled, Is.False);
        int before = changed;
        fixture.Viewer.Focus(); Press(fixture.Window, PhysicalKey.Equal, RawInputModifiers.Control); Press(fixture.Window, PhysicalKey.Minus, RawInputModifiers.Control);
        Assert.That(fixture.Controller.Options.ContextLines, Is.EqualTo(4)); Assert.That(changed, Is.EqualTo(before));
        Assert.That(fixture.Controller.SetOptions(fixture.Controller.Options with { ContextLines = 9 }), Is.False);
        Assert.That(fixture.Controller.Options.ShowEntireFile, Is.True);
        Press(fixture.Window, PhysicalKey.E, RawInputModifiers.Control); Assert.That(fixture.Controller.Options.ShowEntireFile, Is.False);
        Click(fixture.Decrease); Assert.That(fixture.Controller.Options.ContextLines, Is.EqualTo(3));
        Assert.That(fixture.Controller.SetOptions(fixture.Controller.Options with { ContextLines = 0 }), Is.True);
        fixture.Viewer.Focus(); Press(fixture.Window, PhysicalKey.Minus, RawInputModifiers.Control); Assert.That(fixture.Controller.Options.ContextLines, Is.Zero);
        Assert.That(fixture.Decrease.IsEnabled, Is.True, "The original decrease command remains enabled at zero.");
        fixture.Outside.Focus(); Press(fixture.Window, PhysicalKey.Equal, RawInputModifiers.Control); Assert.That(fixture.Controller.Options.ContextLines, Is.Zero);
    }

    [AvaloniaTest]
    public void CapabilitiesBusyAndDisposeSuppressActionsWithoutReloadingOrChangingHiddenOptions()
    {
        using Fixture fixture = new(); int events = 0; fixture.Controller.OptionsChanged += (_, _) => events++;
        fixture.Controller.SetContext(true, false, true);
        Assert.That(fixture.Item("Whitespace_Eol").IsVisible, Is.True); Assert.That(fixture.Item("Whitespace_Change").IsVisible || fixture.Item("Whitespace_AllSpace").IsVisible, Is.False);
        Assert.That(fixture.Controller.SelectWhitespace(IgnoreWhitespaceKind.AllSpace), Is.False);
        Assert.That(fixture.Controller.SelectWhitespace(IgnoreWhitespaceKind.Eol), Is.True);
        int before = events; fixture.Controller.SetContext(false, false, false); fixture.Controller.Refresh();
        Assert.That(events, Is.EqualTo(before)); Assert.That(fixture.Controller.Options.IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.Eol));
        fixture.Viewer.Focus(); Press(fixture.Window, PhysicalKey.E, RawInputModifiers.Control); Assert.That(fixture.Controller.Options.ShowEntireFile, Is.False);
        fixture.Controller.SetContext(true, true, true, isBusy: true);
        fixture.Controller.SetHostBusy(true); fixture.Controller.SetHostBusy(false);
        Assert.That(fixture.Increase.IsEnabled || fixture.Whole.IsEnabled || fixture.Item("Whitespace_Eol").IsEnabled, Is.False);
        Press(fixture.Window, PhysicalKey.W, RawInputModifiers.Control | RawInputModifiers.Shift); Assert.That(events, Is.EqualTo(before));
        Assert.That(fixture.Controller.SetOptions(new(IgnoreWhitespaceKind.AllSpace)), Is.False);
        fixture.Controller.SetContext(true, false, true); fixture.Controller.SetHostBusy(true);
        fixture.Controller.SetContext(true, false, true); Assert.That(fixture.Increase.IsEnabled || fixture.Item("Whitespace_Eol").IsEnabled, Is.False);
        fixture.Controller.SetHostBusy(false); Assert.That(fixture.Increase.IsEnabled && fixture.Item("Whitespace_Eol").IsEnabled, Is.True);
        Assert.That(fixture.Item("Whitespace_Change").IsVisible, Is.False, "Host busy must not widen the Difftastic capabilities.");
        int shared = 0; new DiffViewerPreferencesStore(fixture.Directory.Path).Changed += (_, _) => shared++;
        Assert.That(fixture.Controller.SetOptions(fixture.Controller.Options with { TreatAllFilesAsText = true }), Is.True);
        Assert.That(shared, Is.Zero, "Treat-as-text is local and must not write shared reader defaults.");
        fixture.Controller.Dispose();
        Assert.That(fixture.Increase.IsEnabled, Is.False); Assert.That(fixture.Whole.Content, Is.InstanceOf<Image>());
        Assert.That(((Image)fixture.Whole.Content!).Source, Is.Null, "Disposed readers must release platform bitmaps without leaving dangling image handles in a pending render.");
        Pump(fixture.Window);
    }

    [AvaloniaTest]
    public void RealFrenchMenusAndOriginalPngButtonsRenderInBothThemes()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture; CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            using Fixture fixture = new();
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                fixture.Window.RequestedThemeVariant = theme; Pump(fixture.Window);
                Assert.That(fixture.Controller.SelectWhitespace(IgnoreWhitespaceKind.AllSpace), Is.True);
                // SelectWhitespace toggles an exact current mode; establish it explicitly for the second palette.
                if (fixture.Controller.Options.IgnoreWhitespace != IgnoreWhitespaceKind.AllSpace)
                { Assert.That(fixture.Controller.SelectWhitespace(IgnoreWhitespaceKind.AllSpace), Is.True); }
                MenuItem checkedItem = fixture.Open("Whitespace_AllSpace");
                Assert.That(((AccessText)fixture.Item("EntireFileMenu").Header!).Text, Is.EqualTo("Afficher le fichier en _entier"));
                foreach (MenuItem item in fixture.Menu.ItemsSource!.OfType<MenuItem>().Where(item => item.IsVisible))
                { Assert.That(item.Bounds.Height, Is.EqualTo(22)); Assert.That(((AccessText)item.Header!).Text, Does.Not.Contain("&")); }
                foreach (Button button in new[] { fixture.Increase, fixture.Decrease, fixture.Whole })
                { Assert.That(button.Bounds.Size, Is.EqualTo(new Size(24, 24))); Assert.That(((Image)button.Content!).Bounds.Size, Is.EqualTo(new Size(16, 16))); Assert.That(((Image)button.Content!).Source, Is.Not.Null); }
                TopLevel root = TopLevel.GetTopLevel(checkedItem)!; Pump(root);
                Point point = checkedItem.TranslatePoint(new Point(checkedItem.Bounds.Width / 2, checkedItem.Bounds.Height / 2), root)!.Value;
                root.MouseMove(point); Pump(root);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using WriteableBitmap frame = root.CaptureRenderedFrame() ?? throw new AssertionException("No rendered original comparison menu.");
                AssertCheck(frame, root, checkedItem, theme);
                foreach (MenuItem item in fixture.Menu.ItemsSource!.OfType<MenuItem>().Where(item => item.InputGesture is not null))
                { AssertCompleteGesture(frame, root, fixture.Menu, item); }
                Attach(frame, "diff-comparison-menu-fr-" + theme + ".png");
                fixture.Menu.Close(); Capture(fixture.Window, "diff-comparison-toolbar-" + theme + ".png");
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [AvaloniaTest]
    public async Task OriginalOptionsDriveRealGitWhitespaceAndContextForALiteralFileWithoutStagingAnything()
    {
        using RepositoryFixture repository = await RepositoryFixture.CreateAsync(); using Fixture fixture = new();
        GitComparison comparison = await repository.Repository.GetComparisonAsync(new(), new(Kind: GitCompareRevisionKind.WorkingTree));
        GitRevisionFile file = comparison.Files.Single(); Assert.That(file.Path, Is.EqualTo(RepositoryFixture.FileName));
        string normal = await Diff(); Assert.That(normal, Does.Contain("+alpha    beta")); Assert.That(normal, Does.Contain("+omega   ")); Assert.That(normal, Does.Contain("+xy"));
        Click(fixture.Open("Whitespace_Eol")); string eol = await Diff(); Assert.That(eol, Does.Not.Contain("+omega   ")); Assert.That(eol, Does.Contain("+alpha    beta"));
        Click(fixture.Open("Whitespace_Change")); string amount = await Diff(); Assert.That(amount, Does.Not.Contain("+alpha    beta")); Assert.That(amount, Does.Contain("+xy"));
        fixture.Viewer.Focus(); Press(fixture.Window, PhysicalKey.W, RawInputModifiers.Control | RawInputModifiers.Shift);
        string all = await Diff(); Assert.That(all, Does.Not.Contain("+xy")); Assert.That(all, Does.Contain("+after"));
        Assert.That(fixture.Controller.SetOptions(fixture.Controller.Options with { ContextLines = 0 }), Is.True);
        string narrow = await Diff(); Assert.That(narrow, Does.Not.Contain(" sentinel 100"));
        Click(fixture.Whole); string whole = await Diff(); Assert.That(whole, Does.Contain(" sentinel 100"));
        Assert.That(fixture.Controller.Options.ContextLines, Is.Zero); Assert.That(fixture.Increase.IsEnabled || fixture.Decrease.IsEnabled, Is.False);
        Assert.That((await repository.Git("diff", "--cached", "--name-only")).StandardOutput, Is.Empty);
        Task<string> Diff() => repository.Repository.GetComparisonFileDiffAsync(comparison, file, fixture.Controller.Options);
    }

    [AvaloniaTest]
    public void RealWhitespaceAssetsAndSelectedMenuPixelsFollowEachInheritedThemeWithoutChangingTheOtherIcons()
    {
        using Fixture fixture = new();
        (string Suffix, string Asset, int OpaquePixels)[] glyphs =
        [
            ("Whitespace_Eol", "WhitespaceIgnoreEol.png", 75), ("Whitespace_Change", "WhitespaceIgnore.png", 39),
            ("Whitespace_AllSpace", "WhitespaceIgnoreAll.png", 37)
        ];
        Dictionary<string, byte[]> originals = new(StringComparer.Ordinal);
        Dictionary<string, IImage?> light = new(StringComparer.Ordinal), dark = new(StringComparer.Ordinal);
        foreach ((string suffix, string asset, int opaque) in glyphs)
        {
            using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + asset)); using Bitmap original = new(stream);
            byte[] pixels = ReadBitmap(original); originals.Add(suffix, pixels);
            Assert.That(pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha == 255), Is.EqualTo(opaque));
        }
        Bitmap zoom = (Bitmap)((Image)fixture.Increase.Content!).Source!; byte[] zoomPixels = ReadBitmap(zoom);
        Click(fixture.Open("Whitespace_AllSpace"));
        Assert.That(fixture.Controller.Options.IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.AllSpace));
        int visit = 0;
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Light, ThemeVariant.Dark })
        {
            fixture.Menu.Close(); fixture.Window.RequestedThemeVariant = theme; Pump(fixture.Window);
            MenuItem anchor = fixture.Open("IncreaseContextMenu"); Hover(anchor); TopLevel root = TopLevel.GetTopLevel(anchor)!;
            foreach ((string suffix, _, _) in glyphs)
            {
                MenuItem item = fixture.Item(suffix); Image icon = (Image)item.Icon!; Bitmap source = (Bitmap)icon.Source!;
                Assert.That(icon.ActualThemeVariant, Is.EqualTo(theme), "Each Image must use its inherited effective theme, including a popup surface.");
                Assert.That(item.IsSelected, Is.False); Assert.That(item.IsChecked, Is.True, "Original All-space cumulative checks remain unchanged.");
                byte[] actual = ReadBitmap(source), expected = originals[suffix];
                for (int offset = 0; offset < expected.Length; offset += 4)
                {
                    Assert.That(actual[offset + 3], Is.EqualTo(expected[offset + 3]), "Theme adaptation must preserve every source alpha byte.");
                    if (theme == ThemeVariant.Light) { Assert.That(actual.AsSpan(offset, 4).ToArray(), Is.EqualTo(expected.AsSpan(offset, 4).ToArray())); }
                    else if (expected[offset + 3] != 0)
                    { Assert.That(actual.AsSpan(offset, 3).ToArray(), Is.EqualTo(new byte[] { 240, 240, 240 }), "Black maps exactly to original Dark WindowText."); }
                }
                Dictionary<string, IImage?> cache = theme == ThemeVariant.Light ? light : dark;
                if (cache.TryGetValue(suffix, out IImage? previous)) { Assert.That(icon.Source, Is.SameAs(previous), "Theme round trips reuse local caches rather than transform an already adapted bitmap."); }
                else { cache.Add(suffix, icon.Source); }
            }
            Assert.That(((Image)fixture.Increase.Content!).Source, Is.SameAs(zoom)); Assert.That(ReadBitmap(zoom), Is.EqualTo(zoomPixels), "The colored context icon is outside this monochrome correction.");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using (WriteableBitmap frame = root.CaptureRenderedFrame() ?? throw new AssertionException("No unselected whitespace menu frame."))
            {
                foreach ((string suffix, _, int opaque) in glyphs) { AssertWhitespacePixels(frame, root, fixture.Item(suffix), theme, opaque); }
                Attach(frame, "diff-whitespace-unselected-" + theme + "-" + visit + ".png");
            }
            foreach ((string suffix, _, int opaque) in glyphs)
            {
                MenuItem item = fixture.Item(suffix); Image icon = (Image)item.Icon!; IImage? previous = icon.Source;
                Hover(item); Assert.That(item.IsSelected, Is.True); Assert.That(icon.Source, Is.SameAs(previous), "Original AdaptLightness does not recolor a bitmap for selection.");
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using WriteableBitmap frame = root.CaptureRenderedFrame() ?? throw new AssertionException("No selected whitespace menu frame.");
                AssertWhitespacePixels(frame, root, item, theme, opaque);
                Attach(frame, "diff-whitespace-selected-" + suffix + "-" + theme + "-" + visit + ".png");
            }
            visit++;
        }
        Image[] held = glyphs.Select(glyph => (Image)fixture.Item(glyph.Suffix).Icon!).ToArray();
        fixture.Menu.Close(); fixture.Controller.Dispose();
        Assert.That(held.All(icon => icon.Source is null), Is.True);
        fixture.Window.RequestedThemeVariant = ThemeVariant.Light; Pump(fixture.Window);
        Assert.That(held.All(icon => icon.Source is null), Is.True, "Disposed images must not react to later theme changes or reuse released bitmaps.");
    }

    [AvaloniaTest]
    public void MonochromeAdaptationPreservesAllAlphaValuesAndNeverMutatesItsInput()
    {
        using WriteableBitmap original = new(new PixelSize(16, 16), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (ILockedFramebuffer buffer = original.Lock())
        {
            byte[] pixels = new byte[buffer.RowBytes * buffer.Size.Height];
            for (int index = 0; index < 256; index++) { pixels[(index / 16) * buffer.RowBytes + (index % 16) * 4 + 3] = (byte)index; }
            Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        }
        byte[] before = ReadBitmap(original); using Bitmap adapted = DiffViewerMonochromeBitmap.CreateDark(original, Color.Parse("#f0f0f0"));
        byte[] after = ReadBitmap(adapted);
        Assert.That(ReadBitmap(original), Is.EqualTo(before));
        Assert.That(after.Where((_, index) => index % 4 == 3), Is.EqualTo(Enumerable.Range(0, 256).Select(value => (byte)value)), "Preserve alpha 0..255, rather than accidentally make transparent or antialiased glyph pixels opaque.");
    }

    private static byte[] ReadBitmap(Bitmap bitmap)
    {
        using WriteableBitmap copy = new(bitmap.PixelSize, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using ILockedFramebuffer buffer = copy.Lock(); bitmap.CopyPixels(buffer, AlphaFormat.Unpremul);
        byte[] padded = new byte[buffer.RowBytes * buffer.Size.Height], packed = new byte[buffer.Size.Width * buffer.Size.Height * 4];
        Marshal.Copy(buffer.Address, padded, 0, padded.Length);
        for (int y = 0; y < buffer.Size.Height; y++) { Array.Copy(padded, y * buffer.RowBytes, packed, y * buffer.Size.Width * 4, buffer.Size.Width * 4); }
        return packed;
    }
    private static void Hover(Control control)
    {
        TopLevel root = TopLevel.GetTopLevel(control)!; Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point); Pump(root);
    }
    private static void AssertWhitespacePixels(WriteableBitmap frame, TopLevel root, MenuItem item, ThemeVariant theme, int opaque)
    {
        Image icon = (Image)item.Icon!; Assert.That(icon.Bounds.Size, Is.EqualTo(new Size(16, 16)));
        Point origin = icon.TranslatePoint(default, root)!.Value; Assert.That(origin.X, Is.EqualTo(Math.Round(origin.X))); Assert.That(origin.Y, Is.EqualTo(Math.Round(origin.Y)));
        Border row = item.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_LayoutRoot");
        Color background = ((ISolidColorBrush)row.Background!).Color, foreground = theme == ThemeVariant.Dark ? Color.Parse("#f0f0f0") : Colors.Black;
        using ILockedFramebuffer buffer = frame.Lock(); byte[] pixels = new byte[buffer.RowBytes * buffer.Size.Height]; Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);
        bool bgra = buffer.Format == PixelFormat.Bgra8888; int ink = 0, clear = 0;
        for (int y = (int)origin.Y; y < (int)origin.Y + 16; y++)
        {
            for (int x = (int)origin.X; x < (int)origin.X + 16; x++)
            {
                int offset = y * buffer.RowBytes + x * 4;
                Color pixel = Color.FromArgb(pixels[offset + 3], pixels[offset + (bgra ? 2 : 0)], pixels[offset + 1], pixels[offset + (bgra ? 0 : 2)]);
                if (pixel == foreground) { ink++; } else if (pixel == background) { clear++; }
            }
        }
        Assert.That(ink, Is.EqualTo(opaque), "Every opaque source glyph pixel must reach the real selected/unselected menu in its exact original theme color.");
        Assert.That(clear, Is.EqualTo(256 - opaque), "Transparent glyph pixels must preserve the actual menu background.");
    }

    private static void AssertCheck(WriteableBitmap frame, TopLevel root, MenuItem item, ThemeVariant theme)
    {
        IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(theme == ThemeVariant.Dark);
        Border row = item.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_LayoutRoot");
        ContentControl icon = item.GetVisualDescendants().OfType<ContentControl>().Single(control => control.Name == "PART_ToggleIconPresenter");
        Avalonia.Controls.Shapes.Path check = icon.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
        Color foreground = Color.Parse(colors["HighlightText"]), background = Color.Parse(colors["MenuHighlight"]);
        Assert.That(((ISolidColorBrush)row.Background!).Color, Is.EqualTo(background)); Assert.That(((ISolidColorBrush)check.Fill!).Color, Is.EqualTo(foreground));
        Rect bounds = new(check.TranslatePoint(default, root)!.Value, check.Bounds.Size);
        using ILockedFramebuffer pixels = frame.Lock(); byte[] bytes = new byte[pixels.RowBytes * pixels.Size.Height]; Marshal.Copy(pixels.Address, bytes, 0, bytes.Length);
        bool bgra = pixels.Format == PixelFormat.Bgra8888;
        double dr = foreground.R - background.R, dg = foreground.G - background.G, db = foreground.B - background.B, magnitude = dr * dr + dg * dg + db * db;
        int glyphs = 0;
        for (int y = Math.Max(0, (int)Math.Floor(bounds.Y)); y < Math.Min(pixels.Size.Height, (int)Math.Ceiling(bounds.Bottom)); y++)
        {
            for (int x = Math.Max(0, (int)Math.Floor(bounds.X)); x < Math.Min(pixels.Size.Width, (int)Math.Ceiling(bounds.Right)); x++)
            {
                int offset = y * pixels.RowBytes + x * 4; double r = bytes[offset + (bgra ? 2 : 0)], g = bytes[offset + 1], b = bytes[offset + (bgra ? 0 : 2)];
                double coverage = ((r - background.R) * dr + (g - background.G) * dg + (b - background.B) * db) / magnitude;
                if (coverage >= .25 && coverage <= 1.05 && Math.Abs(r - (background.R + coverage * dr)) <= 3 && Math.Abs(g - (background.G + coverage * dg)) <= 3 && Math.Abs(b - (background.B + coverage * db)) <= 3) { glyphs++; }
            }
        }
        Assert.That(glyphs, Is.GreaterThan(8), "The original checked-state color must reach the actual glyph pixels, including antialiasing.");
    }
    private static void AssertCompleteGesture(WriteableBitmap frame, TopLevel root, ContextMenu menu, MenuItem item)
    {
        TextBlock gesture = item.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == "PART_InputGestureText");
        Assert.That(gesture.Text, Is.Not.Null.And.Not.Empty);
        Assert.That(gesture.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False);
        Assert.That(gesture.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(gesture.Bounds.Width + .5), "The entire original shortcut must fit its actual template column.");
        Rect textBounds = new(gesture.TranslatePoint(default, root)!.Value, gesture.Bounds.Size), menuBounds = new(menu.TranslatePoint(default, root)!.Value, menu.Bounds.Size);
        Assert.That(textBounds.Right, Is.LessThanOrEqualTo(menuBounds.Right - 1));
        Assert.That(new Rect(default, root.ClientSize).Contains(textBounds), Is.True);
        ContentPresenter header = item.GetVisualDescendants().OfType<ContentPresenter>().Single(control => control.Name == "PART_HeaderPresenter");
        Rect headerBounds = new(header.TranslatePoint(default, root)!.Value, header.Bounds.Size);
        Assert.That(headerBounds.Right, Is.LessThanOrEqualTo(textBounds.Left), "The original label and its shortcut must not overlap.");
        Rect lastCharacter = gesture.TextLayout.TextLines.Single().GetTextBounds(gesture.Text!.Length - 1, 1).Single().Rectangle;
        Point origin = gesture.TranslatePoint(default, root)!.Value;
        Rect glyphBounds = new(origin.X + lastCharacter.X, origin.Y + lastCharacter.Y, lastCharacter.Width, lastCharacter.Height);
        Assert.That(menuBounds.Contains(glyphBounds), Is.True, "The last shortcut character must lie inside the actual popup.");
        Border row = item.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_LayoutRoot");
        AssertInk(frame, glyphBounds, ((ISolidColorBrush)gesture.Foreground!).Color, ((ISolidColorBrush)row.Background!).Color);
    }
    private static void AssertInk(WriteableBitmap frame, Rect bounds, Color foreground, Color background)
    {
        using ILockedFramebuffer pixels = frame.Lock(); byte[] bytes = new byte[pixels.RowBytes * pixels.Size.Height]; Marshal.Copy(pixels.Address, bytes, 0, bytes.Length);
        bool bgra = pixels.Format == PixelFormat.Bgra8888;
        double dr = foreground.R - background.R, dg = foreground.G - background.G, db = foreground.B - background.B, magnitude = dr * dr + dg * dg + db * db;
        int ink = 0;
        for (int y = Math.Max(0, (int)Math.Floor(bounds.Y)); y < Math.Min(pixels.Size.Height, (int)Math.Ceiling(bounds.Bottom)); y++)
        {
            for (int x = Math.Max(0, (int)Math.Floor(bounds.X)); x < Math.Min(pixels.Size.Width, (int)Math.Ceiling(bounds.Right)); x++)
            {
                int offset = y * pixels.RowBytes + x * 4; double r = bytes[offset + (bgra ? 2 : 0)], g = bytes[offset + 1], b = bytes[offset + (bgra ? 0 : 2)];
                double alpha = ((r - background.R) * dr + (g - background.G) * dg + (b - background.B) * db) / magnitude;
                if (alpha >= .25 && alpha <= 1.05 && Math.Abs(r - (background.R + alpha * dr)) <= 3 && Math.Abs(g - (background.G + alpha * dg)) <= 3 && Math.Abs(b - (background.B + alpha * db)) <= 3) { ink++; }
            }
        }
        Assert.That(ink, Is.GreaterThan(2), "The last shortcut glyph must have real foreground pixels rather than be clipped at the menu edge.");
    }
    private static void Pump(TopLevel window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Press(TopLevel window, PhysicalKey key, RawInputModifiers modifiers)
    { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); Pump(window); }
    private static void Click(Control control)
    {
        TopLevel root = TopLevel.GetTopLevel(control) ?? throw new AssertionException("No rendered comparison-menu root."); Pump(root);
        Assert.That(control.IsEffectivelyEnabled && control.IsEffectivelyVisible, Is.True);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point); root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump(root);
    }
    private static void Capture(TopLevel root, string file) { Pump(root); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using WriteableBitmap frame = root.CaptureRenderedFrame()!; Attach(frame, file); }
    private static void Attach(WriteableBitmap frame, string file)
    { string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, file); frame.Save(path); TestContext.AddTestAttachment(path); }
    private sealed class Fixture : IDisposable
    {
        internal DiffViewerPreferencesTests.TemporaryDirectory Directory { get; } = new();
        internal DiffViewerComparisonMenu Controller { get; }
        internal ContextMenu Menu { get; }
        internal Button Increase { get; }
        internal Button Decrease { get; }
        internal Button Whole { get; }
        internal TextBox Viewer { get; } = new() { Text = "Read-only viewer", IsReadOnly = true, Height = 70 };
        internal TextBox Outside { get; } = new() { Text = "Outside reader", IsReadOnly = true, Height = 40 };
        internal Window Window { get; }
        internal Fixture()
        {
            Controller = new(new DiffViewerPreferencesStore(Directory.Path));
            Menu = new() { ItemsSource = Controller.CreateContextMenuItems("RevisionDiff") };
            Viewer.ContextMenu = Menu;
            Increase = Controller.CreateIncreaseContextButton("RevisionDiffIncreaseContext"); Decrease = Controller.CreateDecreaseContextButton("RevisionDiffDecreaseContext"); Whole = Controller.CreateWholeFileButton("RevisionDiffEntireFile");
            Viewer.AddHandler(InputElement.KeyDownEvent, (_, args) => Controller.HandleKeyDown(args), Avalonia.Interactivity.RoutingStrategies.Tunnel);
            Window = new() { Width = 980, Height = 420, Content = new StackPanel { Margin = new Thickness(10), Spacing = 3,
                Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { Increase, Decrease, Whole } }, Viewer, Outside } } };
            Window.Show(); Pump(Window);
        }
        internal MenuItem Item(string suffix) => Menu.ItemsSource!.OfType<MenuItem>().Single(item => item.Name == "RevisionDiff" + suffix);
        internal MenuItem Open(string suffix)
        {
            Menu.Close(); Point point = Viewer.TranslatePoint(new Point(8, 8), Window)!.Value;
            Window.MouseMove(point); Window.MouseDown(point, MouseButton.Right); Window.MouseUp(point, MouseButton.Right); Pump(Window);
            Assert.That(Menu.IsOpen, Is.True, "The original viewer context menu must open from a real right click.");
            MenuItem item = Item(suffix); Assert.That(item.IsEffectivelyVisible && item.Bounds.Height > 0, Is.True); return item;
        }
        public void Dispose() { Menu.Close(); Controller.Dispose(); Window.Close(); Dispatcher.UIThread.RunJobs(); Directory.Dispose(); }
    }
    private sealed class RepositoryFixture : IDisposable
    {
        internal const string FileName = "path[ é].txt";
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-comparison-options-" + Guid.NewGuid().ToString("N"));
        internal GitRepository Repository { get; private set; } = null!;
        private readonly GitRunner _runner = new();
        internal static async Task<RepositoryFixture> CreateAsync()
        {
            RepositoryFixture fixture = new();
            try
            {
                System.IO.Directory.CreateDirectory(fixture.Path); await fixture.Git("init", "-b", "main"); await fixture.Git("config", "user.name", "Reader Tester"); await fixture.Git("config", "user.email", "reader@example.org");
                await fixture.Git("config", "commit.gpgsign", "false"); await fixture.Git("config", "core.hooksPath", System.IO.Path.Combine(fixture.Path, "no-hooks"));
                string[] lines = Enumerable.Range(1, 130).Select(row => "sentinel " + row).ToArray(); lines[20] = "alpha beta"; lines[40] = "omega"; lines[60] = "x y"; lines[120] = "before";
                await File.WriteAllTextAsync(System.IO.Path.Combine(fixture.Path, FileName), string.Join('\n', lines) + "\n"); await fixture.Git("add", "--all"); await fixture.Git("commit", "-m", "Initial reader file");
                lines[20] = "alpha    beta"; lines[40] = "omega   "; lines[60] = "xy"; lines[120] = "after";
                await File.WriteAllTextAsync(System.IO.Path.Combine(fixture.Path, FileName), string.Join('\n', lines) + "\n"); fixture.Repository = await GitRepository.OpenAsync(fixture.Path); return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        internal async Task<GitResult> Git(params string[] arguments)
        { GitResult result = await _runner.RunAsync(Path, arguments); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        public void Dispose() { if (System.IO.Directory.Exists(Path)) { System.IO.Directory.Delete(Path, recursive: true); } }
    }
}
