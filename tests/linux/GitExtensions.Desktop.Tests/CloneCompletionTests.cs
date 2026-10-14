using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CloneCompletionTests
{
    [AvaloniaTest]
    public async Task DestinationSuggestionsKeepLiteralDirectoriesAndEscapeOnlyTheirCaptionsInBothThemes()
    {
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            await using CloneInteractionFixture f = await CloneInteractionFixture.Create();
            string parent = Path.Combine(f.Root, "destination '\";$(literal) 雪");
            Directory.CreateDirectory(parent);
            string[] names = ["lit first", "lit quote'\";$(literal)", "lit newline\n\t雪 "];
            foreach (string name in names) { Directory.CreateDirectory(Path.Combine(parent, name)); }
            string rawParent = parent + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar;
            string[] directories = names.Select(name => rawParent + name).Order(StringComparer.Ordinal).ToArray();
            string neighbor = rawParent + "lit neighboring file"; await File.WriteAllTextAsync(neighbor, "file, not a destination\n");
            CloneDialog clone = await f.Show(theme: theme);
            Dictionary<string, byte[]?> histories = f.Histories(); string materials = f.Materials();
            TextBox destination = CloneInteractionFixture.Field<TextBox>(clone, "CloneDestination");
            bool clearSelectionOnLostFocus = destination.ClearSelectionOnLostFocus;
            string prefix = rawParent + "lit ";
            CloneInteractionFixture.Edit(clone, destination, prefix);
            await CloneInteractionFixture.Suggested(clone, destination, directories[0], directories[0][prefix.Length..]);
            Popup popup = CloneInteractionFixture.Field<Popup>(clone, "CloneDirectoryCompletionPopup");
            CloneInteractionFixture.Pump(clone);
            Assert.That(popup.IsOpen, Is.True);
            ListBox suggestions = CloneInteractionFixture.Suggestions(popup);
            Assert.That(suggestions.Items.Cast<string>(), Is.EqualTo(directories));
            Assert.That(suggestions.Items.Cast<string>(), Does.Not.Contain(neighbor));
            Assert.That(destination.Text, Is.EqualTo(directories[0]));
            Assert.That(destination.SelectedText, Is.EqualTo(directories[0][prefix.Length..]));
            Assert.That(destination.SelectionStart, Is.EqualTo(prefix.Length));
            Assert.That(destination.SelectionEnd, Is.EqualTo(directories[0].Length));
            string literal = directories.Single(path => path.Contains('\n'));
            ListBoxItem row = suggestions.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.Content as string == literal);
            TextBlock caption = row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == ExportDialogControls.DisplayPath(Path.GetFileName(literal)));
            Assert.That(caption.Text, Does.Contain("\\n\\t").And.Not.Contain("\n").And.Not.Contain("\t"));
            Assert.That(ToolTip.GetTip(caption), Is.EqualTo(ExportDialogControls.DisplayPath(literal)));
            Assert.That(caption.IsEffectivelyVisible && caption.Bounds.Width > 0, Is.True);
            Control popupBody = popup.Child!;
            Assert.That(popupBody.Bounds.Width, Is.EqualTo(Math.Min(450, destination.Bounds.Width)).Within(2));
            Assert.That(popupBody.Bounds.Width, Is.LessThanOrEqualTo(450));
            Assert.That(popupBody.PointToScreen(default).X, Is.EqualTo(destination.PointToScreen(default).X).Within(2),
                "The autocomplete list aligns with its destination input and leaves the labels visible.");
            Assert.That(popupBody.PointToScreen(default).Y,
                Is.GreaterThanOrEqualTo(destination.PointToScreen(new Point(0, destination.Bounds.Height)).Y - 2));
            CloneInteractionFixture.Capture(clone, "clone-directory-completion-" + theme);
            if (caption.GetVisualRoot() is TopLevel popupRoot && popupRoot != clone)
            { CloneInteractionFixture.Capture(popupRoot, "clone-directory-completion-popup-" + theme); }
            PushProcessTestDriver.Pointer(row);
            Assert.That(suggestions.SelectedItem, Is.EqualTo(literal), "The actual pointed row supplies the accepted raw path.");
            Assert.That(destination.Text, Is.EqualTo(literal), "The pointer accepts the full literal path, including whitespace and shell metacharacters.");
            Assert.That(popup.IsOpen, Is.False); Assert.That(destination.SelectedText, Is.Empty);
            Assert.That(destination.ClearSelectionOnLostFocus, Is.EqualTo(clearSelectionOnLostFocus));
            Assert.That(clone.LastProcess, Is.Null); Assert.That(clone.CreatedRepository, Is.Null);
            f.SameHistories(histories); Assert.That(f.Materials(), Is.EqualTo(materials)); f.NoTransport();
        }
    }

    [AvaloniaTest]
    public async Task ContinuousTypingReplacesTheSuggestedSuffixAndBackspaceOrDeleteDoesNotReappendIt()
    {
        await using CloneInteractionFixture f = await CloneInteractionFixture.Create();
        string first = Path.Combine(f.Root, "alpha one"), second = Path.Combine(f.Root, "alpha two");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        CloneDialog clone = await f.Show(); TextBox destination = CloneInteractionFixture.Field<TextBox>(clone, "CloneDestination");
        string prefix = f.Root + Path.DirectorySeparatorChar + "alpha ";
        CloneInteractionFixture.Edit(clone, destination, prefix);
        await CloneInteractionFixture.Suggested(clone, destination, first, "one");
        Assert.That(destination.Text, Is.EqualTo(first)); Assert.That(destination.SelectedText, Is.EqualTo("one"));
        CloneInteractionFixture.Text(clone, "t", PhysicalKey.T);
        await CloneInteractionFixture.Suggested(clone, destination, second, "wo");
        Assert.That(destination.Text, Is.EqualTo(second)); Assert.That(destination.SelectedText, Is.EqualTo("wo"));
        CloneInteractionFixture.Text(clone, "w", PhysicalKey.W);
        await CloneInteractionFixture.Suggested(clone, destination, second, "o");
        Assert.That(destination.Text, Is.EqualTo(second)); Assert.That(destination.SelectedText, Is.EqualTo("o"));
        CloneInteractionFixture.Press(clone, PhysicalKey.Backspace);
        Assert.That(destination.Text, Is.EqualTo(prefix + "tw")); Assert.That(destination.SelectedText, Is.Empty);
        CloneInteractionFixture.Text(clone, "o", PhysicalKey.O);
        await CloneInteractionFixture.Suggested(clone, destination, second, "");
        Assert.That(destination.Text, Is.EqualTo(second)); Assert.That(destination.SelectedText, Is.Empty);
        CloneInteractionFixture.Edit(clone, destination, prefix);
        await CloneInteractionFixture.Suggested(clone, destination, first, "one");
        Assert.That(destination.SelectedText, Is.EqualTo("one"));
        CloneInteractionFixture.Press(clone, PhysicalKey.Delete);
        Assert.That(destination.Text, Is.EqualTo(prefix)); Assert.That(destination.SelectedText, Is.Empty);
        Assert.That(clone.IsVisible && !clone.IsBusy, Is.True); Assert.That(clone.LastProcess, Is.Null);
        f.NoTransport();
    }

    [AvaloniaTest]
    public async Task DestinationKeyboardPreviewAcceptsWithEnterOrTabAndEscapeRestoresTheTypedPrefixWithoutCloning()
    {
        await using CloneInteractionFixture f = await CloneInteractionFixture.Create();
        string[] paths = new[] { "key first", "key second", "key third" }.Select(name => Path.Combine(f.Root, name)).ToArray();
        foreach (string path in paths) { Directory.CreateDirectory(path); }
        CloneDialog clone = await f.Show(); TextBox destination = CloneInteractionFixture.Field<TextBox>(clone, "CloneDestination");
        string prefix = f.Root + Path.DirectorySeparatorChar + "key ";
        foreach (PhysicalKey accept in new[] { PhysicalKey.Enter, PhysicalKey.Tab })
        {
            CloneInteractionFixture.Edit(clone, destination, prefix);
            await CloneInteractionFixture.Suggested(clone, destination, paths[0], paths[0][prefix.Length..]);
            Popup popup = CloneInteractionFixture.Field<Popup>(clone, "CloneDirectoryCompletionPopup");
            CloneInteractionFixture.Press(clone, PhysicalKey.ArrowDown);
            Assert.That(destination.Text, Is.EqualTo(paths[1]));
            CloneInteractionFixture.Press(clone, PhysicalKey.ArrowDown);
            Assert.That(destination.Text, Is.EqualTo(paths[2]));
            CloneInteractionFixture.Press(clone, PhysicalKey.ArrowUp);
            Assert.That(destination.Text, Is.EqualTo(paths[1]));
            CloneInteractionFixture.Press(clone, accept);
            Assert.That(popup.IsOpen, Is.False); Assert.That(destination.Text, Is.EqualTo(paths[1]));
            Assert.That(destination.SelectedText, Is.Empty);
            Assert.That(clone.IsVisible && !clone.IsBusy, Is.True); Assert.That(clone.LastProcess, Is.Null);
            Assert.That(PushProcessTestDriver.Windows(clone), Is.Empty);
        }
        CloneInteractionFixture.Edit(clone, destination, prefix);
        await CloneInteractionFixture.Suggested(clone, destination, paths[0], paths[0][prefix.Length..]);
        CloneInteractionFixture.Press(clone, PhysicalKey.ArrowDown);
        Assert.That(destination.Text, Is.EqualTo(paths[1]));
        CloneInteractionFixture.Press(clone, PhysicalKey.Escape);
        Assert.That(destination.Text, Is.EqualTo(prefix)); Assert.That(destination.SelectedText, Is.Empty);
        Assert.That(CloneInteractionFixture.Field<Popup>(clone, "CloneDirectoryCompletionPopup").IsOpen, Is.False);
        Assert.That(clone.IsVisible, Is.True, "Escape dismisses the directory suggestion before the parent Clone window.");
        Assert.That(clone.LastProcess, Is.Null); Assert.That(clone.CreatedRepository, Is.Null);
        foreach (string gesture in new[] { "caret", "selection", "focus" })
        {
            await clone.DirectoryCompletionIdle.WaitAsync(TimeSpan.FromSeconds(20));
            PushProcessTestDriver.Pointer(destination);
            CloneInteractionFixture.Press(clone, PhysicalKey.A, RawInputModifiers.Control);
            TaskCompletionSource<(string Prefix, string[] Paths)> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            clone.DirectoryCompletionBeforeApply = (typed, actualPaths) =>
            {
                ready.TrySetResult((typed, actualPaths.ToArray()));
                return release.Task;
            };
            try
            {
                CloneInteractionFixture.Text(clone, prefix);
                await CloneInteractionFixture.Until(() => ready.Task.IsCompleted);
                (string actualPrefix, string[] actualPaths) = await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.That(actualPrefix, Is.EqualTo(prefix));
                Assert.That(actualPaths, Is.EqualTo(paths), "The held response contains paths enumerated from the three actual local directories.");
                Assert.That(clone.IsDirectoryCompletionPending, Is.True, "Completion pending after actual enumeration.");
                Task lateResponse = clone.DirectoryCompletionIdle;
                string SelectionState() => "gesture=" + gesture + " focus=" + destination.IsKeyboardFocusWithin
                    + " caret=" + destination.CaretIndex + " start=" + destination.SelectionStart + " end=" + destination.SelectionEnd
                    + " selected=" + ExportDialogControls.DisplayPath(destination.SelectedText) + " textLength=" + destination.Text?.Length;
                TestContext.WriteLine("Before input: " + SelectionState());
                Assert.That(destination.IsKeyboardFocusWithin, Is.True, SelectionState());
                Assert.That(destination.CaretIndex, Is.EqualTo(prefix.Length), SelectionState());
                Assert.That(destination.SelectionStart, Is.EqualTo(prefix.Length), SelectionState());
                Assert.That(destination.SelectionEnd, Is.EqualTo(prefix.Length), SelectionState());
                Assert.That(destination.SelectedText, Is.Empty, SelectionState());
                if (gesture == "focus")
                {
                    CloneInteractionFixture.Press(clone, PhysicalKey.Tab);
                    TestContext.WriteLine("Focus left: " + SelectionState());
                    Assert.That(destination.IsKeyboardFocusWithin, Is.False, SelectionState());
                    CloneInteractionFixture.Press(clone, PhysicalKey.Tab, RawInputModifiers.Shift);
                    Assert.That(destination.IsKeyboardFocusWithin, Is.True, SelectionState());
                }
                else
                {
                    RawInputModifiers modifiers = gesture == "selection" ? RawInputModifiers.Shift : RawInputModifiers.None;
                    CloneInteractionFixture.Press(clone, PhysicalKey.ArrowLeft, modifiers);
                    // Shift changes the active selection endpoint, retaining CaretIndex at its anchor.
                    // An ordinary Left moves CaretIndex and both collapsed selection endpoints.
                    Assert.That(destination.CaretIndex, Is.EqualTo(gesture == "selection" ? prefix.Length : prefix.Length - 1), SelectionState());
                    Assert.That(destination.SelectionStart, Is.EqualTo(gesture == "selection" ? prefix.Length : prefix.Length - 1), SelectionState());
                    Assert.That(destination.SelectionEnd, Is.EqualTo(prefix.Length - 1), SelectionState());
                    Assert.That(destination.SelectedText, Is.EqualTo(gesture == "selection" ? " " : ""), SelectionState());
                }
                TestContext.WriteLine("After input: " + SelectionState());
                Assert.That(destination.Text, Is.EqualTo(prefix), SelectionState());
                int caret = destination.CaretIndex, start = destination.SelectionStart, end = destination.SelectionEnd;
                release.TrySetResult();
                await CloneInteractionFixture.Until(() => lateResponse.IsCompleted);
                await lateResponse.WaitAsync(TimeSpan.FromSeconds(2));
                CloneInteractionFixture.Pump(clone);
                TestContext.WriteLine("After completion: " + SelectionState());
                Assert.That(CloneInteractionFixture.Field<Popup>(clone, "CloneDirectoryCompletionPopup").IsOpen, Is.False,
                    "The old filesystem response must stay dismissed after " + gesture + " changes, even when focus returns.");
                Assert.That(destination.Text, Is.EqualTo(prefix), SelectionState()); Assert.That(destination.CaretIndex, Is.EqualTo(caret), SelectionState());
                Assert.That(destination.SelectionStart, Is.EqualTo(start), SelectionState()); Assert.That(destination.SelectionEnd, Is.EqualTo(end), SelectionState());
                Assert.That(clone.IsDirectoryCompletionPending, Is.False); Assert.That(clone.LastProcess, Is.Null);
            }
            finally
            {
                clone.DirectoryCompletionBeforeApply = null;
                release.TrySetResult();
                Task idle = clone.DirectoryCompletionIdle;
                await CloneInteractionFixture.Until(() => idle.IsCompleted);
                await idle.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        Assert.That(clone.CreatedRepository, Is.Null); f.NoTransport();
    }

    [AvaloniaTest]
    public async Task TypingSourceOrDestinationDoesNotRequestRemoteBranchesOrVisitEitherHistory()
    {
        await using CloneInteractionFixture f = await CloneInteractionFixture.Create();
        CloneDialog clone = await f.Show();
        Dictionary<string, byte[]?> histories = f.Histories(); string materials = f.Materials();
        string typedSource = "ssh://source-edit-only.invalid/ quoted;$(literal) 雪.git";
        CloneInteractionFixture.Edit(clone, CloneInteractionFixture.Field<TextBox>(clone, "CloneSource"), typedSource);
        Assert.That(CloneInteractionFixture.Field<TextBox>(clone, "CloneSource").Text, Is.EqualTo(typedSource));
        Popup popup = CloneInteractionFixture.Field<Popup>(clone, "CloneDirectoryCompletionPopup");
        Assert.That(popup.IsOpen, Is.False, "Source MRU is independent of local destination autocomplete.");
        string destination = Path.Combine(f.Root, " only destination '\";$(literal)\n雪 ");
        Directory.CreateDirectory(destination);
        string prefix = Path.Combine(f.Root, " only dest");
        CloneInteractionFixture.Edit(clone, CloneInteractionFixture.Field<TextBox>(clone, "CloneDestination"), prefix);
        await CloneInteractionFixture.Suggested(clone, CloneInteractionFixture.Field<TextBox>(clone, "CloneDestination"), destination, destination[prefix.Length..]);
        Assert.That(CloneInteractionFixture.Field<TextBox>(clone, "CloneDestination").Text, Is.EqualTo(destination));
        Assert.That(clone.IsBranchLoading, Is.False); Assert.That(clone.BranchLoadFailures, Is.Empty);
        Assert.That(CloneInteractionFixture.Field<ComboBox>(clone, "CloneBranches").Items.Count, Is.EqualTo(2));
        Assert.That(clone.LastProcess, Is.Null); Assert.That(clone.CreatedRepository, Is.Null);
        f.SameHistories(histories); Assert.That(f.Materials(), Is.EqualTo(materials)); f.NoTransport();
    }

    [AvaloniaTest]
    public async Task AFileCannotBeEnumeratedAsADirectoryAndANewerValidEditClearsTheCompletionError()
    {
        await using CloneInteractionFixture f = await CloneInteractionFixture.Create();
        string parent = Path.Combine(f.Root, "parent is a file '\";$(literal)");
        byte[] bytes = [0, 1, 0xFF, 10]; await File.WriteAllBytesAsync(parent, bytes);
        string valid = Path.Combine(f.Root, "valid directory"); Directory.CreateDirectory(valid);
        CloneDialog clone = await f.Show(); TextBox destination = CloneInteractionFixture.Field<TextBox>(clone, "CloneDestination");
        Dictionary<string, byte[]?> histories = f.Histories(); string materials = f.Materials();
        CloneInteractionFixture.Edit(clone, destination, parent + Path.DirectorySeparatorChar + "child");
        Popup popup = CloneInteractionFixture.Field<Popup>(clone, "CloneDirectoryCompletionPopup");
        TextBlock error = popup.Child!.GetLogicalDescendants().OfType<TextBlock>().Single(control => control.Name == "CloneDirectoryCompletionError");
        await CloneInteractionFixture.Until(() => popup.IsOpen && error.IsVisible && !string.IsNullOrEmpty(error.Text));
        Assert.That(CloneInteractionFixture.Suggestions(popup).Items, Is.Empty);
        Assert.That(destination.Text, Is.EqualTo(parent + Path.DirectorySeparatorChar + "child"));
        Assert.That(clone.LastError, Is.Null); Assert.That(clone.LastProcess, Is.Null); Assert.That(clone.IsVisible && !clone.IsBusy, Is.True);
        string prefix = Path.Combine(f.Root, "valid ");
        CloneInteractionFixture.Edit(clone, destination, prefix);
        await CloneInteractionFixture.Suggested(clone, destination, valid, valid[prefix.Length..]);
        Assert.That(error.IsVisible, Is.False); Assert.That(File.ReadAllBytes(parent), Is.EqualTo(bytes));
        f.SameHistories(histories); Assert.That(f.Materials(), Is.EqualTo(materials)); f.NoTransport();
    }

    [AvaloniaTest]
    public async Task CloneHelpUsesTheVersionedManualAndFullHistoryTooltipUsesItsContextualFrenchTranslationInBothThemes()
    {
        const string english = "The default Git behavior is to download all historical revisions.\n"
            + "If you turn this off, we'll only download the latest revision for all branches.\n\n"
            + "Actual command line (if unchecked): --depth 1 --no-single-branch";
        const string french = "Le fonctionnement par défaut de Git est de télécharger toutes les versions de l'historique.\n"
            + "Si cette fonction est désactivée, seule la dernière version sera téléchargée pour chacune des branches.\n\n"
            + "Ligne de commande actuelle (si décoché) : --depth 1 --no-single-branch";
        await using CloneInteractionFixture f = await CloneInteractionFixture.Create();
        Dictionary<string, byte[]?> histories = f.Histories();
        Uri expected = new("https://git-extensions-documentation.readthedocs.io/en/release-7.2/dashboard.html#clone-repository");
        Assert.That(CloneDialog.HelpUri, Is.EqualTo(expected));
        foreach ((string culture, string tooltip) in new[] { ("en", english), ("fr-FR", french) })
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                List<Uri> opened = [];
                CloneDialog clone = await f.Show(theme: theme, openHelp: opened.Add);
                CheckBox history = CloneInteractionFixture.Field<CheckBox>(clone, "CloneFullHistory");
                Assert.That(ToolTip.GetTip(history), Is.EqualTo(tooltip));
                Assert.That(history.IsChecked, Is.True);
                Button help = CloneInteractionFixture.Field<Button>(clone, "CloneHelp");
                Assert.That(help.IsDefault || help.IsCancel, Is.False);
                PushProcessTestDriver.Pointer(help);
                Assert.That(opened, Is.EqualTo(new[] { expected }));
                PushProcessTestDriver.Pointer(CloneInteractionFixture.Field<TextBox>(clone, "CloneSource"));
                CloneInteractionFixture.Press(clone, PhysicalKey.F1);
                Assert.That(opened, Is.EqualTo(new[] { expected, expected }), "F1 routes to the same contextual help as the actual ? button.");
                Assert.That(clone.IsVisible && !clone.IsBusy, Is.True); Assert.That(clone.LastProcess, Is.Null);
                Assert.That(history.IsChecked, Is.True); f.SameHistories(histories); f.NoTransport();
                CloneInteractionFixture.Capture(clone, "clone-help-" + culture + "-" + theme);
                clone.Close(); await CloneInteractionFixture.Until(() => !clone.IsVisible);
            }
        }
    }
}

