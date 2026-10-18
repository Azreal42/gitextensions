using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Avatars;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AvatarSettingsPageTests
{
    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("fr", true)]
    public async Task OriginalAuthorControlsUseRealProviderSelectionNumericKeyboardAndThemeRenderingWithoutSaving(string culture, bool dark)
    {
        using AvatarSettingsTestProfile profile = new(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        byte[] before = File.ReadAllBytes(profile.Desktop.FilePath); List<(Window Owner, string Url)> help = []; int clears = 0;
        AppearanceSettingsValues initial = new() { Language = "English", Theme = dark ? "Dark" : "Light" };
        AppearanceSettingsPage page = new(initial, TranslationCatalog.LoadDefault(),
            clearAvatarCacheAsync: (owner, _) => { Assert.That(owner.IsVisible, Is.True); clears++; return Task.CompletedTask; },
            avatarHelpAsync: (owner, url) => { help.Add((owner, url)); return Task.CompletedTask; });
        Window window = new() { Content = new ScrollViewer { Content = page, Margin = new Thickness(16) }, Width = 1000, Height = 900,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        try
        {
            using (WriteableBitmap frame = await AvatarUiTestDriver.Frame(window))
            {
                Assert.That(window.ActualThemeVariant, Is.EqualTo(dark ? ThemeVariant.Dark : ThemeVariant.Light));
                Assert.That(window.Background, Is.InstanceOf<ISolidColorBrush>());
                Color expected = Color.Parse(dark ? "#323232" : "#FFFFFF");
                Assert.That(((ISolidColorBrush)window.Background!).Color, Is.EqualTo(expected));
                Assert.That(AvatarUiTestDriver.Pixel(frame, 2, 2), Is.EqualTo(expected), "The actual published frame uses the selected classic palette.");
                string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "avatar-settings-" + culture + (dark ? "-dark.png" : "-light.png"));
                frame.Save(path); TestContext.AddTestAttachment(path);
            }
            ComboBox provider = AvatarUiTestDriver.Field<ComboBox>(window, "AvatarProvider"), fallback = AvatarUiTestDriver.Field<ComboBox>(window, "_NO_TRANSLATE_NoImageService");
            TextBox template = AvatarUiTestDriver.Field<TextBox>(window, "txtCustomAvatarTemplate");
            NumericUpDown days = AvatarUiTestDriver.Field<NumericUpDown>(window, "_NO_TRANSLATE_DaysToCacheImages");
            Assert.That(provider.Items.OfType<AvatarProvider>(), Is.EqualTo(new[] { AvatarProvider.Default, AvatarProvider.Custom, AvatarProvider.None }));
            Assert.That(fallback.Items.OfType<AvatarFallbackType>(), Is.EqualTo(Enum.GetValues<AvatarFallbackType>()));
            Assert.That(template.IsVisible, Is.False); Assert.That(fallback.IsEnabled, Is.True);
            await AvatarUiTestDriver.Choose(window, provider, AvatarProvider.Custom); Assert.That(template.IsEffectivelyVisible, Is.True);
            const string literal = "https://offline.invalid/{email};<Default>"; await AvatarUiTestDriver.Edit(window, template, literal);
            await AvatarUiTestDriver.Choose(window, fallback, AvatarFallbackType.Identicon);
            await AvatarUiTestDriver.Click(window, AvatarUiTestDriver.Field<CheckBox>(window, "ShowAuthorAvatarInCommitInfo"));
            Assert.That(page.Draft.ShowAuthorAvatarInCommitInfo, Is.False); Assert.That(page.Draft.CustomAvatarTemplate, Is.EqualTo(literal));
            TextBox numericEditor = days.GetVisualDescendants().OfType<TextBox>().Single();
            await AvatarUiTestDriver.Edit(window, numericEditor, "0"); Assert.That(page.Validate(out _), Is.True); Assert.That(page.Draft.AvatarImageCacheDays, Is.Zero);
            await AvatarUiTestDriver.Edit(window, numericEditor, "400"); Assert.That(page.Validate(out _), Is.True); Assert.That(page.Draft.AvatarImageCacheDays, Is.EqualTo(400));
            await AvatarUiTestDriver.Edit(window, numericEditor, "401"); Assert.That(page.Validate(out string? error), Is.False); Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(AvatarUiTestDriver.Field<TextBlock>(window, "AppearanceSettingsNotice").IsVisible, Is.True);
            await AvatarUiTestDriver.Edit(window, numericEditor, "13"); Assert.That(page.Validate(out _), Is.True);
            await AvatarUiTestDriver.Choose(window, provider, AvatarProvider.Default); Assert.That(template.IsVisible, Is.False);
            Assert.That(page.Draft.CustomAvatarTemplate, Is.EqualTo(literal), "Temporarily hiding the editor must not discard its draft.");
            await AvatarUiTestDriver.Choose(window, provider, AvatarProvider.None); Assert.That(template.IsVisible, Is.False); Assert.That(fallback.IsEnabled, Is.True);
            await AvatarUiTestDriver.Click(window, AvatarUiTestDriver.Field<Button>(window, "avatarProviderHelp"));
            await AvatarUiTestDriver.Click(window, AvatarUiTestDriver.Field<Button>(window, "pictureAvatarHelp"));
            Assert.That(help.Select(item => item.Owner), Is.All.SameAs(window));
            Assert.That(help.Select(item => item.Url), Is.EqualTo(new[] { AppearanceSettingsPage.AvatarProviderHelpUrl, AppearanceSettingsPage.AvatarFallbackHelpUrl }));
            await AvatarUiTestDriver.Click(window, AvatarUiTestDriver.Field<Button>(window, "ClearImageCache")); Assert.That(clears, Is.EqualTo(1));
            Assert.That(File.ReadAllBytes(profile.Desktop.FilePath), Is.EqualTo(before));
            AppearanceSettingsValues accepted = initial with { AvatarProvider = AvatarProvider.Default, AvatarFallbackType = AvatarFallbackType.Robohash,
                AvatarImageCacheDays = 31, CustomAvatarTemplate = "https://accepted.invalid/{name}" };
            page.AcceptSaved(accepted); Assert.That(page.Draft, Is.EqualTo(accepted)); Assert.That(template.IsVisible, Is.False);
            Assert.That(page.Validate(out _), Is.True); Assert.That(AvatarUiTestDriver.Field<TextBlock>(window, "AppearanceSettingsNotice").IsVisible, Is.False);
        }
        finally { window.Close(); }
    }
}

