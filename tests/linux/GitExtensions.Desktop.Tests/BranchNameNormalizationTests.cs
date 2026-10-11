using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class BranchNameNormalizationTests
{
    private static readonly (string Symbol, string Input, string Expected)[] Replacements =
    [ ("_", "feature 0/.hidden.lock", "feature_0/_hidden_lock"),
      ("-", "feature 1/.hidden.lock", "feature-1/-hidden-lock"),
      ("", "feature 2/.hidden.lock", "feature2/hiddenlock") ];

    [AvaloniaTest]
    public async Task RenamePreservesInputsWithoutAnOriginalValidPathCharacterAndAcceptsLiteralEmoji()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        RenameBranchDialog dialog = new(fixture.Repository, "main"); dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            TextBox name = Field<TextBox>(dialog, "RenameBranchName");
            foreach (string input in new[] { ":^~", "λ:" })
            {
                name.Text = input; name.Focus(); name.CaretIndex = 1;
                Field<Button>(dialog, "RenameBranch").Focus();
                Assert.That(name.Text, Is.EqualTo(input));
                Assert.That(name.CaretIndex, Is.EqualTo(1));
                name.Focus(); PressEnter(dialog); await IdleAsync(dialog, success: false);
                Assert.That(dialog.LastError, Is.Not.Null);
                Assert.That(dialog.IsVisible, Is.True);
                Assert.That(dialog.HasRepositoryChanges, Is.False);
                await UnchangedAsync(fixture, head, ["refs/heads/main"]);
            }
            name.Text = "💡"; name.Focus(); PressEnter(dialog);
            await ClosedAsync(dialog);
            Assert.That(dialog.RenamedBranch, Is.EqualTo("💡"));
            Assert.That((await fixture.Repository.GetStatusAsync()).Branch, Is.EqualTo("💡"));
            Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task CreatePreservesInvalidGuardedInputAndCreatesTheLiteralEmojiInsteadOfUnderscores()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        CreateBranchDialog dialog = new(fixture.Repository); dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            TextBox name = Field<TextBox>(dialog, "CreateBranchName");
            foreach (string input in new[] { ":^~", "λ:" })
            {
                name.Text = input; name.Focus(); PressEnter(dialog); await IdleAsync(dialog, success: false);
                Assert.That(name.Text, Is.EqualTo(input));
                Assert.That(dialog.LastError, Is.Not.Null);
                Assert.That(dialog.IsVisible, Is.True);
                await UnchangedAsync(fixture, head, ["refs/heads/main"]);
            }
            name.Text = "💡"; name.Focus(); Click(dialog, "CreateBranch");
            await ClosedAsync(dialog);
            Assert.That(dialog.CreatedBranch, Is.EqualTo("💡"));
            Assert.That((await fixture.Repository.GetStatusAsync()).Branch, Is.EqualTo("💡"));
            Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task RemoteCheckoutPreservesInvalidGuardedInputAndCreatesALiteralEmojiTrackingBranch()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.CreateRemoteAsync();
        string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        string[] references = (await fixture.Repository.GetReferencesAsync()).Select(reference => reference.Name).ToArray();
        GitReference remote = await RemoteAsync(fixture);
        CheckoutBranchDialog dialog = new(fixture.Repository, remote); dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            TextBox name = Field<TextBox>(dialog, "CheckoutLocalName");
            foreach (string input in new[] { ":^~", "λ:" })
            {
                name.Text = input; name.Focus(); Click(dialog, "CheckoutBranch"); await IdleAsync(dialog, success: false);
                Assert.That(name.Text, Is.EqualTo(input));
                Assert.That(dialog.LastError, Is.Not.Null);
                Assert.That(dialog.IsVisible, Is.True);
                await UnchangedAsync(fixture, head, references);
            }
            name.Text = "💡"; name.Focus(); PressEnter(dialog);
            await ClosedAsync(dialog);
            GitReference created = (await fixture.Repository.GetReferencesAsync()).Single(reference => reference.Name == "refs/heads/💡");
            Assert.That(created.Upstream, Is.EqualTo("origin/main"));
            Assert.That((await fixture.Repository.GetStatusAsync()).Branch, Is.EqualTo("💡"));
            Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task CreateUsesConfiguredReplacementOnFocusLossClickAndEnterAndKeepsTheCaret()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        foreach ((string symbol, string input, string expected) in Replacements)
        {
            CreateBranchDialog dialog = new(fixture.Repository, replacement: symbol); dialog.Show();
            try
            {
                await dialog.InitializeAsync();
                Field<CheckBox>(dialog, "CheckoutAfterCreate").IsChecked = false;
                TextBox name = Field<TextBox>(dialog, "CreateBranchName");
                name.Text = input; name.Focus(); name.CaretIndex = 5;
                Field<TextBox>(dialog, "CreateBranchRevision").Focus();
                Assert.That(name.Text, Is.EqualTo(expected));
                Assert.That(name.CaretIndex, Is.EqualTo(5));
                name.Text = input; name.Focus();
                if (symbol == "-") { PressEnter(dialog); } else { Click(dialog, "CreateBranch"); }
                await ClosedAsync(dialog);
                Assert.That(dialog.CreatedBranch, Is.EqualTo(expected));
                Assert.That((await fixture.Repository.GetReferencesAsync()).Any(reference => reference.Name == "refs/heads/" + expected), Is.True);
                Assert.That((await fixture.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
            }
            finally { dialog.Close(); }
        }
    }

    [AvaloniaTest]
    public async Task RemoteCheckoutUsesConfiguredReplacementOnFocusLossClickAndEnterAndKeepsTheCaret()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.CreateRemoteAsync();
        GitReference remote = await RemoteAsync(fixture);
        foreach ((string symbol, string input, string expected) in Replacements)
        {
            CheckoutBranchDialog dialog = new(fixture.Repository, remote, replacement: symbol); dialog.Show();
            try
            {
                await dialog.InitializeAsync();
                TextBox name = Field<TextBox>(dialog, "CheckoutLocalName");
                name.Text = input; name.Focus(); name.CaretIndex = 5;
                Field<ComboBox>(dialog, "CheckoutBranchSelection").Focus();
                Assert.That(name.Text, Is.EqualTo(expected));
                Assert.That(name.CaretIndex, Is.EqualTo(5));
                name.Text = input; name.Focus();
                if (symbol == "-") { Click(dialog, "CheckoutBranch"); } else { PressEnter(dialog); }
                await ClosedAsync(dialog);
                Assert.That((await fixture.Repository.GetStatusAsync()).Branch, Is.EqualTo(expected));
                Assert.That((await fixture.Repository.GetReferencesAsync()).Single(reference => reference.Name == "refs/heads/" + expected).Upstream, Is.EqualTo("origin/main"));
            }
            finally { dialog.Close(); }
        }
    }

    [AvaloniaTest]
    public async Task DisablingNormalizationPreservesInputAndValidLiteralNamesForCreateAndRemoteCheckout()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        CreateBranchDialog create = new(fixture.Repository, autoNormalize: false, replacement: "-"); create.Show();
        try
        {
            await create.InitializeAsync();
            Field<CheckBox>(create, "CheckoutAfterCreate").IsChecked = false;
            TextBox name = Field<TextBox>(create, "CreateBranchName");
            name.Text = "keep this:name"; name.Focus(); name.CaretIndex = 4;
            Field<TextBox>(create, "CreateBranchRevision").Focus();
            Assert.That(name.Text, Is.EqualTo("keep this:name"));
            Assert.That(name.CaretIndex, Is.EqualTo(4));
            name.Focus(); PressEnter(create); await IdleAsync(create, success: false);
            Assert.That(create.LastError, Is.Not.Null);
            await UnchangedAsync(fixture, head, ["refs/heads/main"]);
            const string literal = "topic/λ;$()'quoted";
            name.Text = literal; name.Focus(); Click(create, "CreateBranch"); await ClosedAsync(create);
            Assert.That(create.CreatedBranch, Is.EqualTo(literal));
        }
        finally { create.Close(); }
        await fixture.CreateRemoteAsync();
        CheckoutBranchDialog checkout = new(fixture.Repository, await RemoteAsync(fixture), autoNormalize: false, replacement: "-"); checkout.Show();
        try
        {
            await checkout.InitializeAsync();
            string[] references = (await fixture.Repository.GetReferencesAsync()).Select(reference => reference.Name).ToArray();
            TextBox name = Field<TextBox>(checkout, "CheckoutLocalName");
            name.Text = "keep this:name"; name.Focus();
            Field<ComboBox>(checkout, "CheckoutBranchSelection").Focus();
            Assert.That(name.Text, Is.EqualTo("keep this:name"));
            name.Focus(); Click(checkout, "CheckoutBranch"); await IdleAsync(checkout, success: false);
            Assert.That(checkout.LastError, Is.Not.Null);
            await UnchangedAsync(fixture, head, references);
            const string literal = "checkout/λ;$()'literal";
            name.Text = literal; name.Focus(); PressEnter(checkout); await ClosedAsync(checkout);
            Assert.That((await fixture.Repository.GetStatusAsync()).Branch, Is.EqualTo(literal));
            Assert.That((await fixture.Repository.GetReferencesAsync()).Single(reference => reference.ShortName == literal).Upstream, Is.EqualTo("origin/main"));
        }
        finally { checkout.Close(); }
    }

    [AvaloniaTest]
    public async Task MainPassesPersistedNormalizationPreferencesToBothNativeDialogs()
    {
        foreach (bool enabled in new[] { true, false })
        {
            using Fixture fixture = await Fixture.CreateAsync();
            await fixture.CreateRemoteAsync();
            new PreferencesStore().Save(new DesktopPreferences { AutoNormaliseBranchName = enabled, AutoNormaliseSymbol = "-" });
            MainWindow main = new() { Width = 1440, Height = 900 }; main.Show();
            try
            {
                await main.OpenRepositoryAsync(fixture.Repository.Path);
                CreateBranchDialog create = await OpenCommandAsync<CreateBranchDialog>(main, "Create branch…");
                Field<TextBox>(create, "CreateBranchName").Text = "create from preferences";
                Field<TextBox>(create, "CreateBranchName").Focus(); PressEnter(create);
                if (enabled)
                {
                    await MainClosedAsync(main, create);
                    Assert.That(create.CreatedBranch, Is.EqualTo("create-from-preferences"));
                }
                else
                {
                    await IdleAsync(create, success: false);
                    Assert.That(create.LastError, Is.Not.Null);
                    Assert.That(Field<TextBox>(create, "CreateBranchName").Text, Is.EqualTo("create from preferences"));
                    create.Close(); await MainClosedAsync(main, create);
                }
                TreeView navigation = Field<TreeView>(main, "RepositoryNavigation");
                navigation.SelectedItem = navigation.Items.OfType<TreeViewItem>().SelectMany(group => group.Items.OfType<TreeViewItem>())
                    .Single(node => node.Tag is GitReference reference && reference.Name == "refs/remotes/origin/main");
                await Until(() => !main.IsBusy);
                CheckoutBranchDialog checkout = await OpenCommandAsync<CheckoutBranchDialog>(main, "Checkout branch…");
                Field<TextBox>(checkout, "CheckoutLocalName").Text = "checkout from preferences";
                Field<TextBox>(checkout, "CheckoutLocalName").Focus(); Click(checkout, "CheckoutBranch");
                if (enabled)
                {
                    await MainClosedAsync(main, checkout);
                    Assert.That((await fixture.Repository.GetStatusAsync()).Branch, Is.EqualTo("checkout-from-preferences"));
                }
                else
                {
                    await IdleAsync(checkout, success: false);
                    Assert.That(checkout.LastError, Is.Not.Null);
                    Assert.That(Field<TextBox>(checkout, "CheckoutLocalName").Text, Is.EqualTo("checkout from preferences"));
                    checkout.Close(); await MainClosedAsync(main, checkout);
                    Assert.That((await fixture.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
                }
                Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
            }
            finally { main.Close(); }
        }
    }

    private static T Field<T>(Window owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Window owner, string name)
    { Button button = Field<Button>(owner, name); Assert.That(button.IsEnabled && button.IsEffectivelyVisible, Is.True, name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static void PressEnter(Window owner)
    { owner.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); if (owner.IsVisible) { owner.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None); } }
    private static async Task IdleAsync(RepositoryDialog dialog, bool success = true)
    { await Until(() => !dialog.IsBusy); if (success) { Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); } }
    private static async Task ClosedAsync(RepositoryDialog dialog)
    { await Until(() => !dialog.IsVisible && !dialog.IsBusy); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); }
    private static async Task MainClosedAsync(MainWindow main, RepositoryDialog dialog)
    { await Until(() => !dialog.IsVisible && !main.OwnedWindows.Contains(dialog) && !main.IsBusy); Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); }
    private static async Task<T> OpenCommandAsync<T>(MainWindow main, string title) where T : RepositoryDialog
    {
        MenuItem commands = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(menu => menu.Header as string == "Commands");
        MenuItem action = commands.Items.OfType<MenuItem>().Single(menu => menu.Header as string == title);
        Assert.That(action.IsEnabled, Is.True);
        action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => main.OwnedWindows.OfType<T>().Any());
        T dialog = main.OwnedWindows.OfType<T>().Single(); await IdleAsync(dialog); return dialog;
    }
    private static async Task<GitReference> RemoteAsync(Fixture fixture)
        => (await fixture.Repository.GetReferencesAsync()).Single(reference => reference.Name == "refs/remotes/origin/main");
    private static async Task UnchangedAsync(Fixture fixture, string head, string[] references)
    {
        Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        Assert.That((await fixture.Repository.GetReferencesAsync()).Select(reference => reference.Name), Is.EquivalentTo(references));
        Assert.That((await fixture.Repository.GetStatusAsync()).Changes, Is.Empty);
    }
    private static async Task Until(Func<bool> predicate)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)); while (!predicate()) { await Task.Delay(20, timeout.Token); } }
}
