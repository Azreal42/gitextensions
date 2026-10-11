namespace GitExtensions.Linux.Plugins;

public sealed record StatisticsOptions(string CodeFiles = StatisticsPlugin.DefaultCodeFiles,
    string DirectoriesToIgnore = StatisticsPlugin.DefaultDirectoriesToIgnore, bool IgnoreSubmodules = true);

public sealed record StatisticsLineTotals(long Total, long Blank, long Comments, long Designer, long Code, long TestCode)
{
    public long ProductionCode => Code - TestCode;
    public static StatisticsLineTotals Empty { get; } = new(0, 0, 0, 0, 0, 0);
    public static StatisticsLineTotals operator +(StatisticsLineTotals left, StatisticsLineTotals right)
        => new(left.Total + right.Total, left.Blank + right.Blank, left.Comments + right.Comments,
            left.Designer + right.Designer, left.Code + right.Code, left.TestCode + right.TestCode);
}

public sealed record StatisticsContributor(string Author, long Commits);
public sealed record StatisticsLanguage(string Extension, long CodeLines);
public sealed record StatisticsFile(string ModulePath, string Path, StatisticsLineTotals Lines, bool IsTestFile);
public sealed record StatisticsPieSlice(string Key, string Label, long Value, double Fraction, string Color);
public sealed record StatisticsPieChart(string Key, string Title, IReadOnlyList<StatisticsPieSlice> Slices);
public sealed record StatisticsProgress(string Phase, int FilesAnalyzed, string? CurrentPath, StatisticsLineTotals Lines);

/// <summary>Models the four tabs, values and original pie palette without a desktop-framework dependency.</summary>
public sealed record StatisticsReport(long TotalCommits, IReadOnlyList<StatisticsContributor> Contributors,
    StatisticsLineTotals Lines, IReadOnlyList<StatisticsLanguage> Languages, IReadOnlyList<StatisticsFile> Files,
    IReadOnlyList<string> Modules, IReadOnlyList<StatisticsPieChart> Charts)
{
    public static IReadOnlyList<string> OriginalPieColors { get; } =
        ["#FF0000", "#FFFF00", "#1E90FF", "#90EE90", "#FF7F50", "#DAA520", "#9ACD32",
         "#9370DB", "#D3D3D3", "#A52A2A", "#FFC0CB", "#00008B", "#800080"];
}
