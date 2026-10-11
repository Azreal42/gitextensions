using GitExtensions.Desktop.Controls.Graph;
using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public class CommitGraphLayoutTests
{
    [TestCase("M:F,B F:R B:R R", 2)]
    [TestCase("M:A,B,C A:R B:R C:R R", 3)]
    [TestCase("M:A,B A:X B:X X:R R", 2)]
    [TestCase("A:B B:C C", 1)]
    [TestCase("0:C,1,2,3,4,5,6,7,8,9,A,B 1:R 2:R 3:R 4:C 5:C 6:C 7:R 8:C 9:R A:C B:R C:D D:E E:F F:G G:H,K,R H:I,R I:J,R J:R K:R R", 10)]
    public void ParentEdgesAreContinuousAndOccupyRealBranchLanes(string specification, int minimumLanes)
    {
        GitCommit[] commits = Parse(specification);
        CommitGraphLayout layout = CommitGraphLayout.Build(commits);
        Assert.That(layout.Nodes.Select(node => node.Commit.Hash), Is.EqualTo(commits.Select(commit => commit.Hash)));
        Assert.That(layout.LaneCount, Is.GreaterThanOrEqualTo(minimumLanes));

        for (int childRow = 0; childRow < commits.Length; childRow++)
        {
            foreach (string parent in commits[childRow].Parents)
            {
                int parentRow = Array.FindIndex(commits, commit => commit.Hash == parent);
                GraphStroke[] edge = layout.Strokes.Where(stroke => stroke.Child == commits[childRow].Hash && stroke.Parent == parent).ToArray();
                Assert.That(edge, Has.Length.EqualTo(2 * (parentRow - childRow)));
                Assert.That(edge.Single(stroke => stroke.Row == childRow).CenterLane, Is.EqualTo(layout.Nodes[childRow].Lane));
                Assert.That(edge.Single(stroke => stroke.Row == parentRow).CenterLane, Is.EqualTo(layout.Nodes[parentRow].Lane));

                // Each half edge meets its neighbour at the same lane midpoint.
                for (int row = childRow; row < parentRow; row++)
                {
                    GraphStroke outgoing = edge.Single(stroke => stroke.Row == row && !stroke.UpperHalf);
                    GraphStroke incoming = edge.Single(stroke => stroke.Row == row + 1 && stroke.UpperHalf);
                    Assert.That(outgoing.CenterLane + outgoing.AdjacentLane, Is.EqualTo(incoming.CenterLane + incoming.AdjacentLane));
                    Assert.That(outgoing.Color, Is.EqualTo(incoming.Color));
                }
            }
        }
    }

    [Test]
    public void MissingAncestorsContinueToHistoryBoundary()
    {
        CommitGraphLayout layout = CommitGraphLayout.Build(Parse("M:A,B A:OLDER_A B:OLDER_B"));
        GraphStroke[] truncated = layout.Strokes.Where(stroke => stroke.ContinuesOutsideHistory).ToArray();
        Assert.That(truncated.Select(stroke => stroke.Parent), Is.EquivalentTo(new[] { "OLDER_A", "OLDER_B" }));
        Assert.That(truncated.All(stroke => stroke.Row == layout.Nodes.Count - 1 && !stroke.UpperHalf), Is.True);
    }

    [Test]
    public void DisconnectedRootHasNoInventedParents()
    {
        CommitGraphLayout layout = CommitGraphLayout.Build(Parse("A:R UNRELATED R"));
        Assert.That(layout.Strokes.Any(stroke => stroke.Child == "UNRELATED" || stroke.Parent == "UNRELATED"), Is.False);
        Assert.That(layout.Nodes, Has.Count.EqualTo(3));
    }

    [Test]
    public void EmptyHistoryAndDuplicateCommitHaveStableDimensions()
    {
        CommitGraphLayout empty = CommitGraphLayout.Build([]);
        Assert.That(empty.Nodes, Is.Empty);
        Assert.That(empty.Strokes, Is.Empty);
        Assert.That(empty.LaneCount, Is.EqualTo(1));
        CommitGraphLayout duplicate = CommitGraphLayout.Build(Parse("A:R A:R R"));
        Assert.That(duplicate.Nodes, Has.Count.EqualTo(2));
    }

    [Test]
    public void FullSha256ObjectIdsArePreserved()
    {
        string child = new('a', 64);
        string parent = new('b', 64);
        CommitGraphLayout layout = CommitGraphLayout.Build(Parse($"{child}:{parent} {parent}"));
        Assert.That(layout.Nodes[0].Commit.Hash, Is.EqualTo(child));
        Assert.That(layout.Strokes.All(stroke => stroke.Child == child && stroke.Parent == parent), Is.True);
    }

    private static GitCommit[] Parse(string specification)
        => specification.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(item =>
        {
            string[] parts = item.Split(':', 2);
            string[] parents = parts.Length == 2 ? parts[1].Split(',') : [];
            return new GitCommit(parts[0], parents, "Author", "author@example.com", DateTimeOffset.UnixEpoch, parts[0], "");
        }).ToArray();
}
