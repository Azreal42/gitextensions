using System.Collections.Specialized;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public sealed class SourceSnapshotTests
{
    [Test]
    public void ResolvePreservesCompleteUnchangedPayloadAcrossFreshReadsWithoutMutatingThePublishedSource()
    {
        GitCommit a = Commit("A", "P", "Q"), b = Commit("B");
        IncrementalRevisionRows source = new(); source.PublishOrder([a, b]);
        List<NotifyCollectionChangedAction> changes = Observe(source);
        object[] request = [b with { Parents = b.Parents.ToArray() }, a with { Parents = a.Parents.ToArray() }];
        IReadOnlyList<object> resolved = source.ResolveSnapshotItems(request);
        Assert.That(resolved[0], Is.SameAs(b)); Assert.That(resolved[1], Is.SameAs(a));
        Assert.That(source[0], Is.SameAs(a)); Assert.That(source[1], Is.SameAs(b));
        Assert.That(request[0], Is.Not.SameAs(b)); Assert.That(request[1], Is.Not.SameAs(a));
        Assert.That(changes, Is.Empty); AssertIndex(source);
    }

    [TestCase("parents")]
    [TestCase("authorName")]
    [TestCase("authorEmail")]
    [TestCase("date")]
    [TestCase("offset")]
    [TestCase("subject")]
    [TestCase("body")]
    [TestCase("fullMessage")]
    public void EveryChangedPayloadFieldReplacesTheSameKeyRatherThanReusingAnOutdatedRow(string fieldName)
    {
        GitCommit original = Commit("A", "P"), stable = Commit("B");
        GitCommit replacement = fieldName switch
        {
            "parents" => original with { Parents = ["rewritten-by-filter"] },
            "authorName" => original with { AuthorName = "Other author" },
            "authorEmail" => original with { AuthorEmail = "other@example.invalid" },
            "date" => original with { AuthoredAt = original.AuthoredAt.AddTicks(1) },
            "offset" => original with { AuthoredAt = original.AuthoredAt.ToOffset(TimeSpan.FromHours(2)) },
            "subject" => original with { Subject = "Changed subject" },
            "body" => original with { Body = "Changed body" },
            "fullMessage" => original with { FullMessage = original.FullMessage + "\n" },
            _ => throw new ArgumentOutOfRangeException(nameof(fieldName))
        };
        IncrementalRevisionRows source = new(); source.PublishOrder([original, stable]);
        List<NotifyCollectionChangedAction> changes = Observe(source);
        IReadOnlyList<object> normalized = source.ResolveSnapshotItems([replacement, stable with { Parents = [] }]);
        Assert.That(normalized[0], Is.SameAs(replacement)); Assert.That(normalized[1], Is.SameAs(stable));
        Assert.That(source[0], Is.SameAs(original)); Assert.That(changes, Is.Empty);
        source.PublishSnapshot(normalized);
        Assert.That(source[0], Is.SameAs(replacement)); Assert.That(source[1], Is.SameAs(stable));
        Assert.That(source.KeyIndex(original.Hash), Is.Zero);
        Assert.That(changes, Is.EqualTo(new[] { NotifyCollectionChangedAction.Replace })); AssertIndex(source);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ParentOrderAndMultiplicityArePartOfTheSnapshotPayload(bool duplicate)
    {
        GitCommit original = Commit("A", "P", "Q");
        GitCommit replacement = original with { Parents = duplicate ? ["P", "Q", "Q"] : ["Q", "P"] };
        IncrementalRevisionRows source = new(); source.PublishOrder([original]);
        source.PublishSnapshot([replacement]);
        Assert.That(source[0], Is.SameAs(replacement));
        Assert.That(HistoryRevisionItems.Parents(source[0]), Is.EqualTo(replacement.Parents));
    }

    [Test]
    public void NullAndEmptyFullMessageRemainDistinctEvenWhenTheVisibleSubjectAndBodyMatch()
    {
        GitCommit original = Commit("A") with { FullMessage = null };
        GitCommit replacement = original with { FullMessage = "" };
        IncrementalRevisionRows source = new(); source.PublishOrder([original]);
        Assert.That(source.ResolveSnapshotItems([replacement])[0], Is.SameAs(replacement));
        source.PublishSnapshot([replacement]); Assert.That(source[0], Is.SameAs(replacement));
    }

    [Test]
    public void AChangedHeadReusesWorkingTreeButReplacesTheIndexWithItsTrueNewParent()
    {
        ArtificialHistoryRevision working = new(GitCompareRevisionKind.WorkingTree, "old-head");
        ArtificialHistoryRevision index = new(GitCompareRevisionKind.Index, "old-head");
        GitCommit real = Commit("A"); IncrementalRevisionRows source = new(); source.PublishOrder([working, index, real]);
        ArtificialHistoryRevision nextWorking = new(GitCompareRevisionKind.WorkingTree, "new-head");
        ArtificialHistoryRevision nextIndex = new(GitCompareRevisionKind.Index, "new-head");
        List<NotifyCollectionChangedAction> changes = Observe(source);
        IReadOnlyList<object> normalized = source.ResolveSnapshotItems([nextWorking, nextIndex, real with { Parents = [] }]);
        Assert.That(normalized[0], Is.SameAs(working)); Assert.That(normalized[1], Is.SameAs(nextIndex));
        source.PublishSnapshot(normalized);
        Assert.That(source[0], Is.SameAs(working)); Assert.That(source[1], Is.SameAs(nextIndex)); Assert.That(source[2], Is.SameAs(real));
        Assert.That(HistoryRevisionItems.Parents(source[1]), Is.EqualTo(new[] { "new-head" }));
        Assert.That(HistoryRevisionItems.Revision(source[1]).Kind, Is.EqualTo(GitCompareRevisionKind.Index));
        Assert.That(changes, Is.EqualTo(new[] { NotifyCollectionChangedAction.Replace })); AssertIndex(source);
    }

    [Test]
    public void AFinishedMixedSnapshotRemovesMovesReplacesAndAddsWithCorrectKeysVisibleInsideEachNotification()
    {
        ArtificialHistoryRevision working = new(GitCompareRevisionKind.WorkingTree, "H");
        ArtificialHistoryRevision index = new(GitCompareRevisionKind.Index, "H");
        GitCommit a = Commit("A", "P"), b = Commit("B"), c = Commit("C"), d = Commit("D");
        IncrementalRevisionRows source = new(); source.PublishOrder([working, index, a, b, c, d]);
        List<NotifyCollectionChangedAction> changes = Observe(source);
        GitCommit rewritten = a with { Parents = ["actual-filter-parent"] }, e = Commit("E");
        ArtificialHistoryRevision nextIndex = new(GitCompareRevisionKind.Index, "next-head");
        object[] desired = [c with { Parents = [] }, nextIndex, rewritten, e, new ArtificialHistoryRevision(GitCompareRevisionKind.WorkingTree, "next-head")];
        source.PublishSnapshot(desired);
        Assert.That(source.Select(HistoryRevisionItems.Key), Is.EqualTo(new[] { "C", ArtificialHistoryRevision.IndexKey, "A", "E", ArtificialHistoryRevision.WorkingTreeKey }));
        Assert.That(source[0], Is.SameAs(c)); Assert.That(source[1], Is.SameAs(nextIndex)); Assert.That(source[2], Is.SameAs(rewritten));
        Assert.That(source[3], Is.SameAs(e)); Assert.That(source[4], Is.SameAs(working));
        Assert.That(source.IndexOfKey("B"), Is.EqualTo(-1)); Assert.That(source.IndexOfHash("D"), Is.EqualTo(-1));
        Assert.That(changes, Does.Contain(NotifyCollectionChangedAction.Remove).And.Contain(NotifyCollectionChangedAction.Move)
            .And.Contain(NotifyCollectionChangedAction.Replace).And.Contain(NotifyCollectionChangedAction.Add)
            .And.Not.Contain(NotifyCollectionChangedAction.Reset));
        AssertIndex(source);
    }

    [Test]
    public void RepeatingAnEquivalentSnapshotIsANoopEvenWhenItContainsFreshArraysAndArtificialInstances()
    {
        ArtificialHistoryRevision working = new(GitCompareRevisionKind.WorkingTree, null), index = new(GitCompareRevisionKind.Index, null);
        GitCommit a = Commit("A", "P"); IncrementalRevisionRows source = new(); source.PublishOrder([working, index, a]);
        List<NotifyCollectionChangedAction> changes = Observe(source);
        for (int repetition = 0; repetition < 3; repetition++)
        {
            source.PublishSnapshot([new ArtificialHistoryRevision(GitCompareRevisionKind.WorkingTree, null),
                new ArtificialHistoryRevision(GitCompareRevisionKind.Index, null), a with { Parents = ["P"] }]);
            Assert.That(source[0], Is.SameAs(working)); Assert.That(source[1], Is.SameAs(index)); Assert.That(source[2], Is.SameAs(a));
        }
        Assert.That(changes, Is.Empty); AssertIndex(source);
    }

    [Test]
    public void AnEmptySnapshotRemovesRowsIndividuallyAndAllowsTheSameKeysToBeAdmittedAgain()
    {
        GitCommit a = Commit("A"), b = Commit("B"); IncrementalRevisionRows source = new(); source.PublishOrder([a, b]);
        List<NotifyCollectionChangedAction> changes = Observe(source); source.PublishSnapshot([]);
        Assert.That(source, Is.Empty); Assert.That(source.KeyIndex("A"), Is.EqualTo(-1)); Assert.That(source.IndexOfHash("B"), Is.EqualTo(-1));
        Assert.That(changes, Is.EqualTo(new[] { NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Remove }));
        changes.Clear(); source.PublishSnapshot([b, a]);
        Assert.That(source[0], Is.SameAs(b)); Assert.That(source[1], Is.SameAs(a)); Assert.That(changes, Does.Not.Contain(NotifyCollectionChangedAction.Reset));
        AssertIndex(source);
    }

    [TestCase("duplicate")]
    [TestCase("unknown")]
    [TestCase("null")]
    [TestCase("empty-key")]
    [TestCase("reserved-key")]
    public void AnInvalidEntireSnapshotIsRejectedBeforeAnyRemovalReplacementOrNotification(string invalid)
    {
        GitCommit original = Commit("A"), retained = Commit("B");
        IncrementalRevisionRows source = new(); source.PublishOrder([original, retained]);
        List<NotifyCollectionChangedAction> changes = Observe(source);
        object bad = invalid switch
        {
            "duplicate" => Commit("A") with { Subject = "different object, same duplicate key" },
            "unknown" => new object(),
            "null" => null!,
            "empty-key" => Commit(""),
            "reserved-key" => Commit(ArtificialHistoryRevision.IndexKey),
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        object[] desired = [original with { Body = "must not replace" }, bad];
        Assert.Throws<ArgumentException>(() => source.ResolveSnapshotItems(desired));
        Assert.Throws<ArgumentException>(() => source.PublishSnapshot(desired));
        Assert.That(source, Has.Count.EqualTo(2)); Assert.That(source[0], Is.SameAs(original)); Assert.That(source[1], Is.SameAs(retained));
        Assert.That(changes, Is.Empty); Assert.That(source.IndexOfKey("B"), Is.EqualTo(1)); AssertIndex(source);
    }

    [Test]
    public void SnapshotAndStreamPublicationsShareTheIndexWithoutChangingTheFirstAdmissionWinsAppendContract()
    {
        GitCommit a = Commit("A", "P"), b = Commit("B"), c = Commit("C");
        IncrementalRevisionRows source = new(); source.PublishOrder([a, b]); List<NotifyCollectionChangedAction> changes = Observe(source);
        source.PublishOrder([a with { Parents = ["ignored-duplicate"] }, b, c]);
        Assert.That(source[0], Is.SameAs(a)); Assert.That(source[2], Is.SameAs(c));
        GitCommit replacement = a with { Parents = ["accepted-query-rewrite"] };
        source.PublishSnapshot([c with { Parents = [] }, replacement]);
        Assert.That(source[0], Is.SameAs(c)); Assert.That(source[1], Is.SameAs(replacement)); Assert.That(source.IndexOfKey("B"), Is.EqualTo(-1));
        source.PublishOrder([c, replacement, b]);
        Assert.That(source[2], Is.SameAs(b)); Assert.That(source.IndexOfKey("B"), Is.EqualTo(2));
        Assert.That(changes, Does.Not.Contain(NotifyCollectionChangedAction.Reset)); AssertIndex(source);
    }

    private static GitCommit Commit(string hash, params string[] parents)
        => new(hash, parents, "Author", "author@example.invalid", new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero), "Subject", "Body")
        { FullMessage = "Subject\n\nBody\n" };

    private static List<NotifyCollectionChangedAction> Observe(IncrementalRevisionRows source)
    {
        List<NotifyCollectionChangedAction> changes = [];
        source.CollectionChanged += (_, change) => { changes.Add(change.Action); AssertIndex(source); };
        return changes;
    }

    private static void AssertIndex(IncrementalRevisionRows source)
    {
        for (int index = 0; index < source.Count; index++)
        {
            string key = HistoryRevisionItems.Key(source[index]);
            Assert.That(source.IndexOfKey(key), Is.EqualTo(index)); Assert.That(source.KeyIndex(key), Is.EqualTo(index));
            Assert.That(source.IndexOfHash(key), Is.EqualTo(index));
        }
    }
}
