using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class RulesFileEditorDialogTests
{
    [AvaloniaTest]
    public async Task FourOriginalEditorVariantsUseTheSharedEditableViewerAndFitTheirHelpAndActionsInBothThemes()
    {
        using Fixture f = await Fixture.CreateAsync();
        foreach (GitRulesFileKind kind in Enum.GetValues<GitRulesFileKind>())
        {
            List<Uri> links = [];
            RulesFileEditorDialog dialog = await ShowEditorAsync(f.Repository, kind, links.Add);
            try
            {
                bool ignore = kind is GitRulesFileKind.Ignore or GitRulesFileKind.LocalExclude;
                Assert.That(dialog.Title, Is.EqualTo("Edit " + FileName(kind)));
                Assert.That(dialog.Snapshot, Is.Not.Null);
                Assert.That(dialog.Viewer, Is.SameAs(Field<DiffView>(dialog, "RulesFileViewer")));
                Assert.That(dialog.Viewer.Mode, Is.EqualTo(DiffViewMode.PlainText));
                Assert.That(dialog.Viewer.Editor.IsReadOnly, Is.False);
                Assert.That(dialog.Viewer.IsModified, Is.False);
                Assert.That(dialog.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "CancelRulesFile"), Is.EqualTo(ignore));
                Assert.That(dialog.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "AddDefaultIgnores"), Is.EqualTo(ignore));
                foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    dialog.RequestedThemeVariant = theme;
                    Capture(dialog, "rules-editor-" + kind + "-" + theme + ".png");
                    Assert.That(dialog.ClientSize.Width, Is.EqualTo(634).Within(1));
                    Assert.That(dialog.ClientSize.Height, Is.EqualTo(ignore ? 623 : 474).Within(1));
                    AssertInside(dialog, dialog.Viewer, Field<Button>(dialog, "SaveRulesFile"), Field<ScrollViewer>(dialog, "RulesFileHelpScroll"));
                    string help = ((TextBlock)Field<ScrollViewer>(dialog, "RulesFileHelpScroll").Content!).Text!;
                    Assert.That(help, Does.Contain(kind switch
                    {
                        GitRulesFileKind.Attributes => "git help gitattributes",
                        GitRulesFileKind.MailMap => "git help shortlog",
                        _ => "Specify filepatterns you want git to ignore."
                    }));
                    if (ignore)
                    {
                        AssertInside(dialog, Field<Button>(dialog, "AddDefaultIgnores"), Field<Button>(dialog, "AddIgnorePattern"),
                            Field<Button>(dialog, "CancelRulesFile"), Field<Button>(dialog, "lnkGitIgnoreGenerate"), Field<Button>(dialog, "lnkGitIgnorePatterns"));
                        Assert.That(Field<Button>(dialog, "SaveRulesFile").GetVisualDescendants().OfType<Image>().Any(image => image.Tag as string == "Save.png"), Is.True);
                    }
                }
                if (ignore)
                {
                    Pointer(dialog, "lnkGitIgnoreGenerate"); Pointer(dialog, "lnkGitIgnorePatterns");
                    Assert.That(links.Select(link => link.AbsoluteUri), Is.EqualTo(new[] { "https://www.gitignore.io/", "https://github.com/github/gitignore" }));
                }
                bool existed = dialog.Snapshot!.Exists;
                string path = dialog.Snapshot.Path;
                byte[]? original = existed ? await File.ReadAllBytesAsync(path) : null;
                dialog.Close(); await Until(() => !dialog.IsVisible);
                Assert.That(File.Exists(path), Is.EqualTo(existed), "Opening and closing an unchanged editor must not create a rules file.");
                if (original is not null) { Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(original)); }
            }
            finally { await CloseAsync(dialog); }
        }
    }

    [AvaloniaTest]
    public async Task SavingEachEditorChangesTheActualGitIgnoreAttributesAndMailmapBehaviorWithoutStagingAnything()
    {
        using Fixture f = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(f.PathInRepository("ignored.tmp"), "ignored\n");
        await File.WriteAllTextAsync(f.PathInRepository("secret.local"), "local\n");
        await File.WriteAllTextAsync(f.PathInRepository("image.jpg"), "image\n");
        byte[] index = await File.ReadAllBytesAsync(f.PathInRepository(".git/index"));
        foreach ((GitRulesFileKind kind, string text) in new[]
        {
            (GitRulesFileKind.Ignore, "/ignored.tmp"),
            (GitRulesFileKind.LocalExclude, "/secret.local"),
            (GitRulesFileKind.Attributes, "*.jpg binary"),
            (GitRulesFileKind.MailMap, "Canonical λ <canonical@example.com> Reference Tester <reference@example.com>")
        })
        {
            RulesFileEditorDialog dialog = await ShowEditorAsync(f.Repository, kind);
            try
            {
                Edit(dialog, text); Assert.That(dialog.Viewer.IsModified, Is.True);
                Pointer(dialog, "SaveRulesFile"); await Idle(dialog);
                Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
                Assert.That(dialog.IsVisible, Is.False);
                Assert.That(dialog.HasRepositoryChanges, Is.True);
                Assert.That(await File.ReadAllTextAsync(dialog.Snapshot!.Path), Is.EqualTo(text + "\n"));
            }
            finally { await CloseAsync(dialog); }
        }
        Assert.That((await f.GitAsync("check-ignore", "--", "ignored.tmp", "secret.local")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            Is.EqualTo(new[] { "ignored.tmp", "secret.local" }));
        Assert.That((await f.GitAsync("check-attr", "binary", "text", "--", "image.jpg")).StandardOutput,
            Does.Contain("image.jpg: binary: set").And.Contain("image.jpg: text: unset"));
        Assert.That((await f.GitAsync("check-mailmap", "Reference Tester <reference@example.com>")).StandardOutput.TrimEnd('\r', '\n'),
            Is.EqualTo("Canonical λ <canonical@example.com>"));
        Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/index")), Is.EqualTo(index));
        Assert.That((await f.GitAsync("log", "-1", "--format=%aN <%aE>")).StandardOutput.Trim(), Is.EqualTo("Canonical λ <canonical@example.com>"));
    }

    [AvaloniaTest]
    public async Task UndoBackToTheLoadedTextClearsDirtyStateAndCloseCancelNoAndYesHaveTheirActualDiskEffects()
    {
        using Fixture f = await Fixture.CreateAsync();
        foreach (GitRulesFileKind kind in Enum.GetValues<GitRulesFileKind>())
        {
            GitRulesFileSnapshot baseline = await f.Repository.GetRulesFileSnapshotAsync(kind);
            string original = baseline.Text;
            RulesFileEditorDialog dialog = await ShowEditorAsync(f.Repository, kind);
            try
            {
                Edit(dialog, original + "# unsaved λ\n");
                Assert.That(dialog.Viewer.IsModified, Is.True);
                dialog.Viewer.Editor.Document.UndoStack.Undo();
                Assert.That(dialog.Viewer.Text, Is.EqualTo(original));
                Assert.That(dialog.Viewer.IsModified, Is.False, "Undo to the loaded bytes removes the close prompt.");
                dialog.Viewer.Editor.Document.UndoStack.Redo();
                Assert.That(dialog.Viewer.IsModified, Is.True);
                dialog.Close();
                Window question = await SaveQuestionAsync(dialog);
                Assert.That(Field<Button>(question, "RulesSaveYes").IsDefault, Is.True);
                Pointer(question, "RulesSaveCancel"); await Until(() => dialog.OwnedWindows.Count == 0);
                Assert.That(dialog.IsVisible && dialog.Viewer.IsModified, Is.True);
                Assert.That(dialog.Viewer.Text, Is.EqualTo(original + "# unsaved λ\n"));
                dialog.Close(); question = await SaveQuestionAsync(dialog); Pointer(question, "RulesSaveNo");
                await Until(() => !dialog.IsVisible);
                Assert.That((await f.Repository.GetRulesFileSnapshotAsync(kind)).Text, Is.EqualTo(original));
            }
            finally { await CloseAsync(dialog); }

            dialog = await ShowEditorAsync(f.Repository, kind);
            try
            {
                Edit(dialog, original + "# saved on close λ\n"); dialog.Close();
                Window question = await SaveQuestionAsync(dialog); Pointer(question, "RulesSaveYes");
                await Until(() => !dialog.IsVisible && !dialog.IsBusy);
                Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
                Assert.That((await f.Repository.GetRulesFileSnapshotAsync(kind)).Text, Is.EqualTo(original + "# saved on close λ\n"));
            }
            finally { await CloseAsync(dialog); }
        }
    }

    [AvaloniaTest]
    public async Task FailedSaveFromTheCloseQuestionKeepsTheDirtyEditorAndOriginalDiskContent()
    {
        using Fixture f = await Fixture.CreateAsync();
        string path = f.PathInRepository(".gitattributes"); await File.WriteAllTextAsync(path, "# original\n");
        RulesFileEditorDialog dialog = await ShowEditorAsync(f.Repository, GitRulesFileKind.Attributes);
        try
        {
            Edit(dialog, "# original\ninvalid\0content\n"); dialog.Close();
            Window question = await SaveQuestionAsync(dialog); Pointer(question, "RulesSaveYes"); await Idle(dialog);
            Assert.That(dialog.IsVisible, Is.True);
            Assert.That(dialog.LastError, Does.Contain("NUL"));
            Assert.That(dialog.Viewer.IsModified, Is.True);
            Assert.That(dialog.Viewer.Text, Is.EqualTo("# original\ninvalid\0content\n"));
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("# original\n"));
            Assert.That(dialog.OwnedWindows, Is.Empty);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task CtrlSSavesTheSharedEditorWithoutClosingItAndUndoCanStillReturnBeforeThatSave()
    {
        using Fixture f = await Fixture.CreateAsync();
        RulesFileEditorDialog dialog = await ShowEditorAsync(f.Repository, GitRulesFileKind.Ignore);
        try
        {
            Edit(dialog, "*.saved\n"); dialog.Viewer.FocusEditor();
            dialog.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control); dialog.KeyReleaseQwerty(PhysicalKey.S, RawInputModifiers.Control);
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.IsVisible, Is.True);
            Assert.That(dialog.Viewer.IsModified, Is.False);
            Assert.That(await File.ReadAllTextAsync(f.PathInRepository(".gitignore")), Is.EqualTo("*.saved\n"));
            dialog.Viewer.Editor.Document.UndoStack.Undo();
            Assert.That(dialog.Viewer.IsModified, Is.True, "Saving a rules file preserves undo history in the shared FileViewer.");
            Assert.That(await File.ReadAllTextAsync(f.PathInRepository(".gitignore")), Is.EqualTo("*.saved\n"));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task ExternalModificationRefusesSaveAndKeepsBothTheExternalBytesAndTheUsersBuffer()
    {
        using Fixture f = await Fixture.CreateAsync();
        string path = f.PathInRepository(".gitignore"); await File.WriteAllTextAsync(path, "# loaded\n");
        RulesFileEditorDialog dialog = await ShowEditorAsync(f.Repository, GitRulesFileKind.Ignore);
        try
        {
            Edit(dialog, "# my edit\n*.tmp\n"); await File.WriteAllTextAsync(path, "# external edit\n*.external\n");
            Pointer(dialog, "SaveRulesFile"); await Idle(dialog);
            Assert.That(dialog.IsVisible && dialog.Viewer.IsModified, Is.True);
            Assert.That(dialog.LastError, Does.Contain("changed"));
            Assert.That(dialog.Viewer.Text, Is.EqualTo("# my edit\n*.tmp\n"));
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("# external edit\n*.external\n"));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task FailedEncodingReloadRestoresTheActualCodecAndTheNextSaveUsesThatCodec()
    {
        using Fixture f = await Fixture.CreateAsync();
        string path = f.PathInRepository(".gitignore"); await File.WriteAllTextAsync(path, "# café λ\n*.tmp\n");
        RulesFileEditorDialog dialog = await ShowEditorAsync(f.Repository, GitRulesFileKind.Ignore);
        try
        {
            GitRulesFileSnapshot baseline = dialog.Snapshot!; byte[] original = await File.ReadAllBytesAsync(path);
            Assert.That(baseline.EncodingName, Is.EqualTo("utf-8"));
            dialog.Viewer.FileEncodingName = "us-ascii";
            await Until(() => !dialog.IsBusy && dialog.LastError is not null);
            Assert.That(dialog.LastError, Does.Contain("decoded"));
            Assert.That(dialog.Viewer.FileEncodingName, Is.EqualTo("utf-8"), "The encoding selector must describe the bytes actually displayed, even after a failed reload.");
            Assert.That(dialog.Snapshot, Is.SameAs(baseline));
            Assert.That(dialog.Viewer.Text, Is.EqualTo("# café λ\n*.tmp\n"));
            Assert.That(dialog.Viewer.IsModified, Is.False);
            Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(original));
            Edit(dialog, "# café λ\n*.new\n");
            dialog.Viewer.FocusEditor(); dialog.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control); dialog.KeyReleaseQwerty(PhysicalKey.S, RawInputModifiers.Control);
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.IsVisible, Is.True);
            Assert.That(dialog.Viewer.IsModified, Is.False);
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("# café λ\n*.new\n"));
            Assert.That(dialog.Snapshot!.EncodingName, Is.EqualTo(dialog.Viewer.FileEncodingName));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task CancellingDirtyCloseAfterAnInterruptedReadConsumesTheDeferredCloseBeforeTheNextCtrlS()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("A cancellable private Git read requires Linux."); return; }
        using Fixture f = await Fixture.CreateAsync();
        string path = f.PathInRepository(".gitignore"); await File.WriteAllTextAsync(path, "# loaded\n");
        string gate = f.Output("gate-encoding-config"), actors = f.Output("encoding-read-actors"), script = f.Output("encoding-read-git");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nif test -f " + ShellQuote(gate) + "; then\n for arg do\n"
            + "  if test \"$arg\" = " + ShellQuote("^i18n\\.filesencoding$") + "; then\n"
            + "   sleep 60 &\n   child=$!\n   printf '%s %s\\n' \"$$\" \"$child\" > " + ShellQuote(actors) + "\n   wait\n  fi\n done\nfi\nexec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        RulesFileEditorDialog dialog = await ShowEditorAsync(new GitRepository(f.Repository.Path, new GitRunner(script)), GitRulesFileKind.Ignore);
        int[] pids = [];
        try
        {
            dialog.Viewer.FileEncodingName = "utf-8"; await Idle(dialog);
            Edit(dialog, "# keep this edit\n*.saved\n");
            await File.WriteAllTextAsync(gate, "gate\n");
            dialog.Viewer.FileEncodingName = null;
            Window question = await SaveQuestionAsync(dialog);
            Pointer(question, "RulesSaveNo");
            await Until(() => ReadActors(actors).Length == 2); pids = ReadActors(actors);
            Assert.That(dialog.IsBusy, Is.True);
            dialog.Close();
            question = await SaveQuestionAsync(dialog);
            Assert.That(pids.Any(ActorAlive), Is.False, "The interrupted read must drain before the close decision is offered.");
            Pointer(question, "RulesSaveCancel"); await Until(() => dialog.OwnedWindows.Count == 0 && !dialog.IsBusy);
            Assert.That(dialog.IsVisible && dialog.Viewer.IsModified, Is.True);
            Assert.That(dialog.Viewer.Text, Is.EqualTo("# keep this edit\n*.saved\n"));
            File.Delete(gate);
            dialog.Viewer.FocusEditor(); dialog.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control); dialog.KeyReleaseQwerty(PhysicalKey.S, RawInputModifiers.Control);
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.IsVisible, Is.True, "Cancel revoked the prior close request; Ctrl+S must now save without closing.");
            Assert.That(dialog.Viewer.IsModified, Is.False);
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("# keep this edit\n*.saved\n"));
        }
        finally
        {
            File.Delete(gate); await CloseAsync(dialog);
            foreach (int pid in pids.Where(ActorAlive))
            {
                try { using Process process = Process.GetProcessById(pid); process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
            }
        }
    }

    [AvaloniaTest]
    public async Task FinishingOrCancellingAnOlderPreviewCannotEraseTheErrorFromAFailedAppend()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("A private delayed native preview requires Linux."); return; }
        using Fixture f = await Fixture.CreateAsync();
        string path = f.PathInRepository(".gitignore"); await File.WriteAllTextAsync(path, "# loaded\n");
        await File.WriteAllTextAsync(f.PathInRepository("sample.tmp"), "sample\n");
        string actors = f.Output("append-preview-actors"), release = f.Output("append-preview-release"), script = f.Output("append-preview-git");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nfor arg do\n if test \"$arg\" = ls-files; then\n"
            + "  sleep 60 &\n  child=$!\n  printf '%s %s\\n' \"$$\" \"$child\" > " + ShellQuote(actors)
            + "\n  while test ! -f " + ShellQuote(release) + "; do sleep 0.02; done\n  kill \"$child\" 2>/dev/null\n  wait \"$child\" 2>/dev/null\n fi\ndone\nexec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        AddToGitIgnoreDialog dialog = new(new GitRepository(f.Repository.Path, new GitRunner(script)), patterns: ["*.tmp"]);
        dialog.Show(); int[] pids = [];
        try
        {
            await dialog.InitializeAsync(); await Until(() => ReadActors(actors).Length == 2); pids = ReadActors(actors);
            Assert.That(dialog.IsPreviewLoading, Is.True);
            await File.WriteAllTextAsync(path, "# external change\n*.external\n");
            Pointer(dialog, "AppendIgnorePatterns"); await Idle(dialog);
            Assert.That(dialog.LastError, Does.Contain("changed"));
            await File.WriteAllTextAsync(release, "release\n"); await Until(() => !dialog.IsPreviewLoading);
            Pump(dialog);
            Assert.That(dialog.IsVisible, Is.True);
            Assert.That(dialog.LastError, Does.Contain("changed"));
            TextBlock status = Field<TextBlock>(dialog, "OperationStatus");
            Assert.That(status.Text, Is.EqualTo(dialog.LastError), "The completion of a preview must not overwrite a newer failed save.");
            Assert.That(status.IsEffectivelyVisible, Is.True);
            Assert.That(pids.Any(ActorAlive), Is.False);
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("# external change\n*.external\n"));
        }
        finally
        {
            await File.WriteAllTextAsync(release, "release\n"); await CloseAsync(dialog);
            foreach (int pid in pids.Where(ActorAlive))
            {
                try { using Process process = Process.GetProcessById(pid); process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
            }
        }
    }

    [AvaloniaTest]
    public async Task DefaultIgnoresUseTheXdgOverrideAndOnlyARealSaveWritesTheEditedBuffer()
    {
        using Fixture f = await Fixture.CreateAsync();
        string path = f.PathInRepository(".gitignore"); const string original = "# keep\n*.bak\n";
        await File.WriteAllTextAsync(path, original);
        string custom = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "DefaultIgnorePatterns.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
        await File.WriteAllTextAsync(custom, "*.bak\nsample/\nsample/\ncustom-λ/\n");
        RulesFileEditorDialog dialog = await ShowEditorAsync(f.Repository, GitRulesFileKind.Ignore);
        try
        {
            Pointer(dialog, "AddDefaultIgnores"); await Idle(dialog);
            string expected = original + "\nsample/\ncustom-λ/\n";
            Assert.That(dialog.Viewer.Text, Is.EqualTo(expected));
            Assert.That(dialog.Viewer.IsModified, Is.True);
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(original));
            Pointer(dialog, "AddDefaultIgnores"); await Idle(dialog);
            Assert.That(dialog.Viewer.Text, Is.EqualTo(expected), "Adding the same defaults twice must not duplicate their rules.");
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(original));
            Pointer(dialog, "SaveRulesFile"); await Idle(dialog);
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(expected));
        }
        finally { await CloseAsync(dialog); }
        File.Delete(custom);
        dialog = await ShowEditorAsync(f.Repository, GitRulesFileKind.Ignore);
        try
        {
            string saved = await File.ReadAllTextAsync(path);
            Pointer(dialog, "AddDefaultIgnores"); await Idle(dialog);
            Assert.That(dialog.Viewer.Text, Does.Contain("Thumbs.db").And.Contain("*.obj").And.Contain("[Bb]in").And.Contain("packages/"));
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(saved));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task AddPatternDoesNotOpenOrReloadAfterAPreSaveFailureAndReloadsOnlyAfterTheSuccessfulChildAppend()
    {
        using Fixture f = await Fixture.CreateAsync();
        string path = f.PathInRepository(".gitignore");
        RulesFileEditorDialog dialog = await ShowEditorAsync(f.Repository, GitRulesFileKind.Ignore);
        try
        {
            Edit(dialog, "# preserve unsaved\n*.tmp\n"); Directory.CreateDirectory(path);
            Pointer(dialog, "AddIgnorePattern"); await Idle(dialog);
            Assert.That(dialog.LastError, Is.Not.Null);
            Assert.That(dialog.IsVisible && dialog.Viewer.IsModified, Is.True);
            Assert.That(dialog.Viewer.Text, Is.EqualTo("# preserve unsaved\n*.tmp\n"));
            Assert.That(dialog.OwnedWindows.OfType<AddToGitIgnoreDialog>(), Is.Empty);
            Directory.Delete(path);
            Pointer(dialog, "AddIgnorePattern");
            await Until(() => dialog.OwnedWindows.OfType<AddToGitIgnoreDialog>().Any(child => !child.IsBusy && !child.IsPreviewLoading));
            AddToGitIgnoreDialog child = dialog.OwnedWindows.OfType<AddToGitIgnoreDialog>().Single();
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("# preserve unsaved\n*.tmp\n"));
            Assert.That(child.CurrentPatterns, Is.EqualTo(new[] { "*.dll" }));
            Field<TextBox>(child, "IgnorePatterns").Text = "/generated.dll";
            await Until(() => !child.IsPreviewLoading);
            Pointer(child, "AppendIgnorePatterns"); await Until(() => !child.IsVisible && !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.Viewer.Text, Is.EqualTo("# preserve unsaved\n*.tmp\n/generated.dll\n"));
            Assert.That(dialog.Viewer.IsModified, Is.False);
            Assert.That(dialog.Snapshot!.Text, Is.EqualTo(dialog.Viewer.Text));
            Assert.That(dialog.HasRepositoryChanges, Is.True);
        }
        finally
        {
            if (Directory.Exists(path)) { Directory.Delete(path); }
            await CloseAsync(dialog);
        }
    }

    [AvaloniaTest]
    public async Task OpeningFromNestedDirectoriesWritesRootRulesAndLinkedWorktreesShareTheGitResolvedExclude()
    {
        using Fixture f = await Fixture.CreateAsync();
        string nested = f.PathInRepository("nested/deeper"); Directory.CreateDirectory(nested);
        GitRepository fromNested = await GitRepository.OpenAsync(nested);
        RulesFileEditorDialog dialog = await ShowEditorAsync(fromNested, GitRulesFileKind.Ignore);
        try
        {
            Assert.That(dialog.Snapshot!.Path, Is.EqualTo(f.PathInRepository(".gitignore")));
            Edit(dialog, "/root-only.tmp\n"); Pointer(dialog, "SaveRulesFile"); await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null);
            Assert.That(File.Exists(Path.Combine(nested, ".gitignore")), Is.False);
            Assert.That(await File.ReadAllTextAsync(f.PathInRepository(".gitignore")), Is.EqualTo("/root-only.tmp\n"));
        }
        finally { await CloseAsync(dialog); }

        string linked = f.Output("linked-worktree"); await f.Repository.AddWorktreeAsync(linked, "HEAD", "rules-linked");
        GitRepository worktree = await GitRepository.OpenAsync(linked);
        string expected = (await GitAtAsync(linked, "rev-parse", "--path-format=absolute", "--git-path", "info/exclude")).StandardOutput.TrimEnd('\r', '\n');
        dialog = await ShowEditorAsync(worktree, GitRulesFileKind.LocalExclude);
        try
        {
            Assert.That(dialog.Snapshot!.Path, Is.EqualTo(expected));
            Edit(dialog, "/shared-secret.tmp\n"); Pointer(dialog, "SaveRulesFile"); await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(await File.ReadAllTextAsync(expected), Is.EqualTo("/shared-secret.tmp\n"));
            Assert.That((await f.GitAsync("check-ignore", "--", "shared-secret.tmp")).StandardOutput.Trim(), Is.EqualTo("shared-secret.tmp"));
            Assert.That((await GitAtAsync(linked, "check-ignore", "--", "shared-secret.tmp")).StandardOutput.Trim(), Is.EqualTo("shared-secret.tmp"));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task BareRepositoriesKeepEveryEditorReadOnlyAndCannotStartAppendingRules()
    {
        using Fixture f = await Fixture.CreateAsync();
        string path = f.Output("bare-rules.git"); await f.GitAsync("clone", "--bare", f.Repository.Path, path);
        GitRepository bare = await GitRepository.OpenAsync(path);
        foreach (GitRulesFileKind kind in Enum.GetValues<GitRulesFileKind>())
        {
            RulesFileEditorDialog dialog = new(bare, kind); dialog.Show();
            try
            {
                await dialog.InitializeAsync();
                Assert.That(dialog.LastError, Does.Contain("working tree"));
                Assert.That(dialog.Snapshot, Is.Null);
                Assert.That(dialog.Viewer.Editor.IsReadOnly, Is.True);
                Assert.That(Field<Button>(dialog, "SaveRulesFile").IsEnabled, Is.False);
            }
            finally { await CloseAsync(dialog); }
        }
        AddToGitIgnoreDialog append = new(bare, patterns: ["*.tmp"]); append.Show();
        try
        {
            await append.InitializeAsync();
            Assert.That(append.LastError, Does.Contain("working tree"));
            Assert.That(Field<Button>(append, "AppendIgnorePatterns").IsEnabled, Is.False);
        }
        finally { await CloseAsync(append); }
        Assert.That(File.Exists(Path.Combine(path, ".gitignore")), Is.False);
        Assert.That(File.Exists(Path.Combine(path, ".gitattributes")), Is.False);
        Assert.That(File.Exists(Path.Combine(path, ".mailmap")), Is.False);
    }

    [AvaloniaTest]
    public async Task PatternPreviewPreservesTrackedAndLiteralNewlineFilesWhileAppendKeepsNegationOrderAndDuplicates()
    {
        using Fixture f = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(f.PathInRepository("tracked.log"), "tracked\n");
        await f.Repository.StageAsync(["tracked.log"]); await f.GitAsync("commit", "-m", "Tracked preview input");
        foreach (string name in new[] { "a.log", "keep.log", "line\nbreak.log" }) { await File.WriteAllTextAsync(f.PathInRepository(name), name); }
        AddToGitIgnoreDialog dialog = await ShowAppendAsync(f.Repository, patterns: ["*.log", "!keep.log", "*.log", "!keep.log"]);
        try
        {
            Assert.That(dialog.CurrentPatterns, Is.EqualTo(new[] { "*.log", "!keep.log", "*.log", "!keep.log" }));
            Assert.That(Preview(dialog), Is.EquivalentTo(new[] { "a.log", "tracked.log", "line\nbreak.log" }));
            Assert.That(Field<TextBlock>(dialog, "IgnorePatternCount").Text, Is.EqualTo("3 file(s) matched"));
            Assert.That(Field<StackPanel>(dialog, "NoIgnorePatternMatches").IsVisible, Is.False);
            Pump(dialog);
            Assert.That(Field<ListBox>(dialog, "IgnorePatternPreview").GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "line\\nbreak.log"), Is.True,
                "The display must escape a newline without splitting or changing the path stored in the list item.");
            Assert.That(File.Exists(f.PathInRepository(".gitignore")), Is.False);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme; Capture(dialog, "add-ignore-pattern-preview-" + theme + ".png");
                Assert.That(dialog.ClientSize.Width, Is.EqualTo(599).Within(1)); Assert.That(dialog.ClientSize.Height, Is.EqualTo(341).Within(1));
                AssertInside(dialog, Field<TextBox>(dialog, "IgnorePatterns"), Field<ListBox>(dialog, "IgnorePatternPreview"),
                    Field<Button>(dialog, "AppendIgnorePatterns"), Field<Button>(dialog, "CancelIgnorePatterns"));
            }
            Pointer(dialog, "AppendIgnorePatterns"); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(await File.ReadAllTextAsync(f.PathInRepository(".gitignore")), Is.EqualTo("*.log\n!keep.log\n*.log\n!keep.log\n"));
            Assert.That((await f.GitAsync("check-ignore", "--", "a.log", "keep.log", "line\nbreak.log")).StandardOutput, Does.Contain("a.log"));
            Assert.That((await f.GitAsync("ls-files", "--", "tracked.log")).StandardOutput.Trim(), Is.EqualTo("tracked.log"), "Adding a rule does not stop tracking a file.");
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task EmptyPatternsAndNoMatchesRenderTheirOwnStateAndCancelOrEmptyIgnoreNeverWrites()
    {
        using Fixture f = await Fixture.CreateAsync();
        foreach (bool local in new[] { false, true })
        {
            GitRulesFileSnapshot before = await f.Repository.GetRulesFileSnapshotAsync(local ? GitRulesFileKind.LocalExclude : GitRulesFileKind.Ignore);
            AddToGitIgnoreDialog dialog = await ShowAppendAsync(f.Repository, local, ["nothing-matches-λ-*", ""]);
            try
            {
                Assert.That(dialog.Title, Is.EqualTo(local ? "Add file(s) to .git/info/exclude" : "Add file(s) to .gitignore"));
                Assert.That(Preview(dialog), Is.Empty);
                Assert.That(Field<StackPanel>(dialog, "NoIgnorePatternMatches").IsVisible, Is.True);
                Assert.That(Field<TextBlock>(dialog, "IgnorePatternCount").Text, Is.EqualTo("0 file(s) matched"));
                Capture(dialog, "add-ignore-no-matches-" + local + ".png");
                Field<TextBox>(dialog, "IgnorePatterns").Text = "\r\n\n";
                await Until(() => !dialog.IsPreviewLoading);
                Assert.That(dialog.CurrentPatterns, Is.Empty);
                Pointer(dialog, "AppendIgnorePatterns"); await Until(() => !dialog.IsVisible);
                Assert.That(dialog.HasRepositoryChanges, Is.False);
                Assert.That((await f.Repository.GetRulesFileSnapshotAsync(local ? GitRulesFileKind.LocalExclude : GitRulesFileKind.Ignore)).ContentHash, Is.EqualTo(before.ContentHash));
                Assert.That(File.Exists(before.Path), Is.EqualTo(before.Exists));
            }
            finally { await CloseAsync(dialog); }
        }
        AddToGitIgnoreDialog cancelled = await ShowAppendAsync(f.Repository, patterns: ["*.cancelled"]);
        Pointer(cancelled, "CancelIgnorePatterns"); await Until(() => !cancelled.IsVisible);
        Assert.That(File.Exists(f.PathInRepository(".gitignore")), Is.False);
    }

    [AvaloniaTest]
    public async Task LatestPatternRequestWinsAndClosingPreviewDrainsTheActualNativeGitActors()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private native Git wrapper and procfs require Linux."); return; }
        using Fixture f = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(f.PathInRepository("old.slow"), "old\n");
        await File.WriteAllTextAsync(f.PathInRepository("latest.new"), "new\n");
        string actors = f.Output("preview-actors"), script = f.Output("preview-git");
        await File.WriteAllTextAsync(script, "#!/bin/sh\ncommand=\nslow=\nfor arg do\n"
            + " if test \"$arg\" = ls-files; then command=1; fi\n if test \"$arg\" = '*.slow'; then slow=1; fi\ndone\n"
            + "if test \"$command:$slow\" = 1:1; then\n sleep 60 &\n child=$!\n printf '%s %s\\n' \"$$\" \"$child\" > " + ShellQuote(actors)
            + "\n wait\nfi\nexec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRepository repository = new(f.Repository.Path, new GitRunner(script));
        AddToGitIgnoreDialog dialog = new(repository, patterns: ["*.slow"]); dialog.Show(); int[] pids = [];
        try
        {
            await dialog.InitializeAsync(); await Until(() => ReadActors(actors).Length == 2);
            pids = ReadActors(actors); Assert.That(dialog.IsPreviewLoading, Is.True);
            Field<TextBox>(dialog, "IgnorePatterns").Text = "*.new";
            await Until(() => !dialog.IsPreviewLoading);
            Assert.That(Preview(dialog), Is.EqualTo(new[] { "latest.new" }));
            Assert.That(pids.Any(ActorAlive), Is.False, "Changing a pattern must drain the superseded native process and its hook actor.");
            Assert.That(Field<TextBlock>(dialog, "IgnorePatternCount").Text, Is.EqualTo("1 file(s) matched"));
            File.Delete(actors); Field<TextBox>(dialog, "IgnorePatterns").Text = "*.slow";
            await Until(() => ReadActors(actors).Length == 2); pids = ReadActors(actors);
            dialog.Close(); await Until(() => !dialog.IsVisible && !dialog.IsPreviewLoading);
            Assert.That(pids.Any(ActorAlive), Is.False, "The preview window cannot finish closing while its native command is still alive.");
            Assert.That(File.Exists(f.PathInRepository(".gitignore")), Is.False);
        }
        finally
        {
            await CloseAsync(dialog);
            foreach (int pid in pids.Where(ActorAlive))
            {
                try { using Process process = Process.GetProcessById(pid); process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
            }
        }
    }

    [AvaloniaTest]
    public async Task RepositoryMenuOpensAllFourOriginalEditorsAndRetainsTheirOriginalIconPlacement()
    {
        using Fixture f = await Fixture.CreateAsync();
        MainWindow main = new() { Width = 1280, Height = 800 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(f.Repository.Path); await Until(() => !main.IsBusy);
            MenuItem repository = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Repository");
            foreach ((string name, GitRulesFileKind kind) in new[]
            {
                ("editgitignoreToolStripMenuItem1", GitRulesFileKind.Ignore), ("editgitinfoexcludeToolStripMenuItem", GitRulesFileKind.LocalExclude),
                ("editGitAttributesToolStripMenuItem", GitRulesFileKind.Attributes), ("editmailmapToolStripMenuItem", GitRulesFileKind.MailMap)
            })
            {
                MenuItem item = repository.Items.OfType<MenuItem>().Single(menu => menu.Name == name);
                Assert.That(item.IsEnabled, Is.True);
                if (kind == GitRulesFileKind.Ignore) { Assert.That(item.Icon, Is.TypeOf<Image>()); Assert.That(((Image)item.Icon!).Tag, Is.EqualTo("EditGitIgnore.png")); }
                else { Assert.That(item.Icon, Is.Null, "The original Repository menu intentionally supplies only the gitignore icon."); }
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Until(() => main.OwnedWindows.OfType<RulesFileEditorDialog>().Any(dialog => !dialog.IsBusy && dialog.Snapshot is not null));
                RulesFileEditorDialog editor = main.OwnedWindows.OfType<RulesFileEditorDialog>().Single();
                Assert.That(editor.Snapshot!.Kind, Is.EqualTo(kind));
                editor.Close(); await Until(() => !main.IsBusy && !editor.IsVisible);
                Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
            }
        }
        finally { await CloseAsync(main); }
    }

    [AvaloniaTest]
    public async Task CommitFileSettingsOpenBothEditorsAndIgnoreContextEscapesTheSelectedLiteralFileBeforeRefreshing()
    {
        using Fixture f = await Fixture.CreateAsync();
        const string literal = "a[1].tmp", other = "a1.tmp";
        await File.WriteAllTextAsync(f.PathInRepository(literal), "literal\n");
        await File.WriteAllTextAsync(f.PathInRepository(other), "other\n");
        CommitDialog commit = new(f.Repository); commit.Show();
        try
        {
            await commit.InitializeAsync(); await Until(() => !commit.IsBusy); Pump(commit);
            Assert.That(commit.LastError, Is.Null);
            FileStatusListView unstaged = Field<FileStatusListView>(commit, "UnstagedFileStatusList");
            foreach ((string name, GitRulesFileKind kind) in new[] { ("tsmiEditGitIgnore", GitRulesFileKind.Ignore), ("tmsiEditLocallyIgnoredFiles", GitRulesFileKind.LocalExclude) })
            {
                MenuItem action = unstaged.SettingsMenu.Items.OfType<MenuItem>().Single(item => item.Name == name);
                Assert.That(action.Icon, Is.TypeOf<Image>()); Assert.That(((Image)action.Icon!).Tag, Is.EqualTo("EditGitIgnore.png"));
                action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Until(() => commit.OwnedWindows.OfType<RulesFileEditorDialog>().Any(editor => !editor.IsBusy && editor.Snapshot is not null));
                RulesFileEditorDialog editor = commit.OwnedWindows.OfType<RulesFileEditorDialog>().Single();
                Assert.That(editor.Snapshot!.Kind, Is.EqualTo(kind)); editor.Close(); await Until(() => !commit.IsBusy);
            }
            unstaged.SelectTags(unstaged.FilteredEntries.Where(entry => entry.Path == literal).Select(entry => entry.Tag).ToArray());
            await Until(() => !commit.IsBusy);
            Assert.That(commit.LastError, Is.Null);
            Assert.That(unstaged.SelectedEntries.Select(entry => entry.Path), Is.EqualTo(new[] { literal }));
            ContextMenu menu = unstaged.Tree.ContextMenu!;
            bool openingObserved = false;
            menu.Opening += (_, _) => openingObserved = true;
            unstaged.Tree.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = unstaged.Tree });
            Pump(commit);
            Assert.That(menu.IsOpen, Is.True, "The Commit tree must open its real context menu through ContextRequested.");
            Assert.That(openingObserved, Is.True, "Opening must initialize the captured selection and availability before the menu action runs.");
            MenuItem append = menu.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiAddFileToGitIgnore");
            Assert.That(commit.IsBusy, Is.False, "The selected-file diff must finish before invoking a repository mutation.");
            Assert.That(append.IsEnabled, Is.True); Assert.That(((Image)append.Icon!).Tag, Is.EqualTo("AddToGitIgnore.png"));
            append.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.Close();
            await Until(() => commit.OwnedWindows.OfType<AddToGitIgnoreDialog>().Any(dialog => !dialog.IsBusy && !dialog.IsPreviewLoading),
                () => "Commit busy=" + commit.IsBusy + "; error=" + commit.LastError + "; children=" + string.Join("; ", commit.OwnedWindows.Select(window =>
                    window.Name + " visible=" + window.IsVisible + (window is AddToGitIgnoreDialog child ? " busy=" + child.IsBusy + " preview=" + child.IsPreviewLoading + " error=" + child.LastError : ""))));
            AddToGitIgnoreDialog child = commit.OwnedWindows.OfType<AddToGitIgnoreDialog>().Single();
            Assert.That(child.CurrentPatterns, Is.EqualTo(new[] { "/a\\[1\\].tmp" }));
            Assert.That(Preview(child), Is.EqualTo(new[] { literal }));
            Pointer(child, "AppendIgnorePatterns"); await Until(() => !child.IsVisible && !commit.IsBusy);
            Assert.That(commit.LastError, Is.Null);
            Assert.That(commit.HasRepositoryChanges, Is.True);
            Assert.That((await f.GitAsync("check-ignore", "--", literal)).StandardOutput.Trim(), Is.EqualTo(literal));
            GitResult otherIgnored = await new GitRunner().RunAsync(f.Repository.Path, ["check-ignore", "--", other]);
            Assert.That(otherIgnored.ExitCode, Is.EqualTo(1), "The selected brackets must be literal, not a wildcard matching another file.");
            Assert.That(unstaged.FilteredEntries.Select(entry => entry.Path), Does.Not.Contain(literal).And.Contain(other), "The Commit file list must refresh after rules change.");
        }
        finally { await CloseAsync(commit); }
    }

    private static string FileName(GitRulesFileKind kind) => kind switch
    {
        GitRulesFileKind.Ignore => ".gitignore", GitRulesFileKind.LocalExclude => ".git/info/exclude",
        GitRulesFileKind.Attributes => ".gitattributes", GitRulesFileKind.MailMap => ".mailmap", _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static async Task<RulesFileEditorDialog> ShowEditorAsync(GitRepository repository, GitRulesFileKind kind, Action<Uri>? links = null)
    {
        RulesFileEditorDialog dialog = new(repository, kind, links); dialog.Show(); await dialog.InitializeAsync(); Pump(dialog);
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); return dialog;
    }
    private static async Task<AddToGitIgnoreDialog> ShowAppendAsync(GitRepository repository, bool local = false, IEnumerable<string>? patterns = null)
    {
        AddToGitIgnoreDialog dialog = new(repository, local, patterns); dialog.Show(); await dialog.InitializeAsync();
        await Until(() => !dialog.IsBusy && !dialog.IsPreviewLoading); Pump(dialog);
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); return dialog;
    }
    private static void Edit(RulesFileEditorDialog dialog, string text)
    {
        Assert.That(dialog.Viewer.Editor.IsReadOnly, Is.False);
        dialog.Viewer.Editor.Document.Replace(0, dialog.Viewer.Editor.Document.TextLength, text);
    }
    private static string[] Preview(AddToGitIgnoreDialog dialog) => Field<ListBox>(dialog, "IgnorePatternPreview").Items.OfType<string>().ToArray();
    private static async Task<Window> SaveQuestionAsync(RulesFileEditorDialog dialog)
    {
        await Until(() => dialog.OwnedWindows.Any(window => window.Name == "SaveRulesFileQuestion"));
        return dialog.OwnedWindows.Single(window => window.Name == "SaveRulesFileQuestion");
    }
    private static async Task<GitResult> GitAtAsync(string path, params string[] arguments)
    {
        GitResult result = await new GitRunner().RunAsync(path, arguments); Assert.That(result.Success, Is.True, result.StandardError); return result;
    }
    private static T Field<T>(TopLevel window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pointer(Window window, string name) => PushProcessTestDriver.Pointer(Field<Button>(window, name));
    private static Task Idle(RepositoryDialog dialog) => Until(() => !dialog.IsBusy);
    private static async Task Until(Func<bool> condition, Func<string>? diagnostic = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "The real rules-file workflow did not settle within 20 seconds. " + (condition() ? "" : diagnostic?.Invoke()));
    }
    private static void Pump(TopLevel window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
    }
    private static void Capture(Window window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private static void AssertInside(Window window, params Control[] controls)
    {
        Pump(window);
        foreach (Control control in controls)
        {
            Point origin = control.TranslatePoint(default, window)!.Value;
            Assert.That(origin.X, Is.GreaterThanOrEqualTo(-.5), control.Name); Assert.That(origin.Y, Is.GreaterThanOrEqualTo(-.5), control.Name);
            Assert.That(origin.X + control.Bounds.Width, Is.LessThanOrEqualTo(window.ClientSize.Width + .5), control.Name);
            Assert.That(origin.Y + control.Bounds.Height, Is.LessThanOrEqualTo(window.ClientSize.Height + .5), control.Name);
        }
    }
    private static async Task CloseAsync(Window window)
    {
        foreach (Window child in window.OwnedWindows.ToArray())
        {
            if (child.Name == "SaveRulesFileQuestion") { Pointer(child, "RulesSaveNo"); }
            else { await CloseAsync(child); }
        }
        if (window is RulesFileEditorDialog editor && editor.IsVisible && editor.Viewer.IsModified)
        {
            editor.Close(); Window question = await SaveQuestionAsync(editor); Pointer(question, "RulesSaveNo");
        }
        else { window.Close(); }
        await Until(() => !window.IsVisible);
    }
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static int[] ReadActors(string path)
    {
        try { return File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray(); }
        catch (Exception error) when (error is IOException or FormatException) { return []; }
    }
    private static bool ActorAlive(int pid)
    {
        try
        {
            string stat = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
            return stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[0] != "Z";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