/// <summary>Owns all Git repositories, preferences, environment and transport actors used by the Clone interaction tests.</summary>
internal sealed class CloneInteractionFixture : IAsyncDisposable
{
    private readonly Dictionary<string, string?> _environment = [];
    private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
    private readonly List<Window> _windows = [];
    private readonly List<CloneBranchGate> _gates = [];
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-clone-input-" + Guid.NewGuid().ToString("N"));
    internal GitRepository Source { get; private set; } = null!;
    internal GitRepository Other { get; private set; } = null!;
    internal string FileSource => new Uri(Source.Path).AbsoluteUri;
    internal string OtherFileSource => new Uri(Other.Path).AbsoluteUri;
    internal string SshSource => "ssh://private-clone-branches.invalid/owned-local.git";
    internal string Trace => Path.Combine(Root, "native-git.trace");

    private CloneInteractionFixture()
    {
        Directory.CreateDirectory(Root);
        foreach ((string key, string? value) in new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = Path.Combine(Root, "profile"), ["GIT_CONFIG_GLOBAL"] = "/dev/null", ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_OPTIONAL_LOCKS"] = "0", ["GIT_ALLOW_PROTOCOL"] = "file:ssh", ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_SSH_COMMAND"] = null, ["GIT_SSH"] = null, ["GIT_TRACE"] = Trace
        }) { Set(key, value); }
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    private void Set(string key, string? value)
    { _environment.TryAdd(key, Environment.GetEnvironmentVariable(key)); Environment.SetEnvironmentVariable(key, value); }

    internal static async Task<CloneInteractionFixture> Create()
    {
        CloneInteractionFixture f = new();
        try
        {
            f.Source = await f.Seed("source '\";$(literal) 雪", "topic-original");
            f.Other = await f.Seed("other source", "topic-new");
            new CloneSourcesStore().Visit(f.FileSource, 30);
            new RepositoryHistoryStore().Save(new() { Recent = [new(f.Source.Path)] });
            new ClonePreferencesStore().Save(new(f.Root, false));
            new ProcessDialogPreferencesStore().Save(new(false));
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }

    private async Task<GitRepository> Seed(string name, string branch)
    {
        GitRepository repository = await GitRepository.InitAsync(Path.Combine(Root, name), new GitInitOptions("main"));
        await repository.SetConfigAsync("user.name", "Clone input UI");
        await repository.SetConfigAsync("user.email", "clone-input@example.invalid");
        await repository.SetConfigAsync("commit.gpgsign", "false");
        await repository.SetConfigAsync("core.hooksPath", "/dev/null");
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "seed.txt"), name + "\n");
        await repository.StageAsync(["seed.txt"]); await repository.CommitAsync("Private clone input seed");
        await repository.CreateBranchAsync(branch, checkout: false); return repository;
    }

    internal Window Owner()
    { Window owner = new() { Width = 900, Height = 700 }; _windows.Add(owner); owner.Show(); Pump(owner); return owner; }

    internal async Task<CloneDialog> Show(string? source = null, ThemeVariant? theme = null, Window? owner = null, Action<Uri>? openHelp = null)
    {
        CloneDialog clone = new(Root, source ?? FileSource, openHelp: openHelp) { RequestedThemeVariant = theme ?? ThemeVariant.Light };
        _windows.Add(clone);
        if (owner is null) { clone.Show(); } else { _ = clone.ShowDialog(owner); }
        await clone.InitializeAsync(); Pump(clone);
        Field<TextBox>(clone, "CloneSubdirectory").Text = "created destination";
        return clone;
    }

    internal string Materials() => Tree(Source.Path) + Tree(Other.Path);
    private static string Tree(string root) => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    internal Dictionary<string, byte[]?> Histories() => new[] { new CloneSourcesStore().FilePath, new RepositoryHistoryStore().FilePath,
        Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "clone-preferences.json") }
        .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
    internal void SameHistories(Dictionary<string, byte[]?> before)
    { foreach ((string path, byte[]? bytes) in before) { Assert.That(File.Exists(path), Is.EqualTo(bytes is not null), path); if (bytes is not null) { Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes), path); } } }
    internal void NoTransport()
    {
        string trace = File.Exists(Trace) ? File.ReadAllText(Trace) : "";
        Assert.That(trace, Does.Not.Match(@"(?:built-in: git |exec: git-)(?:ls-remote|fetch|clone|fetch-pack|upload-pack|remote-https)\b"));
        Assert.That(trace, Does.Not.Contain("git-upload-pack").And.Not.Contain("git-remote-https"));
    }

    internal async Task<CloneBranchGate> Gate()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
        string directory = Path.Combine(Root, "owned branch transport");
        Directory.CreateDirectory(directory);
        string helper = Path.Combine(directory, "local ssh");
        await File.WriteAllTextAsync(helper, "#!/bin/sh\ncase \" $* \" in *private-clone-branches.invalid*) ;; *) exit 89 ;; esac\n"
            + "sleep 60 &\nchild=$!\ntrap 'kill \"$child\" 2>/dev/null || :; wait \"$child\" 2>/dev/null || :' 0\n"
            + "directory=" + Quote(directory) + "\nmarker=\"$directory/$$.actors\"\nprintf '%s\\n' \"$@\" > \"$marker.args\"\n"
            + "for older in \"$directory\"/*.actors; do\n"
            + "test -f \"$older\" || continue\nread helper_pid git_pid sleeper_pid < \"$older\"\n"
            + "for actor in \"$helper_pid\" \"$git_pid\" \"$sleeper_pid\"; do\n"
            + "if test -r \"/proc/$actor/stat\"; then cat \"/proc/$actor/stat\" 2>/dev/null || :; fi\ndone\ndone > \"$marker.before\"\n"
            + "printf '%s %s %s\\n' \"$$\" \"$PPID\" \"$child\" > \"$marker.tmp\"\nmv \"$marker.tmp\" \"$marker\"\n"
            + "while test ! -f \"$marker.release\"; do sleep 0.02; done\n"
            + "kill \"$child\" 2>/dev/null || :\nwait \"$child\" 2>/dev/null || :\ntrap - 0\n"
            + "exec /usr/bin/git upload-pack " + Quote(Source.Path) + "\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Set("GIT_SSH", helper); Set("GIT_SSH_COMMAND", null); Set("GIT_SSH_VARIANT", "ssh");
        CloneBranchGate gate = new(directory); _gates.Add(gate); return gate;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    internal static T Field<T>(TopLevel owner, string name) where T : Control
        => owner.GetVisualDescendants().OfType<Control>().Concat(owner.GetLogicalDescendants().OfType<Control>()).Distinct().OfType<T>().Single(control => control.Name == name);
    internal static ListBox Suggestions(Popup popup)
        => (popup.Child as ListBox) ?? popup.Child!.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "CloneDirectorySuggestions");
    internal static void Pump(TopLevel owner)
    { Dispatcher.UIThread.RunJobs(); owner.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    internal static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    internal static void Text(Window window, string text, PhysicalKey key = PhysicalKey.A)
    { window.KeyPressQwerty(key, RawInputModifiers.None); window.KeyTextInput(text); if (window.IsVisible) { window.KeyReleaseQwerty(key, RawInputModifiers.None); } Pump(window); }
    internal static void Edit(Window window, TextBox field, string text)
    { PushProcessTestDriver.Pointer(field); Press(window, PhysicalKey.A, RawInputModifiers.Control); Text(window, text); }
    internal static async Task Suggested(CloneDialog clone, TextBox editor, string path, string suffix)
    {
        Popup popup = Field<Popup>(clone, "CloneDirectoryCompletionPopup");
        await Until(() => popup.IsOpen && editor.Text == path && editor.SelectedText == suffix,
            "Wait for the actual filesystem suggestion and selected suffix.");
        Pump(clone);
        if (popup.Child?.GetVisualRoot() is TopLevel root && root != clone) { Pump(root); }
    }
    internal static async Task Until(Func<bool> condition, string? because = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20))
        { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, because);
    }
    internal static void Capture(TopLevel owner, string name)
    {
        Pump(owner); using var frame = owner.CaptureRenderedFrame() ?? throw new AssertionException("No Clone interaction frame.");
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name + ".png"); frame.Save(path); TestContext.AddTestAttachment(path);
    }
    public async ValueTask DisposeAsync()
    {
        bool drained = false;
        try
        {
            foreach (Window window in _windows.AsEnumerable().Reverse()) { if (window.IsVisible) { window.Close(); } }
            if (_windows.Count > 0)
            {
                await Until(() => _windows.All(window => !window.IsVisible && (window is not CloneDialog clone || !clone.IsBusy && !clone.IsBranchLoading)));
            }
            foreach (CloneDialog clone in _windows.OfType<CloneDialog>())
            {
                Task idle = clone.DirectoryCompletionIdle;
                await Until(() => idle.IsCompleted);
                await idle.WaitAsync(TimeSpan.FromSeconds(2));
            }
            await Until(() => _gates.All(gate => gate.AllActorsStopped()));
            drained = true;
        }
        finally
        {
            Dispatcher.UIThread.RunJobs();
            foreach ((string key, string? value) in _environment) { Environment.SetEnvironmentVariable(key, value); }
            CultureInfo.CurrentUICulture = _culture;
            if (drained && Directory.Exists(Root)) { Directory.Delete(Root, true); }
        }
    }
}

