using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class DialogAccessKeyTests
{
    [AvaloniaTest]
    public async Task OriginalCloneFieldAndOptionAccessKeysKeepLiteralPathsAndEnterClonesTheChosenDestination()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CloneDialog dialog = new(fixture.Root, fixture.Repository.Path); dialog.Show(); await dialog.InitializeAsync();
        try
        {
            Assert.That(Field<TextBox>(dialog, "CloneSource").IsFocused, Is.True, "The original first input must receive the initial focus.");
            SetInitialFocus(dialog, "CloneDestination");
            FocusWithAlt(dialog, PhysicalKey.C, "CloneSource");
            FocusWithAlt(dialog, PhysicalKey.D, "CloneDestination");
            FocusWithAlt(dialog, PhysicalKey.S, "CloneSubdirectory");
            string child = " keyboard clone\n雪 "; Field<TextBox>(dialog, "CloneSubdirectory").Text = child;
            FocusWithAlt(dialog, PhysicalKey.A, "CloneBranch");
            PressAlt(dialog, PhysicalKey.H); Assert.That(Field<CheckBox>(dialog, "CloneFullHistory").IsChecked, Is.False);
            PressAlt(dialog, PhysicalKey.H); Assert.That(Field<CheckBox>(dialog, "CloneFullHistory").IsChecked, Is.True);
            PressAlt(dialog, PhysicalKey.U); Assert.That(Field<RadioButton>(dialog, "CloneBare").IsChecked, Is.True);
            PressAlt(dialog, PhysicalKey.P); Assert.That(Field<RadioButton>(dialog, "ClonePersonal").IsChecked, Is.True);
            dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            if (dialog.IsVisible) { dialog.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None); }
            await Until(() => dialog.OwnedWindows.OfType<GitProcessDialog>().Any());
            GitProcessDialog process = dialog.OwnedWindows.OfType<GitProcessDialog>().Single();
            await Until(() => !process.IsRunning);
            Assert.That(process.Result?.Success, Is.True, process.Error?.ToString());
            Assert.That(dialog.IsVisible, Is.True, "Clone must wait for the real Process acknowledgment.");
            Assert.That(dialog.CreatedRepository, Is.Null);
            PushProcessTestDriver.Pointer(Field<Button>(process, "Ok"));
            await Until(() => !dialog.IsVisible);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.CreatedRepository!.Path, Is.EqualTo(Path.Combine(fixture.Root, child)));
            Assert.That((await dialog.CreatedRepository.GetStatusAsync()).Head, Is.EqualTo((await fixture.Repository.GetStatusAsync()).Head));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task FrenchCloneAccessKeysAndOriginalBrowseIconsRenderInBothThemesAndEscapeCloses()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        CloneDialog dialog = new(fixture.Root, fixture.Repository.Path); dialog.Show(); await dialog.InitializeAsync();
        try
        {
            Assert.That(Field<TextBox>(dialog, "CloneSource").IsFocused, Is.True);
            SetInitialFocus(dialog, "CloneDestination");
            FocusWithAlt(dialog, PhysicalKey.C, "CloneSource");
            FocusWithAlt(dialog, PhysicalKey.D, "CloneDestination");
            FocusWithAlt(dialog, PhysicalKey.S, "CloneSubdirectory");
            FocusWithAlt(dialog, PhysicalKey.A, "CloneBranch");
            Assert.That(Field<Label>(dialog, "CloneAccessbrachLabel").GetVisualDescendants().OfType<AccessText>().Single().Text, Is.EqualTo("Br_anche :"));
            foreach (string name in new[] { "BrowseCloneSource", "BrowseCloneDestination" })
            {
                Button button = Field<Button>(dialog, name);
                Assert.That(button.GetVisualDescendants().OfType<Image>().Single().Source, Is.Not.Null);
                Assert.That(button.GetVisualDescendants().OfType<AccessText>().Single().AccessKey, Is.EqualTo('P'));
            }
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme;
                PressAltDown(dialog); Capture(dialog, $"classic-clone-access-keys-fr-{theme}.png"); ReleaseAlt(dialog);
            }
            dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.That(dialog.IsVisible, Is.False); Assert.That(dialog.CreatedRepository, Is.Null);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task OriginalCheckoutAccessKeysSelectLocalChangeActionsAndThePrimaryButtonSwitchesTheBranch()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        await fixture.Repository.CreateBranchAsync("keyboard-topic", checkout: false);
        string file = Path.Combine(fixture.Repository.Path, "literal work\n雪"); await File.WriteAllTextAsync(file, "keep this work");
        GitReference target = (await fixture.Repository.GetReferencesAsync()).Single(reference => reference.ShortName == "keyboard-topic");
        CheckoutBranchDialog dialog = new(fixture.Repository, target); dialog.Show(); await dialog.InitializeAsync();
        try
        {
            Assert.That(Field<RadioButton>(dialog, "CheckoutLocalBranch").IsFocused, Is.True);
            SetInitialFocus(dialog, "CheckoutLocalBranch");
            FocusWithAlt(dialog, PhysicalKey.S, "CheckoutBranchSelection");
            Field<RadioButton>(dialog, "CheckoutRevisionMode").IsChecked = true;
            SetInitialFocus(dialog, "CheckoutLocalBranch");
            PressAlt(dialog, PhysicalKey.S);
            Assert.That(Field<TextBox>(dialog, "CheckoutRevision").IsFocused, Is.False, "The previous branch caption's access key must be unregistered when that caption disappears.");
            Field<RadioButton>(dialog, "CheckoutLocalBranch").IsChecked = true;
            FocusWithAlt(dialog, PhysicalKey.S, "CheckoutBranchSelection");
            PressAlt(dialog, PhysicalKey.M); Assert.That(Field<RadioButton>(dialog, "CheckoutMergeChanges").IsChecked, Is.True);
            PressAlt(dialog, PhysicalKey.T); Assert.That(Field<RadioButton>(dialog, "CheckoutStashChanges").IsChecked, Is.True);
            PressAlt(dialog, PhysicalKey.R); Assert.That(Field<RadioButton>(dialog, "CheckoutDiscardChanges").IsChecked, Is.True);
            Assert.That(Field<CheckBox>(dialog, "CheckoutChangesDefault").IsEnabled, Is.False);
            PressAlt(dialog, PhysicalKey.N); Assert.That(Field<RadioButton>(dialog, "CheckoutKeepChanges").IsChecked, Is.True);
            PressAlt(dialog, PhysicalKey.D); Assert.That(Field<CheckBox>(dialog, "CheckoutChangesDefault").IsChecked, Is.True);
            PressAlt(dialog, PhysicalKey.C); await Until(() => !dialog.IsVisible);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That((await fixture.Repository.GetStatusAsync()).Branch, Is.EqualTo("keyboard-topic"));
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo("keep this work"));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task FrenchCheckoutUsesTranslatedAccessKeysAndRendersTheirUnderlineInBothThemes()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, "local.txt"), "keep this work");
        CheckoutBranchDialog dialog = new(fixture.Repository); dialog.Show(); await dialog.InitializeAsync();
        try
        {
            SetInitialFocus(dialog, "CheckoutLocalBranch");
            FocusWithAlt(dialog, PhysicalKey.S, "CheckoutBranchSelection");
            PressAlt(dialog, PhysicalKey.F); Assert.That(Field<RadioButton>(dialog, "CheckoutMergeChanges").IsChecked, Is.True);
            PressAlt(dialog, PhysicalKey.N); Assert.That(Field<RadioButton>(dialog, "CheckoutKeepChanges").IsChecked, Is.True);
            Assert.That(Field<Button>(dialog, "CheckoutBranch").GetVisualDescendants().OfType<AccessText>().Single().Text, Is.EqualTo("_Charger"));
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme;
                PressAltDown(dialog); Capture(dialog, $"classic-checkout-access-keys-fr-{theme}.png"); ReleaseAlt(dialog);
            }
            dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.That(dialog.IsVisible, Is.False);
            Assert.That(dialog.LastResult, Is.Null);
        }
        finally { dialog.Close(); }
    }

    private static T Field<T>(TopLevel owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void SetInitialFocus(Window owner, string name)
    {
        owner.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.That(Field<Control>(owner, name).Focus(), Is.True);
    }
    private static void FocusWithAlt(Window owner, PhysicalKey key, string name)
    { PressAlt(owner, key); Assert.That(Field<Control>(owner, name).IsFocused, Is.True, $"Alt+{key} must focus {name}."); }
    private static void PressAltDown(Window owner)
    {
        owner.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        owner.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.Alt);
    }
    private static void ReleaseAlt(Window owner) { if (owner.IsVisible) { owner.KeyReleaseQwerty(PhysicalKey.AltLeft, RawInputModifiers.None); } }
    private static void PressAlt(Window owner, PhysicalKey key)
    {
        PressAltDown(owner); owner.KeyPressQwerty(key, RawInputModifiers.Alt);
        if (owner.IsVisible) { owner.KeyReleaseQwerty(key, RawInputModifiers.Alt); }
        ReleaseAlt(owner); Dispatcher.UIThread.RunJobs();
    }
    private static async Task Until(Func<bool> condition)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)); while (!condition()) { await Task.Delay(20, timeout.Token); } }
    private static void Capture(TopLevel owner, string name)
    {
        owner.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = owner.CaptureRenderedFrame() ?? throw new AssertionException("No dialog frame.");
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts/linux/screenshots"); Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, name); frame.Save(file); TestContext.AddTestAttachment(file);
    }
}
