using GitExtensions.Desktop.Controls.Graph;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public sealed class ParentChildNavigationHistoryTests
{
    private static readonly GitCompareRevision Parent = new(new string('a', 40));
    private static readonly GitCompareRevision OtherParent = new(new string('b', 40));
    private static readonly GitCompareRevision Child = new(new string('c', 40));
    private static readonly GitCompareRevision Grandchild = new(new string('d', 64));
    private static readonly GitCompareRevision WorkingTree = new("", GitCompareRevisionKind.WorkingTree);
    private static readonly GitCompareRevision Index = new("", GitCompareRevisionKind.Index);

    [TestCase("Parent")]
    [TestCase("FirstParent")]
    [TestCase("LastParent")]
    [TestCase("Child")]
    public void NoNeighbourAndNoRememberedRouteDoesNotPublishASelection(string command)
    {
        List<GitCompareRevision> selected = [];
        ParentChildNavigationHistory history = new(selected.Add);

        Assert.That(Execute(history, command, Child, []), Is.False);
        Assert.That(selected, Is.Empty);
        Assert.That(history.HasPreviousChild || history.HasPreviousParent, Is.False);
    }

    [Test]
    public void ParentReturnsToTheOriginalSecondParentAfterVisitingItsMergeChild()
    {
        List<GitCompareRevision> selected = [];
        ParentChildNavigationHistory history = new(selected.Add);
        // Child is a merge whose native parent order is OtherParent, Parent.
        Assert.That(history.NavigateChild(Parent, [Child]), Is.True);
        Assert.That(history.NavigateParent(Child, [OtherParent, Parent]), Is.True);

        Assert.That(selected, Is.EqualTo(new[] { Child, Parent }));
        Assert.That(history.HasPreviousChild, Is.True);
        Assert.That(history.HasPreviousParent, Is.False);
    }

    [Test]
    public void ChildReturnsToTheVisitedChildInsteadOfChoosingAnotherFirstChild()
    {
        List<GitCompareRevision> selected = [];
        ParentChildNavigationHistory history = new(selected.Add);
        Assert.That(history.NavigateParent(Child, [Parent]), Is.True);
        Assert.That(history.NavigateChild(Parent, [Grandchild, Child]), Is.True);

        Assert.That(selected, Is.EqualTo(new[] { Parent, Child }));
        Assert.That(history.HasPreviousChild, Is.False);
        Assert.That(history.HasPreviousParent, Is.True);
    }

    [TestCase("FirstParent")]
    [TestCase("LastParent")]
    public void ExplicitParentCommandsBypassTheRememberedParentAndKeepSuppliedOrder(string command)
    {
        List<GitCompareRevision> selected = [];
        ParentChildNavigationHistory history = new(selected.Add);
        history.NavigateChild(Parent, [Child]);
        GitCompareRevision[] parents = [Grandchild, Parent, OtherParent];
        GitCompareRevision expected = command == "FirstParent" ? Grandchild : OtherParent;

        Assert.That(Execute(history, command, Child, parents), Is.True);
        Assert.That(selected[^1], Is.SameAs(expected));
        Assert.That(history.HasPreviousParent, Is.True, "Explicit first/last must not pop the remembered Parent.");
        Assert.That(history.NavigateChild(expected, []), Is.True);
        Assert.That(selected[^1], Is.SameAs(Child));
        Assert.That(parents, Is.EqualTo(new[] { Grandchild, Parent, OtherParent }));
    }

    [Test]
    public void AChainCanBeTraversedAndReversedMoreThanOnceWithoutRediscoveringNeighbours()
    {
        List<GitCompareRevision> selected = [];
        ParentChildNavigationHistory history = new(selected.Add);
        Assert.That(history.NavigateChild(Parent, [Child]), Is.True);
        Assert.That(history.NavigateChild(Child, [Grandchild]), Is.True);
        Assert.That(history.NavigateParent(Grandchild, []), Is.True);
        Assert.That(history.NavigateParent(Child, []), Is.True);
        Assert.That(history.NavigateChild(Parent, []), Is.True);
        Assert.That(history.NavigateChild(Child, []), Is.True);

        Assert.That(selected, Is.EqualTo(new[] { Child, Grandchild, Child, Parent, Child, Grandchild }));
    }

    [Test]
    public void InternalSelectionNotificationsKeepTheTypedIndexAndWorkingTreeReturnPaths()
    {
        List<GitCompareRevision> selected = [];
        ParentChildNavigationHistory? history = null;
        history = new(destination =>
        {
            selected.Add(destination);
            history!.RevisionsSelectionChanged();
        });

        Assert.That(history.NavigateParent(WorkingTree, [Index]), Is.True);
        Assert.That(history.NavigateParent(Index, [Parent]), Is.True);
        Assert.That(history.NavigateChild(Parent, []), Is.True);
        Assert.That(history.NavigateChild(Index, []), Is.True);

        Assert.That(selected, Is.EqualTo(new[] { Index, Parent, Index, WorkingTree }));
        Assert.That(selected[0], Is.SameAs(Index));
        Assert.That(selected[^1], Is.SameAs(WorkingTree));
        Assert.That(selected.Where(target => target.Kind != GitCompareRevisionKind.Commit).All(target => target.Revision.Length == 0), Is.True);
        Assert.That(selected.Where(target => target.Kind == GitCompareRevisionKind.Commit).Select(target => target.Revision), Is.EqualTo(new[] { Parent.Revision }));
    }

    [Test]
    public void AnExternalSelectionChangeClearsBothDirectionsBeforeTheNextNavigation()
    {
        List<GitCompareRevision> selected = [];
        ParentChildNavigationHistory history = new(selected.Add);
        history.NavigateChild(Parent, [Child]);
        history.NavigateFirstParent(Child, [OtherParent]);
        Assert.That(history.HasPreviousParent && history.HasPreviousChild, Is.True);

        history.RevisionsSelectionChanged();

        Assert.That(history.HasPreviousParent || history.HasPreviousChild, Is.False);
        Assert.That(history.NavigateChild(OtherParent, [Grandchild]), Is.True);
        Assert.That(selected[^1], Is.SameAs(Grandchild), "The discarded Child route must not override this graph's first child.");
    }

    [Test]
    public void ClearIsExplicitAndIdempotentAndDoesNotPublishAnotherSelection()
    {
        List<GitCompareRevision> selected = [];
        ParentChildNavigationHistory history = new(selected.Add);
        history.NavigateParent(Child, [Parent]);
        history.Clear(); history.Clear();

        Assert.That(history.HasPreviousParent || history.HasPreviousChild, Is.False);
        Assert.That(selected, Is.EqualTo(new[] { Parent }));
        Assert.That(history.NavigateChild(Parent, []), Is.False);
    }

    [Test]
    public void CallbackFailureKeepsTheAttemptedRouteButReleasesTheInternalSelectionGuard()
    {
        List<GitCompareRevision> attempts = [];
        InvalidOperationException failure = new("The target was not selected.");
        ParentChildNavigationHistory? history = null;
        history = new(destination =>
        {
            attempts.Add(destination);
            history!.RevisionsSelectionChanged();
            throw failure;
        });

        Assert.That(Assert.Throws<InvalidOperationException>(() => history.NavigateFirstParent(Child, [Parent])), Is.SameAs(failure));
        Assert.That(attempts, Is.EqualTo(new[] { Parent }));
        Assert.That(history.HasPreviousChild, Is.True, "The model records an attempt, not a fabricated transactional rollback.");

        history.RevisionsSelectionChanged();
        Assert.That(history.HasPreviousChild || history.HasPreviousParent, Is.False, "A failed callback must not leave the direction guard active.");
    }

    [Test]
    public void DestinationsAndInputCollectionsAreKeptWithoutResolutionOrNormalization()
    {
        List<GitCompareRevision> selected = [];
        ParentChildNavigationHistory history = new(selected.Add);
        GitCompareRevision[] children = [Grandchild, Child];
        GitCompareRevision[] original = children.ToArray();

        Assert.That(history.NavigateChild(Parent, children), Is.True);
        Assert.That(selected.Single(), Is.SameAs(Grandchild));
        Assert.That(selected.Single().Revision, Is.EqualTo(new string('d', 64)));
        Assert.That(children, Is.EqualTo(original));
        Assert.That(history.NavigateParent(Grandchild, []), Is.True);
        Assert.That(selected[^1], Is.SameAs(Parent));
        Assert.That(selected[^1].Revision, Is.EqualTo(new string('a', 40)));
    }

    [Test]
    public void IndependentInstancesDoNotShareHistoryOrTheInternalDirectionGuard()
    {
        ParentChildNavigationHistory other = new(_ => { });
        other.NavigateParent(Child, [Parent]);
        ParentChildNavigationHistory? history = null;
        history = new(_ =>
        {
            history!.RevisionsSelectionChanged();
            other.RevisionsSelectionChanged();
        });

        history.NavigateParent(Grandchild, [Child]);

        Assert.That(history.HasPreviousChild, Is.True);
        Assert.That(other.HasPreviousChild || other.HasPreviousParent, Is.False);
    }

    [Test]
    public void ExplicitClearDuringTheCallbackIsNotUndoneWhenSelectionReturns()
    {
        ParentChildNavigationHistory? history = null;
        history = new(_ => history!.Clear());

        Assert.That(history.NavigateParent(Child, [Parent]), Is.True);
        Assert.That(history.HasPreviousChild || history.HasPreviousParent, Is.False);
        Assert.That(history.NavigateChild(Parent, []), Is.False);
    }

    [Test]
    public void NonTopologicalGraphKeepsChildInsertionOrderInsteadOfTheDrawingOrder()
    {
        GitCommit[] commits = [Commit(Parent), Commit(Child, Parent), Commit(Grandchild, Child, Parent)];

        CommitGraphLayout layout = CommitGraphLayout.Build(commits);

        Assert.That(layout.Nodes.Select(node => node.Commit.Hash), Is.EqualTo(new[] { Grandchild.Revision, Child.Revision, Parent.Revision }));
        Assert.That(layout.Children[Parent.Revision], Is.EqualTo(new[] { Child.Revision, Grandchild.Revision }));
        List<GitCompareRevision> selected = [];
        ParentChildNavigationHistory history = new(selected.Add);
        Assert.That(history.NavigateChild(Parent, layout.Children[Parent.Revision].Select(hash => new GitCompareRevision(hash)).ToArray()), Is.True);
        Assert.That(selected.Single().Revision, Is.EqualTo(Child.Revision));
    }

    [Test]
    public void GraphChildrenDoNotRepeatDuplicateRowsOrDuplicateParentEdges()
    {
        GitCommit duplicateParents = Commit(Child, Parent, Parent);
        GitCommit[] commits = [duplicateParents, Commit(Child, Parent), Commit(Grandchild, Parent), Commit(Parent)];

        CommitGraphLayout layout = CommitGraphLayout.Build(commits);

        Assert.That(layout.Nodes, Has.Count.EqualTo(3));
        Assert.That(layout.Children[Parent.Revision], Is.EqualTo(new[] { Child.Revision, Grandchild.Revision }));
        Assert.That(duplicateParents.Parents, Is.EqualTo(new[] { Parent.Revision, Parent.Revision }), "Drawing and navigation must not rewrite the supplied commit metadata.");
        Assert.That(layout.Children[Child.Revision], Is.Empty);
        Assert.That(layout.Children[Grandchild.Revision], Is.Empty);
    }

    [Test]
    public void FirstParentGraphOmitsTheSecondParentFromChildNavigation()
    {
        GitCommit[] commits = [Commit(Grandchild, Child, OtherParent), Commit(Child, Parent), Commit(OtherParent, Parent), Commit(Parent)];

        CommitGraphLayout allParents = CommitGraphLayout.Build(commits);
        CommitGraphLayout firstParent = CommitGraphLayout.Build(commits, onlyFirstParent: true);

        Assert.That(allParents.Children[OtherParent.Revision], Is.EqualTo(new[] { Grandchild.Revision }));
        Assert.That(firstParent.Children[OtherParent.Revision], Is.Empty);
        Assert.That(firstParent.Children[Child.Revision], Is.EqualTo(new[] { Grandchild.Revision }));
        Assert.That(firstParent.Children[Parent.Revision], Is.EqualTo(new[] { Child.Revision, OtherParent.Revision }));
        Assert.That(firstParent.Strokes.Any(stroke => stroke.Child == Grandchild.Revision && stroke.Parent == OtherParent.Revision), Is.False);
        ParentChildNavigationHistory history = new(_ => Assert.Fail("An omitted second-parent edge must not create a navigation target."));
        Assert.That(history.NavigateChild(OtherParent, firstParent.Children[OtherParent.Revision].Select(hash => new GitCompareRevision(hash)).ToArray()), Is.False);
    }

    private static GitCommit Commit(GitCompareRevision revision, params GitCompareRevision[] parents)
        => new(revision.Revision, parents.Select(parent => parent.Revision).ToArray(), "Author", "author@example.com", DateTimeOffset.UnixEpoch, revision.Revision, "");

    private static bool Execute(ParentChildNavigationHistory history, string command, GitCompareRevision current,
        IReadOnlyList<GitCompareRevision> neighbours) => command switch
    {
        "Parent" => history.NavigateParent(current, neighbours),
        "FirstParent" => history.NavigateFirstParent(current, neighbours),
        "LastParent" => history.NavigateLastParent(current, neighbours),
        "Child" => history.NavigateChild(current, neighbours),
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };
}
