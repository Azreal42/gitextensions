using System.Globalization;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public class CommitInformationPresentationTests
{
    private sealed record Identity(string FullName, string Kind, string ShortName, string ObjectHash);

    [Test]
    public void DefaultsMatchTheSixIndependentWindowsOptions()
    {
        CommitInformationOptions options = new();
        Assert.Multiple(() =>
        {
            Assert.That(options, Is.EqualTo(new CommitInformationOptions(true, false, false, true, true, true)));
            Assert.That(options.WantsLocal, Is.True);
            Assert.That(options.WantsRemote, Is.False);
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("current")]
    [TestCase("(no branch)")]
    public void WindowsBucketsUseCurrentThenPriorityLocalAndRemoteThenOtherLocalAndRemote(string? current)
    {
        List<string> expected =
        [
            "master", "master2", "release/v1.0",
            "remotes/zzz/master", "remotes/origin/master", "remotes/upstream/master", "remotes/123AAA/master",
            "remotes/AAA/master", "remotes/myrepo/master", "remotes/other/master", "remotes/z_other/master",
            "remotes/myrepo/master2", "remotes/upstream/release/v1.0", "remotes/upstream/release/v1.1",
            "remotes/123AAA/release/tmp", "remotes/myrepo/release/v1.1",
            "1234_issue", "current/2", "current_2", "feature/1234_issue", "fix/master", "mastr", "repro/issue",
            "remotes/zzz/b1", "remotes/origin/b1", "remotes/origin/b2", "remotes/upstream/b1", "remotes/upstream/b2",
            "remotes/123AAA/123", "remotes/myrepo/b1", "remotes/myrepo/b2", "remotes/other/b1", "remotes/other/b2",
            "remotes/z_other/b1", "remotes/z_other/b2"
        ];
        if (current is not null) { expected.Insert(0, current); }
        CommitInformationReference<Identity>[] references = expected.Select(name => Reference(name)).ToArray();
        foreach (IEnumerable<CommitInformationReference<Identity>> input in new[] { references, references.Reverse().ToArray(), references.OrderBy(reference => reference.LegacySortName, StringComparer.Ordinal).ToArray() })
        {
            CommitInformationReferenceList<Identity> result = CommitInformationPresentation.Branches(input,
                new(Local: true, Remote: true), current ?? "", current == "(no branch)",
                "master;dummy;main[^/]*|master[^/]*;release/.*", "zzz;origin|upstream", showAll: true);
            Assert.That(result.Items.Select(item => item.Identity), Is.EqualTo(references.Select(reference => reference.Identity)));
            Assert.That(result.ShowAll, Is.False);
        }
    }

    [TestCase("(no branch)")]
    [TestCase("(detached from abc123)")]
    [TestCase("(HEAD detached at abc123)")]
    public void TypedDetachedIdentityIsKeptWithoutInventingAReference(string legacyName)
    {
        object detached = new { Hash = "abc123", ContainsSelectedCommit = true };
        CommitInformationReference<object>[] references = [new(new object(), "master"), new(detached, legacyName)];
        CommitInformationReferenceList<object> result = CommitInformationPresentation.Branches(references, new(), "", true, "master", "origin");
        Assert.That(result.Items[0].Identity, Is.SameAs(detached));
        Assert.That(result.Items[0].Text, Is.EqualTo(legacyName));
    }

    [TestCase("en-US")]
    [TestCase("tr-TR")]
    public void EqualPriorityUsesOrdinalRatherThanCultureOrVersionOrder(string cultureName)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            CommitInformationReference<Identity>[] references = [Reference("v2"), Reference("ä"), Reference("a"), Reference("v10"), Reference("I"), Reference("i")];
            Assert.That(Branches(references, new(), "", "").Items.Select(item => item.Text), Is.EqualTo(new[] { "I", "a", "i", "v10", "v2", "ä" }));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Test]
    public void NamedCapturesAlternativesAndSemicolonPriorityLevelsMatchWindowsExplicitCapture()
    {
        CommitInformationReference<Identity>[] references = [Reference("unmatched"), Reference("release/1"), Reference("feature/feature"), Reference("main")];
        Assert.That(Branches(references, new(), " ; (?<part>feature)/\\k<part> ; main|release/.* ; ", "").Items.Select(item => item.Text),
            Is.EqualTo(new[] { "feature/feature", "main", "release/1", "unmatched" }));
        Assert.Throws<ArgumentException>(() => Branches([Reference("feature/feature")], new(), "(feature)/\\1", ""),
            "An anonymous numeric capture is not silently enabled by the presentation helper.");
    }

    [Test]
    public void InvalidExpressionsAreRejectedBeforePresentationLikeLinuxPreferenceValidation()
    {
        Assert.Throws<ArgumentException>(() => Branches([Reference("main")], new(), "main;[", ""));
        Assert.Throws<ArgumentException>(() => Branches([Reference("main")], new(), "", "origin;["));
    }

    [Test]
    public void AllSixtyFourOptionCombinationsKeepBranchFlagsIndependentAndApplyTwelveThirteenLimits()
    {
        for (int bits = 0; bits < 64; bits++)
        {
            CommitInformationOptions options = new((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0,
                (bits & 8) != 0, (bits & 16) != 0, (bits & 32) != 0);
            string[] expected = (bits & 7) switch
            {
                0 => [], 1 => ["main", "topic"], 2 => ["origin/main", "origin/topic"],
                3 => ["main", "origin/main", "topic", "origin/topic"],
                4 => [], 5 => ["main", "topic"], 6 => [], 7 => ["main", "topic"],
                _ => throw new InvalidOperationException()
            };
            CommitInformationReference<Identity>[] mixed = [Reference("remotes/origin/topic"), Reference("topic"), Reference("remotes/origin/main"), Reference("main")];
            // Git's requested scope omits unrequested kinds; mixed scope preserves its remotes/ marker.
            IEnumerable<CommitInformationReference<Identity>> input = mixed.Where(reference => reference.Identity.Kind == "LocalBranch" ? options.WantsLocal : options.WantsRemote);
            if (!options.WantsLocal && options.WantsRemote) { input = input.Select(reference => reference with { LegacySortName = reference.LegacySortName["remotes/".Length..] }); }
            CommitInformationReferenceList<Identity> branches = Branches(input, options, "main", "origin");
            Assert.Multiple(() =>
            {
                Assert.That(options.WantsLocal, Is.EqualTo(options.Local || options.RemoteIfNoLocal), $"options {bits}");
                Assert.That(options.WantsRemote, Is.EqualTo(options.Remote || options.RemoteIfNoLocal), $"options {bits}");
                Assert.That(branches.IsVisible, Is.EqualTo(options.WantsLocal || options.WantsRemote), $"options {bits}");
                Assert.That(branches.Items.Select(item => item.Text), Is.EqualTo(expected), $"options {bits}");
            });

            foreach (int count in new[] { 12, 13 })
            {
                CommitInformationReference<Identity>[] tags = Enumerable.Range(0, count).Select(index => Reference("tag" + index, "Tag")).ToArray();
                CommitInformationReferenceList<Identity> result = CommitInformationPresentation.Tags(tags, options);
                Assert.That(result.IsVisible, Is.EqualTo(options.Tags), $"tags {bits}/{count}");
                Assert.That(result.Items.Count, Is.EqualTo(options.Tags ? (count == 13 ? 10 : 12) : 0), $"tags {bits}/{count}");
                Assert.That(result.ShowAll, Is.EqualTo(options.Tags && count == 13), $"tags {bits}/{count}");
            }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FallbackStopsOnlySubsequentRemotesEvenWhenExplicitRemoteOptionIsTrue(bool remote)
    {
        CommitInformationReference<Identity>[] input = [Reference("remotes/origin/other"), Reference("topic"), Reference("remotes/origin/main")];
        CommitInformationReferenceList<Identity> hiddenLocal = Branches(input, new(Local: false, Remote: remote, RemoteIfNoLocal: true), "main", "origin");
        Assert.That(hiddenLocal.Items.Select(item => item.Text), Is.EqualTo(new[] { "origin/main" }),
            "The earlier prioritized remote survives a later local even when that local is hidden.");
        CommitInformationReferenceList<Identity> shownLocal = Branches(input, new(Local: true, Remote: remote, RemoteIfNoLocal: true), "main", "origin");
        Assert.That(shownLocal.Items.Select(item => item.Text), Is.EqualTo(new[] { "origin/main", "topic" }));
    }

    [Test]
    public void RemoteOnlyLegacyNamesAreNotNormalizedIntoTheMixedScopePriorityBuckets()
    {
        CommitInformationReference<Identity>[] input = [Reference("z/main", "RemoteBranch"), Reference("origin/topic", "RemoteBranch")];
        CommitInformationReferenceList<Identity> result = Branches(input, new(Local: false, Remote: true), "main", "z;origin");
        Assert.That(result.Items.Select(item => item.Text), Is.EqualTo(new[] { "origin/topic", "z/main" }));
        Assert.That(result.Items.Select(item => item.Identity), Is.EqualTo(input.Reverse().Select(item => item.Identity)));
    }

    [Test]
    public void VisualNameCollisionsNeverMergeOrReconstructTypedActionIdentities()
    {
        Identity local = new("refs/heads/origin/main", "LocalBranch", "origin/main", "local-oid");
        Identity remote = new("refs/remotes/origin/main", "RemoteBranch", "origin/main", "remote-oid");
        CommitInformationReference<Identity>[] references = [new(local, "origin/main"), new(remote, "remotes/origin/main")];
        CommitInformationReferenceList<Identity> result = Branches(references, new(Local: true, Remote: true), "main", "origin");
        Assert.That(result.Items.Select(item => item.Text), Is.EqualTo(new[] { "origin/main", "origin/main" }));
        Assert.That(result.Items[0].Identity, Is.SameAs(remote));
        Assert.That(result.Items[1].Identity, Is.SameAs(local));

        Identity historicallyAmbiguousLocal = local with { FullName = "refs/heads/remotes/origin/main", ShortName = "remotes/origin/main" };
        result = Branches([new(historicallyAmbiguousLocal, "remotes/origin/main"), new(remote, "remotes/origin/main")],
            new(Local: true, Remote: true), "main", "origin");
        Assert.That(result.Items.Count, Is.EqualTo(2));
        Assert.That(result.Items.Select(item => item.Identity), Is.EquivalentTo(new[] { historicallyAmbiguousLocal, remote }));
        Assert.That(result.Items.Select(item => item.Text), Is.All.EqualTo("origin/main"));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(10)]
    [TestCase(11)]
    [TestCase(12)]
    [TestCase(13)]
    [TestCase(15)]
    public void BranchAndTagThresholdsAreIndependentAndExpansionDoesNotMutateInput(int count)
    {
        CommitInformationReference<Identity>[] input = Enumerable.Range(0, count).Select(index => Reference("branch" + index.ToString("D2"))).ToArray();
        CommitInformationReference<Identity>[] original = input.ToArray();
        CommitInformationReferenceList<Identity> branches = Branches(input, new(), "", "");
        CommitInformationReferenceList<Identity> tags = CommitInformationPresentation.Tags(input, new(), showAll: true);
        Assert.That(branches.Items.Count, Is.EqualTo(count > 12 ? 10 : count));
        Assert.That(branches.ShowAll, Is.EqualTo(count > 12));
        Assert.That(tags.Items.Count, Is.EqualTo(count));
        Assert.That(tags.ShowAll, Is.False);
        Assert.That(Branches(input, new(), "", "", showAll: true).Items.Count, Is.EqualTo(count));
        Assert.That(CommitInformationPresentation.Tags(input, new()).ShowAll, Is.EqualTo(count > 12));
        Assert.That(input, Is.EqualTo(original));
        Assert.That(Branches(input, new(), "", "").ShowAll, Is.EqualTo(count > 12), "An earlier expanded result does not change another presentation call.");
    }

    [Test]
    public void BranchLimitCountsOnlyReferencesActuallyAdmittedByTheSequentialFilter()
    {
        CommitInformationReference<Identity>[] input = [Reference("main"), .. Enumerable.Range(0, 15).Select(index => Reference("remotes/origin/branch" + index))];
        CommitInformationReferenceList<Identity> result = Branches(input, new(Local: true, RemoteIfNoLocal: true), "main", "origin");
        Assert.That(result.Items.Select(item => item.Text), Is.EqualTo(new[] { "main" }));
        Assert.That(result.ShowAll, Is.False);
    }

    [Test]
    public void TagsPreserveTheSuppliedNativeOrderAndMissingFullNameRanksBeforeKnownNames()
    {
        CommitInformationReference<Identity>[] tags = [Reference("v1.10", "Tag"), Reference("v1.2", "Tag"), Reference("v1.0", "Tag")];
        Assert.That(CommitInformationPresentation.Tags(tags, new()).Items.Select(item => item.Identity), Is.EqualTo(tags.Select(tag => tag.Identity)));
        IReadOnlyDictionary<string, int> rank = new Dictionary<string, int>(StringComparer.Ordinal) { ["refs/tags/v1.10"] = 0, ["refs/tags/v1.2"] = 1 };
        Assert.That(CommitInformationPresentation.TagRank("refs/tags/v1.10", rank), Is.Zero);
        Assert.That(CommitInformationPresentation.TagRank("refs/tags/v1.2", rank), Is.EqualTo(1));
        Assert.That(CommitInformationPresentation.TagRank("refs/tags/v1.0", rank), Is.EqualTo(-1));
        Assert.That(CommitInformationPresentation.TagRank("refs/tags/V1.2", rank), Is.EqualTo(-1));
    }

    [Test]
    public void LiteralDisplayTextAndTypedTargetsAreKeptWithoutHtmlOrUriReconstruction()
    {
        const string name = "topic/<&'λ>";
        CommitInformationReference<Identity> reference = Reference(name);
        CommitInformationReferenceItem<Identity> item = Branches([reference], new(), "", "").Items.Single();
        Assert.That(item.Text, Is.EqualTo(name));
        Assert.That(item.Identity, Is.SameAs(reference.Identity));
    }

    [Test]
    public void UnloadedAndDisabledBlocksAreHiddenWhileLoadedEmptyBlocksRemainVisible()
    {
        Assert.That(CommitInformationPresentation.Branches<Identity>(null, new(), "", false, "", "").IsVisible, Is.False);
        Assert.That(CommitInformationPresentation.Tags<Identity>(null, new()).IsVisible, Is.False);
        Assert.That(Branches([], new(), "", "").IsVisible, Is.True);
        Assert.That(CommitInformationPresentation.Tags<Identity>([], new()).IsVisible, Is.True);
        Assert.That(Branches([Reference("main")], new(Local: false), "", "").IsVisible, Is.False);
        Assert.That(CommitInformationPresentation.Tags<Identity>([Reference("tag", "Tag")], new(Tags: false)).IsVisible, Is.False);
    }

    private static CommitInformationReferenceList<Identity> Branches(IEnumerable<CommitInformationReference<Identity>> input,
        CommitInformationOptions options, string priorities, string remotes, bool showAll = false)
        => CommitInformationPresentation.Branches(input, options, "", false, priorities, remotes, showAll);

    private static CommitInformationReference<Identity> Reference(string legacyName, string? kind = null)
    {
        bool remote = kind == "RemoteBranch" || legacyName.StartsWith("remotes/", StringComparison.Ordinal);
        string shortName = legacyName.StartsWith("remotes/", StringComparison.Ordinal) ? legacyName["remotes/".Length..] : legacyName;
        kind ??= remote ? "RemoteBranch" : "LocalBranch";
        string prefix = kind == "Tag" ? "refs/tags/" : remote ? "refs/remotes/" : "refs/heads/";
        return new(new(prefix + shortName, kind, shortName, "oid:" + legacyName), legacyName);
    }
}
