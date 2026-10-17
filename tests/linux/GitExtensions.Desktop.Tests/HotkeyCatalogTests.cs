using Avalonia.Input;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public sealed class HotkeyCatalogTests
{
    // Windows oracle: CreateDefaultSettings plus explicit Command enum values; never enum declaration order.
    [Test]
    public void All171WindowsDefaultsKeepTheNineContextsExplicitIdsNamesAndPresentationOrder()
    {
        Assert.That(HotkeyCatalog.Contexts, Is.EqualTo(new[] { "Commit", "Browse", "LeftPanel", "RevisionGrid", "FileViewer", "FormMergeConflicts", "BrowseDiff", "Stash", "Scripts" }));
        Dictionary<string, Expected[]> expected = new(StringComparer.Ordinal)
        {
            ["Commit"] =
            [
                new(16, "AddSelectionToCommitMessage", Key.C, KeyModifiers.None),
                new(25, "ConventionalCommit_PrefixMessage", Key.T, KeyModifiers.Control),
                new(26, "ConventionalCommit_PrefixMessageWithScope", Key.T, KeyModifiers.Control | KeyModifiers.Shift),
                new(17, "CreateBranch", Key.B, KeyModifiers.Control),
                new(2, "FocusUnstagedFiles", Key.D1, KeyModifiers.Control),
                new(3, "FocusSelectedDiff", Key.D2, KeyModifiers.Control),
                new(4, "FocusStagedFiles", Key.D3, KeyModifiers.Control),
                new(5, "FocusCommitMessage", Key.D4, KeyModifiers.Control),
                new(12, "OpenWithDifftool", Key.F3, KeyModifiers.None),
                new(18, "Refresh", Key.F5, KeyModifiers.None),
                new(19, "SelectNext", Key.N, KeyModifiers.Control),
                new(20, "SelectNext_AlternativeHotkey1", Key.Down, KeyModifiers.Alt),
                new(21, "SelectNext_AlternativeHotkey2", Key.Right, KeyModifiers.Alt),
                new(22, "SelectPrevious", Key.P, KeyModifiers.Control),
                new(23, "SelectPrevious_AlternativeHotkey1", Key.Up, KeyModifiers.Alt),
                new(24, "SelectPrevious_AlternativeHotkey2", Key.Left, KeyModifiers.Alt),
                new(11, "StageAll", Key.S, KeyModifiers.Control),
                new(10, "ToggleSelectionFilter", Key.F, KeyModifiers.Control),
            ],
            ["Browse"] =
            [
                new(8, "AddNotes", Key.N, KeyModifiers.Control | KeyModifiers.Shift),
                new(10, "CheckoutBranch", Key.OemPeriod, KeyModifiers.Control),
                new(15, "CloseRepository", Key.W, KeyModifiers.Control),
                new(7, "Commit", Key.Space, KeyModifiers.Control),
                new(41, "CreateBranch", Key.B, KeyModifiers.Control),
                new(43, "CreateTag", Key.T, KeyModifiers.Control),
                new(22, "EditFile", Key.F4, KeyModifiers.None),
                new(9, "FindFileInSelectedCommit", Key.F, KeyModifiers.Control | KeyModifiers.Shift),
                new(25, "FocusLeftPanel", Key.D0, KeyModifiers.Control),
                new(3, "FocusRevisionGrid", Key.D1, KeyModifiers.Control),
                new(4, "FocusCommitInfo", Key.D2, KeyModifiers.Control),
                new(5, "FocusDiff", Key.D3, KeyModifiers.Control),
                new(6, "FocusFileTree", Key.D4, KeyModifiers.Control),
                new(26, "FocusGpgInfo", Key.D5, KeyModifiers.Control),
                new(29, "FocusGitConsole", Key.D6, KeyModifiers.Control),
                new(30, "FocusBuildServerStatus", Key.D7, KeyModifiers.Control),
                new(47, "FocusOutputHistoryAndToggleIfPanel", Key.D9, KeyModifiers.Control),
                new(31, "FocusNextTab", Key.Tab, KeyModifiers.Control),
                new(32, "FocusPrevTab", Key.Tab, KeyModifiers.Control | KeyModifiers.Shift),
                new(18, "FocusFilter", Key.E, KeyModifiers.Control),
                new(0, "GitBash", Key.G, KeyModifiers.Control),
                new(1, "GitGui", null, KeyModifiers.None),
                new(2, "GitGitK", null, KeyModifiers.None),
                new(37, "GoToChild", Key.N, KeyModifiers.Control),
                new(38, "GoToParent", Key.P, KeyModifiers.Control),
                new(28, "GoToSubmodule", null, KeyModifiers.None),
                new(27, "GoToSuperproject", null, KeyModifiers.None),
                new(49, "ManageWorkTrees", Key.W, KeyModifiers.Control | KeyModifiers.Alt),
                new(42, "MergeBranches", Key.M, KeyModifiers.Control),
                new(23, "OpenAsTempFile", Key.F3, KeyModifiers.Control),
                new(24, "OpenAsTempFileWith", Key.F3, KeyModifiers.Control | KeyModifiers.Shift),
                new(35, "OpenCommitsWithDifftool", null, KeyModifiers.None),
                new(45, "OpenRepo", Key.O, KeyModifiers.Control),
                new(20, "OpenSettings", Key.OemComma, KeyModifiers.Control),
                new(19, "OpenWithDifftool", Key.F3, KeyModifiers.None),
                new(33, "OpenWithDifftoolFirstToLocal", Key.F3, KeyModifiers.Alt),
                new(34, "OpenWithDifftoolSelectedToLocal", Key.F3, KeyModifiers.Shift | KeyModifiers.Alt),
                new(39, "PullOrFetch", Key.Down, KeyModifiers.Control),
                new(40, "Push", Key.Up, KeyModifiers.Control),
                new(11, "QuickFetch", Key.Down, KeyModifiers.Control | KeyModifiers.Shift),
                new(12, "QuickPull", Key.P, KeyModifiers.Control | KeyModifiers.Shift),
                new(48, "QuickPullOrFetch", Key.F8, KeyModifiers.None),
                new(13, "QuickPush", Key.Up, KeyModifiers.Control | KeyModifiers.Shift),
                new(44, "Rebase", Key.E, KeyModifiers.Control | KeyModifiers.Shift),
                new(16, "Stash", Key.Up, KeyModifiers.Control | KeyModifiers.Alt),
                new(17, "StashPop", Key.Down, KeyModifiers.Control | KeyModifiers.Alt),
                new(46, "StashStaged", Key.Up, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt),
                new(36, "ToggleBetweenArtificialAndHeadCommits", Key.OemBackslash, KeyModifiers.Control),
                new(21, "ToggleLeftPanel", Key.C, KeyModifiers.Control | KeyModifiers.Alt),
            ],
            ["LeftPanel"] =
            [
                new(0, "Delete", Key.Delete, KeyModifiers.None),
                new(3, "MultiSelect", Key.Space, KeyModifiers.Control),
                new(4, "MultiSelectWithChildren", Key.Space, KeyModifiers.Control | KeyModifiers.Shift),
                new(1, "Rename", Key.F2, KeyModifiers.None),
                new(2, "Search", Key.F3, KeyModifiers.None),
            ],
            ["RevisionGrid"] =
            [
                new(30, "CompareSelectedCommits", null, KeyModifiers.None),
                new(24, "CompareToBase", Key.R, KeyModifiers.Control),
                new(29, "CompareToBranch", null, KeyModifiers.None),
                new(28, "CompareToCurrentBranch", null, KeyModifiers.None),
                new(27, "CompareToWorkingDirectory", Key.D, KeyModifiers.Control),
                new(44, "CreateAmendCommit", null, KeyModifiers.None),
                new(25, "CreateFixupCommit", Key.X, KeyModifiers.Control),
                new(43, "CreateSquashCommit", Key.X, KeyModifiers.Control | KeyModifiers.Shift),
                new(41, "DeleteRef", Key.Delete, KeyModifiers.None),
                new(15, "GoToChild", Key.N, KeyModifiers.Control),
                new(20, "GoToCommit", Key.G, KeyModifiers.Control | KeyModifiers.Shift),
                new(45, "GoToFirstParent", Key.Left, KeyModifiers.Control),
                new(46, "GoToLastParent", Key.Right, KeyModifiers.Control),
                new(31, "GoToMergeBase", Key.K, KeyModifiers.Control | KeyModifiers.Shift),
                new(14, "GoToParent", Key.P, KeyModifiers.Control),
                new(21, "NavigateBackward", Key.Left, KeyModifiers.Alt),
                new(39, "NavigateBackward_AlternativeHotkey", Key.BrowserBack, KeyModifiers.None),
                new(22, "NavigateForward", Key.Right, KeyModifiers.Alt),
                new(40, "NavigateForward_AlternativeHotkey", Key.BrowserForward, KeyModifiers.None),
                new(17, "NextQuickSearch", Key.Down, KeyModifiers.Alt),
                new(32, "OpenCommitsWithDifftool", null, KeyModifiers.None),
                new(18, "PrevQuickSearch", Key.Up, KeyModifiers.Alt),
                new(42, "RenameRef", Key.F2, KeyModifiers.None),
                new(37, "ResetRevisionPathFilter", Key.H, KeyModifiers.Control | KeyModifiers.Shift),
                new(36, "ResetRevisionFilter", Key.I, KeyModifiers.Control | KeyModifiers.Shift),
                new(1, "RevisionFilter", Key.I, KeyModifiers.Control),
                new(23, "SelectAsBaseToCompare", Key.L, KeyModifiers.Control),
                new(19, "SelectCurrentRevision", Key.C, KeyModifiers.Control | KeyModifiers.Shift),
                new(38, "SelectNextForkPointAsDiffBase", Key.K, KeyModifiers.Control),
                new(9, "ShowAllBranches", Key.A, KeyModifiers.Control | KeyModifiers.Shift),
                new(10, "ShowCurrentBranchOnly", Key.U, KeyModifiers.Control | KeyModifiers.Shift),
                new(11, "ShowFilteredBranches", Key.T, KeyModifiers.Control | KeyModifiers.Shift),
                new(13, "ShowFirstParent", Key.S, KeyModifiers.Control | KeyModifiers.Shift),
                new(34, "ShowReflogReferences", Key.L, KeyModifiers.Control | KeyModifiers.Shift),
                new(12, "ShowRemoteBranches", Key.R, KeyModifiers.Control | KeyModifiers.Shift),
                new(2, "ToggleAuthorDateCommitDate", null, KeyModifiers.None),
                new(33, "ToggleBetweenArtificialAndHeadCommits", Key.OemBackslash, KeyModifiers.Control),
                new(5, "ToggleDrawNonRelativesGray", null, KeyModifiers.None),
                new(16, "ToggleHighlightSelectedBranch", Key.B, KeyModifiers.Control | KeyModifiers.Shift),
                new(3, "ToggleOrderRevisionsByDate", null, KeyModifiers.None),
                new(0, "ToggleRevisionGraph", null, KeyModifiers.None),
                new(6, "ToggleShowGitNotes", null, KeyModifiers.None),
                new(47, "ToggleShowGitNotesColumn", null, KeyModifiers.None),
                new(8, "ToggleHideMergeCommits", Key.M, KeyModifiers.Control | KeyModifiers.Shift),
                new(4, "ToggleShowRelativeDate", null, KeyModifiers.None),
                new(26, "ToggleShowTags", Key.T, KeyModifiers.Control | KeyModifiers.Alt),
            ],
            ["FileViewer"] =
            [
                new(0, "Find", Key.F, KeyModifiers.Control),
                new(16, "Replace", Key.H, KeyModifiers.Control),
                new(8, "FindNextOrOpenWithDifftool", Key.F3, KeyModifiers.None),
                new(9, "FindPrevious", Key.F3, KeyModifiers.Shift),
                new(1, "GoToLine", Key.G, KeyModifiers.Control),
                new(2, "IncreaseNumberOfVisibleLines", Key.OemPlus, KeyModifiers.Control),
                new(3, "DecreaseNumberOfVisibleLines", Key.OemMinus, KeyModifiers.Control),
                new(6, "NextChange", Key.Down, KeyModifiers.Alt),
                new(7, "PreviousChange", Key.Up, KeyModifiers.Alt),
                new(4, "ShowEntireFile", Key.E, KeyModifiers.Control),
                new(17, "ShowSyntaxHighlighting", Key.X, KeyModifiers.None),
                new(18, "ShowGitWordColoring", Key.D, KeyModifiers.Control),
                new(19, "ShowDifftastic", Key.T, KeyModifiers.Control),
                new(5, "TreatFileAsText", null, KeyModifiers.None),
                new(10, "NextOccurrence", Key.Right, KeyModifiers.Alt),
                new(11, "PreviousOccurrence", Key.Left, KeyModifiers.Alt),
                new(12, "StageLines", Key.S, KeyModifiers.None),
                new(13, "UnstageLines", Key.U, KeyModifiers.None),
                new(14, "ResetLines", Key.R, KeyModifiers.None),
                new(15, "IgnoreAllWhitespace", Key.W, KeyModifiers.Control | KeyModifiers.Shift),
            ],
            ["FormMergeConflicts"] =
            [
                new(4, "ChooseBase", Key.B, KeyModifiers.None),
                new(3, "ChooseLocal", Key.L, KeyModifiers.None),
                new(2, "ChooseRemote", Key.R, KeyModifiers.None),
                new(0, "Merge", Key.M, KeyModifiers.None),
                new(1, "Rescan", Key.F5, KeyModifiers.None),
            ],
            ["BrowseDiff"] =
            [
                new(22, "AddFileToGitIgnore", null, KeyModifiers.None),
                new(2, "Blame", Key.B, KeyModifiers.None),
                new(0, "DeleteSelectedFiles", Key.Delete, KeyModifiers.None),
                new(4, "EditFile", Key.F4, KeyModifiers.None),
                new(13, "FilterFileInGrid", Key.F, KeyModifiers.None),
                new(15, "FindFile", null, KeyModifiers.None),
                new(17, "FindInCommitFilesUsingGitGrep_DiffTab", Key.F, KeyModifiers.Control | KeyModifiers.Shift),
                new(24, "FindInCommitFilesUsingGitGrep_FileTreeTab", null, KeyModifiers.None),
                new(18, "GoToFirstParent", Key.Left, KeyModifiers.Control),
                new(19, "GoToLastParent", Key.Right, KeyModifiers.Control),
                new(5, "OpenAsTempFile", Key.F3, KeyModifiers.Control),
                new(6, "OpenAsTempFileWith", Key.F3, KeyModifiers.Control | KeyModifiers.Shift),
                new(21, "OpenInVisualStudio", Key.S, KeyModifiers.Control | KeyModifiers.Shift),
                new(3, "OpenWithDifftool", Key.F3, KeyModifiers.None),
                new(7, "OpenWithDifftoolFirstToLocal", Key.F3, KeyModifiers.Alt),
                new(8, "OpenWithDifftoolSelectedToLocal", Key.F3, KeyModifiers.Shift | KeyModifiers.Alt),
                new(20, "OpenWorkingDirectoryFile", Key.F4, KeyModifiers.Shift),
                new(16, "OpenWorkingDirectoryFileWith", Key.F4, KeyModifiers.Control | KeyModifiers.Shift),
                new(23, "RenameMove", Key.F2, KeyModifiers.None),
                new(9, "ResetSelectedFiles", Key.R, KeyModifiers.None),
                new(14, "SelectFirstGroupChanges", Key.A, KeyModifiers.Control),
                new(12, "ShowFileTree", Key.T, KeyModifiers.None),
                new(1, "ShowHistory", Key.H, KeyModifiers.None),
                new(10, "StageSelectedFile", Key.S, KeyModifiers.None),
                new(11, "UnStageSelectedFile", Key.U, KeyModifiers.None),
            ],
            ["Stash"] =
            [
                new(0, "NextStash", Key.N, KeyModifiers.Control),
                new(1, "PreviousStash", Key.P, KeyModifiers.Control),
                new(2, "Refresh", Key.F5, KeyModifiers.None),
            ],
            ["Scripts"] =
            [
            ],
        };

        Assert.That(HotkeyCatalog.Contexts.Sum(context => HotkeyCatalog.For(context).Count), Is.EqualTo(171));
        foreach ((string context, Expected[] rows) in expected)
        {
            IReadOnlyList<HotkeyCommand> actual = HotkeyCatalog.For(context);
            Assert.That(actual.Count, Is.EqualTo(rows.Length), context);
            for (int i = 0; i < rows.Length; i++)
            {
                Expected row = rows[i];
                HotkeyCommand command = actual[i];
                Assert.Multiple(() =>
                {
                    Assert.That(command.Context, Is.EqualTo(context));
                    Assert.That(command.Code, Is.EqualTo(row.Code), $"{context} row {i}");
                    Assert.That(command.Name, Is.EqualTo(row.Name));
                    Assert.That(command.DefaultGesture?.Key, Is.EqualTo(row.Key), $"{context}:{row.Code}");
                    Assert.That(command.DefaultGesture?.KeyModifiers ?? KeyModifiers.None, Is.EqualTo(row.Modifiers), $"{context}:{row.Code}");
                    Assert.That(HotkeyCatalog.Find(context, row.Code), Is.SameAs(command));
                    Assert.That(HotkeyCatalog.Find(context, row.Name), Is.SameAs(command));
                });
            }
        }
    }
    [Test]
    public void ContextAndCommandListsCannotBeModifiedThroughTheirCollectionInterfaces()
    {
        Assert.Throws<NotSupportedException>(() => ((IList<string>)HotkeyCatalog.Contexts)[0] = "Changed");
        IReadOnlyList<HotkeyCommand> commands = HotkeyCatalog.For("Commit");
        Assert.Throws<NotSupportedException>(() => ((IList<HotkeyCommand>)commands)[0] = commands[0] with { Name = "Changed" });
        Assert.That(HotkeyCatalog.Contexts[0], Is.EqualTo("Commit"));
        Assert.That(HotkeyCatalog.For("Commit")[0].Name, Is.EqualTo("AddSelectionToCommitMessage"));
    }

    [Test]
    public void LookupIsOrdinalAndDoesNotInventScriptsReservedIdsOrAMissingPreset()
    {
        Assert.That(HotkeyCatalog.For("browse"), Is.Empty);
        Assert.That(HotkeyCatalog.Find("Browse", "createtag"), Is.Null);
        Assert.That(HotkeyCatalog.Find("Commit", 0), Is.Null, "Obsolete Commit slots must not be reused.");
        Assert.That(HotkeyCatalog.Find("RevisionGrid", 35), Is.Null, "ShowStashes is declared in Windows but has no Hk preset.");
        Assert.That(HotkeyCatalog.Find("RevisionGrid", 3)!.DefaultGesture, Is.Null, "A real None preset differs from the missing command above.");
        Assert.That(HotkeyCatalog.For("Scripts"), Is.Empty);
        Assert.That(HotkeyCatalog.Find("Scripts", 9000), Is.Null, "Do not import disabled Windows examples as shortcuts.");
        HotkeyBindings bindings = new();
        Assert.That(bindings.Get("unknown", "NoSuchCommand"), Is.Null);
        Assert.That(bindings.Get("Browse", -1), Is.Null);
        Assert.That(bindings.Get("Browse", "NoSuchCommand"), Is.Null);
    }

    [Test]
    public void ConstructAndResetRestoreEveryDefaultWhileAnExistingDraftStaysIndependent()
    {
        HotkeyBindings bindings = new();
        foreach (string context in HotkeyCatalog.Contexts)
        {
            foreach (HotkeyCommand command in HotkeyCatalog.For(context))
            {
                Assert.That(Describe(bindings.Get(context, command.Name)), Is.EqualTo(Describe(command.DefaultGesture)));
                bindings.Set(context, command.Code, null);
            }
        }

        HotkeyBindings cleared = bindings.Clone();
        bindings.ResetToDefaults();
        foreach (string context in HotkeyCatalog.Contexts)
        {
            foreach (HotkeyCommand command in HotkeyCatalog.For(context))
            {
                Assert.That(Describe(bindings.Get(context, command.Code)), Is.EqualTo(Describe(command.DefaultGesture)));
                Assert.That(cleared.Get(context, command.Code), Is.Null, "Resetting one draft must not reset another.");
            }
        }
    }

    [Test]
    public void ACloneKeepsIndependentUnassignmentsAndDistinctAlternativeCommandIds()
    {
        HotkeyBindings original = new();
        HotkeyBindings draft = original.Clone();
        draft.Set("Commit", 19, null);
        draft.Set("Commit", 20, new(Key.F11, KeyModifiers.Meta));
        Assert.That(original.Get("Commit", 19)!.Key, Is.EqualTo(Key.N));
        Assert.That(original.Get("Commit", 20)!.Key, Is.EqualTo(Key.Down));
        Assert.That(draft.Get("Commit", "SelectNext"), Is.Null);
        Assert.That(draft.Get("Commit", "SelectNext_AlternativeHotkey1")!.KeyModifiers, Is.EqualTo(KeyModifiers.Meta));
        Assert.That(draft.Get("Commit", "SelectNext_AlternativeHotkey2")!.Key, Is.EqualTo(Key.Right));
        original.Set("Commit", 21, null);
        Assert.That(draft.Get("Commit", 21), Is.Not.Null);
    }

    [TestCase("Unknown", 16)]
    [TestCase("Browse", 99999)]
    [TestCase("Commit", 0)]
    [TestCase("Scripts", 9000)]
    public void SettingAnUnknownIdentityFailsWithoutChangingARealBinding(string context, int code)
    {
        HotkeyBindings bindings = new();
        string before = Describe(bindings.Get("Browse", 43));
        Assert.Throws<ArgumentException>(() => bindings.Set(context, code, new(Key.F11)));
        Assert.That(Describe(bindings.Get("Browse", 43)), Is.EqualTo(before));
        Assert.That(HotkeyCatalog.Find(context, code), Is.Null);
    }

    [TestCase(Key.None, KeyModifiers.None)]
    [TestCase((Key)99999, KeyModifiers.Control)]
    [TestCase(Key.A, (KeyModifiers)256)]
    public void InvalidLogicalKeysOrModifierBitsAreNotAcceptedAsAnotherUnassignment(Key key, KeyModifiers modifiers)
    {
        HotkeyBindings bindings = new();
        string before = Describe(bindings.Get("FileViewer", 0));
        Assert.Throws<ArgumentException>(() => bindings.Set("FileViewer", 0, new(key, modifiers)));
        Assert.That(Describe(bindings.Get("FileViewer", 0)), Is.EqualTo(before));
        bindings.Set("FileViewer", 0, null);
        Assert.That(bindings.Get("FileViewer", 0), Is.Null);
    }

    [Test]
    public void GlobalWarningIncludesTheCurrentAssignmentAndAcceptsCrossContextCollisions()
    {
        HotkeyBindings bindings = new();
        foreach (string context in HotkeyCatalog.Contexts)
        {
            foreach (HotkeyCommand command in HotkeyCatalog.For(context))
            {
                bindings.Set(context, command.Code, null);
            }
        }

        KeyGesture assigned = new(Key.F11, KeyModifiers.Meta | KeyModifiers.Shift);
        bindings.Set("Commit", 19, assigned);
        Assert.That(bindings.IsAssigned(new(Key.F11, KeyModifiers.Meta | KeyModifiers.Shift)), Is.True, "The command's own assignment counts.");
        Assert.That(bindings.IsAssigned(new(Key.F11, KeyModifiers.Meta)), Is.False);
        Assert.That(bindings.IsAssigned(null), Is.False);
        bindings.Set("FileViewer", 0, assigned);
        bindings.Set("Commit", 19, null);
        Assert.That(bindings.IsAssigned(assigned), Is.True, "A different context still owns the same gesture.");
        bindings.Set("FileViewer", 0, null);
        Assert.That(bindings.IsAssigned(assigned), Is.False);
    }

    [Test]
    public void MatchingUsesWindowsPresentationOrderRatherThanNumericCodeOrInsertionOrder()
    {
        HotkeyBindings bindings = new();
        KeyGesture collision = new(Key.F11, KeyModifiers.Meta);
        bindings.Set("Browse", 0, collision); // GitBash is later in the Windows catalog.
        bindings.Set("Browse", 8, collision); // AddNotes is the first entry, although its ID is greater.
        KeyEventArgs args = new() { Key = Key.F11, KeyModifiers = KeyModifiers.Meta };
        Assert.That(bindings.Match("Browse", args), Is.SameAs(HotkeyCatalog.Find("Browse", 8)));
        Assert.That(args.Handled, Is.False, "Resolution does not execute or consume an event.");
        bindings.Set("Browse", 8, null);
        Assert.That(bindings.Match("Browse", args)!.Code, Is.EqualTo(0));
        Assert.That(bindings.Match("FileViewer", args), Is.Null);
    }

    [Test]
    public void ASharedDefaultChordResolvesToEachScopeOwnStableCommand()
    {
        HotkeyBindings bindings = new();
        KeyEventArgs args = new() { Key = Key.T, KeyModifiers = KeyModifiers.Control };
        Assert.That(bindings.Match("Browse", args)!.Code, Is.EqualTo(43));
        Assert.That(bindings.Match("Commit", args)!.Code, Is.EqualTo(25));
        Assert.That(bindings.Match("FileViewer", args)!.Code, Is.EqualTo(19));
        bindings.Set("Browse", 43, null);
        Assert.That(bindings.Match("Browse", args), Is.Null);
        Assert.That(bindings.Match("FileViewer", args)!.Name, Is.EqualTo("ShowDifftastic"));
    }

    [Test]
    public void MatchingIsExactAndDoesNotInventKeypadAliasesOrIgnoreExtraModifiers()
    {
        HotkeyBindings bindings = new();
        Assert.That(bindings.Match("FileViewer", new() { Key = Key.G, KeyModifiers = KeyModifiers.Control })!.Name, Is.EqualTo("GoToLine"));
        Assert.That(bindings.Match("FileViewer", new() { Key = Key.G, KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift }), Is.Null);
        Assert.That(bindings.Match("FileViewer", new() { Key = Key.OemPlus, KeyModifiers = KeyModifiers.Control })!.Code, Is.EqualTo(2));
        Assert.That(bindings.Match("FileViewer", new() { Key = Key.Add, KeyModifiers = KeyModifiers.Control }), Is.Null);
        Assert.That(bindings.Match("FileViewer", new() { Key = Key.None }), Is.Null);
        Assert.That(bindings.Match("Unknown", new() { Key = Key.G, KeyModifiers = KeyModifiers.Control }), Is.Null);
    }

    [Test]
    public void MetaRemainsAnIndependentLinuxModifierAndAllFourFlagsCanBeCombined()
    {
        HotkeyBindings bindings = new();
        bindings.Set("FileViewer", 0, new(Key.K, KeyModifiers.Meta));
        Assert.That(bindings.Match("FileViewer", new() { Key = Key.K, KeyModifiers = KeyModifiers.Meta })!.Code, Is.EqualTo(0));
        Assert.That(bindings.Match("FileViewer", new() { Key = Key.K, KeyModifiers = KeyModifiers.Control }), Is.Null);
        KeyModifiers all = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta;
        bindings.Set("FileViewer", 0, new(Key.K, all));
        Assert.That(bindings.Match("FileViewer", new() { Key = Key.K, KeyModifiers = all })!.Code, Is.EqualTo(0));
        Assert.That(bindings.IsAssigned(new(Key.K, all)), Is.True);
    }

    private static string Describe(KeyGesture? gesture) => gesture is null ? "None" : $"{gesture.Key}|{gesture.KeyModifiers}";
    private sealed record Expected(int Code, string Name, Key? Key, KeyModifiers Modifiers);
}
