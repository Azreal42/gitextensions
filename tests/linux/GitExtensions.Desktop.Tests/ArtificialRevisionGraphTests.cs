using System.Collections.Specialized;
using System.Reflection;
using System.Text.Json;
using GitExtensions.Desktop.Controls.Graph;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ArtificialRevisionGraphTests
{
    [Test]
    public void AnotherBranchCanArriveBeforeTheHeadAndItsPairIsAdmittedImmediatelyBeforeTheExactHead()
    {
        GitCommit other = Commit("O"), head = Commit("H", "R"), root = Commit("R");
        IncrementalCommitGraphLayout model = new(false, new(), head.Hash, includeArtificial: true);
        model.Append([other]);
        Assert.That(model.HasPendingArtificials, Is.True); Assert.That(Keys(model), Is.EqualTo(new[] { "O" }));
        Assert.That(model.Count, Is.EqualTo(1)); Assert.That(model.RowCount, Is.EqualTo(1));
        model.Append([head]);
        Assert.That(model.HasPendingArtificials, Is.False);
        Assert.That(Keys(model), Is.EqualTo(new[] { "O", W, I, "H" }));
        object[] admitted = model.OrderedItems.ToArray();
        model.Append([root]); model.Complete();
        Assert.That(Keys(model), Is.EqualTo(new[] { "O", W, I, "H", "R" }));
        Assert.That(model.Count, Is.EqualTo(3)); Assert.That(model.RowCount, Is.EqualTo(5));
        Assert.That(model.Commits, Is.EqualTo(new[] { other, head, root })); Assert.That(model.OrderedCommits, Is.EqualTo(new[] { other, head, root }));
        for (int row = 0; row < admitted.Length; row++) { Assert.That(model.OrderedItems[row], Is.SameAs(admitted[row])); }
        Assert.That(model.CachedCount, Is.Zero, "Admitting/inserting rows is not a request to build their lane cache.");
    }

    [Test]
    public void ADetachedCapturedHeadRequiresNoBranchNameOrCurrentReference()
    {
        string oid = new('a', 40); GitCommit detached = Commit(oid, "R");
        IncrementalCommitGraphLayout model = new(false, new(), oid, includeArtificial: true);
        model.Append([detached, Commit("R")]); model.Complete();
        Assert.That(Keys(model), Is.EqualTo(new[] { W, I, oid, "R" }));
        Assert.That(model.OrderedCommits[0], Is.SameAs(detached));
        Assert.That(model.IsRelative(oid), Is.True); Assert.That(model.IsRelative(W), Is.False); Assert.That(model.IsRelative(I), Is.False);
    }

    [Test]
    public void AHashWithTheSamePrefixCannotConsumeThePendingHeadPair()
    {
        string head = new('a', 40), similar = new string('a', 39) + "b";
        IncrementalCommitGraphLayout model = new(false, new(), head, includeArtificial: true);
        model.Append([Commit(similar)]); Assert.That(model.HasPendingArtificials, Is.True); Assert.That(model.RowCount, Is.EqualTo(1));
        model.Append([Commit(head)]);
        Assert.That(Keys(model), Is.EqualTo(new[] { similar, W, I, head })); Assert.That(model.HasPendingArtificials, Is.False);
    }

    [Test]
    public void AFilteredHeadUsesTheFirstProvidedVisibleAncestorForPositionWithoutInventingItsParentage()
    {
        GitCommit other = Commit("O"), ancestor = Commit("A", "R"), root = Commit("R");
        IncrementalCommitGraphLayout model = new(false, new(), "H", includeArtificial: true);
        model.Append([other, ancestor, root]); Assert.That(model.HasPendingArtificials, Is.True);
        model.Complete(["H", "not-loaded", "A", "R"]);
        Assert.That(Keys(model), Is.EqualTo(new[] { "O", W, I, "A", "R" }));
        ArtificialHistoryRevision index = model.OrderedItems.OfType<ArtificialHistoryRevision>().Single(item => item.Kind == GitCompareRevisionKind.Index);
        Assert.That(HistoryRevisionItems.Parents(index), Is.EqualTo(new[] { "H" }));
        Assert.That(model.GetChildren("A"), Does.Not.Contain(I)); Assert.That(model.GetChildren(I), Is.EqualTo(new[] { W }));
        Assert.That(Native(model).TryGetNode(I, out RevisionGraphRevision? indexNode), Is.True);
        Assert.That(indexNode!.Parents, Is.Empty, "Native Insert supplies WT→Index only; it must not connect Index to the display-position ancestor.");
        model.PrepareViewport(0, model.RowCount - 1);
        Assert.That(AllSegments(model).Any(segment => segment.Child == I && segment.Parent == "A"), Is.False);
        Assert.That(model.Count, Is.EqualTo(3)); Assert.That(model.OrderedCommits, Is.EqualTo(new[] { other, ancestor, root }));
    }

    [Test]
    [TestCase(false)] [TestCase(true)]
    public void AMissingHeadWithNoVisibleAncestorInsertsAtTheTop(bool suppliedButMissing)
    {
        IncrementalCommitGraphLayout model = new(false, new(), "H", includeArtificial: true);
        GitCommit one = Commit("O", "R"), root = Commit("R"); model.Append([one, root]);
        model.Complete(suppliedButMissing ? ["absent", "also-absent"] : null);
        Assert.That(Keys(model), Is.EqualTo(new[] { W, I, "O", "R" }));
        Assert.That(model.OrderedCommits, Is.EqualTo(new[] { one, root })); Assert.That(model.HasPendingArtificials, Is.False);
        Assert.That(((ArtificialHistoryRevision)model.OrderedItems[1]).ParentIds, Is.EqualTo(new[] { "H" }));
    }

    [Test]
    [TestCase(false)] [TestCase(true)]
    public void EmptyEofKeepsUnbornArtificialParentsTypedOrRemainsEmptyWhenCallerDisablesThem(bool includeArtificial)
    {
        IncrementalCommitGraphLayout model = new(false, new(), null, includeArtificial);
        Assert.That(model.HasPendingArtificials, Is.EqualTo(includeArtificial)); model.Complete();
        Assert.That(model.Count, Is.Zero); Assert.That(model.Commits, Is.Empty); Assert.That(model.OrderedCommits, Is.Empty);
        Assert.That(model.RowCount, Is.EqualTo(includeArtificial ? 2 : 0)); Assert.That(model.HasPendingArtificials, Is.False);
        if (!includeArtificial) { Assert.That(model.OrderedItems, Is.Empty); return; }
        Assert.That(Keys(model), Is.EqualTo(new[] { W, I }));
        Assert.That(HistoryRevisionItems.Parents(model.OrderedItems[0]), Is.EqualTo(new[] { I }));
        Assert.That(HistoryRevisionItems.Parents(model.OrderedItems[1]), Is.Empty);
        foreach (object item in model.OrderedItems)
        { Assert.That(HistoryRevisionItems.Revision(item).Revision, Is.Empty); Assert.That(item, Is.Not.TypeOf<GitCommit>()); }
        model.PrepareViewport(0, 1);
        Assert.That(model.GetPreparedItemNode(0)!.Item, Is.SameAs(model.OrderedItems[0]));
        Assert.That(model.GetPreparedItemNode(1)!.Item, Is.SameAs(model.OrderedItems[1]));
        Assert.That(model.GetPreparedNode(0), Is.Null); Assert.That(model.GetPreparedNode(1), Is.Null);
    }

    [Test]
    public void AnUnbornHeadWithOtherReachableReferencesStillAddsThePairBeforeTheFirstRealRevision()
    {
        GitCommit other = Commit("O", "R"), root = Commit("R");
        IncrementalCommitGraphLayout model = new(false, new(), null, includeArtificial: true);
        model.Append([other, root]); model.Complete();
        Assert.That(Keys(model), Is.EqualTo(new[] { W, I, "O", "R" }));
        Assert.That(((ArtificialHistoryRevision)model.OrderedItems[1]).ParentIds, Is.Empty);
        Assert.That(model.GetChildren("O"), Does.Not.Contain(I));
    }

    [Test]
    public void CallerOptOutForBareOrDisabledPreferenceNeverAddsEndpointsEvenWithMissingHeadAncestors()
    {
        GitCommit other = Commit("O", "R"), root = Commit("R");
        IncrementalCommitGraphLayout model = new(false, new(), "H", includeArtificial: false);
        model.Append([other, root]); model.Complete(["R"]);
        Assert.That(model.RowCount, Is.EqualTo(model.Count)); Assert.That(model.OrderedItems, Is.EqualTo(new object[] { other, root }));
        Assert.That(model.OrderedItems.OfType<ArtificialHistoryRevision>(), Is.Empty); Assert.That(model.HasPendingArtificials, Is.False);
    }

    [Test]
    public void DuplicateHeadAndOptionReplayPreserveTheSameArtificialAndRealObjectsWithoutDoubleAdmission()
    {
        GitCommit head = Commit("H", "R"), root = Commit("R");
        IncrementalCommitGraphLayout model = new(false, new(), "H", includeArtificial: true);
        model.Append([head, root]); object[] before = model.OrderedItems.ToArray();
        model.Append([head with { Subject = "must not replace" }]);
        Assert.That(model.Count, Is.EqualTo(2)); Assert.That(model.RowCount, Is.EqualTo(4));
        model.Reconfigure(false, new(false, false, false)); model.Complete(); model.Complete(["unused"]);
        Assert.That(Keys(model), Is.EqualTo(new[] { W, I, "H", "R" }));
        for (int row = 0; row < before.Length; row++) { Assert.That(model.OrderedItems[row], Is.SameAs(before[row])); }
        Assert.That(model.Commits[0], Is.SameAs(head)); Assert.That(model.RowCount, Is.EqualTo(4));
    }

    [Test]
    public void CompletedFallbackReplayKeepsItsCapturedAncestorListAndObjectsAfterCallerMutation()
    {
        IncrementalCommitGraphLayout model = new(false, new(), "H", includeArtificial: true);
        model.Append([Commit("O"), Commit("A", "R"), Commit("R")]); List<string> ancestors = ["A", "R"];
        model.Complete(ancestors); object[] before = model.OrderedItems.ToArray(); ancestors.Clear(); ancestors.Add("O");
        model.Reconfigure(false, new(false, true, false));
        Assert.That(Keys(model), Is.EqualTo(new[] { "O", W, I, "A", "R" }));
        for (int row = 0; row < before.Length; row++) { Assert.That(model.OrderedItems[row], Is.SameAs(before[row])); }
        Assert.That(model.IsComplete, Is.True); Assert.That(model.HasPendingArtificials, Is.False);
    }

    [Test]
    [TestCase(false)] [TestCase(true)]
    public void MixedNodesAndPathsUseTheExactNativeAddGeometryAndNeverProjectArtificialRowsAsGitCommits(bool merge)
    {
        RevisionGraphOptions options = new(merge, true, true);
        GitCommit other = Commit("O", "R"), head = Commit("H", "R"), root = Commit("R");
        IncrementalCommitGraphLayout model = new(false, options, "H", includeArtificial: true);
        model.Append([other, head, root]); model.Complete(); model.PrepareViewport(0, model.RowCount - 1);
        ArtificialHistoryRevision[] pair = model.OrderedItems.OfType<ArtificialHistoryRevision>().ToArray();
        RevisionGraph expected = new(options) { HeadId = "H" };
        expected.Add(Layout(other)); expected.Add(Layout(pair[0])); expected.Add(Layout(pair[1])); expected.Add(Layout(head)); expected.Add(Layout(root));
        expected.LoadingCompleted(); expected.CacheTo(expected.Count, expected.Count);
        Assert.That(Keys(model), Is.EqualTo(Enumerable.Range(0, expected.Count).Select(row => expected.GetNodeForRow(row)!.Objectid)));
        for (int row = 0; row < expected.Count; row++)
        {
            PreparedHistoryGraphNode node = model.GetPreparedItemNode(row)!;
            Assert.That(node.Item, Is.SameAs(model.OrderedItems[row]));
            Assert.That(node.Lane, Is.EqualTo(expected.GetSegmentsForRow(row)!.GetCurrentRevisionLane()));
            Assert.That(JsonSerializer.Serialize(model.RenderSegmentsForRow(row, 12, 24, true, null)),
                Is.EqualTo(JsonSerializer.Serialize(RevisionGraphRenderer.GetSegmentsForRow(expected, row, 12, 24, true))));
            if (node.Item is ArtificialHistoryRevision) { Assert.That(model.GetPreparedNode(row), Is.Null); }
            else { Assert.That(model.GetPreparedNode(row)!.Commit, Is.SameAs(node.Item)); }
        }
        Assert.That(AllSegments(model).Any(segment => segment.Child == W && segment.Parent == I), Is.True);
        Assert.That(AllSegments(model).Any(segment => segment.Child == I && segment.Parent == "H"), Is.True);
    }

    [Test]
    public void NativeHeadAncestorsAreRelativeWhileTheirOtherChildrenAndTheArtificialDescendantsRemainNonRelative()
    {
        IncrementalCommitGraphLayout model = new(false, new(), "H", includeArtificial: true);
        model.Append([Commit("O", "R"), Commit("H", "P"), Commit("P", "R"), Commit("R")]);
        Assert.That(new[] { "H", "P", "R" }.Select(model.IsRelative), Is.All.True);
        Assert.That(new[] { "O", W, I, "absent" }.Select(model.IsRelative), Is.All.False);
    }

    [Test]
    public void AStableMixedSourceUsesRealCollectionMovesAndKeysWithoutReplacingAnyAdmittedObject()
    {
        GitCommit other = Commit("O"), head = Commit("H");
        ArtificialHistoryRevision working = new(GitCompareRevisionKind.WorkingTree, "H"), index = new(GitCompareRevisionKind.Index, "H");
        IncrementalRevisionRows source = new(); object identity = source; List<NotifyCollectionChangedAction> events = [];
        source.CollectionChanged += (_, change) => events.Add(change.Action);
        source.PublishOrder([other]); source.PublishOrder([other, working, index, head]); source.PublishOrder([working, index, other, head]);
        Assert.That(source, Is.SameAs(identity)); Assert.That(source.Cast<object>(), Is.EqualTo(new object[] { working, index, other, head }));
        Assert.That(events, Does.Contain(NotifyCollectionChangedAction.Move).And.Not.Contain(NotifyCollectionChangedAction.Reset));
        Assert.That(source.IndexOfKey(W), Is.Zero); Assert.That(source.KeyIndex(I), Is.EqualTo(1)); Assert.That(source.IndexOfHash("H"), Is.EqualTo(3));
        source.PublishOrder([new ArtificialHistoryRevision(GitCompareRevisionKind.WorkingTree, "H"), index, other, head]);
        Assert.That(source[0], Is.SameAs(working), "A repeated view key cannot replace the admitted endpoint object.");
    }

    private const string W = ArtificialHistoryRevision.WorkingTreeKey, I = ArtificialHistoryRevision.IndexKey;
    private static string[] Keys(IncrementalCommitGraphLayout model) => model.OrderedItems.Select(HistoryRevisionItems.Key).ToArray();
    private static GitCommit Commit(string key, params string[] parents) => new(key, parents, "Tester", "tester@example.invalid", DateTimeOffset.UnixEpoch, key, "");
    private static LayoutRevision Layout(object item) => new(HistoryRevisionItems.Key(item), HistoryRevisionItems.Parents(item), HistoryRevisionItems.Subject(item));
    private static IEnumerable<GraphRenderSegment> AllSegments(IncrementalCommitGraphLayout model)
        => Enumerable.Range(0, model.RowCount).SelectMany(row => model.RenderSegmentsForRow(row, 12, 24, true, null));
    private static RevisionGraph Native(IncrementalCommitGraphLayout model) => (RevisionGraph)typeof(IncrementalCommitGraphLayout)
        .GetField("_graph", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
}
