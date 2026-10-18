using Avalonia.Controls;
using Avalonia.Layout;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class CommitDialog
{
    private CompactMenuToolbar? _commitToolbarOverflow;

    private Menu BuildCommitToolbar(MenuItem options, MenuItem templates)
    {
        Menu toolbar = new() { Name = "CommitToolbar", Height = 28, HorizontalAlignment = HorizontalAlignment.Stretch };
        _commitToolbarOverflow = new(toolbar, [_historyMenu, options, templates, CreateBranchToolbarItem()], options);
        _commitToolbarOverflow.OpeningFromOverflow += item => { if (ReferenceEquals(item, _templatesMenu)) { _templateKeyboardSelection.Begin(); } };
        Closed += (_, _) => _commitToolbarOverflow.Dispose();
        return toolbar;
    }
}
