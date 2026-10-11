using System.Globalization;

namespace GitExtensions.Linux.Plugins;

public sealed record GitImpactOptions(bool IncludeSubmodules = false, bool RespectMailmap = true, DayOfWeek? FirstDayOfWeek = null);
public sealed record GitImpactProgress(string ModulePath, int ModulesLoaded, int CommitsLoaded);
public readonly record struct GitImpactData(long Commits, long AddedLines, long DeletedLines)
{
    public long ChangedLines => AddedLines + DeletedLines;
    public static GitImpactData operator +(GitImpactData left, GitImpactData right)
        => new(left.Commits + right.Commits, left.AddedLines + right.AddedLines, left.DeletedLines + right.DeletedLines);
}
public sealed record GitImpactFileChange(string Path, string? PreviousPath, long AddedLines, long DeletedLines, bool IsBinary);
public sealed record GitImpactCommit(string ModulePath, string Hash, DateOnly AuthoredDate, DateOnly Week, string Author,
    GitImpactData Data, IReadOnlyList<GitImpactFileChange> Files);
public sealed record GitImpactAuthor(string Name, GitImpactData Data, string Color);
public sealed record GitImpactWeekAuthor(string Author, GitImpactData Data, bool IsIntermediateEmptyWeek);
public sealed record GitImpactWeek(DateOnly Date, IReadOnlyList<GitImpactWeekAuthor> Authors);
public readonly record struct GitImpactPoint(double X, double Y);
public readonly record struct GitImpactRectangle(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}
public sealed record GitImpactBlock(DateOnly Week, GitImpactRectangle Bounds, GitImpactData Data, bool ShowChangedLinesLabel);
public sealed record GitImpactSegment(GitImpactPoint End, GitImpactPoint? Control1 = null, GitImpactPoint? Control2 = null);
public sealed record GitImpactArea(string Author, string Color, GitImpactPoint Start, IReadOnlyList<GitImpactSegment> Segments,
    IReadOnlyList<GitImpactBlock> Blocks);
public sealed record GitImpactWeekLabel(DateOnly Week, GitImpactPoint Center);
public sealed record GitImpactLayout(double Width, double Height, IReadOnlyList<GitImpactArea> Areas,
    IReadOnlyList<GitImpactWeekLabel> WeekLabels, IReadOnlyList<string> DrawOrder);

/// <summary>Author totals, weekly stacks and the original logarithmic graph geometry; no Forms/Drawing dependency.</summary>
public sealed record GitImpactReport(IReadOnlyList<GitImpactAuthor> Authors, IReadOnlyList<GitImpactWeek> Weeks,
    IReadOnlyList<GitImpactCommit> Commits, IReadOnlyList<string> Modules, IReadOnlyList<string> DrawOrder, DayOfWeek FirstDayOfWeek)
{
    public GitImpactData Total => Authors.Aggregate(new GitImpactData(), (total, author) => total + author.Data);

    public GitImpactLayout CreateLayout(double viewportHeight, double dpiScale = 1)
    {
        if (!double.IsFinite(viewportHeight) || viewportHeight < 0 || !double.IsFinite(dpiScale) || dpiScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewportHeight));
        // The original scales the 60/50-pixel horizontal blocks with DPI and scales height to 90% of the viewport.
        int blockWidth = Math.Max(1, (int)Math.Round(60 * dpiScale));
        int transitionWidth = Math.Max(1, (int)Math.Round(50 * dpiScale));
        int blockHalfWidth = blockWidth / 2, transitionHalfWidth = transitionWidth / 2;
        Dictionary<string, List<(DateOnly Week, GitImpactRectangle Bounds, GitImpactData Data)>> points = new(StringComparer.Ordinal);
        List<GitImpactWeekLabel> labels = [];
        double maxHeight = 0, x = 0;
        foreach (GitImpactWeek week in Weeks)
        {
            double y = 0;
            foreach (GitImpactWeekAuthor author in week.Authors.OrderByDescending(item => item.Data.ChangedLines))
            {
                int height = Math.Max(1, (int)Math.Round(Math.Pow(Math.Log(Math.Max(1, author.Data.ChangedLines)), 1.5) * 4));
                if (!points.TryGetValue(author.Author, out var blocks)) points.Add(author.Author, blocks = []);
                blocks.Add((week.Date, new(x, y, blockWidth, height), author.Data));
                y += height + 2;
            }
            maxHeight = Math.Max(maxHeight, y);
            labels.Add(new(week.Date, new(x + blockHalfWidth, y)));
            x += blockWidth + transitionWidth;
        }
        double heightFactor = maxHeight > 0 ? 0.9 * viewportHeight / maxHeight : 1;
        List<GitImpactArea> areas = [];
        foreach ((string author, var source) in points)
        {
            GitImpactBlock[] blocks = source.Select(item =>
            {
                GitImpactRectangle bounds = new(item.Bounds.X, (int)(item.Bounds.Y * heightFactor), item.Bounds.Width,
                    Math.Max(1, (int)(item.Bounds.Height * heightFactor)));
                return new GitImpactBlock(item.Week, bounds, item.Data, bounds.Height > 10 * 1.5);
            }).ToArray();
            List<GitImpactSegment> segments = [];
            GitImpactRectangle first = blocks[0].Bounds, last = blocks[^1].Bounds;
            GitImpactPoint start = new(first.X, first.Bottom);
            segments.Add(new(new(first.X, first.Y)));
            for (int i = 0; i < blocks.Length; i++)
            {
                GitImpactRectangle rectangle = blocks[i].Bounds;
                segments.Add(new(new(rectangle.Right, rectangle.Y)));
                if (i + 1 < blocks.Length)
                {
                    GitImpactRectangle next = blocks[i + 1].Bounds;
                    segments.Add(new(new(next.X, next.Y), new(rectangle.Right + transitionHalfWidth, rectangle.Y),
                        new(rectangle.Right + transitionHalfWidth, next.Y)));
                }
            }
            segments.Add(new(new(last.Right, last.Bottom)));
            for (int i = blocks.Length - 1; i >= 0; i--)
            {
                GitImpactRectangle rectangle = blocks[i].Bounds;
                segments.Add(new(new(rectangle.X, rectangle.Bottom)));
                if (i > 0)
                {
                    GitImpactRectangle previous = blocks[i - 1].Bounds;
                    segments.Add(new(new(previous.Right, previous.Bottom), new(rectangle.X - transitionHalfWidth, rectangle.Bottom),
                        new(rectangle.X - transitionHalfWidth, previous.Bottom)));
                }
            }
            areas.Add(new(author, Authors.Single(item => item.Name == author).Color, start, segments, blocks));
        }
        return new(Math.Max(0, Weeks.Count * (blockWidth + transitionWidth) - transitionWidth), viewportHeight, areas,
            labels.Select(label => label with { Center = label.Center with { Y = label.Center.Y * heightFactor } }).ToArray(), DrawOrder);
    }

    internal static string OriginalColor(string author)
        => "#" + ((uint)author.GetHashCode() & 0x00ffffff).ToString("X6", CultureInfo.InvariantCulture);
}
