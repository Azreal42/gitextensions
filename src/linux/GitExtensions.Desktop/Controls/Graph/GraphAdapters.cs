namespace GitExtensions.Desktop.Controls.Graph;

internal sealed record LayoutRevision(string ObjectId, IReadOnlyList<string> ParentIds, string Subject);

internal readonly struct RevisionGraphConfig
{
    public bool MergeGraphLanesHavingCommonParent => false;
    public bool ReduceGraphCrossings => true;
    public bool RenderGraphWithDiagonals => true;
    public bool StraightenGraphDiagonals => true;
    public int StraightenGraphSegmentsLimit => 80;
}

internal static class RevisionGraphLaneColor
{
    internal const int PaletteSize = 7;

    public static int GetColorForLane(int seed) => (int)(Math.Abs((long)seed) % PaletteSize);

    public static int Hash(string objectId)
    {
        if (GitExtensions.Extensibility.Git.ObjectId.TryParse(objectId, out GitExtensions.Extensibility.Git.ObjectId sha1))
        {
            return sha1.GetHashCode();
        }

        uint hash = 2166136261;
        foreach (char character in objectId)
        {
            hash = (hash ^ character) * 16777619;
        }

        return unchecked((int)hash);
    }
}

internal static class GraphEnumerableExtensions
{
    public static IOrderedEnumerable<T> OrderBy<T>(this IEnumerable<T> source, Func<T, T> keySelector, Comparison<T> comparison)
        => Enumerable.OrderBy(source, keySelector, Comparer<T>.Create(comparison));
}
