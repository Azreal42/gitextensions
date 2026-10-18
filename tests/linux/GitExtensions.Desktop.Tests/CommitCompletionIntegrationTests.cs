using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using GitUI.AutoCompletion;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CommitCompletionIntegrationTests
{
    [AvaloniaTest]
    public async Task RealChangedFilesCompleteWithDictionaryNoneAndTypingKeepsTheExistingListsAndNativeUndo()
    {
        using GitFixture git = await GitFixture.CreateAsync();
        await File.WriteAllTextAsync(git.PathInRepository("PortableZebra.cs"), "public class PortableZebra {}\n");
        await using Host host = await Host.Open(git, "none");
        await Until(() => host.Completion.IsCorpusReady);
        Assert.That(host.Completion.Corpus, Does.Contain("PortableZebra.cs").And.Contain("PortableZebra"));
        TreeView files = Field<TreeView>(host.Window, "Unstaged"); object? rows = files.ItemsSource;
        TextBox original = host.Editor;
        await AvatarUiTestDriver.Edit(host.Window, host.Editor, "Portab");
        await Until(() => host.Completion.IsOpen);
        Assert.That(files.ItemsSource, Is.SameAs(rows)); Assert.That(host.Editor, Is.SameAs(original));
        Assert.That(host.Spelling.Misspellings, Is.Empty, "None controls spelling independently of Git completion.");
        Accept(host, "PortableZebra.cs", PhysicalKey.Tab);
        Assert.That(host.Editor.Text, Is.EqualTo("PortableZebra.cs")); Assert.That(host.Editor.IsKeyboardFocusWithin, Is.True);
        Assert.That(host.Window.HasCommitted, Is.False); Assert.That(host.Window.IsVisible, Is.True);
        AvatarUiTestDriver.Press(host.Window, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.That(host.Editor.Text, Is.EqualTo("Portab"));
        AvatarUiTestDriver.Press(host.Window, PhysicalKey.Space, RawInputModifiers.Control);
        Assert.That(host.Completion.IsOpen, Is.True);
        AvatarUiTestDriver.Press(host.Window, PhysicalKey.Escape);
        Assert.That(host.Completion.IsOpen, Is.False); Assert.That(host.Window.IsVisible, Is.True);
        Assert.That(host.Editor.Text, Is.EqualTo("Portab")); Assert.That(files.ItemsSource, Is.SameAs(rows));
    }

    [AvaloniaTest]
    public async Task TheRealContextFlagPublishesItsSavedPolicyAndCanReenableGitCompletionUnderNone()
    {
        using GitFixture git = await GitFixture.CreateAsync();
        await File.WriteAllTextAsync(git.PathInRepository("ToggleZebra.cs"), "public class ToggleZebra {}\n");
        await using Host host = await Host.Open(git, "none");
        await Until(() => host.Completion.IsCorpusReady);
        await AvatarUiTestDriver.Edit(host.Window, host.Editor, "Togg"); await Until(() => host.Completion.IsOpen);
        await host.Menu("ProvideAutocompletion");
        Assert.That(host.Preferences.Load().ProvideAutocompletion, Is.False);
        Assert.That(host.Completion.IsOpen, Is.False); Assert.That(host.Completion.Corpus, Is.Empty);
        string text = host.Editor.Text!;
        await AvatarUiTestDriver.Click(host.Window, host.Editor);
        AvatarUiTestDriver.Press(host.Window, PhysicalKey.Space, RawInputModifiers.Control);
        Assert.That(host.Completion.IsOpen, Is.False); Assert.That(host.Editor.Text, Is.EqualTo(text));
        await host.Menu("ProvideAutocompletion");
        Assert.That(host.Preferences.Load().ProvideAutocompletion, Is.True);
        await Until(() => host.Completion.IsCorpusReady && host.Completion.Corpus.Contains("ToggleZebra.cs"));
        await AvatarUiTestDriver.Edit(host.Window, host.Editor, "Togg");
        AvatarUiTestDriver.Press(host.Window, PhysicalKey.Space, RawInputModifiers.Control);
        Assert.That(host.Completion.IsOpen, Is.True); Accept(host, "ToggleZebra.cs", PhysicalKey.Enter);
        Assert.That(host.Editor.Text, Is.EqualTo("ToggleZebra.cs")); Assert.That(host.Editor.IsKeyboardFocusWithin, Is.True);
        Assert.That(host.Window.HasCommitted, Is.False);
    }

    [AvaloniaTest]
    public async Task ARealRefreshReplacesTheGitSnapshotWithoutReplacingTheMessageEditor()
    {
        using GitFixture git = await GitFixture.CreateAsync();
        string old = git.PathInRepository("BeforeRefreshMarker.cs"); await File.WriteAllTextAsync(old, "class BeforeRefreshMarker {}\n");
        await using Host host = await Host.Open(git, "none");
        await Until(() => host.Completion.IsCorpusReady && host.Completion.Corpus.Contains("BeforeRefreshMarker.cs"));
        TextBox editor = host.Editor; await AvatarUiTestDriver.Edit(host.Window, editor, "keep this draft");
        File.Delete(old); await File.WriteAllTextAsync(git.PathInRepository("AfterRefreshMarker.cs"), "class AfterRefreshMarker {}\n");
        AvatarUiTestDriver.Press(host.Window, PhysicalKey.F5);
        await Until(() => host.Completion.IsCorpusReady && host.Completion.Corpus.Contains("AfterRefreshMarker.cs"));
        Assert.That(host.Completion.Corpus, Does.Not.Contain("BeforeRefreshMarker.cs"));
        Assert.That(host.Editor, Is.SameAs(editor)); Assert.That(host.Editor.Text, Is.EqualTo("keep this draft"));
        Assert.That(host.Window.HasCommitted, Is.False);
    }

    [AvaloniaTest]
    public async Task CompletionWordsExemptSpellingInMemoryAndAreReinjectedAfterARealDictionarySwap()
    {
        using GitFixture git = await GitFixture.CreateAsync();
        const string token = "QzxCompletionToken";
        await File.WriteAllTextAsync(git.PathInRepository(token + ".cs"), "public class " + token + " {}\n");
        await using Host host = await Host.Open(git, "en-US");
        await Until(() => host.Completion.IsCorpusReady && host.Completion.Corpus.Contains(token));
        await AvatarUiTestDriver.Edit(host.Window, host.Editor, token + " wrld");
        await Until(() => host.Spelling.Misspellings.Count == 1);
        Assert.That(host.Spelling.Misspellings.Single().Word, Is.EqualTo("wrld"));
        await host.Menu("CommitDictionary_none");
        await Until(() => host.Spelling.Misspellings.Count == 0);
        await host.Menu("CommitDictionary_en-US");
        await Until(() => host.Spelling.Misspellings.Count == 1);
        Assert.That(host.Spelling.Misspellings.Single().Word, Is.EqualTo("wrld"));
        Assert.That(host.Editor.Text, Is.EqualTo(token + " wrld"));
        Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(host.Preferences.FilePath)!, "NetSpell", "user.dic")), Is.False);
        Assert.That((await git.Repository.ReadEffectiveDictionaryAsync("en-US")).LocalDictionary, Is.EqualTo("en-US"));
    }

    [AvaloniaTest]
    public async Task InvalidRepositoryCompletionRulesAreVisibleWhileTheRealNativeEditorRemainsUsable()
    {
        using GitFixture git = await GitFixture.CreateAsync();
        PreferencesStore preferences = new(); Directory.CreateDirectory(Path.GetDirectoryName(preferences.FilePath)!);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(preferences.FilePath)!, "AutoCompleteRegexes.txt"), ".cs = (\n");
        await File.WriteAllTextAsync(git.PathInRepository("RulesFailure.cs"), "class RulesFailure {}\n");
        await using Host host = await Host.Open(git, "none");
        await Until(() => host.Completion.Notice.IsVisible);
        Assert.That(host.Completion.Notice.Text, Does.Contain("AutoCompleteRegexes.txt"));
        Assert.That(host.Completion.Failures.Count, Is.EqualTo(1));
        await AvatarUiTestDriver.Edit(host.Window, host.Editor, "still editable");
        Assert.That(host.Editor.Text, Is.EqualTo("still editable")); Assert.That(host.Editor.IsKeyboardFocusWithin, Is.True);
        Assert.That(host.Window.HasCommitted, Is.False); Assert.That(host.Spelling.Notice.IsVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task TheRealWindowDrainsAnUncooperativeCompletionProviderBeforeClosed()
    {
        using GitFixture git = await GitFixture.CreateAsync(); await using Host host = await Host.Open(git, "none");
        await Until(() => host.Completion.IsCorpusReady);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Completion.Refresh(async _ => { entered.TrySetResult(); await release.Task; return [new AutoCompleteWord("Late")]; });
        try
        {
            await entered.Task.WaitAsync(Limit); await AvatarUiTestDriver.Edit(host.Window, host.Editor, "La");
            host.Window.Close();
            Assert.That(host.Closed, Is.False); Assert.That(host.Window.IsVisible, Is.True);
            Assert.That(host.Window.SpellingCloseCompletion.IsCompleted, Is.False);
            Assert.That(Field<Button>(host.Window, "Commit").IsEnabled, Is.False);
            release.TrySetResult(); await Until(() => host.Closed);
            Assert.That(host.DrainedAtClosed, Is.True); Assert.That(host.Completion.IsOpen, Is.False);
            Assert.That(host.Completion.Corpus, Does.Not.Contain("Late"));
            Assert.That(host.Editor.Text, Is.EqualTo("La"));
        }
        finally { release.TrySetResult(); }
    }

    private static void Accept(Host host, string word, PhysicalKey key)
    {
        int index = host.Completion.Suggestions.ToList().IndexOf(word); Assert.That(index, Is.GreaterThanOrEqualTo(0));
        AvatarUiTestDriver.Press(host.Window, PhysicalKey.Home);
        for (int i = 0; i < index; i++) { AvatarUiTestDriver.Press(host.Window, PhysicalKey.ArrowDown); }
        AvatarUiTestDriver.Press(host.Window, key);
    }
    private static T Field<T>(Window window, string name) where T : Control => AvatarUiTestDriver.Field<T>(window, name);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private static async Task Until(Func<bool> complete)
    {
        Stopwatch watch = Stopwatch.StartNew(); while (!complete() && watch.Elapsed < Limit) { await Task.Delay(10); }
        Assert.That(complete(), Is.True, "The actual Commit completion operation did not finish.");
    }
    private sealed class Host : IAsyncDisposable
    {
        internal CommitDialog Window { get; }
        internal PreferencesStore Preferences { get; } = new();
        internal CommitCompletionController Completion { get; }
        internal CommitSpellingController Spelling { get; }
        internal TextBox Editor => Field<TextBox>(Window, "Message");
        internal bool Closed { get; private set; }
        internal bool DrainedAtClosed { get; private set; }
        private Host(GitRepository repository, string dictionary)
        {
            Preferences.Save(Preferences.Load() with { Dictionary = dictionary, ProvideAutocompletion = true });
            CommitPreferencesStore commit = new(); commit.Save(new() { SelectStagedOnMessageFocus = false });
            Window = new(repository, commit);
            Completion = (CommitCompletionController)typeof(CommitDialog).GetField("_completion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
            Spelling = (CommitSpellingController)typeof(CommitDialog).GetField("_spelling", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
            Window.Closed += (_, _) => { Closed = true; DrainedAtClosed = Window.SpellingCloseCompletion.IsCompletedSuccessfully; };
        }
        internal static async Task<Host> Open(GitFixture git, string dictionary)
        {
            Host host = new(git.Repository, dictionary); host.Window.Show();
            try { await host.Window.InitializeAsync(); return host; }
            catch { await host.DisposeAsync(); throw; }
        }
        internal async Task Menu(string name)
        {
            TextBox editor = Editor;
            using (await AvatarUiTestDriver.Frame(Window)) { }
            Point point = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), Window)!.Value;
            Assert.That((Window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(editor), Is.True);
            Window.MouseMove(point); Window.MouseDown(point, MouseButton.Right); Window.MouseUp(point, MouseButton.Right);
            ContextMenu menu = editor.ContextMenu!;
            await Until(() => menu.Items.OfType<MenuItem>().Any(item => item.Name == "CommitDictionary" && item.IsEnabled));
            MenuItem item = All(menu.Items).Single(item => item.Name == name);
            Assert.That(item.IsEnabled, Is.True); item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.Close();
            await Spelling.Completion.WaitAsync(Limit);
            static IEnumerable<MenuItem> All(ItemCollection items)
            {
                foreach (MenuItem item in items.OfType<MenuItem>())
                { yield return item; foreach (MenuItem child in All(item.Items)) { yield return child; } }
            }
        }
        public async ValueTask DisposeAsync()
        {
            if (!Closed) { Window.Close(); await Until(() => Closed); }
            await Window.SpellingCloseCompletion.WaitAsync(Limit);
        }
    }
}
