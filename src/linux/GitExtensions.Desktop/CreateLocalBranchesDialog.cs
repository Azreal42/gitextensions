using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Platform;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original compact all-remote-branches action, using the shared portable plugin engine.</summary>
public sealed class CreateLocalBranchesDialog : RepositoryDialog
{
    private readonly CreateLocalBranchesPlugin _plugin = new();
    private readonly TextBox _remote = new() { Name = "_NO_TRANSLATE_Remote", Text = "origin", MinHeight = 24, TabIndex = 2 };
    private readonly Button _create;
    private bool _closing;
    private Task _work = Task.CompletedTask;
    public PluginResult? LastResult { get; private set; }
    internal Task WindowWorkCompletion => _work;

    public CreateLocalBranchesDialog(GitRepository repository) : base(repository, "Create local tracking branches")
    {
        Name = "CreateLocalBranchesForm"; Title = Caption("$this", "Create local tracking branches");
        // The upstream designer uses 120 DPI. Avalonia layout uses 96-DPI DIPs.
        // These are minimums: longer translations or a larger application font can grow the measured window.
        MinWidth = 518 * 96d / 120; MinHeight = 79 * 96d / 120;
        SizeToContent = SizeToContent.WidthAndHeight; CanResize = false; CanMinimize = false; CanMaximize = false; ShowInTaskbar = false;
        using (Stream icon = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/git-extensions-logo.ico"))) { Icon = new WindowIcon(icon); }
        // The source has only the remote field and action. Errors and results use the common owned message.
        if (Status.Parent is Control statusRow) { Root.Children.Remove(statusRow); }
        _create = new() { Name = "button1", Content = new AccessText { Text = Caption("button1", "Create local tracking branches") },
            MinWidth = 377 * 96d / 120, MinHeight = 24, TabIndex = 0, Margin = new Thickness(40.8, 0, 43.2, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch };
        _create.Click += (_, _) => { if (!_closing && !IsBusy) { _work = CreateAsync(); } };
        Grid field = new() { Name = "CreateLocalBranchesRemote", ColumnDefinitions = new("Auto,31.2,140.8") };
        TextBlock label = new() { Name = "label1", Text = Caption("label1", "Remote to create tracking branches for"),
            MinWidth = 267 * 96d / 120, VerticalAlignment = VerticalAlignment.Center };
        field.Children.Add(label); Grid.SetColumn(_remote, 2); field.Children.Add(_remote);
        Grid layout = new() { Name = "CreateLocalBranchesLayout", RowDefinitions = new("Auto,1.6,Auto"), Margin = new Thickness(12, 6.4, 16.8, 7.2) };
        layout.Children.Add(field); Grid.SetRow(_create, 2); layout.Children.Add(_create); Root.Children.Add(layout);
        Track(_remote, _create);
        Closing += (_, _) => _closing = true;
        Opened += (_, _) =>
        {
            if (Owner is Window owner) { RequestedThemeVariant = owner.ActualThemeVariant; }
            _create.Focus();
        };
    }

    public override Task InitializeAsync() => Task.CompletedTask;
    private string Caption(string item, string source) => Translations.Translate("CreateLocalBranchesForm", item, "Text", source);

    private async Task CreateAsync()
    {
        bool completed = false;
        string remote = _remote.Text ?? "";
        await RunAsync("Create local tracking branches", async token =>
        {
            try
            {
                using IDisposable environment = Repository.BeginOperation();
                // The engine may be interrupted after an acquired branch before it can return its result.
                // Retain uncertainty separately from the result's actual acquired-effects flag.
                MarkRepositoryMutationAttempted();
                PluginResult result = await _plugin.ExecuteAsync(new(Repository), new Dictionary<string, string?>
                { ["remote"] = remote, ["branches"] = "" }, token);
                LastResult = result;
                if (result.RepositoryMayHaveChanges) { MarkRepositoryMutationAttempted(); }
                if (result.RepositoryChanged) { MarkRepositoryChanged(); }
                if (result.Table?.Rows.Count == 0) { throw new InvalidOperationException(T("No remote branches found.")); }
                if (!result.Success)
                {
                    string details = string.Join("\n", result.Table?.Rows.Where(row => row.Cells.Count >= 4 && row.Cells[2] == "Failed")
                        .Select(row => row.Cells[0] + ": " + row.Cells[3]) ?? []);
                    throw new InvalidOperationException(result.Text + (details.Length == 0 ? "" : "\n\n" + details));
                }
                await Dialogs.ShowInformationAsync(this, T("Information"), result.Text, token);
                token.ThrowIfCancellationRequested(); completed = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                ReportError(error);
                await Dialogs.ShowMessageAsync(this, T("Error"), error.Message, warning: false, token);
            }
        });
        if (completed && !_closing && IsVisible) { Close(); }
    }
}
