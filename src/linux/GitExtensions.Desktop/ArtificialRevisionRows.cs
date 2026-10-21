using GitExtensions.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

/// <summary>The compact worktree/index rows shared by Browse and the comparison commit chooser.</summary>
internal static class ArtificialRevisionRows
{
    /// <summary>The same label painter as real reference badges, hosted in a selectable native history row.</summary>
    internal static Grid CreateItem(ArtificialHistoryRevision revision, Func<string, string> translate,
        HistoryViewPreferences view, bool showStatus, string prefix = "Choose")
    {
        Grid row = new() { Name = "ArtificialRevisionHistoryRow", Tag = revision,
            ColumnDefinitions = RevisionHistoryColumns.From(view).CreateDefinitions(), ClipToBounds = true };
        Button label = new() { Name = prefix + (revision.Kind == GitCompareRevisionKind.WorkingTree ? "WorkingDirectoryRevision" : "IndexRevision"),
            Focusable = false, IsHitTestVisible = false, MinHeight = 0, MinWidth = 0, Padding = default, BorderThickness = default, Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Content = RevisionHistoryRow.CreateArtificialLabel(translate(revision.Subject), view.FillRefLabels) };
        Image status = Controls.OriginalMenuItems.Icon("RepoStateUnknown.png");
        status.Name = "ArtificialRevisionStatus"; status.IsVisible = showStatus; status.Margin = new Thickness(6, 0, 0, 0);
        ToolTip.SetTip(status, translate("Unknown"));
        row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0),
            VerticalAlignment = VerticalAlignment.Center, Children = { label, status } });
        return row;
    }

    internal static void ApplyStatus(Control row, GitCompareRevisionKind kind, GitStatus? status, bool show, Func<string, string> translate)
    {
        if (row is not Grid grid || grid.Children.OfType<StackPanel>().FirstOrDefault() is not { } panel) { return; }
        Control? existing = panel.Children.FirstOrDefault(child => child.Name == "ArtificialRevisionStatus");
        if (show && existing?.Tag is ValueTuple<GitStatus?, GitCompareRevisionKind> cached
            && ReferenceEquals(cached.Item1, status) && cached.Item2 == kind) { return; }
        foreach (Control old in panel.Children.Where(child => child.Name == "ArtificialRevisionStatus").ToArray()) { panel.Children.Remove(old); }
        if (!show) { return; }
        StackPanel summary = new() { Name = "ArtificialRevisionStatus", Tag = (status, kind), Orientation = Orientation.Horizontal,
            Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        void Icon(string image, int? count = null)
        {
            summary.Children.Add(Controls.OriginalMenuItems.Icon(image));
            if (count.HasValue) { summary.Children.Add(new TextBlock { Text = count.Value.ToString(System.Globalization.CultureInfo.CurrentCulture), VerticalAlignment = VerticalAlignment.Center }); }
        }
        if (status is null) { Icon("RepoStateUnknown.png"); }
        else
        {
            GitChange[] changes = status.Changes.Where(change => kind == GitCompareRevisionKind.Index ? change.IsStaged : change.IsModified || change.IsUntracked).ToArray();
            char State(GitChange change) => kind == GitCompareRevisionKind.Index ? change.IndexStatus : change.WorkTreeStatus;
            if (changes.Length == 0) { Icon("RepoStateClean.png"); }
            else
            {
                int added = changes.Count(change => !change.IsSubmodule && (change.IsUntracked || State(change) is 'A' or '?'));
                int deleted = changes.Count(change => !change.IsSubmodule && State(change) == 'D');
                int modified = changes.Count(change => !change.IsSubmodule) - added - deleted;
                if (modified > 0) { Icon("FileStatusModified.png", modified); }
                if (added > 0) { Icon("FileStatusAdded.png", added); }
                if (deleted > 0) { Icon("FileStatusRemoved.png", deleted); }
                // The current status model does not distinguish changed/dirty gitlinks; never invent that split.
                if (changes.Any(change => change.IsSubmodule)) { Icon("RepoStateUnknown.png", changes.Count(change => change.IsSubmodule)); }
            }
            ToolTip.SetTip(summary, changes.Length + " " + translate("changes"));
        }
        panel.Children.Add(summary);
    }

}
