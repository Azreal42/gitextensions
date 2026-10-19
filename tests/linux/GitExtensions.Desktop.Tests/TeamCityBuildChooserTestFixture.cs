using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

internal sealed class TeamCityChooserTestClient : ITeamCityClient
{
    internal Func<CancellationToken, Task<TeamCityPage<TeamCityProject>>>? ProjectsRead;
    internal Func<string, CancellationToken, Task<TeamCityPage<TeamCityBuildType>>>? BuildsRead;
    internal int ProjectReads, RuntimeReads, SingleBuildReads, DisposeCalls;
    internal List<(string ProjectId, CancellationToken Token)> BuildReads { get; } = [];
    internal static TeamCityProject[] Projects =>
    [new("Zulu", "Zulu", "_Root"), new("App", "Application", "_Root"), new("Zeta", "Zeta child", "App"),
        new("Alpha", "Alpha child", "App"), new("_Root", "<Root project>", null), new("Archived", "Archived", "_Root", true)];
    internal static TeamCityBuildType[] Builds(string id) => id switch
    {
        "App" => [new("Z_build", "Alpha display name", "App"), new("A_build", "Zulu display name", "App")],
        "Zulu" => [new("Z_release", "Release", "Zulu")], "Alpha" => [new("Nested_build", "Nested", "Alpha")], _ => []
    };
    public Task<TeamCityPage<TeamCityProject>> GetProjectsAsync(CancellationToken cancellationToken = default)
    { ProjectReads++; return ProjectsRead?.Invoke(cancellationToken) ?? Task.FromResult(new TeamCityPage<TeamCityProject>(Projects)); }
    public Task<TeamCityPage<TeamCityBuildType>> GetProjectBuildTypesAsync(string projectId, CancellationToken cancellationToken = default)
    { BuildReads.Add((projectId, cancellationToken)); return BuildsRead?.Invoke(projectId, cancellationToken) ?? Task.FromResult(new TeamCityPage<TeamCityBuildType>(Builds(projectId))); }
    public Task<TeamCityBuildType> GetBuildTypeAsync(string buildTypeId, CancellationToken cancellationToken = default)
    { SingleBuildReads++; throw new InvalidOperationException("The chooser must restore within the selected project, not load a foreign build."); }
    public Task<TeamCityBuildSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default)
    { RuntimeReads++; throw new InvalidOperationException("Opening a chooser must not start CI polling."); }
    public Uri? BuildPageUri(TeamCityBuild build) => throw new InvalidOperationException("A chooser must not open build pages.");
    public void Dispose() => DisposeCalls++;
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>No external requests or persistence. All deliberately uncooperative reads are released in cleanup.</summary>
internal sealed class TeamCityChooserTestFixture : IAsyncDisposable
{
    internal TeamCityChooserTestClient Client { get; } = new();
    internal Window Owner { get; }
    internal TeamCityBuildChooserDialog Dialog { get; private set; } = null!;
    internal Task<TeamCityBuildSelection?> Modal { get; private set; } = null!;
    internal List<Action> ReleaseOnDispose { get; } = [];
    internal TranslationCatalog Translations { get; }
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture, _uiCulture = CultureInfo.CurrentUICulture;
    internal TeamCityChooserTestFixture(string language = "en-US", bool dark = false)
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        Translations = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.CurrentUICulture);
        Owner = new Window { Width = 700, Height = 500, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
    }
    internal void Show(TeamCityBuildSelection? initial = null)
    {
        Dialog = new(Client, initial, Translations) { RequestedThemeVariant = Owner.RequestedThemeVariant };
        Assert.That(Client.ProjectReads, Is.Zero, "The constructor must perform no network read.");
        Owner.Show(); Modal = Dialog.ShowDialog<TeamCityBuildSelection?>(Owner); Pump();
    }
    internal async Task ReadyAsync() { await UntilAsync(() => !Dialog.IsBusy); await Dialog.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Pump(); }
    internal T Field<T>(string name) where T : Control => Dialog.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    internal TreeView Tree => Field<TreeView>("treeViewTeamCityProjects");
    internal TreeViewItem Project(string id) => Nodes(Tree.Items.OfType<TreeViewItem>()).Single(node => node.Tag is TeamCityProject project && project.Id == id);
    internal TreeViewItem Build(string id) => Nodes(Tree.Items.OfType<TreeViewItem>()).Single(node => node.Tag is TeamCityBuildType build && build.Id == id);
    internal static IEnumerable<TreeViewItem> Nodes(IEnumerable<TreeViewItem> roots)
    { foreach (TreeViewItem root in roots) { yield return root; foreach (TreeViewItem child in Nodes(root.Items.OfType<TreeViewItem>())) { yield return child; } } }
    internal static string Label(TreeViewItem item) => item.Header is TextBlock label ? label.Text ?? "" : item.Header?.ToString() ?? "";
    internal Border Header(TreeViewItem item)
    {
        Pump(); return item.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_LayoutRoot" && control.TemplatedParent == item);
    }
    internal void Click(string name) { PushProcessTestDriver.Pointer(Field<Control>(name)); Pump(); }
    internal void RowClick(TreeViewItem item) { PushProcessTestDriver.Pointer(Header(item)); Pump(); }
    internal void Expand(string id)
    { TreeViewItem item = Project(id); RowClick(item); if (!item.IsExpanded) { Press(PhysicalKey.ArrowRight); } Assert.That(item.IsExpanded, Is.True); }
    internal void Collapse(string id)
    { TreeViewItem item = Project(id); if (!item.IsKeyboardFocusWithin) { RowClick(item); } if (item.IsExpanded) { Press(PhysicalKey.ArrowLeft); } Assert.That(item.IsExpanded, Is.False); }
    internal void Press(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { Dialog.KeyPressQwerty(key, modifiers); if (Dialog.IsVisible) { Dialog.KeyReleaseQwerty(key, modifiers); Pump(); } }
    internal void DoubleClick(TreeViewItem item)
    {
        Border header = Header(item); Point point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), Dialog)!.Value;
        for (int index = 0; index < 2 && Dialog.IsVisible; index++)
        {
            Pump(); Visual? hit = Dialog.InputHitTest(point) as Visual;
            Assert.That(hit?.GetSelfAndVisualAncestors().Contains(header), Is.True, "A double-click must hit the actual native row.");
            Dialog.MouseMove(point); Dialog.MouseDown(point, MouseButton.Left);
            if (Dialog.IsVisible) { Dialog.MouseUp(point, MouseButton.Left); }
        }
        if (Dialog.IsVisible) { Pump(); } else { Dispatcher.UIThread.RunJobs(); }
    }
    internal TaskCompletionSource<T> Gate<T>()
    {
        TaskCompletionSource<T> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ReleaseOnDispose.Add(() => gate.TrySetCanceled()); return gate;
    }
    internal void Pump()
    {
        Dispatcher.UIThread.RunJobs(); if (Dialog is not null && Dialog.IsVisible) { Dialog.UpdateLayout(); }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
    }
    internal async Task UntilAsync(Func<bool> condition, string? reason = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); await Task.Delay(5); }
        Pump(); Assert.That(condition(), Is.True, reason ?? "The native chooser did not reach its expected state within ten seconds.");
    }
    internal async Task<TeamCityBuildSelection?> ClosedAsync()
    { await UntilAsync(() => Modal.IsCompleted); return await Modal.WaitAsync(TimeSpan.FromSeconds(5)); }
    internal Rect Bounds(Control control) => new(control.TranslatePoint(default, Dialog)!.Value, control.Bounds.Size);
    internal void Capture(string name)
    {
        Pump(); using var image = Dialog.CaptureRenderedFrame(); Assert.That(image, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); image!.Save(path); TestContext.AddTestAttachment(path);
    }
    public async ValueTask DisposeAsync()
    {
        foreach (Action release in ReleaseOnDispose) { release(); }
        try
        {
            if (Dialog is not null)
            {
                Task draining = Dialog.BeginClose(); if (Dialog.IsVisible) { Dialog.Close(); }
                await UntilAsync(() => draining.IsCompleted); await draining.WaitAsync(TimeSpan.FromSeconds(5));
                await UntilAsync(() => !Dialog.IsVisible);
            }
        }
        finally { Owner.Close(); Client.Dispose(); CultureInfo.CurrentCulture = _culture; CultureInfo.CurrentUICulture = _uiCulture; }
    }
}
