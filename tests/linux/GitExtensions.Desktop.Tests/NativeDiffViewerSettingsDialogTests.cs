using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands.Settings;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class NativeDiffViewerSettingsDialogTests
{
    [AvaloniaTest]
    public async Task OriginalSettingsApplyTogetherAndCancelDoesNotWriteOrEraseRuntimeValues()
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new(); DiffViewerPreferencesStore store = new(directory.Path);
        using DiffViewerComparisonMenu first = new(store), second = new(store);
        using DiffViewerAppearanceMenu appearance = new(store); appearance.SetContext(true, true);
        Window owner = Owner(); owner.Show();
        try
        {
            NativeDiffViewerSettingsDialog cancel = new(store); Task<bool> canceled = cancel.ShowDialog<bool>(owner); Pump(cancel);
            Assert.That(Choice(cancel, "chkRememberIgnoreWhiteSpacePreference").IsChecked, Is.True);
            Assert.That(Choice(cancel, "chkRememberNumberOfContextLines").IsChecked, Is.False);
            Click(Choice(cancel, "chkRememberShowEntireFilePreference"));
            cancel.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); owner.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.That(await canceled, Is.False); Assert.That(store.Load().RememberWholeFile, Is.False); Assert.That(File.Exists(store.FilePath), Is.False);

            NativeDiffViewerSettingsDialog dialog = new(store); Task<bool> accepted = dialog.ShowDialog<bool>(owner); Pump(dialog);
            foreach (string name in new[] { "chkRememberShowEntireFilePreference", "chkRememberDiffAppearancePreference", "chkRememberNumberOfContextLines" }) { Click(Choice(dialog, name)); }
            Click(Choice(dialog, "chkRememberIgnoreWhiteSpacePreference"));
            Click(Choice(dialog, "chkUseGitColoring"));
            Assert.That(Choice(dialog, "chkUseGEThemeGitColoring").IsEnabled, Is.False);
            Assert.That(Choice(dialog, "chkUseGEThemeGitColoring").IsChecked, Is.True, "Original settings disable Reverse without discarding it.");
            Assert.That(store.Load(), Is.EqualTo(new DiffViewerPreferences()), "Settings remain a page draft until OK.");
            // A newer reader changes the session while the settings page is already open.
            Assert.That(first.SelectWhitespace(IgnoreWhitespaceKind.AllSpace), Is.True);
            Assert.That(first.ToggleWholeFile(), Is.True);
            Assert.That(store.Save(store.Load() with { Appearance = DiffViewerAppearance.GitWordDiff }), Is.True);
            int changes = 0; store.Changed += (_, _) => changes++;
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "DiffViewerSettingsAccept"));
            Assert.That(await accepted, Is.True); Assert.That(changes, Is.EqualTo(1), "The settings page publishes one atomic change.");
            DiffViewerPreferences saved = store.Load();
            Assert.That(saved.UseGitColoring, Is.False); Assert.That(saved.ReverseGitColoring, Is.True);
            Assert.That(saved.RememberWhitespace, Is.False); Assert.That(saved.RememberWholeFile && saved.RememberContextLines && saved.RememberAppearance, Is.True);
            Assert.That(saved.IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.AllSpace)); Assert.That(saved.ShowEntireFile, Is.True); Assert.That(saved.Appearance, Is.EqualTo(DiffViewerAppearance.GitWordDiff));
            Assert.That(second.Options.IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.None)); Assert.That(second.Options.ShowEntireFile, Is.False, "Open readers retain their local options.");
            Assert.That(second.RememberWhitespace, Is.False); Assert.That(second.RememberWholeFile, Is.True); Assert.That(appearance.Preferences.UseGitColoring, Is.False);
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(store.FilePath));
            Assert.That(json.RootElement.GetProperty("Appearance").GetInt32(), Is.Zero);
            Assert.That(json.RootElement.GetProperty("IgnoreWhitespace").GetInt32(), Is.Zero);
            Assert.That(json.RootElement.GetProperty("ShowEntireFile").GetBoolean(), Is.False, "Remember flags do not automatically save runtime values as defaults.");
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"), Is.Empty);
        }
        finally { foreach (Window child in owner.OwnedWindows.ToArray()) { child.Close(); } owner.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaTest]
    public async Task OriginalSaveDefaultsButtonUsesLatestSharedRuntimeEvenFromAnOldSettingsPage()
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new(); DiffViewerPreferencesStore store = new(directory.Path);
        using DiffViewerComparisonMenu first = new(store); Window owner = Owner(); owner.Show();
        try
        {
            NativeDiffViewerSettingsDialog dialog = new(store); Task<bool> closed = dialog.ShowDialog<bool>(owner); Pump(dialog);
            using DiffViewerComparisonMenu newer = new(store);
            Assert.That(newer.SelectWhitespace(IgnoreWhitespaceKind.Change), Is.True); Assert.That(newer.ToggleWholeFile(), Is.True);
            Assert.That(store.Save(store.Load() with { Appearance = DiffViewerAppearance.Difftastic }), Is.True);
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "btnSaveCurrentViewSettingsAsDefault"));
            Assert.That(dialog.IsVisible, Is.True); Assert.That(store.Load().RememberAppearance, Is.False);
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(store.FilePath));
            Assert.That(json.RootElement.GetProperty("Appearance").GetInt32(), Is.EqualTo((int)DiffViewerAppearance.Difftastic));
            Assert.That(json.RootElement.GetProperty("IgnoreWhitespace").GetInt32(), Is.EqualTo((int)IgnoreWhitespaceKind.Change));
            Assert.That(json.RootElement.GetProperty("ShowEntireFile").GetBoolean(), Is.True);
            using DiffViewerAppearanceMenu nextAppearance = new(store); using DiffViewerComparisonMenu nextComparison = new(store);
            Assert.That(nextAppearance.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.Difftastic));
            Assert.That(nextComparison.Options.ShowEntireFile, Is.True, "Saved defaults load even with Remember=false.");
            Assert.That(first.Options.ShowEntireFile, Is.False);
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "DiffViewerSettingsCancel"));
            Assert.That(await closed, Is.False, "The immediate original save-default button survives Cancel of unapplied page edits.");
        }
        finally { foreach (Window child in owner.OwnedWindows.ToArray()) { child.Close(); } owner.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaTest]
    public async Task SaveFailureKeepsTheSettingsDraftVisibleAndPublishesNoPartialFlags()
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new(); DiffViewerPreferencesStore store = new(directory.Path);
        Directory.CreateDirectory(store.FilePath); Window owner = Owner(); owner.Show();
        try
        {
            NativeDiffViewerSettingsDialog dialog = new(store); Task<bool> closed = dialog.ShowDialog<bool>(owner); Pump(dialog);
            Click(Choice(dialog, "chkRememberShowEntireFilePreference")); Click(Choice(dialog, "chkUseGitColoring"));
            int changed = 0; store.Changed += (_, _) => changed++;
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "DiffViewerSettingsAccept"));
            Assert.That(closed.IsCompleted, Is.False); Assert.That(dialog.IsVisible, Is.True);
            Assert.That(dialog.GetVisualDescendants().OfType<TextBlock>().Single(label => label.Name == "DiffViewerSettingsError").IsVisible, Is.True);
            Assert.That(Choice(dialog, "chkRememberShowEntireFilePreference").IsChecked, Is.True); Assert.That(changed, Is.Zero);
            Assert.That(store.Load(), Is.EqualTo(new DiffViewerPreferences()));
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"), Is.Empty);
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "DiffViewerSettingsCancel")); Assert.That(await closed, Is.False);
        }
        finally { foreach (Window child in owner.OwnedWindows.ToArray()) { child.Close(); } owner.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaTest]
    public async Task RealFrenchSettingsPagePreservesOriginalGroupsDensityAndOwnerPalettes()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture; CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            using DiffViewerPreferencesTests.TemporaryDirectory directory = new(); DiffViewerPreferencesStore store = new(directory.Path);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Window owner = Owner(); owner.RequestedThemeVariant = theme; owner.Show();
                try
                {
                    NativeDiffViewerSettingsDialog dialog = new(store); Task<bool> closed = dialog.ShowDialog<bool>(owner); Pump(dialog);
                    Assert.That(dialog.ActualThemeVariant, Is.EqualTo(theme)); Assert.That(dialog.Title, Is.EqualTo("Visualisateur de différences"));
                    TextBlock general = dialog.GetVisualDescendants().OfType<TextBlock>().Single(label => label.Name == "gbGeneralLabel"), colors = dialog.GetVisualDescendants().OfType<TextBlock>().Single(label => label.Name == "gbDiffColoringLabel");
                    Assert.That(general.Text, Is.EqualTo("Général")); Assert.That(colors.Text, Is.EqualTo("Coloration des différences"));
                    CheckBox whitespace = Choice(dialog, "chkRememberIgnoreWhiteSpacePreference"), whole = Choice(dialog, "chkRememberShowEntireFilePreference"), appearance = Choice(dialog, "chkRememberDiffAppearancePreference"), context = Choice(dialog, "chkRememberNumberOfContextLines");
                    Assert.That(((AccessText)whitespace.Content!).Text, Is.EqualTo("Mémoriser la préférence 'Ignorer les espaces'"));
                    Assert.That(((AccessText)whole.Content!).Text, Is.EqualTo("Mémoriser la préférence 'Afficher le fichier en entier'"));
                    Assert.That(((AccessText)appearance.Content!).Text, Is.EqualTo("Mémoriser la préférence 'Apparence des différences'"));
                    Assert.That(((AccessText)context.Content!).Text, Is.EqualTo("Mémoriser la préférence 'Nombre de lignes de contexte'"));
                    Assert.That(((AccessText)Choice(dialog, "chkUseGitColoring").Content!).Text, Is.EqualTo("Coloration git"));
                    Assert.That(((AccessText)Choice(dialog, "chkUseGEThemeGitColoring").Content!).Text, Is.EqualTo("Inverser la couleur d'arrière-plan"));
                    Assert.That(whitespace.TranslatePoint(default, dialog)!.Value.Y, Is.LessThan(whole.TranslatePoint(default, dialog)!.Value.Y));
                    Assert.That(whole.TranslatePoint(default, dialog)!.Value.Y, Is.LessThan(appearance.TranslatePoint(default, dialog)!.Value.Y));
                    Assert.That(appearance.TranslatePoint(default, dialog)!.Value.Y, Is.LessThan(context.TranslatePoint(default, dialog)!.Value.Y));
                    Assert.That(context.TranslatePoint(default, dialog)!.Value.Y, Is.LessThan(colors.TranslatePoint(default, dialog)!.Value.Y));
                    foreach (CheckBox check in dialog.GetVisualDescendants().OfType<CheckBox>())
                    {
                        Assert.That(check.Bounds.Height, Is.EqualTo(24)); Assert.That(((AccessText)check.Content!).Bounds.Bottom, Is.LessThanOrEqualTo(check.Bounds.Height));
                        Assert.That(((AccessText)check.Content!).Text, Does.Not.Contain("&"));
                    }
                    IReadOnlyDictionary<string, string> palette = ClassicStyles.OriginalColors(theme == ThemeVariant.Dark);
                    Color foreground = Color.Parse(palette["ControlText"]), background = Color.Parse(palette["Window"]);
                    Assert.That(((ISolidColorBrush)dialog.Background!).Color, Is.EqualTo(background)); Assert.That(((ISolidColorBrush)whitespace.Foreground!).Color, Is.EqualTo(foreground));
                    Assert.That(Contrast(foreground, background), Is.GreaterThanOrEqualTo(4.5));
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using WriteableBitmap frame = dialog.CaptureRenderedFrame() ?? throw new AssertionException("No native settings frame.");
                    AssertTextPixels(frame, dialog, (AccessText)whitespace.Content!, foreground, background);
                    string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "diff-viewer-settings-fr-" + theme + ".png"); frame.Save(path); TestContext.AddTestAttachment(path);
                    Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "DiffViewerSettingsCancel")); Assert.That(await closed, Is.False);
                }
                finally { foreach (Window child in owner.OwnedWindows.ToArray()) { child.Close(); } owner.Close(); Dispatcher.UIThread.RunJobs(); }
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private static Window Owner() => new() { Width = 750, Height = 430, Content = new TextBlock { Text = "Diff viewer owner" } };
    private static CheckBox Choice(Window window, string name) => window.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == name);
    private static void Pump(TopLevel root) { root.UpdateLayout(); Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); }
    private static void Click(Control control)
    {
        TopLevel root = TopLevel.GetTopLevel(control)!; Pump(root); Assert.That(control.IsEffectivelyEnabled && control.IsEffectivelyVisible, Is.True);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point); root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump(root);
    }
    private static void AssertTextPixels(WriteableBitmap frame, TopLevel root, Control label, Color foreground, Color background)
    {
        Rect bounds = new(label.TranslatePoint(default, root)!.Value, label.Bounds.Size); using ILockedFramebuffer pixels = frame.Lock();
        byte[] bytes = new byte[pixels.RowBytes * pixels.Size.Height]; Marshal.Copy(pixels.Address, bytes, 0, bytes.Length); bool bgra = pixels.Format == PixelFormat.Bgra8888;
        double dr = foreground.R - background.R, dg = foreground.G - background.G, db = foreground.B - background.B, magnitude = dr * dr + dg * dg + db * db; int ink = 0;
        for (int y = Math.Max(0, (int)Math.Floor(bounds.Y)); y < Math.Min(pixels.Size.Height, (int)Math.Ceiling(bounds.Bottom)); y++)
        {
            for (int x = Math.Max(0, (int)Math.Floor(bounds.X)); x < Math.Min(pixels.Size.Width, (int)Math.Ceiling(bounds.Right)); x++)
            {
                int offset = y * pixels.RowBytes + x * 4; double r = bytes[offset + (bgra ? 2 : 0)], g = bytes[offset + 1], b = bytes[offset + (bgra ? 0 : 2)];
                double alpha = ((r - background.R) * dr + (g - background.G) * dg + (b - background.B) * db) / magnitude;
                if (alpha >= .25 && alpha <= 1.05 && Math.Abs(r - (background.R + alpha * dr)) <= 3 && Math.Abs(g - (background.G + alpha * dg)) <= 3 && Math.Abs(b - (background.B + alpha * db)) <= 3) { ink++; }
            }
        }
        Assert.That(ink, Is.GreaterThan(30), "The original foreground must reach the actual French label pixels in both owner palettes.");
    }
    private static double Contrast(Color foreground, Color background)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        }
        double a = Luminance(foreground), b = Luminance(background); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
