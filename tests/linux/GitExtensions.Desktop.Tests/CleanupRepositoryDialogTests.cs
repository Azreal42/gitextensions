using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed class CleanupRepositoryDialogTests
{
    [AvaloniaTest] public Task OriginalChoicesLogAndKeyboardRenderInLight() => Original(false);
    [AvaloniaTest] public Task OriginalChoicesLogAndKeyboardRenderInDark() => Original(true);
    private static async Task Original(bool dark)
    {
        await using Fixture f = await Fixture.Create(); CleanupRepositoryDialog dialog = await f.Show(dark); string before = f.EveryByte();
        Assert.That(dialog.Title, Is.EqualTo("Clean working directory")); Assert.That(dialog.Width, Is.EqualTo(446)); Assert.That(dialog.Height, Is.EqualTo(591));
        Assert.That(dialog.LastResult, Is.Null, "Opening never starts an automatic preview.");
        Assert.That(dialog.ReadOptions().Mode, Is.EqualTo(CleanMode.All)); Assert.That(dialog.ReadOptions().RemoveDirectories, Is.True); Assert.That(dialog.ReadOptions().CleanSubmodules, Is.False);
        Assert.That(dialog.ReadOptions().IncludePaths, Is.Empty); Assert.That(dialog.ReadOptions().ExcludePaths, Is.Empty);
        Assert.That(Field<RadioButton>(dialog, "CleanupAll").IsKeyboardFocusWithin, Is.True);
        foreach (string name in new[] { "CleanupIncludePaths", "CleanupExcludePaths" }) { Assert.That(Field<TextBox>(dialog, name).IsEnabled, Is.False); }
        foreach (string name in new[] { "CleanupIncludeHint", "CleanupExcludeHint" }) { Assert.That(Field<TextBlock>(dialog, name).IsVisible, Is.False); }
        foreach (string name in new[] { "PreviewCleanup", "ExecuteCleanup" }) { Assert.That(Field<Button>(dialog, name).IsDefault, Is.False); }
        Assert.That(Field<Button>(dialog, "CloseCleanup").IsCancel, Is.True);
        Assert.That(Field<Button>(dialog, "AddCleanupInclude").IsEnabled, Is.True, "The picker can enable an initially disabled filter.");
        Assert.That(Field<Button>(dialog, "AddCleanupExclude").IsEnabled, Is.True);
        Assert.That(Field<Button>(dialog, "PreviewCleanup").GetVisualDescendants().OfType<Image>().Single().Tag, Is.EqualTo("Preview.png"));
        Assert.That(Field<Button>(dialog, "ExecuteCleanup").GetVisualDescendants().OfType<Image>().Single().Tag, Is.EqualTo("CleanupRepo.png"));
        await Pointer(Field<CheckBox>(dialog, "CleanupIncludeFilter")); TextBox input = Field<TextBox>(dialog, "CleanupIncludePaths");
        input.Text = "dir loose"; input.CaretIndex = input.Text.Length; Assert.That(input.Focus(), Is.True); Assert.That(input.IsKeyboardFocusWithin, Is.True);
        Press(dialog, PhysicalKey.Enter); Assert.That(input.Text, Does.Contain('\n')); Assert.That(dialog.LastResult, Is.Null);
        await Pointer(Field<CheckBox>(dialog, "CleanupIncludeFilter"));
        await Preview(dialog); Assert.That(dialog.LastResult!.Success, Is.True); Assert.That(dialog.HasRepositoryChanges, Is.False);
        TextBox log = Field<TextBox>(dialog, "CleanupLog"); Assert.That(log.IsReadOnly, Is.True); Assert.That(log.TextWrapping, Is.EqualTo(TextWrapping.NoWrap));
        Assert.That(ScrollViewer.GetHorizontalScrollBarVisibility(log), Is.EqualTo(ScrollBarVisibility.Auto)); Assert.That(ScrollViewer.GetVerticalScrollBarVisibility(log), Is.EqualTo(ScrollBarVisibility.Auto));
        Assert.That(log.Text, Is.EqualTo(Output(dialog.LastResult))); Assert.That(log.Text, Does.Contain("Would remove"));
        Geometry(dialog); ISolidColorBrush foreground = (ISolidColorBrush)log.Foreground!, background = (ISolidColorBrush)log.Background!;
        Assert.That(Contrast(foreground.Color, background.Color), Is.GreaterThanOrEqualTo(4.5));
        Assert.That(background.Color, Is.EqualTo(Color.Parse(dark ? "#323232" : "#ffffff")));
        Capture(dialog, "cleanup-original-" + (dark ? "dark" : "light") + ".png");
        Assert.That(f.EveryByte(), Is.EqualTo(before)); Assert.That(input.Focus(), Is.False, "The disabled multiline field cannot acquire focus.");
        Assert.That(log.Focus(), Is.True); Press(dialog, PhysicalKey.Escape, f.Owner); await Until(() => !dialog.IsVisible);
        Assert.That(f.EveryByte(), Is.EqualTo(before)); Assert.That(dialog.HasRepositoryChanges, Is.False);
    }

    [AvaloniaTest]
    public async Task FrenchCatalogueKeepsTheCompactControlsAndYesNoQuestionVisible()
    {
        await using Fixture f = await Fixture.Create(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr"); string before = f.EveryByte();
        foreach (bool dark in new[] { false, true })
        {
            CleanupRepositoryDialog dialog = await f.Show(dark);
            Assert.That(dialog.Title, Is.EqualTo("Nettoyer le répertoire de travail"));
            Assert.That(((AccessText)Field<RadioButton>(dialog, "CleanupNonIgnored").Content!).Text, Is.EqualTo("Retirer uniquement les fichiers non traqués non-ignorés"));
            foreach (string name in new[] { "AddCleanupInclude", "AddCleanupExclude" }) { Assert.That(((AccessText)Field<Button>(dialog, name).Content!).Text, Is.EqualTo("Ajouter un chemin…")); }
            await Pointer(Field<CheckBox>(dialog, "CleanupIncludeFilter")); await Pointer(Field<CheckBox>(dialog, "CleanupExcludeFilter"));
            await Preview(dialog); Geometry(dialog); Capture(dialog, "cleanup-original-french-" + (dark ? "dark" : "light") + ".png");
            Window question = await Question(dialog);
            Assert.That(question.Owner, Is.SameAs(dialog)); Assert.That(question.ActualThemeVariant, Is.EqualTo(dialog.ActualThemeVariant));
            Assert.That(question.Title, Is.EqualTo("Nettoyer")); Assert.That(Field<TextBlock>(question, "CleanupQuestion").Text, Is.EqualTo("Etes vous sûr de vouloir nettoyer le dépôt?"));
            Assert.That(Field<Button>(question, "ConfirmCleanup").IsDefault, Is.True); Assert.That(Field<Button>(question, "CancelCleanup").IsCancel, Is.True);
            Capture(question, "cleanup-question-french-" + (dark ? "dark" : "light") + ".png");
            await Pointer(Field<Button>(question, "CancelCleanup")); await Until(() => !dialog.IsBusy); Assert.That(dialog.HasRepositoryChanges, Is.False); dialog.Close();
        }
        Assert.That(f.EveryByte(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task PreviewAllThreeModesAndDirectoryChoicesMatchesNativeGitWithoutMutatingAnyByte()
    {
        await using Fixture f = await Fixture.Create(); CleanupRepositoryDialog dialog = await f.Show(); string before = f.EveryByte();
        foreach ((string radio, string? flag) in new[] { ("CleanupAll", "-x"), ("CleanupNonIgnored", (string?)null), ("CleanupIgnored", "-X") })
        {
            await Pointer(Field<RadioButton>(dialog, radio));
            foreach (bool directories in new[] { false, true })
            {
                await Set(dialog, "CleanupDirectories", directories); await Preview(dialog);
                List<string> arguments = ["clean"]; if (flag is not null) { arguments.Add(flag); } if (directories) { arguments.Add("-d"); } arguments.Add("--dry-run"); arguments.Add("--");
                GitResult oracle = await f.Git(arguments.ToArray()); GitCleanCommandResult actual = dialog.LastResult!.Commands.Single();
                Assert.That(actual.Arguments, Is.EqualTo(arguments)); Assert.That(actual.Arguments, Does.Not.Contain("-f"));
                Assert.That(actual.Result, Is.Not.Null); Assert.That(actual.StandardOutput, Is.EqualTo(oracle.StandardOutput)); Assert.That(actual.StandardError, Is.EqualTo(oracle.StandardError));
                Assert.That(actual.ExitCode, Is.EqualTo(oracle.ExitCode)); Assert.That(Field<TextBox>(dialog, "CleanupLog").Text, Is.EqualTo(oracle.StandardOutput + oracle.StandardError));
                Assert.That(dialog.LastResult.IsPreview, Is.True); Assert.That(dialog.LastResult.MutationStarted, Is.False); Assert.That(f.EveryByte(), Is.EqualTo(before));
            }
        }
    }

    [AvaloniaTest]
    public async Task NoEscapeAndClosingTheQuestionNeverStartCleanupOrClearTheLastPreview()
    {
        await using Fixture f = await Fixture.Create(); CleanupRepositoryDialog dialog = await f.Show(); await Preview(dialog); GitCleanResult preview = dialog.LastResult!; string before = f.EveryByte();
        foreach (int choice in new[] { 0, 1, 2 })
        {
            Window question = await Question(dialog); Assert.That(Field<Button>(question, "ConfirmCleanup").IsDefault, Is.True);
            if (choice == 0) { await Pointer(Field<Button>(question, "CancelCleanup")); }
            else if (choice == 1) { Press(question, PhysicalKey.Escape, dialog); } else { question.Close(); }
            await Until(() => !dialog.IsBusy); Assert.That(dialog.LastResult, Is.SameAs(preview)); Assert.That(Field<TextBox>(dialog, "CleanupLog").Text, Is.EqualTo(Output(preview)));
            Assert.That(f.EveryByte(), Is.EqualTo(before)); Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(dialog.IsVisible, Is.True);
        }
    }

    [AvaloniaTest]
    public async Task ConfirmedCleanupUsesCurrentGitPathsAndMatchesSingleForceNativeGitWhileKeepingTrackedChanges()
    {
        await using Fixture f = await Fixture.Create(); CleanupRepositoryDialog dialog = await f.Show(); await Preview(dialog);
        f.Write("created after preview λ.tmp", "new\n"); string invariant = f.Metadata(), twin = f.Twin();
        GitResult expected = await new GitRunner().RunAsync(twin, ["--no-pager", "-c", "color.ui=false", "-c", "core.quotepath=false", "clean", "-x", "-d", "-f", "--"]);
        Assert.That(expected.Success, Is.True, expected.StandardError);
        Window question = await Question(dialog); Press(question, PhysicalKey.Enter, dialog); await Until(() => !dialog.IsBusy);
        Assert.That(dialog.LastResult!.Success, Is.True); Assert.That(dialog.LastResult.MutationStarted, Is.True); Assert.That(dialog.LastResult.IsPreview, Is.False);
        Assert.That(dialog.LastResult.Commands.Single().Arguments.Count(argument => argument == "-f"), Is.EqualTo(1));
        Assert.That(f.Worktree(), Is.EqualTo(Fixture.Tree(twin, omitRootGit: true))); Assert.That(f.Metadata(), Is.EqualTo(invariant));
        Assert.That(System.IO.File.ReadAllText(f.Path("tracked.txt")), Is.EqualTo("dirty tracked\n")); Assert.That(System.IO.File.ReadAllText(f.Path("staged λ.txt")), Is.EqualTo("staged\n"));
        Assert.That(Directory.Exists(f.Path("nested-repo/.git")), Is.True, "A single force must not erase an untracked nested Git repository.");
        Assert.That(System.IO.File.Exists(f.Path("created after preview λ.tmp")), Is.False); Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.HasRepositoryChanges, Is.True);
        Assert.That(System.IO.File.ReadAllText(Path.Combine(f.Root, "outside.txt")), Is.EqualTo("outside\n"), "Cleaning a worktree symlink never deletes or changes its outside target.");
        Assert.That(Field<TextBox>(dialog, "CleanupLog").Text, Does.Contain("created after preview λ.tmp")); await Preview(dialog); Assert.That(dialog.LastResult!.IsPreview, Is.True);
    }

    [AvaloniaTest]
    public async Task RawMultilineIncludesAndExclusionsKeepPatternsSpacesAndDisabledValuesAsOriginalData()
    {
        await using Fixture f = await Fixture.Create(); CleanupRepositoryDialog dialog = await f.Show(); string before = f.EveryByte();
        await Set(dialog, "CleanupIncludeFilter", true); await Set(dialog, "CleanupExcludeFilter", true);
        Field<TextBox>(dialog, "CleanupIncludePaths").Text = "*.tmp\n\n--dash.tmp\n leading.tmp\nquote\"file.tmp";
        Field<TextBox>(dialog, "CleanupExcludePaths").Text = "Café libre λ.tmp\n\ndir loose/inside.tmp\nquote\\\"file.tmp";
        Assert.That(dialog.ReadOptions().IncludePaths, Is.EqualTo(new[] { "*.tmp", "", "--dash.tmp", " leading.tmp", "quote\"file.tmp" }));
        Assert.That(dialog.ReadOptions().ExcludePaths, Is.EqualTo(new[] { "Café libre λ.tmp", "", "dir loose/inside.tmp", "quote\\\"file.tmp" }));
        await Preview(dialog); string[] native = ["clean", "-x", "-d", "--dry-run", "--exclude=Café?libre?λ.tmp", "--exclude=dir?loose/inside.tmp", "--exclude=quote\\\"file.tmp", "--", "*.tmp", "--dash.tmp", " leading.tmp", "quote\"file.tmp"];
        GitResult oracle = await f.Git(native); Assert.That(dialog.LastResult!.Commands.Single().Arguments, Is.EqualTo(native)); Assert.That(Field<TextBox>(dialog, "CleanupLog").Text, Is.EqualTo(oracle.StandardOutput + oracle.StandardError));
        await Set(dialog, "CleanupIncludeFilter", false); await Set(dialog, "CleanupExcludeFilter", false); await Preview(dialog);
        Assert.That(dialog.LastResult!.Commands.Single().Arguments, Is.EqualTo(new[] { "clean", "-x", "-d", "--dry-run", "--" })); Assert.That(Field<TextBox>(dialog, "CleanupIncludePaths").Text, Does.Contain("quote\"file.tmp"));
        // Native -X treats excludes as ignore rules, so compare the actual Git decision instead of assuming protection.
        await Pointer(Field<RadioButton>(dialog, "CleanupIgnored")); await Set(dialog, "CleanupExcludeFilter", true); await Preview(dialog);
        oracle = await f.Git("clean", "-X", "-d", "--dry-run", "--exclude=Café?libre?λ.tmp", "--exclude=dir?loose/inside.tmp", "--exclude=quote\\\"file.tmp", "--");
        Assert.That(Field<TextBox>(dialog, "CleanupLog").Text, Is.EqualTo(oracle.StandardOutput + oracle.StandardError)); Assert.That(f.EveryByte(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task TheInitialPathPopulatesBothFiltersWithoutPreviewAndNoSettingsPersistToTheNextDialog()
    {
        await using Fixture f = await Fixture.Create(); string path = f.Path("dir loose"); CleanupRepositoryDialog dialog = await f.Show(path: path); string before = f.EveryByte();
        Assert.That(Field<CheckBox>(dialog, "CleanupIncludeFilter").IsChecked, Is.True); Assert.That(Field<CheckBox>(dialog, "CleanupExcludeFilter").IsChecked, Is.True);
        Assert.That(dialog.ReadOptions().IncludePaths, Is.EqualTo(new[] { path })); Assert.That(dialog.ReadOptions().ExcludePaths, Is.EqualTo(new[] { path })); Assert.That(dialog.LastResult, Is.Null);
        await Preview(dialog); GitResult native = await f.Git("clean", "-x", "-d", "--dry-run", "--exclude=" + path.Replace(' ', '?'), "--", path);
        Assert.That(Field<TextBox>(dialog, "CleanupLog").Text, Is.EqualTo(native.StandardOutput + native.StandardError));
        dialog.SetPathArgument(null); Assert.That(Field<TextBox>(dialog, "CleanupIncludePaths").Text, Is.Empty); Assert.That(dialog.ReadOptions().IncludePaths, Is.Empty);
        dialog.SetPathArgument(" "); Assert.That(dialog.ReadOptions().IncludePaths, Is.EqualTo(new[] { " " }), "Whitespace is not silently trimmed.");
        await Pointer(Field<RadioButton>(dialog, "CleanupIgnored")); dialog.Close(); dialog = await f.Show(); Assert.That(dialog.ReadOptions().Mode, Is.EqualTo(CleanMode.All)); Assert.That(dialog.ReadOptions().IncludePaths, Is.Empty); Assert.That(f.EveryByte(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task RealFolderAndFilePickerSelectionsAppendAbsoluteIncludesAndRelativeExcludesAndCancelIsInert()
    {
        await using Fixture f = await Fixture.Create(); CleanupRepositoryDialog dialog = await f.Show(); string before = f.EveryByte();
        Field<TextBox>(dialog, "CleanupIncludePaths").Text = "first"; Field<TextBox>(dialog, "CleanupExcludePaths").Text = "earlier";
        FolderPickerOpenOptions? folders = null; FilePickerOpenOptions? files = null; string? startLocation = null;
        dialog.FolderPicker = async options => { folders = options; startLocation = options.SuggestedStartLocation?.TryGetLocalPath(); return await Folder(dialog, f.Path("dir loose")); };
        dialog.FilePicker = async options => { files = options; return await File(dialog, f.Path("Café libre λ.tmp")); };
        await Pointer(Field<Button>(dialog, "AddCleanupInclude")); await Until(() => !dialog.IsBusy);
        Assert.That(dialog.LastError, Is.Null); Assert.That(Field<TextBox>(dialog, "CleanupIncludePaths").Text, Is.EqualTo("first\n" + f.Path("dir loose"))); Assert.That(Field<CheckBox>(dialog, "CleanupIncludeFilter").IsChecked, Is.True);
        await Pointer(Field<Button>(dialog, "AddCleanupExclude")); await Until(() => !dialog.IsBusy);
        Assert.That(dialog.LastError, Is.Null); Assert.That(Field<TextBox>(dialog, "CleanupExcludePaths").Text, Is.EqualTo("earlier\nCafé libre λ.tmp"));
        Assert.That(folders!.AllowMultiple, Is.False); Assert.That(files!.AllowMultiple, Is.False); Assert.That(startLocation, Is.EqualTo(f.Repository.Path));
        dialog.SetPathArgument(null); dialog.FolderPicker = _ => Task.FromResult<IStorageFolder?>(null); dialog.FilePicker = _ => Task.FromResult<IStorageFile?>(null);
        await Pointer(Field<Button>(dialog, "AddCleanupInclude")); await Until(() => !dialog.IsBusy); await Pointer(Field<Button>(dialog, "AddCleanupExclude")); await Until(() => !dialog.IsBusy);
        Assert.That(dialog.ReadOptions().IncludePaths, Is.Empty); Assert.That(dialog.ReadOptions().ExcludePaths, Is.Empty); Assert.That(Field<CheckBox>(dialog, "CleanupIncludeFilter").IsChecked, Is.False); Assert.That(f.EveryByte(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task PickersRejectPhysicalOutsideAndGitPathsButAcceptAnOutsideAliasBackIntoTheWorktree()
    {
        await using Fixture f = await Fixture.Create(); CleanupRepositoryDialog dialog = await f.Show();
        string outside = Path.Combine(f.Root, "repo-neighbor"), outsideAlias = f.Path("outside-link"), insideAlias = Path.Combine(f.Root, "inside-link"); Directory.CreateDirectory(outside);
        System.IO.File.WriteAllText(Path.Combine(outside, "outside.txt"), "outside\n"); Directory.CreateSymbolicLink(outsideAlias, outside); Directory.CreateSymbolicLink(insideAlias, f.Repository.Path);
        string before = f.EveryByte();
        foreach (string forbidden in new[] { outside, outsideAlias, f.Path(".git"), f.Path(".git/objects") })
        {
            dialog.FolderPicker = async _ => await Folder(dialog, forbidden); await Pointer(Field<Button>(dialog, "AddCleanupInclude")); await Until(() => !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Not.Null); Assert.That(Field<TextBox>(dialog, "CleanupIncludePaths").Text, Is.Null.Or.Empty); Assert.That(Field<CheckBox>(dialog, "CleanupIncludeFilter").IsChecked, Is.False);
        }
        foreach (string forbidden in new[] { Path.Combine(outside, "outside.txt"), Path.Combine(outsideAlias, "outside.txt"), f.Path(".git/config") })
        {
            dialog.FilePicker = async _ => await File(dialog, forbidden); await Pointer(Field<Button>(dialog, "AddCleanupExclude")); await Until(() => !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Not.Null); Assert.That(Field<TextBox>(dialog, "CleanupExcludePaths").Text, Is.Null.Or.Empty);
        }
        dialog.FolderPicker = async _ => await Folder(dialog, Path.Combine(insideAlias, "dir loose")); await Pointer(Field<Button>(dialog, "AddCleanupInclude")); await Until(() => !dialog.IsBusy);
        Assert.That(dialog.LastError, Is.Null); Assert.That(Field<TextBox>(dialog, "CleanupIncludePaths").Text, Is.EqualTo(f.Path("dir loose")));
        dialog.FilePicker = async _ => await File(dialog, Path.Combine(insideAlias, "Café libre λ.tmp")); await Pointer(Field<Button>(dialog, "AddCleanupExclude")); await Until(() => !dialog.IsBusy);
        Assert.That(dialog.LastError, Is.Null); Assert.That(Field<TextBox>(dialog, "CleanupExcludePaths").Text, Is.EqualTo("Café libre λ.tmp")); Assert.That(f.EveryByte(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ClosingTheOwnerDuringARealPendingPickerCannotAppendADeferredSelectionOrStartGit()
    {
        foreach (bool include in new[] { true, false })
        {
            await using Fixture f = await Fixture.Create(); CleanupRepositoryDialog dialog = await f.Show(); string before = f.EveryByte(); bool waiting = false;
            TaskCompletionSource<IStorageItem?> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (include) { dialog.FolderPicker = async _ => { waiting = true; return (IStorageFolder?)await gate.Task; }; }
            else { dialog.FilePicker = async _ => { waiting = true; return (IStorageFile?)await gate.Task; }; }
            await Pointer(Field<Button>(dialog, include ? "AddCleanupInclude" : "AddCleanupExclude")); await Until(() => waiting);
            TextBox includeText = Field<TextBox>(dialog, "CleanupIncludePaths"), excludeText = Field<TextBox>(dialog, "CleanupExcludePaths");
            IStorageItem chosen = include ? await Folder(dialog, f.Path("dir loose")) : await File(dialog, f.Path("loose.txt"));
            f.Owner.Close(); gate.SetResult(chosen); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            Assert.That(includeText.Text, Is.Null.Or.Empty); Assert.That(excludeText.Text, Is.Null.Or.Empty); Assert.That(dialog.LastResult, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(f.EveryByte(), Is.EqualTo(before));
        }
    }

    [AvaloniaTest]
    public async Task RecursivePreviewAndCleanupRetainNativeOrderAndOmitParentExclusionsFromChildren()
    {
        await using Fixture f = await Fixture.Create(); await f.Submodules(); CleanupRepositoryDialog dialog = await f.Show(); await Set(dialog, "CleanupSubmodules", true); await Set(dialog, "CleanupExcludeFilter", true);
        Field<TextBox>(dialog, "CleanupExcludePaths").Text = "keep.tmp"; f.Write("keep.tmp", "parent keep\n"); string before = f.EveryByte();
        await Preview(dialog); Assert.That(dialog.LastResult!.Commands.Select(command => command.Scope), Is.EqualTo(new[] { GitCleanCommandScope.Worktree, GitCleanCommandScope.Submodules }));
        GitResult nativeParent = await f.Git("clean", "-x", "-d", "--dry-run", "--exclude=keep.tmp", "--");
        GitResult nativeChildren = await f.Git("submodule", "foreach", "--recursive", "git --no-pager -c color.ui=false -c core.quotepath=false clean -x -d --dry-run --");
        Assert.That(Field<TextBox>(dialog, "CleanupLog").Text, Is.EqualTo(nativeParent.StandardOutput + nativeParent.StandardError + nativeChildren.StandardOutput + nativeChildren.StandardError)); Assert.That(f.EveryByte(), Is.EqualTo(before));
        await Set(dialog, "CleanupIncludeFilter", true); Field<TextBox>(dialog, "CleanupIncludePaths").Text = "*.tmp"; await Preview(dialog);
        nativeParent = await f.Git("clean", "-x", "-d", "--dry-run", "--exclude=keep.tmp", "--", "*.tmp");
        nativeChildren = await f.Git("submodule", "foreach", "--recursive", "git --no-pager -c color.ui=false -c core.quotepath=false clean -x -d --dry-run -- \"*.tmp\"");
        Assert.That(Field<TextBox>(dialog, "CleanupLog").Text, Is.EqualTo(nativeParent.StandardOutput + nativeParent.StandardError + nativeChildren.StandardOutput + nativeChildren.StandardError), "The same literal pathspec reaches native Git in each initialized child.");
        Assert.That(f.EveryByte(), Is.EqualTo(before)); await Set(dialog, "CleanupIncludeFilter", false);
        string metadata = f.Metadata(), twin = f.Twin(); GitRunner git = new();
        _ = await git.RunAsync(twin, ["clean", "-x", "-d", "-f", "--exclude=keep.tmp", "--"]);
        GitResult oracle = await git.RunAsync(twin, ["submodule", "foreach", "--recursive", "git --no-pager -c color.ui=false -c core.quotepath=false clean -x -d -f --"]); Assert.That(oracle.Success, Is.True, oracle.StandardError);
        Window question = await Question(dialog); await Pointer(Field<Button>(question, "ConfirmCleanup")); await Until(() => !dialog.IsBusy);
        Assert.That(dialog.LastResult!.Success, Is.True); Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(f.Worktree(), Is.EqualTo(Fixture.Tree(twin, true))); Assert.That(f.Metadata(), Is.EqualTo(metadata));
        Assert.That(System.IO.File.Exists(f.Path("keep.tmp")), Is.True); Assert.That(System.IO.File.Exists(f.Path("module/keep.tmp")), Is.False); Assert.That(System.IO.File.Exists(f.Path("module/nested/keep.tmp")), Is.False);
        Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.LastResult.Commands[1].Arguments.Last(), Does.Not.Contain("exclude"));
    }

    [AvaloniaTest]
    public async Task ARealChildCleanFailureKeepsParentEffectsAndNativeErrorVisible()
    {
        if (OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Linux filesystem-permission fixture."); }
        await using Fixture f = await Fixture.Create(); await f.Submodules(); CleanupRepositoryDialog dialog = await f.Show(); await Set(dialog, "CleanupSubmodules", true);
        string locked = f.Path("module/locked"); Directory.CreateDirectory(locked); System.IO.File.WriteAllText(Path.Combine(locked, "untracked.txt"), "cannot unlink\n");
        System.IO.File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            string metadata = f.Metadata(); Window question = await Question(dialog); await Pointer(Field<Button>(question, "ConfirmCleanup")); await Until(() => !dialog.IsBusy);
            Assert.That(dialog.LastResult!.Success, Is.False); Assert.That(dialog.LastResult.Commands[0].Success, Is.True); Assert.That(dialog.LastResult.Commands[1].Result, Is.Not.Null); Assert.That(dialog.LastResult.Commands[1].ExitCode, Is.Not.Null.And.Not.Zero);
            Assert.That(dialog.LastResult.Commands[1].StandardError, Is.Not.Null.And.Not.Empty); Assert.That(Field<TextBox>(dialog, "CleanupLog").Text, Is.EqualTo(Output(dialog.LastResult)));
            Assert.That(Field<TextBlock>(dialog, "OperationStatus").Text, Is.Not.Null.And.Not.Empty); Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(dialog.IsVisible, Is.True);
            Assert.That(System.IO.File.Exists(f.Path("loose.txt")), Is.False); Assert.That(System.IO.File.Exists(Path.Combine(locked, "untracked.txt")), Is.True); Assert.That(f.Metadata(), Is.EqualTo(metadata));
        }
        finally { System.IO.File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [AvaloniaTest] public Task ClosingBusyPreviewKillsTheActualRunnerAndPreservesEveryByte() => CancelDuring("preview");
    [AvaloniaTest] public Task ClosingBusyCleanupBeforeGitWritesStillMarksItsStartedMutation() => CancelDuring("cleanup");
    [AvaloniaTest] public Task ClosingBusyRecursiveCleanupKeepsParentEffectsAndNeverExecutesBlockedChildren() => CancelDuring("submodules");
    private static async Task CancelDuring(string stage)
    {
        await using Fixture f = await Fixture.Create(); if (stage == "submodules") { await f.Submodules(); }
        (GitRepository gated, string marker) = await f.Gated(stage); CleanupRepositoryDialog dialog = await f.Show(repository: gated); await Set(dialog, "CleanupSubmodules", true);
        string before = f.EveryByte(), metadata = f.Metadata();
        if (stage == "preview") { await Pointer(Field<Button>(dialog, "PreviewCleanup")); }
        else { Window question = await Question(dialog); await Pointer(Field<Button>(question, "ConfirmCleanup")); }
        await Until(() => System.IO.File.Exists(marker) && System.IO.File.ReadAllText(marker).Length != 0); int pid = int.Parse(System.IO.File.ReadAllText(marker), CultureInfo.InvariantCulture); Assert.That(dialog.IsBusy, Is.True);
        if (stage == "preview") { f.Owner.Close(); } else { await Pointer(Field<Button>(dialog, "CloseCleanup")); }
        await Until(() => !dialog.IsVisible && !dialog.IsBusy); await Until(() => Exited(pid));
        Assert.That(dialog.LastResult, Is.Not.Null); Assert.That(dialog.LastResult!.Cancelled, Is.True); Assert.That(dialog.LastResult.Success, Is.False);
        Assert.That(dialog.LastResult.MutationStarted, Is.EqualTo(stage != "preview")); Assert.That(dialog.HasRepositoryChanges, Is.EqualTo(stage != "preview"));
        Assert.That(dialog.LastResult.Commands.Last().Result, Is.Null); Assert.That(dialog.LastResult.Commands.Last().ExitCode, Is.Null);
        Assert.That(f.Metadata(), Is.EqualTo(metadata));
        if (stage == "submodules")
        { Assert.That(System.IO.File.Exists(f.Path("loose.txt")), Is.False); Assert.That(System.IO.File.Exists(f.Path("module/keep.tmp")), Is.True); Assert.That(dialog.LastResult.Commands[0].Success, Is.True); }
        else { Assert.That(f.EveryByte(), Is.EqualTo(before)); Assert.That(dialog.LastResult.Commands.Count, Is.EqualTo(1), "No recursive command starts after cancellation."); }
    }

    [AvaloniaTest]
    public async Task BrowseCommandsRoutesTheWorktreeRatherThanSelectedCommitAndDisablesBareAndBusy()
    {
        await using Fixture f = await Fixture.Create(); MainWindow main = new() { Width = 1440, Height = 900, RequestedThemeVariant = ThemeVariant.Dark }; f.Windows.Add(main); main.Show(); await main.OpenRepositoryAsync(f.Repository.Path); Pump(main);
        ListBox history = Field<ListBox>(main, "RevisionHistory"); history.SelectedIndex = history.ItemCount - 1; string before = f.EveryByte();
        MenuItem commands = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Commands");
        MenuItem cleanup = commands.Items.OfType<MenuItem>().Single(item => item.Name == "BrowseCleanupRepository"); Assert.That(cleanup.Tag, Is.True);
        Assert.That(((AccessText)cleanup.Header!).Text, Is.EqualTo("Clean _working directory…")); Assert.That(((Image)cleanup.Icon!).Tag, Is.EqualTo("CleanupRepo.png"));
        await Pointer(commands); await Pointer(cleanup); await Until(() => main.OwnedWindows.OfType<CleanupRepositoryDialog>().Any(dialog => dialog.IsVisible && !dialog.IsBusy));
        CleanupRepositoryDialog dialog = main.OwnedWindows.OfType<CleanupRepositoryDialog>().Single(); Assert.That(main.IsBusy, Is.True); Assert.That(cleanup.IsEnabled, Is.False); Assert.That(dialog.Owner, Is.SameAs(main)); Assert.That(dialog.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
        Window question = await Question(dialog); main.Close(); await Until(() => !question.IsVisible && !dialog.IsVisible && !main.IsBusy); Assert.That(f.EveryByte(), Is.EqualTo(before));
        string bare = Path.Combine(f.Root, "bare.git"); await f.Git("clone", "--bare", f.Repository.Path, bare); main = new(); f.Windows.Add(main); main.Show(); await main.OpenRepositoryAsync(bare); Pump(main);
        commands = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Commands"); cleanup = commands.Items.OfType<MenuItem>().Single(item => item.Name == "BrowseCleanupRepository"); Assert.That(cleanup.IsEnabled, Is.False); main.Close();
        await f.Git("symbolic-ref", "HEAD", "refs/heads/unborn"); CleanupRepositoryDialog unborn = await f.Show(); await Preview(unborn); Assert.That(unborn.LastResult!.Success, Is.True, "A worktree need not have HEAD to preview clean.");
    }

    private static async Task Preview(CleanupRepositoryDialog dialog) { await Pointer(Field<Button>(dialog, "PreviewCleanup")); await Until(() => !dialog.IsBusy); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); }
    private static async Task<Window> Question(CleanupRepositoryDialog dialog)
    { await Pointer(Field<Button>(dialog, "ExecuteCleanup")); await Until(() => dialog.OwnedWindows.Any(window => window.Name == "CleanupConfirmation" && window.IsVisible)); Window question = dialog.OwnedWindows.Single(window => window.Name == "CleanupConfirmation" && window.IsVisible); Pump(question); return question; }
    private static async Task Set(CleanupRepositoryDialog dialog, string name, bool value) { CheckBox check = Field<CheckBox>(dialog, name); if (check.IsChecked != value) { await Pointer(check); } Assert.That(check.IsChecked, Is.EqualTo(value)); }
    private static T Field<T>(Control owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static string Output(GitCleanResult result) => string.Concat(result.Commands.Select(command => command.StandardOutput + command.StandardError));
    private static async Task<IStorageFolder> Folder(Window owner, string path) { IStorageFolder? item = await owner.StorageProvider.TryGetFolderFromPathAsync(new Uri(path)); Assert.That(item, Is.Not.Null, "Picker seam must return a real storage folder."); return item!; }
    private static async Task<IStorageFile> File(Window owner, string path) { IStorageFile? item = await owner.StorageProvider.TryGetFileFromPathAsync(new Uri(path)); Assert.That(item, Is.Not.Null, "Picker seam must return a real storage file."); return item!; }
    private static bool Exited(int pid) { try { using Process process = Process.GetProcessById(pid); return process.HasExited; } catch (ArgumentException) { return true; } }
    private static void Press(TopLevel window, PhysicalKey key, TopLevel? releaseOn = null) { window.KeyPressQwerty(key, RawInputModifiers.None); (releaseOn ?? window).KeyReleaseQwerty(key, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static Rect Bounds(TopLevel window, Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    private static void Geometry(CleanupRepositoryDialog dialog)
    {
        Pump(dialog); Rect settings = Bounds(dialog, Field<StackPanel>(dialog, "CleanupSettings")), actions = Bounds(dialog, Field<Grid>(dialog, "CleanupActions")), log = Bounds(dialog, Field<TextBox>(dialog, "CleanupLog"));
        Assert.That(settings.Bottom, Is.LessThanOrEqualTo(actions.Top)); Assert.That(actions.Bottom, Is.LessThanOrEqualTo(log.Top)); Assert.That(log.Height, Is.GreaterThanOrEqualTo(64));
        foreach (string name in new[] { "PreviewCleanup", "ExecuteCleanup", "CloseCleanup", "AddCleanupInclude", "AddCleanupExclude", "CleanupIncludePaths", "CleanupExcludePaths", "CleanupLog" })
        { Control control = Field<Control>(dialog, name); Rect rect = Bounds(dialog, control); Assert.That(rect.Right, Is.LessThanOrEqualTo(dialog.ClientSize.Width)); Assert.That(rect.Bottom, Is.LessThanOrEqualTo(dialog.ClientSize.Height)); Assert.That(rect.Left, Is.GreaterThanOrEqualTo(0)); Assert.That(rect.Top, Is.GreaterThanOrEqualTo(0)); }
        foreach (string name in new[] { "CleanupIncludeBlock", "CleanupExcludeBlock" })
        {
            Grid block = Field<Grid>(dialog, name); CheckBox check = block.Children.OfType<CheckBox>().Single();
            Button picker = block.Children.OfType<Button>().Single(button => Grid.GetColumn(button) == 1 && button.Name == (name == "CleanupIncludeBlock" ? "AddCleanupInclude" : "AddCleanupExclude"));
            Assert.That(Bounds(dialog, check).Right, Is.LessThanOrEqualTo(Bounds(dialog, picker).Left));
            AccessText caption = (AccessText)picker.Content!; Rect buttonBounds = Bounds(dialog, picker), captionBounds = Bounds(dialog, caption);
            Assert.That(caption.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(caption.Bounds.Width + .5), "The full picker caption must fit its actual text column.");
            Assert.That(caption.TextLayout.Height, Is.LessThanOrEqualTo(caption.Bounds.Height + .5), "A wrapped second line must not be clipped by the 25px button.");
            Assert.That(captionBounds.Left, Is.GreaterThanOrEqualTo(buttonBounds.Left + picker.Padding.Left + picker.BorderThickness.Left - .5));
            Assert.That(captionBounds.Right, Is.LessThanOrEqualTo(buttonBounds.Right - picker.Padding.Right - picker.BorderThickness.Right + .5));
            Assert.That(captionBounds.Top, Is.GreaterThanOrEqualTo(buttonBounds.Top + picker.Padding.Top + picker.BorderThickness.Top - .5));
            Assert.That(captionBounds.Bottom, Is.LessThanOrEqualTo(buttonBounds.Bottom - picker.Padding.Bottom - picker.BorderThickness.Bottom + .5));
        }
    }
    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color) { static double Linear(byte value) { double c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); } return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B); }
        return (Math.Max(Luminance(first), Luminance(second)) + .05) / (Math.Min(Luminance(first), Luminance(second)) + .05);
    }
    private static async Task Pointer(Control control)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!; Stopwatch timer = Stopwatch.StartNew(); Point point = default; bool ready = false; string hit = "";
        do
        {
            Pump(top); point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value; top.MouseMove(point); Visual? visual = top.InputHitTest(point) as Visual;
            hit = string.Join(" > ", visual?.GetSelfAndVisualAncestors().Select(item => item.GetType().Name + "#" + (item as Control)?.Name) ?? []);
            ready = control.IsEffectivelyEnabled && control.IsEffectivelyVisible && visual?.GetSelfAndVisualAncestors().Contains(control) == true;
            if (ready) { break; } await Task.Delay(10);
        } while (timer.Elapsed < TimeSpan.FromSeconds(8));
        Assert.That(ready, Is.True, control.Name + " needs a real pointer target; hit=" + hit); top.MouseDown(point, MouseButton.Left); top.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static async Task Until(Func<bool> condition) { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True, "Clean dialog did not settle."); }
    private static void Capture(TopLevel window, string name) { Pump(window); using Bitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path); }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Dictionary<string, string?> _environment = [];
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-clean-ui-" + Guid.NewGuid().ToString("N"));
        public GitRepository Repository { get; }
        public Window Owner { get; } = new() { Width = 1000, Height = 760 };
        public List<Window> Windows { get; } = [];
        private Fixture()
        {
            Repository = new(System.IO.Path.Combine(Root, "repo")); Directory.CreateDirectory(Repository.Path);
            foreach ((string name, string value) in new[] { ("XDG_CONFIG_HOME", System.IO.Path.Combine(Root, "config")), ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_OPTIONAL_LOCKS", "0"), ("LC_ALL", "C") })
            { _environment[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); } CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        }
        public string Path(string relative) => System.IO.Path.Combine(Repository.Path, relative);
        public void Write(string relative, string contents) { string path = Path(relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); System.IO.File.WriteAllText(path, contents); }
        public async Task<GitResult> Git(params string[] arguments) { GitResult result = await new GitRunner().RunAsync(Repository.Path, ["--no-pager", "-c", "color.ui=false", "-c", "core.quotepath=false", .. arguments]); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        public static async Task<Fixture> Create()
        {
            Fixture f = new();
            try
            {
                await f.Git("init", "-b", "main"); await f.Git("config", "user.name", "Clean Test"); await f.Git("config", "user.email", "clean@example.invalid"); await f.Git("config", "commit.gpgsign", "false"); await f.Git("config", "core.hooksPath", "/dev/null");
                f.Write("tracked.txt", "tracked\n"); f.Write(".gitignore", "*.ignored\nignored-dir/\n"); await f.Git("add", "."); await f.Git("commit", "-m", "Initial"); f.Write("second.txt", "second\n"); await f.Git("add", "second.txt"); await f.Git("commit", "-m", "Second");
                f.Write("tracked.txt", "dirty tracked\n"); f.Write("staged λ.txt", "staged\n"); await f.Git("add", "staged λ.txt"); f.Write(".git/info/exclude", "info.ignored\n");
                foreach (string name in new[] { "loose.txt", "Café libre λ.tmp", "--dash.tmp", " leading.tmp", "quote\"file.tmp", "ignored.ignored", "info.ignored", "dir loose/inside.tmp", "ignored-dir/inside.txt" }) { f.Write(name, name + "\n"); }
                Directory.CreateDirectory(f.Path("nested-repo")); GitResult nested = await new GitRunner().RunAsync(f.Path("nested-repo"), ["init", "-b", "main"]); Assert.That(nested.Success, Is.True, nested.StandardError); f.Write("nested-repo/safe.txt", "nested\n");
                string outside = System.IO.Path.Combine(f.Root, "outside.txt"); System.IO.File.WriteAllText(outside, "outside\n"); System.IO.File.CreateSymbolicLink(f.Path("file-link"), outside);
                f.Owner.Show(); Pump(f.Owner); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async Task<CleanupRepositoryDialog> Show(bool dark = false, string? path = null, GitRepository? repository = null)
        { CleanupRepositoryDialog dialog = new(repository ?? Repository, path) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; Windows.Add(dialog); await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); _ = dialog.ShowDialog(Owner); Pump(dialog); return dialog; }
        public string EveryByte() => Tree(Repository.Path, false);
        public string Metadata() => Tree(Path(".git"), false);
        public string Worktree() => Tree(Repository.Path, true);
        public static string Tree(string root, bool omitRootGit)
        {
            SortedDictionary<string, string> entries = new(StringComparer.Ordinal);
            void Visit(string directory)
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
                {
                    if (omitRootGit && path == System.IO.Path.Combine(root, ".git")) { continue; }
                    string relative = System.IO.Path.GetRelativePath(root, path); FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                    if (info.LinkTarget is { } target) { entries[relative] = "link:" + target; }
                    else if (Directory.Exists(path)) { entries[relative] = "dir"; Visit(path); }
                    else { entries[relative] = Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path))); }
                }
            }
            Visit(root); return JsonSerializer.Serialize(entries);
        }
        public string Twin()
        {
            string twin = System.IO.Path.Combine(Root, "native-twin");
            void Copy(string source, string destination)
            {
                Directory.CreateDirectory(destination);
                foreach (string item in Directory.EnumerateFileSystemEntries(source))
                {
                    string target = System.IO.Path.Combine(destination, System.IO.Path.GetFileName(item)); FileSystemInfo info = Directory.Exists(item) ? new DirectoryInfo(item) : new FileInfo(item);
                    if (info.LinkTarget is { } link) { if (info is DirectoryInfo) { Directory.CreateSymbolicLink(target, link); } else { System.IO.File.CreateSymbolicLink(target, link); } }
                    else if (Directory.Exists(item)) { Copy(item, target); } else { System.IO.File.Copy(item, target); }
                }
            }
            Copy(Repository.Path, twin); return twin;
        }
        public async Task Submodules()
        {
            string source = System.IO.Path.Combine(Root, "module-source"), nested = System.IO.Path.Combine(Root, "nested-source"); Directory.CreateDirectory(source); Directory.CreateDirectory(nested); GitRunner git = new();
            foreach (string path in new[] { source, nested })
            {
                string[][] commands = [["init", "-b", "main"], ["config", "user.name", "Clean"], ["config", "user.email", "clean@example.invalid"], ["config", "commit.gpgsign", "false"]];
                foreach (string[] arguments in commands)
                { GitResult result = await git.RunAsync(path, arguments); Assert.That(result.Success, Is.True, result.StandardError); }
                System.IO.File.WriteAllText(System.IO.Path.Combine(path, "tracked.txt"), "module tracked\n"); Assert.That((await git.RunAsync(path, ["add", "."])).Success, Is.True); Assert.That((await git.RunAsync(path, ["commit", "-m", "Module"])).Success, Is.True);
            }
            GitResult add = await git.RunAsync(source, ["-c", "protocol.file.allow=always", "submodule", "add", nested, "nested"]); Assert.That(add.Success, Is.True, add.StandardError); Assert.That((await git.RunAsync(source, ["commit", "-am", "Nested"])).Success, Is.True);
            await Git("-c", "protocol.file.allow=always", "submodule", "add", source, "module"); await Git("-c", "protocol.file.allow=always", "submodule", "update", "--init", "--recursive");
            await Git("-c", "protocol.file.allow=always", "submodule", "add", nested, "not-initialized"); await Git("submodule", "deinit", "-f", "--", "not-initialized");
            Write("module/keep.tmp", "child untracked\n"); Write("module/nested/keep.tmp", "nested untracked\n");
        }
        public async Task<(GitRepository Repository, string Marker)> Gated(string stage)
        {
            if (OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Linux Git process fixture."); }
            string script = System.IO.Path.Combine(Root, "gated-git"), marker = System.IO.Path.Combine(Root, "runner.pid");
            string condition = stage == "submodules" ? "'submodule' in a and 'foreach' in a" : "'clean' in a and " + (stage == "preview" ? "'--dry-run' in a" : "'-f' in a");
            await System.IO.File.WriteAllTextAsync(script, "#!/usr/bin/python3\nimport os,sys,time\na=sys.argv[1:]\nif " + condition + ":\n"
                + TestSignalFile.Python(JsonSerializer.Serialize(marker), "str(os.getpid())", 1)
                + " time.sleep(60)\nos.execvp('git',['git']+a)\n");
            System.IO.File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return (new GitRepository(Repository.Path, new GitRunner(script)), marker);
        }
        public async ValueTask DisposeAsync()
        {
            static void CloseChildren(Window owner) { foreach (Window child in owner.OwnedWindows.ToArray()) { CloseChildren(child); child.Close(); } }
            Window[] windows = Windows.Append(Owner).Distinct().ToArray();
            foreach (Window window in Windows.AsEnumerable().Reverse()) { CloseChildren(window); window.Close(); } CloseChildren(Owner); Owner.Close();
            // Closing seals each browser before taking its work snapshot. A cancelled read
            // may still be unwinding, and its final Close is posted to the dispatcher.
            Task pending = Task.WhenAll(windows.OfType<MainWindow>().Select(window => window.WindowWorkCompletion));
            Stopwatch timer = Stopwatch.StartNew();
            while ((windows.Any(window => window.IsVisible) || Windows.OfType<RepositoryDialog>().Any(dialog => dialog.IsBusy) || !pending.IsCompleted)
                && timer.Elapsed < TimeSpan.FromSeconds(15))
            { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            Assert.That(windows.Where(window => window.IsVisible).Select(window => window.GetType().Name), Is.Empty,
                "All fixture windows must actually close before the headless scope and repository profile end.");
            Assert.That(Windows.OfType<RepositoryDialog>().Any(dialog => dialog.IsBusy), Is.False,
                "The existing repository operations must finish their cancellation before cleanup.");
            Assert.That(pending.IsCompleted, Is.True, "Each browser must drain WindowWorkCompletion before restoring XDG or deleting its repository.");
            try { await pending; }
            catch (OperationCanceledException) { }
            Dispatcher.UIThread.RunJobs();
            foreach ((string name, string? value) in _environment) { Environment.SetEnvironmentVariable(name, value); } CultureInfo.CurrentUICulture = _culture;
            if (Directory.Exists(Root)) { Directory.Delete(Root, true); }
        }
    }
}
