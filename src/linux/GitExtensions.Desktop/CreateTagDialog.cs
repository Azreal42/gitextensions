using GitExtensions.Linux.Scripts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitCommands.Git.Tag;

namespace GitExtensions.Linux.Desktop;

public sealed class CreateTagDialog : RepositoryDialog
{
    private readonly TextBox _name = ReferenceDialogControls.Text("CreateTagName");
    private readonly TextBox _revision = ReferenceDialogControls.Text("CreateTagRevision");
    private readonly ComboBox _operation = ReferenceDialogControls.Choice("CreateTagOperation");
    private readonly TextBox _key = ReferenceDialogControls.Text("TagSigningKey");
    private readonly TextBox _message = new() { Name = "CreateTagMessage", AcceptsReturn = true, Height = 90 };
    private readonly CheckBox _force = new() { Name = "ForceTag" };
    private readonly CheckBox _push = new() { Name = "PushCreatedTag" };
    private readonly ComboBox _remote = ReferenceDialogControls.Choice("CreateTagRemote");
    private readonly TextBlock _summary = new() { Name = "CreateTagSummary", TextWrapping = TextWrapping.Wrap };
    private readonly Button _create;
    private bool _hasCommits;
    public string? CreatedTag { get; private set; }

    public CreateTagDialog(GitRepository repository, string revision = "HEAD") : base(repository, "Create tag")
    {
        Width = 620;
        MinWidth = 540;
        SizeToContent = SizeToContent.Height;
        _revision.Text = revision;
        _operation.ItemsSource = new[] { T("Lightweight tag"), T("Annotated tag"), T("Sign with default GPG"), T("Sign with specific GPG") };
        _operation.SelectedIndex = 0;
        _force.Content = T("Force");
        _push.Content = T("Push tag to");
        ReferenceDialogControls.Compact(_force, _push);
        _create = Action("CreateTag", "Create tag", CreateAsync);
        _create.IsDefault = true;
        StackPanel buttons = ExportDialogControls.Buttons(Action("CancelCreateTag", "Cancel", () => { Close(); return Task.CompletedTask; }), _create);
        DockPanel.SetDock(buttons, Dock.Bottom);
        Root.Children.Add(buttons);
        Grid fields = ReferenceDialogControls.Fields();
        ReferenceDialogControls.Field(fields, T("Tag name"), _name);
        ReferenceDialogControls.Field(fields, T("Tag type"), _operation, _force);
        ReferenceDialogControls.Field(fields, T("Specific Key Id"), _key);
        ReferenceDialogControls.Field(fields, T("Create tag at this revision"), _revision, Action("ChooseTagRevision", "Choose…", ChooseAsync));
        ReferenceDialogControls.Field(fields, T("Message"), _message);
        ReferenceDialogControls.Field(fields, _push, _remote);
        Root.Children.Add(new StackPanel { Margin = new Thickness(10), Spacing = 8, Children = { fields, ClassicChrome.Border(_summary, new Thickness(1), new Thickness(8)) } });
        Track(_name, _revision, _operation, _key, _message, _force, _push, _remote);
        _name.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateActions(); } };
        _operation.SelectionChanged += (_, _) => UpdateActions();
        _push.IsCheckedChanged += (_, _) => UpdateActions();
        UpdateActions();
        Opened += (_, _) => _name.Focus();
    }

    public override Task InitializeAsync() => RunAsync("Create tag", async token =>
    {
        _hasCommits = !(await Repository.GetStatusAsync(token)).IsUnborn;
        if (!_hasCommits && ReadOptions().Revision != "HEAD")
        { await Repository.ResolveRevisionAsync(ReadOptions().Revision, token); _hasCommits = true; }
        _remote.ItemsSource = (await Repository.GetRemotesAsync(token)).Select(remote => remote.Name).ToArray();
        _remote.SelectedIndex = _remote.Items.Count > 0 ? 0 : -1;
        _summary.Text = _hasCommits ? await ReferenceDialogControls.SummaryAsync(Repository, ReadOptions().Revision, token) : T("This repository has no commits.");
        Status.Text = T("Tag name");
    });

    public GitCreateTagOptions ReadOptions() => new((_name.Text ?? "").Trim(), _revision.Text ?? "HEAD", (TagOperation)Math.Max(0, _operation.SelectedIndex),
        _message.Text ?? "", _key.Text, _force.IsChecked == true, _push.IsChecked == true ? _remote.SelectedItem as string : null);

    private async Task ChooseAsync()
    {
        GitCommit? selected = await ExportRevisionPicker.ShowAsync(this, Repository, _revision.Text);
        if (selected is null) { return; }
        _revision.Text = selected.Hash;
        await RunAsync("Revision", async token => _summary.Text = await ReferenceDialogControls.SummaryAsync(Repository, selected.Hash, token));
    }

    private async Task CreateAsync()
    {
        if (IsBusy || !IsVisible) { return; }
        GitCreateTagOptions options = ReadOptions();
        if (options.Force && (await Repository.GetReferencesAsync()).Any(reference => reference.Name == "refs/tags/" + options.Name)
            && !await Dialogs.ConfirmAsync(this, T("Replace tag"), T("Replace the existing local tag?") + "\n" + options.Name)) { return; }
        if (!IsVisible) { return; }
        await RunAsync("Create tag", async token =>
        {
            _summary.Text = await ReferenceDialogControls.SummaryAsync(Repository, options.Revision, token);
            try
            {
                await Repository.CreateTagAsync(options with { PushRemote = null }, token);
                CreatedTag = options.Name;
                if (options.PushRemote is string remote)
                {
                    using ScriptWorkflowScope scripts = new(this, Repository, token);
                    if (!await RunScriptEventAsync(scripts, ScriptEvent.BeforePush)) { return; }
                    scripts.ThrowIfStopped();
                    await Repository.PushTagReferenceAsync(remote, options.Name, cancellationToken: scripts.Token);
                    scripts.ThrowIfStopped();
                    if (!await ScriptWorkflowScope.IsGitOperationPendingAsync(Repository, scripts.Token))
                    { await RunScriptEventAsync(scripts, ScriptEvent.AfterPush); }
                }
            }
            finally { MarkRepositoryChanged(); }
            Close();
        });
    }

    protected override void UpdateActions()
    {
        _message.IsEnabled = !IsBusy && _operation.SelectedIndex > 0;
        _key.IsEnabled = !IsBusy && _operation.SelectedIndex == 3;
        _remote.IsEnabled = !IsBusy && _push.IsChecked == true;
        _push.IsEnabled = !IsBusy && _remote.Items.Count > 0;
        _create.IsEnabled = !IsBusy && _hasCommits && !string.IsNullOrWhiteSpace(_name.Text) && (_push.IsChecked != true || _remote.SelectedItem is not null);
    }
}