internal sealed class CloneBranchGate(string directory)
{
    private readonly Dictionary<int, CloneBranchActor> _observed = [];
    internal string[] Markers => Directory.GetFiles(directory, "*.actors").Order(StringComparer.Ordinal).ToArray();
    internal CloneBranchActor[] Actors(string marker)
    {
        int[] ids = File.ReadAllText(marker).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        Assert.That(ids, Has.Length.EqualTo(3));
        CloneBranchActor[] actors = ids.Select(CloneBranchActor.Observe).ToArray();
        foreach (CloneBranchActor actor in actors) { _observed[actor.Pid] = actor; }
        Assert.That(actors.All(actor => actor.Alive()), Is.True, "The witness must capture live Git, helper and sleeper identities.");
        Assert.That(actors.Select(actor => actor.ProcessGroup).Distinct().Count(), Is.EqualTo(1));
        return actors;
    }
    internal Task Release(string marker) => File.WriteAllTextAsync(marker + ".release", "release\n");
    internal void PriorActorsStoppedAtEntry(string marker, IReadOnlyList<CloneBranchActor> expected)
    {
        foreach (string value in File.ReadAllLines(marker + ".before"))
        {
            int pid = int.Parse(value[..value.IndexOf(' ')], CultureInfo.InvariantCulture);
            string[] fields = value[(value.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            CloneBranchActor? old = expected.SingleOrDefault(actor => actor.Pid == pid && actor.Start == fields[19]);
            Assert.That(old is not null && fields[0] is not ("Z" or "X" or "x"), Is.False,
                "The real new transport observed an older owned actor still running at entry: " + value);
        }
    }
    internal bool AllActorsStopped() => _observed.Values.All(actor => !actor.Alive());
}

internal sealed record CloneBranchActor(int Pid, string Start, string ProcessGroup)
{
    internal static CloneBranchActor Observe(int pid)
    {
        string value = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
        string[] fields = value[(value.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new(pid, fields[19], fields[2]);
    }
    internal bool Alive()
    {
        try
        {
            string path = "/proc/" + Pid.ToString(CultureInfo.InvariantCulture) + "/stat";
            if (!File.Exists(path)) { return false; }
            string value = File.ReadAllText(path); string[] fields = value[(value.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields[19] == Start && fields[0] is not ("Z" or "X" or "x");
        }
        catch (IOException) { return false; }
    }
}