internal static class AvatarUiTestDriver
{
    internal static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    internal static async Task Click(Window window, Control target)
    {
        target.BringIntoView(); using (await Frame(window)) { }
        Assert.That(target.IsEffectivelyVisible && target.IsEffectivelyEnabled, Is.True, target.Name);
        Point point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        Visual? hit = window.InputHitTest(point) as Visual;
        Assert.That(hit?.GetSelfAndVisualAncestors().Contains(target), Is.True, "The published first hit must target " + target.Name);
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }
    internal static async Task Choose(Window window, ComboBox combo, object value)
    {
        await Click(window, combo); using (await Frame(window)) { }
        Assert.That(combo.IsDropDownOpen, Is.True); int index = combo.Items.IndexOf(value); Assert.That(index, Is.GreaterThanOrEqualTo(0));
        if (combo.SelectedIndex < 0) { Press(window, PhysicalKey.ArrowDown); }
        Press(window, PhysicalKey.Home); for (int item = 0; item < index; item++) { Press(window, PhysicalKey.ArrowDown); }
        Press(window, PhysicalKey.Enter); using (await Frame(window)) { }
        Assert.That(combo.SelectedItem, Is.EqualTo(value)); Assert.That(combo.IsDropDownOpen, Is.False);
    }
    internal static async Task Edit(Window window, TextBox editor, string text)
    {
        await Click(window, editor); Press(window, PhysicalKey.A, RawInputModifiers.Control); Press(window, PhysicalKey.Backspace);
        window.KeyTextInput(text); Assert.That(editor.Text, Is.EqualTo(text));
    }
    internal static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); Dispatcher.UIThread.RunJobs(); }
    internal static async Task<WriteableBitmap> Frame(TopLevel window)
    {
        using CancellationTokenSource timeout = new(OfflineAvatars.Limit); window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background, timeout.Token);
        Size client = window.ClientSize; double scale = window.RenderScaling;
        var visual = ElementComposition.GetElementVisual(window); Assert.That(visual, Is.Not.Null);
        var batch = visual!.Compositor.RequestCompositionBatchCommitAsync(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await batch.Rendered.WaitAsync(timeout.Token); await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background, timeout.Token);
        WriteableBitmap frame = window.GetLastRenderedFrame() ?? throw new AssertionException("The rendered batch must leave a real frame.");
        try
        {
            Assert.That(window.ClientSize, Is.EqualTo(client)); Assert.That(window.RenderScaling, Is.EqualTo(scale));
            Assert.That(frame.PixelSize, Is.EqualTo(PixelSize.FromSize(client, scale))); Assert.That(window.IsMeasureValid && window.IsArrangeValid, Is.True); return frame;
        }
        catch { frame.Dispose(); throw; }
    }
    internal static Color Pixel(WriteableBitmap frame, int x, int y)
    {
        using ILockedFramebuffer buffer = frame.Lock(); byte[] pixel = new byte[4]; Marshal.Copy(IntPtr.Add(buffer.Address, y * buffer.RowBytes + x * 4), pixel, 0, 4);
        bool bgra = buffer.Format == PixelFormat.Bgra8888; return Color.FromArgb(pixel[3], pixel[bgra ? 2 : 0], pixel[1], pixel[bgra ? 0 : 2]);
    }
    internal static async Task Close(Window window)
    {
        TaskCompletionSource closed = OfflineAvatars.Signal(); window.Closed += (_, _) => closed.TrySetResult();
        await AvatarWindowLifetime.For(window).BeginClose().WaitAsync(OfflineAvatars.Limit);
        bool visible = window.IsVisible; window.Close(); if (visible) { await closed.Task.WaitAsync(OfflineAvatars.Limit); }
        if (window is MainWindow main) { await main.WindowWorkCompletion.WaitAsync(OfflineAvatars.Limit); }
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }
}
