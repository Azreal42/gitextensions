using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Plugins;
using NUnit.Framework;
using PieControl = GitExtensions.Desktop.Controls.StatisticsPieChart;
using PieModel = GitExtensions.Linux.Plugins.StatisticsPieChart;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class StatisticsDialogTests
{
    [AvaloniaTest]
    public async Task FourOriginalStatisticsTabsRenderTheirActualValuesWithProjectedPiesInBothThemes()
    {
        using ChartFixture fixture = await ChartFixture.CreateAsync();
        StatisticsDialog dialog = new(fixture.Repository);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(dialog.LastError, Is.Null);
            Assert.That(dialog.LastReport!.Charts.Count, Is.EqualTo(4));
            Assert.That(dialog.LastReport.TotalCommits, Is.EqualTo(3));
            Assert.That(dialog.LastReport.Lines.TestCode, Is.EqualTo(21));
            Assert.That(dialog.HasRepositoryChanges, Is.False);
            TabControl tabs = Field<TabControl>(dialog, "StatisticsTabs");
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme;
                for (int index = 0; index < 4; index++)
                {
                    tabs.SelectedIndex = index;
                    dialog.UpdateLayout();
                    PieControl chart = dialog.GetVisualDescendants().OfType<PieControl>().Single();
                    using WriteableBitmap frame = Capture(dialog, $"classic-statistics-{index}-{theme}.png");
                    Assert.That(chart.Chart, Is.SameAs(dialog.LastReport.Charts[index]));
                    Assert.That(chart.RenderedThickness, Is.GreaterThan(20), "Projection must have the original 20% visible side wall, not a flat pie.");
                    Assert.That(chart.Bounds.Width, Is.EqualTo(chart.Bounds.Height).Within(1), "Upstream SetPieStyle uses a square chart host.");
                    if (theme == ThemeVariant.Light)
                        Assert.That(CountColor(frame, Color.Parse("#FF0000")), Is.GreaterThan(100), "Each pie retains the first original palette color.");
                }
            }
            string before = (await fixture.Repository.RunAsync(["show-ref"])).StandardOutput;
            Field<Expander>(dialog, "StatisticsSettings").IsExpanded = true;
            dialog.UpdateLayout();
            Field<TextBox>(dialog, "StatisticsCodeFiles").Text = "*.py";
            Click(dialog, "RefreshStatistics");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null);
            Assert.That(dialog.LastReport.Languages.Single().Extension, Is.EqualTo(".py"));
            Assert.That(dialog.LastReport.Lines.Code, Is.EqualTo(10));
            Assert.That((await fixture.Repository.RunAsync(["show-ref"])).StandardOutput, Is.EqualTo(before));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public void PieSelectionAndTooltipsIncludeTheVisibleSideWallAndRespectZeroSlices()
    {
        PieControl pie = new()
        {
            Chart = new PieModel("types", "Lines of code per type",
                [new("blank", "Blank lines", 0, 0, "#FF0000"), new("code", "Lines of code", 10, 1, "#FFFF00")])
        };
        Window window = new() { Width = 260, Height = 260, Content = pie };
        int selected = 0;
        pie.SliceSelected += (_, slice) => { Assert.That(slice.Key, Is.EqualTo("code")); selected++; };
        try
        {
            window.Show();
            using WriteableBitmap frame = Capture(window, "classic-statistics-projected-side.png");
            double size = Math.Min(pie.Bounds.Width, pie.Bounds.Height) - 20;
            Point side = new(10 + size / 2, 10 + size * 0.8 + size * 0.1);
            Assert.That(pie.SliceAt(side), Is.EqualTo(1));
            Point translated = pie.TranslatePoint(side, window)!.Value;
            window.MouseMove(translated);
            window.MouseDown(translated, MouseButton.Left);
            window.MouseUp(translated, MouseButton.Left);
            Assert.That(selected, Is.EqualTo(1));
            Assert.That(pie.HighlightedSlice, Is.EqualTo(1));
            Assert.That(ToolTip.GetTip(pie)?.ToString(), Does.Contain("10"));
            Assert.That(CountColor(frame, Color.Parse("#FFFF00")), Is.GreaterThan(1000), "The flat top must retain its source color.");
            Assert.That(CountColor(frame, Color.Parse("#B2B200")), Is.GreaterThan(50), "The 30%-darker source edge and shaded side must be visible.");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task NativeImpactGraphRendersBezierRibbonsAndUpdatesSelectedAuthorTotalsInBothThemes()
    {
        using ChartFixture fixture = await ChartFixture.CreateAsync();
        GitImpactDialog dialog = new(fixture.Repository, new(FirstDayOfWeek: DayOfWeek.Monday));
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(dialog.LastError, Is.Null);
            Assert.That(dialog.LastReport!.Total.Commits, Is.EqualTo(3));
            GitImpactGraph graph = Field<GitImpactGraph>(dialog, "ImpactGraph");
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme;
                dialog.UpdateLayout();
                using WriteableBitmap frame = Capture(dialog, $"classic-impact-{theme}.png");
                Point background = graph.TranslatePoint(new Point(graph.Bounds.Width - 10, graph.Bounds.Height - 20), dialog)!.Value;
                Assert.That(PixelAt(frame, (int)background.X, (int)background.Y),
                    Is.EqualTo(Color.Parse(theme == ThemeVariant.Dark ? "#323232" : "#ffffff")),
                    "The actual graph canvas must follow the original Window/Editor background in the owning window's theme.");
                Assert.That(graph.LayoutModel!.Areas.Any(area => area.Segments.Any(segment => segment.Control1 is not null)), Is.True);
                GitImpactArea area = graph.LayoutModel.Areas.First(area => area.Author == "Alice");
                GitImpactRectangle block = area.Blocks[0].Bounds;
                Point point = new(block.X + block.Width / 2, block.Y + block.Height / 3);
                windowMove(dialog, graph.TranslatePoint(point, dialog)!.Value);
                Assert.That(graph.SelectedAuthor, Is.EqualTo("Alice"));
                Assert.That(Field<TextBlock>(dialog, "ImpactSelectedAuthor").Text, Does.Contain("Alice").And.Contain("2"));
                Assert.That(Field<Border>(dialog, "ImpactAuthorColor").Background, Is.TypeOf<SolidColorBrush>());
                Assert.That(CountColor(frame, graph.AuthorColor("Alice")), Is.GreaterThan(100), "Actual ribbon fill must use the original themed author color.");
            }
            Assert.That(dialog.HasRepositoryChanges, Is.False);
            Assert.That(Field<ScrollViewer>(dialog, "ImpactScroll").HorizontalScrollBarVisibility,
                Is.EqualTo(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ImpactSubmoduleCheckboxReloadsRealHistoryAndClosingCancelsWithoutMutations()
    {
        using ChartFixture root = await ChartFixture.CreateAsync();
        using ChartFixture child = await ChartFixture.CreateAsync();
        await root.Repository.RunAsync(["-c", "protocol.file.allow=always", "submodule", "add", "--", child.Repository.Path, "child ü"]);
        await root.CommitAsync("Submodule", "2026-03-01T12:00:00+00:00", "Alice");
        string before = (await root.Repository.RunAsync(["show-ref"])).StandardOutput;
        GitImpactDialog dialog = new(root.Repository);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(dialog.LastReport!.Modules.Count, Is.EqualTo(1));
            Field<CheckBox>(dialog, "ImpactIncludeSubmodules").IsChecked = true;
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null);
            Assert.That(dialog.LastReport!.Modules.Count, Is.EqualTo(2));
            Assert.That(dialog.LastReport.Total.Commits, Is.EqualTo(7));
            Assert.That((await root.Repository.RunAsync(["show-ref"])).StandardOutput, Is.EqualTo(before));
            Task operation = dialog.InitializeAsync();
            Assert.That(dialog.IsBusy, Is.True);
            dialog.Close();
            await operation;
            Assert.That(dialog.IsVisible, Is.False);
            Assert.That(dialog.IsBusy, Is.False);
            Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
        finally { if (dialog.IsVisible) dialog.Close(); }
    }

    private static void windowMove(Window window, Point point) { window.MouseMove(point); window.UpdateLayout(); }
    private static T Field<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Window window, string name) => Field<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Idle(RepositoryDialog dialog)
    {
        for (int i = 0; i < 1000 && dialog.IsBusy; i++) await Task.Delay(10);
        Assert.That(dialog.IsBusy, Is.False);
    }
    private static WriteableBitmap Capture(Window window, string filename)
    {
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new AssertionException("No native rendered frame.");
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, filename);
        frame.Save(path);
        TestContext.AddTestAttachment(path);
        return frame;
    }
    private static int CountColor(WriteableBitmap frame, Color expected)
    {
        using ILockedFramebuffer buffer = frame.Lock();
        byte[] pixels = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);
        bool bgra = buffer.Format == PixelFormat.Bgra8888;
        int count = 0;
        for (int y = 0; y < buffer.Size.Height; y++)
            for (int x = 0; x < buffer.Size.Width; x++)
            {
                int offset = y * buffer.RowBytes + x * 4;
                if (pixels[offset + (bgra ? 2 : 0)] == expected.R && pixels[offset + 1] == expected.G
                    && pixels[offset + (bgra ? 0 : 2)] == expected.B) count++;
            }
        return count;
    }
    private static Color PixelAt(WriteableBitmap frame, int x, int y)
    {
        using ILockedFramebuffer buffer = frame.Lock();
        byte[] pixel = new byte[4];
        Marshal.Copy(buffer.Address + y * buffer.RowBytes + x * 4, pixel, 0, 4);
        bool bgra = buffer.Format == PixelFormat.Bgra8888;
        return Color.FromRgb(pixel[bgra ? 2 : 0], pixel[1], pixel[bgra ? 0 : 2]);
    }

    private sealed class ChartFixture : IDisposable
    {
        private readonly string _root;
        internal GitRepository Repository { get; }
        private ChartFixture(string root, GitRepository repository) { _root = root; Repository = repository; }
        internal static async Task<ChartFixture> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "ge-chart-" + Guid.NewGuid().ToString("N"));
            GitRepository repository = await GitRepository.InitAsync(root);
            await repository.SetConfigAsync("user.name", "Chart User");
            await repository.SetConfigAsync("user.email", "chart@example.invalid");
            await repository.SetConfigAsync("commit.gpgsign", "false");
            await repository.SetConfigAsync("core.hooksPath", "/dev/null");
            ChartFixture fixture = new(root, repository);
            await System.IO.File.WriteAllTextAsync(Path.Combine(root, "main.cs"), string.Concat(Enumerable.Repeat("var value = 1;\n", 100)) + "// comment\n\n");
            Directory.CreateDirectory(Path.Combine(root, "unitTests"));
            await System.IO.File.WriteAllTextAsync(Path.Combine(root, "unitTests", "Case.cs"), "[Test]\n" + string.Concat(Enumerable.Repeat("Assert();\n", 20)));
            await System.IO.File.WriteAllTextAsync(Path.Combine(root, "Window.Designer.cs"), string.Concat(Enumerable.Repeat("generated\n", 5)));
            await System.IO.File.WriteAllTextAsync(Path.Combine(root, "script.py"), string.Concat(Enumerable.Repeat("print(1)\n", 10)));
            await fixture.CommitAsync("First", "2026-01-05T12:00:00+00:00", "Alice");
            await fixture.CommitAsync("Second", "2026-01-19T12:00:00+00:00", "Bob");
            await System.IO.File.AppendAllTextAsync(Path.Combine(root, "main.cs"), "var next = 2;\n");
            await fixture.CommitAsync("Third", "2026-02-02T12:00:00+00:00", "Alice");
            return fixture;
        }
        internal async Task CommitAsync(string message, string date, string author)
        {
            await Repository.StageAllAsync();
            string[] arguments = ["commit", "--allow-empty", "-m", message];
            GitResult result = await new GitRunner().RunAsync(Repository.Path, arguments,
                environment: new Dictionary<string, string?> { ["GIT_AUTHOR_NAME"] = author, ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date });
            if (!result.Success) throw new GitCommandException(arguments, result);
        }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
