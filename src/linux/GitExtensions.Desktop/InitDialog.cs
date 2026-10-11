using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace GitExtensions.Linux.Desktop;

public sealed class InitDialog : StartupRepositoryDialog
{
    private readonly TextBox _directory = new() { Name = "InitDirectory", Height = 24 };
    private readonly RadioButton _personal = new() { Name = "InitPersonal", Content = "Personal repository", GroupName = "InitType", IsChecked = true, Height = 24, Padding = new Thickness(0) };
    private readonly RadioButton _central = new() { Name = "InitCentral", Content = "Central repository, no working directory  (--bare --shared=all)", GroupName = "InitType", Height = 24, Padding = new Thickness(0) };

    public InitDialog(string directory = "") : base("Create new repository")
    {
        Width = 570;
        _directory.Text = directory;
        _personal.Content = T("Personal repository");
        _central.Content = T("Central repository, no working directory  (--bare --shared=all)");
        Grid fields = ExportDialogControls.Fields();
        fields.ColumnDefinitions = new ColumnDefinitions("65,*,Auto");
        ExportDialogControls.Field(fields, T("Directory"), _directory, Action("BrowseInitDirectory", "Browse", async () =>
        {
            string? path = await ExportDialogControls.FolderAsync(this, T("Choose directory"));
            if (path is not null) { _directory.Text = path; }
        }));
        StackPanel body = new() { Margin = new Thickness(12), Spacing = 6 };
        body.Children.Add(fields);
        body.Children.Add(ExportDialogControls.Group(T("Repository type"), new StackPanel { Spacing = 4, Children = { _personal, _central } }));
        StackPanel buttons = ExportDialogControls.Buttons(Action("CancelInit", "Cancel", () => { Close(); return Task.CompletedTask; }), Action("CreateRepository", "Create", CreateAsync));
        DockPanel.SetDock(buttons, Dock.Bottom);
        Root.Children.Add(buttons);
        Root.Children.Add(body);
        Track(_directory, _personal, _central);
    }

    private Task CreateAsync() => RunAsync("Create new repository", async token =>
    {
        string path = AbsoluteDirectory(_directory.Text);
        bool central = _central.IsChecked == true;
        Complete(await GitRepository.InitAsync(path, new GitInitOptions(Bare: central, Shared: central), token));
    });
}
