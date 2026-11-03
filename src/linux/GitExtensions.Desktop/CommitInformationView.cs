using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

public enum CommitInformationCommand { NavigateRevision, NavigateReference, AddNotes, Backward, Forward, OpenExternalLink }

/// <summary>The shared, independently loaded CommitInfo panel from Browse, file history and blame.</summary>
public sealed class CommitInformationView : UserControl, IDisposable
{
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly CommitInformationPreferencesStore _preferences;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _reads = [];
    private readonly RevisionLinksController _revisionLinks = new();
    private readonly StackPanel _related = new() { Name = "CommitInformationRelatedLinks", IsVisible = false };
    private string _relatedText = "";
    internal Func<Uri, Task> RevisionLinkLauncher { get; set; } = uri =>
    {
        ProcessStartInfo start = DesktopResourceLauncher.CreateStartInfo(uri.AbsoluteUri); LoggedExternalProcess.Start(start, GitCommandLogProcessKind.OpenResource);
        return Task.CompletedTask;
    };
    private readonly Grid _header = new() { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12, Margin = new(8, 8, 16, 8) };
    private readonly Grid _headerLayout = new() { ColumnDefinitions = new("Auto,*") };
    private readonly AvatarImageControl _authorAvatar = new(role: AvatarImageRole.CommitInfo, placeholder: AvatarPlaceholder.User80)
    { Name = "CommitInformationAvatar", Width = 80, Height = 80, Margin = new(8, 8, 0, 8), VerticalAlignment = VerticalAlignment.Top, IsVisible = false };
    private readonly Dictionary<string, (TextBlock Label, Control Value)> _fields = [];
    private readonly StackPanel _supplementary = new() { Name = "CommitInformationReferences", Margin = new(8, 8, 16, 8), Spacing = 10 };
    private readonly List<MenuItem> _optionItems = [];
    private readonly MenuItem _copyLink;
    private readonly MenuItem _addNotes;
    private GitRepository? _repository;
    private GitCommit? _revision;
    private IReadOnlyList<GitCommit> _graph = [];
    private string[] _graphChildren = [];
    private string _headerBeforeChildren = "", _headerAfterChildren = "";
    private readonly Dictionary<string, IReadOnlyList<(string Hash, Control Control)>> _revisionLinkControls = [];
    private string? _context;
    private CancellationTokenSource? _current;
    private Task _pending = Task.CompletedTask;
    private long _generation;
    private long _loadedGeneration = -1;
    private long _pendingGeneration = -1;
    private bool _disposed, _closing, _showAllBranches, _showAllTags, _pointerMenuPending;
    private GitCommitContainment? _containment;
    private IReadOnlyList<GitAnnotatedTagMessage> _annotations = [];
    private GitCommitDescription? _description;
    private string _headerText = "", _copyHeaderText = "", _referenceText = "";
    private string _prioritizedBranches = GitReferencePriorities.DefaultBranchPatterns;
    private string _prioritizedRemotes = GitReferencePriorities.DefaultRemotePatterns;
    private (CommitInformationCommand Command, string Target)? _contextLink;

    public SelectableTextBlock MessageControl { get; } = new()
    {
        Name = "CommitInformationMessage", TextWrapping = TextWrapping.Wrap, Focusable = true,
        Margin = new(8), MinHeight = 20, Background = Brushes.Transparent
    };
    public CommitInformationOptions Options { get; private set; }
    public bool ShowBranchesAsLinks { get; set; }
    public bool ShowRevisionLinks { get; set; } = true;
    public string CopyText => _copyHeaderText + (string.IsNullOrEmpty(MessageControl.Text) ? "" : "\n\n" + MessageControl.Text);
    public string Text => _headerText + (string.IsNullOrEmpty(MessageControl.Text) ? "" : "\n\n" + MessageControl.Text)
        + (string.IsNullOrEmpty(_relatedText) ? "" : "\n\n" + _relatedText)
        + (string.IsNullOrEmpty(_referenceText) ? "" : "\n\n" + _referenceText);
    public bool IsLoading => _reads.Any(task => !task.IsCompleted) || _revisionLinks.IsLoading;
    public Task Completion => Task.WhenAll(_reads.Append(_authorAvatar.Completion).Append(_revisionLinks.Completion).ToArray());
    public event Action<CommitInformationCommand, string?>? CommandRequested;
    public event Action<string>? InteractionError;

    public CommitInformationView() : this(new CommitInformationPreferencesStore()) { }
    public CommitInformationView(CommitInformationPreferencesStore preferences)
    {
        _preferences = preferences; Options = preferences.Load();
        _revisionLinks.Updated += RenderRelatedLinks;
        AttachedToVisualTree += (_, _) => { _revisionLinks.Attach(); if (_loadedGeneration == _generation) { _ = _revisionLinks.LoadAsync(); } };
        DetachedFromVisualTree += (_, _) => _revisionLinks.Detach();
        DesktopFonts.Apply(MessageControl, DesktopFontRole.Commit);
        Focusable = false;
        foreach (string field in new[] { "Author", "Date", "Committer", "Commit date", "Commit hash", "Children", "Parents", "File" })
        {
            int row = _header.RowDefinitions.Count; _header.RowDefinitions.Add(new(GridLength.Auto));
            TextBlock label = new() { Text = T(field) + ":", VerticalAlignment = VerticalAlignment.Top };
            Control value = field is "Children" or "Parents" ? new WrapPanel { Orientation = Orientation.Horizontal }
                : new SelectableTextBlock { TextWrapping = TextWrapping.NoWrap, Focusable = true, Background = Brushes.Transparent, Name = "CommitInformation" + field.Replace(" ", "") };
            Grid.SetRow(label, row); Grid.SetRow(value, row); Grid.SetColumn(value, 1);
            _header.Children.Add(label); _header.Children.Add(value); _fields.Add(field, (label, value));
        }
        Border message = new() { Child = MessageControl };
        message.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ClassicMenuBrush"));
        _headerLayout.Children.Add(_authorAvatar); Grid.SetColumn(_header, 1); _headerLayout.Children.Add(_header);
        ScrollViewer headerScroll = new()
        {
            Name = "CommitInformationHeaderScroll", Content = _headerLayout, Focusable = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        StackPanel content = new() { Children = { headerScroll, message, _supplementary } };
        ScrollViewer viewer = new()
        {
            Name = "CommitInformationScroll", Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        viewer.Bind(BackgroundProperty, new DynamicResourceExtension("ClassicWindowBrush"));
        Content = viewer;
        _copyLink = Menu("copyLinkToolStripMenuItem", "Copy link", async () =>
        {
            if (_contextLink is { } link) { await CopyAsync(LinkAddress(link)); }
        });
        MenuItem copy = Menu("copyCommitInfoToolStripMenuItem", "&Copy commit info", () => CopyAsync(CopyText));
        AddOption("showContainedInBranchesToolStripMenuItem", "Show local branches containing this commit", o => o.Local, o => o with { Local = !o.Local });
        AddOption("showContainedInBranchesRemoteToolStripMenuItem", "Show remote branches containing this commit", o => o.Remote, o => o with { Remote = !o.Remote });
        AddOption("showContainedInBranchesRemoteIfNoLocalToolStripMenuItem", "Show remote branches only when no local branch contains this commit", o => o.RemoteIfNoLocal, o => o with { RemoteIfNoLocal = !o.RemoteIfNoLocal });
        AddOption("showContainedInTagsToolStripMenuItem", "Show tags containing this commit", o => o.Tags, o => o with { Tags = !o.Tags });
        AddOption("showMessagesOfAnnotatedTagsToolStripMenuItem", "Show messages of annotated tags", o => o.Annotated, o => o with { Annotated = !o.Annotated });
        AddOption("showTagThisCommitDerivesFromMenuItem", "Show the most recent tag this commit derives from", o => o.Derives, o => o with { Derives = !o.Derives });
        _addNotes = Menu("addNoteToolStripMenuItem", "Add &notes", () =>
        {
            if (_revision is not null) { CommandRequested?.Invoke(CommitInformationCommand.AddNotes, _revision.Hash); }
            return Task.CompletedTask;
        });
        ContextMenu = new() { Name = "commitInfoContextMenuStrip", ItemsSource = new Control[] { _copyLink, copy, new Separator() }.Concat(_optionItems).Concat([new Separator(), _addNotes]).ToArray() };
        MessageControl.ContextMenu = ContextMenu;
        foreach (var field in _fields.Values) { field.Value.ContextMenu = ContextMenu; }
        SynchronizeMenu();
        ContextMenu.Opened += (_, _) =>
        {
            if (!_pointerMenuPending) { _contextLink = null; }
            _pointerMenuPending = false;
            CommitInformationOptions saved = _preferences.Load();
            if (saved != Options) { _ = ApplyOptionsAsync(saved, persist: false); }
            _copyLink.IsVisible = _contextLink is not null; _addNotes.IsEnabled = _revision is not null;
            SynchronizeMenu();
        };
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                _pointerMenuPending = true;
                Button? link = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault();
                _contextLink = link?.Tag is ValueTuple<CommitInformationCommand, string> target ? target : null;
            }
            if (e.GetCurrentPoint(this).Properties.IsXButton1Pressed) { CommandRequested?.Invoke(CommitInformationCommand.Backward, null); }
            if (e.GetCurrentPoint(this).Properties.IsXButton2Pressed) { CommandRequested?.Invoke(CommitInformationCommand.Forward, null); }
        }, RoutingStrategies.Tunnel);
        SetMessage("");
    }

    public void FocusContent() => MessageControl.Focus();

    // No Git and no focus transition here. The owner decides whether the panel is visible.
    public void SetRevision(GitRepository? repository, GitCommit? revision, IReadOnlyList<GitCommit>? graph = null, string? context = null)
    {
        if (_disposed || _closing) { return; }
        bool same = ReferenceEquals(repository, _repository) && revision?.Hash == _revision?.Hash;
        Cancel(); _revisionLinks.SetRevision(repository, revision?.Hash); _repository = repository; _revision = revision; _graph = graph ?? []; _context = context;
        Options = _preferences.Load(); _contextLink = null; _pointerMenuPending = false;
        _containment = null; _annotations = []; _description = null; _showAllBranches = _showAllTags = false;
        if (repository is null || revision is null) { ClearContent(); return; }
        if (!same) { ClearRelatedLinks(); PublishHeader(KnownDetails(revision)); _supplementary.Children.Clear(); _referenceText = ""; }
        _authorAvatar.SetActive(true);
    }

    // A graph append changes local Children links, not the selected revision's metadata generation.
    internal void UpdateGraph(GitRepository repository, IReadOnlyList<GitCommit> graph)
    {
        if (_disposed || _closing || !ReferenceEquals(repository, _repository) || _revision is null) { return; }
        _graph = graph;
        string[] children = graph.Where(commit => commit.Parents.Contains(_revision.Hash, StringComparer.Ordinal))
            .Select(commit => commit.Hash).ToArray();
        if (_graphChildren.SequenceEqual(children)) { return; }
        _graphChildren = children;
        SetRevisionLinks("Children", children);
        string line = children.Length == 0 ? "" : T("Children") + ": " + string.Join(' ', children) + "\n";
        _headerText = (_headerBeforeChildren + line + _headerAfterChildren).TrimEnd('\n');
    }

    public void SetMessage(string text)
    {
        Cancel(); _revisionLinks.SetRevision(null, null); _repository = null; _revision = null; ClearContent(); MessageControl.Text = text;
    }

    public void Cancel()
    {
        _generation++; _loadedGeneration = -1; _current?.Cancel(); _current = null;
        _authorAvatar.SetActive(false); _revisionLinks.Cancel();
    }

    public void BeginClose()
    {
        if (_closing) { return; }
        _closing = true; Cancel(); _lifetime.Cancel(); _authorAvatar.BeginClose(); _ = _revisionLinks.BeginClose();
    }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _closing || _repository is null || _revision is null || _loadedGeneration == _generation) { return Task.CompletedTask; }
        if (_pendingGeneration == _generation && !_pending.IsCompleted) { return _pending; }
        _current = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _pendingGeneration = _generation;
        _pending = ReadAsync(_repository, _revision, _generation, Options, _current);
        Track(_pending);
        return _pending;
    }

    private void Track(Task task)
    {
        _reads.Add(task);
        _ = RemoveWhenCompletedAsync(task);
    }

    private async Task RemoveWhenCompletedAsync(Task task)
    {
        try { await task; } catch { /* The owner (or option action) observes and reports its request. */ }
        finally { _reads.Remove(task); }
    }

    private async Task ReadAsync(GitRepository repository, GitCommit revision, long generation, CommitInformationOptions options, CancellationTokenSource source)
    {
        CancellationToken token = source.Token;
        try
        {
            GitCommitReferenceScope scope = (options.WantsLocal ? GitCommitReferenceScope.LocalBranches : 0)
                | (options.WantsRemote ? GitCommitReferenceScope.RemoteBranches : 0)
                | (options.Tags || options.Annotated ? GitCommitReferenceScope.Tags : 0);
            Task relatedLinks = _revisionLinks.LoadAsync(token);
            Task<GitCommitDetails> details = repository.GetCommitDetailsAsync(revision.Hash, includeNotes: true, token);
            Task<GitCommitContainment> containment = repository.GetCommitContainingReferencesAsync(revision.Hash, scope, token);
            Task<GitCommitDescription?> description = options.Derives ? repository.GetCommitDescribeAsync(revision.Hash, token) : Task.FromResult<GitCommitDescription?>(null);
            // Await every independently started read even if another fails, so cancellation/close drains all children.
            try { await Task.WhenAll(details, containment, description, relatedLinks); }
            catch { source.Cancel(); try { await Task.WhenAll(details, containment, description, relatedLinks); } catch { } throw; }
            token.ThrowIfCancellationRequested();
            GitCommitContainment refs = await containment;
            GitReference[] attached = refs.References.Where(reference => reference.Kind == GitCommitReferenceKind.Tag && reference.RevisionHash == revision.Hash)
                .Select(reference => new GitReference(reference.FullName, reference.ObjectHash, null, false) { RevisionHash = reference.RevisionHash! }).ToArray();
            IReadOnlyList<GitAnnotatedTagMessage> annotations = options.Annotated
                ? await repository.GetAnnotatedTagMessagesAsync(revision.Hash, attached, token) : [];
            token.ThrowIfCancellationRequested();
            if (_disposed || generation != _generation || !ReferenceEquals(_repository, repository) || _revision?.Hash != revision.Hash) { return; }
            DesktopPreferences priorities = new PreferencesStore().Load();
            _prioritizedBranches = priorities.PrioritizedBranchNames; _prioritizedRemotes = priorities.PrioritizedRemoteNames;
            _containment = refs; _annotations = annotations; _description = await description;
            PublishHeader(await details); RenderReferences(); _loadedGeneration = generation;
        }
        finally { if (_current == source) { _current = null; } source.Dispose(); }
    }

    private void PublishHeader(GitCommitDetails details)
    {
        bool datesEqual = details.Author.Date.EqualsExact(details.Committer.Date);
        bool identitiesEqual = details.Author.Name == details.Committer.Name && details.Author.Email == details.Committer.Email;
        StringBuilder plain = new(), copy = new();
        Set("Author", T("Author"), Identity(details.Author));
        Set("Date", T(datesEqual ? "Date" : "Author date"), Date(details.Author.Date));
        Set("Committer", T("Committer"), identitiesEqual ? null : Identity(details.Committer));
        Set("Commit date", T("Commit date"), datesEqual ? null : Date(details.Committer.Date));
        Set("Commit hash", T("Commit hash"), details.Hash);
        _graphChildren = _graph.Where(commit => commit.Parents.Contains(details.Hash, StringComparer.Ordinal)).Select(commit => commit.Hash).ToArray();
        _headerBeforeChildren = plain.ToString();
        SetLinks("Children", _graphChildren);
        int afterChildren = plain.Length;
        SetLinks("Parents", details.Parents);
        Set("File", T("File"), _context);
        _headerAfterChildren = plain.ToString(afterChildren, plain.Length - afterChildren);
        _headerText = plain.ToString().TrimEnd('\n');
        _copyHeaderText = copy.ToString().TrimEnd('\n');
        string message = details.Message.TrimEnd('\r', '\n');
        if (!string.IsNullOrEmpty(details.Notes)) { message += "\n\n" + T("Notes") + ":\n" + details.Notes.TrimEnd('\r', '\n'); }
        if (MessageControl.Text != message) { MessageControl.Text = message; }
        _header.IsVisible = _headerLayout.IsVisible = true;
        _authorAvatar.SetIdentity(string.IsNullOrWhiteSpace(details.Author.Email) ? details.Committer.Email : details.Author.Email,
            string.IsNullOrWhiteSpace(details.Author.Name) ? details.Committer.Name : details.Author.Name, details.Hash);
        _authorAvatar.SetActive(true);

        void Set(string key, string label, string? value)
        {
            var field = _fields[key]; field.Label.Text = label + ":"; field.Label.IsVisible = field.Value.IsVisible = value is not null;
            ((SelectableTextBlock)field.Value).Text = value;
            if (value is not null)
            {
                plain.Append(label).Append(": ").Append(value).Append('\n');
                string copied = key == "Date" ? details.Author.Date.LocalDateTime.ToString("G", CultureInfo.CurrentCulture)
                    : key == "Commit date" ? details.Committer.Date.LocalDateTime.ToString("G", CultureInfo.CurrentCulture) : value;
                copy.Append(label).Append(": ").Append(copied).Append('\n');
            }
        }
        void SetLinks(string key, IReadOnlyList<string> hashes)
        {
            SetRevisionLinks(key, hashes);
            if (hashes.Count != 0) { plain.Append(T(key)).Append(": ").AppendJoin(" ", hashes).Append('\n'); }
        }
    }

    private void SetRevisionLinks(string key, IReadOnlyList<string> hashes)
    {
        var linkField = _fields[key]; linkField.Label.IsVisible = linkField.Value.IsVisible = hashes.Count != 0;
        WrapPanel links = (WrapPanel)linkField.Value;
        IReadOnlyList<(string Hash, Control Control)> previous = _revisionLinkControls.GetValueOrDefault(key) ?? [];
        List<(string Hash, Control Control)> desired = [];
        foreach (string hash in hashes)
        {
            Control? retained = previous.FirstOrDefault(item => item.Hash == hash && (item.Control is Button) == ShowRevisionLinks
                && !desired.Any(other => ReferenceEquals(other.Control, item.Control))).Control;
            Control control = retained ?? (ShowRevisionLinks
                ? Link(hash[..Math.Min(10, hash.Length)], CommitInformationCommand.NavigateRevision, hash)
                : Selectable(hash[..Math.Min(10, hash.Length)]));
            desired.Add((hash, control));
        }
        for (int index = links.Children.Count - 1; index >= 0; index--)
        {
            if (!desired.Any(item => ReferenceEquals(item.Control, links.Children[index]))) { links.Children.RemoveAt(index); }
        }
        for (int index = 0; index < desired.Count; index++)
        {
            int current = links.Children.IndexOf(desired[index].Control);
            if (current < 0) { links.Children.Insert(index, desired[index].Control); }
            else if (current != index) { links.Children.Move(current, index); }
        }
        _revisionLinkControls[key] = desired;
    }

    private void RenderReferences()
    {
        _supplementary.Children.Clear(); _referenceText = "";
        if (_containment is null) { return; }
        List<string> sections = [];
        foreach (GitAnnotatedTagMessage annotation in _annotations)
        {
            string name = annotation.FullName.StartsWith("refs/tags/", StringComparison.Ordinal) ? annotation.FullName[10..] : annotation.FullName;
            StackPanel block = new(); SelectableTextBlock label = Selectable(name); label.TextDecorations = TextDecorations.Underline; block.Children.Add(label);
            block.Children.Add(Selectable(annotation.Message)); _supplementary.Children.Add(block); sections.Add(name + "\n" + annotation.Message);
        }
        _supplementary.Children.Add(_related);
        bool mixed = Options.WantsLocal && Options.WantsRemote;
        List<CommitInformationReference<GitCommitReference>> branches = _containment.References.Where(reference => reference.Kind != GitCommitReferenceKind.Tag)
            .Select(reference => new CommitInformationReference<GitCommitReference>(reference,
                reference.Kind == GitCommitReferenceKind.RemoteBranch && mixed ? "remotes/" + reference.ShortName : reference.ShortName)).ToList();
        bool detached = _containment.DetachedHead is { ContainsSelectedCommit: true };
        if (Options.WantsLocal && detached)
        {
            string name = "(HEAD detached at " + _containment.DetachedHead!.Hash[..10] + ")";
            branches.Add(new(new(GitCommitReferenceKind.LocalBranch, "HEAD", name, _containment.DetachedHead.Hash, _containment.DetachedHead.Hash, null, true, null), name));
        }
        string current = _containment.CurrentBranchFullName?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? _containment.CurrentBranchFullName[11..] : "";
        var branchList = CommitInformationPresentation.Branches(branches, Options, current, detached, _prioritizedBranches, _prioritizedRemotes, _showAllBranches);
        AddList(branchList, "branches", "Contained in branches:", "Contained in no branch", () => { _showAllBranches = true; RenderReferences(); });
        var tags = _containment.References.Where(reference => reference.Kind == GitCommitReferenceKind.Tag)
            .Select(reference => new CommitInformationReference<GitCommitReference>(reference, reference.ShortName));
        var tagList = CommitInformationPresentation.Tags(tags, Options, _showAllTags);
        AddList(tagList, "tags", "Contained in tags:", "Contained in no tag", () => { _showAllTags = true; RenderReferences(); });
        if (Options.Derives)
        {
            string text = _description is null ? T("Derives from no tag") : T("Derives from tag:") + " " + _description.TagName +
                (_description.CommitDistance is > 0 ? " + " + _description.CommitDistance.Value.ToString(CultureInfo.CurrentCulture) + " " + T("commits") : "");
            if (_description is not null && ShowBranchesAsLinks)
            {
                WrapPanel line = new() { Orientation = Orientation.Horizontal };
                line.Children.Add(Selectable(T("Derives from tag:") + " "));
                line.Children.Add(Link(_description.TagName, CommitInformationCommand.NavigateReference, "refs/tags/" + _description.TagName));
                if (_description.CommitDistance is > 0) { line.Children.Add(Selectable("+ " + _description.CommitDistance.Value.ToString(CultureInfo.CurrentCulture) + " " + T("commits"))); }
                _supplementary.Children.Add(line);
            }
            else { _supplementary.Children.Add(Selectable(text)); }
            sections.Add(text);
        }
        _referenceText = string.Join("\n\n", sections);

        void AddList(CommitInformationReferenceList<GitCommitReference> list, string kind, string heading, string empty, Action showAll)
        {
            if (!list.IsVisible) { return; }
            StackPanel block = new() { Name = "CommitInformationContained" + kind };
            string label = list.Items.Count == 0 ? T(empty) : T(heading); block.Children.Add(Selectable(label));
            List<string> names = [];
            foreach (var item in list.Items)
            {
                names.Add(item.Text);
                block.Children.Add(ShowBranchesAsLinks ? Link(item.Text, CommitInformationCommand.NavigateReference, item.Identity.FullName) : Selectable(item.Text));
            }
            if (list.ShowAll)
            {
                Button all = Link(T("Show all"), CommitInformationCommand.NavigateReference, "showall/" + kind);
                all.Name = "CommitInformationShowAll" + kind;
                all.Click += (_, _) =>
                {
                    bool hadFocus = all.IsKeyboardFocusWithin; showAll();
                    if (hadFocus) { FocusContent(); }
                };
                block.Children.Add(all); names.Add(T("Show all"));
            }
            _supplementary.Children.Add(block); sections.Add(label + (names.Count == 0 ? "" : "\n" + string.Join('\n', names)));
        }
    }

    private Button Link(string text, CommitInformationCommand command, string target)
    {
        Button link = new()
        {
            Content = new TextBlock { Text = text, TextDecorations = TextDecorations.Underline }, Tag = (command, target),
            Padding = new(0), Margin = new(0, 0, 6, 0), MinHeight = 0, MinWidth = 0, BorderThickness = new(0), Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left, Cursor = new(StandardCursorType.Hand)
        };
        link.Bind(ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundAccentBrush"));
        link.Click += async (_, _) =>
        {
            if (_closing || _disposed) { return; }
            if (command == CommitInformationCommand.OpenExternalLink)
            {
                try { await ActivateRevisionLinkAsync(target); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
                { InteractionError?.Invoke(error.Message); }
            }
            else if (!target.StartsWith("showall/", StringComparison.Ordinal)) { CommandRequested?.Invoke(command, target); }
        };
        return link;
    }

    private void ClearRelatedLinks()
    { _related.Children.Clear(); _related.IsVisible = false; _relatedText = ""; }

    private void RenderRelatedLinks(RevisionLinksResult result)
    {
        ClearRelatedLinks();
        if (result.Links.Count != 0)
        {
            string heading = T("Related links:");
            _related.Children.Add(Selectable(heading));
            WrapPanel line = new() { Orientation = Orientation.Horizontal };
            foreach (var external in result.Links)
            {
                if (line.Children.Count != 0) { line.Children.Add(Selectable(", ")); }
                Button link = Link(external.Caption ?? external.Uri, CommitInformationCommand.OpenExternalLink, external.Uri);
                link.Name = "CommitInformationRelatedLink"; line.Children.Add(link);
            }
            _related.Children.Add(line); _related.IsVisible = true;
            _relatedText = heading + "\n" + string.Join(", ", result.Links.Select(link => link.Caption ?? link.Uri));
        }
        string diagnostic = string.Join("\n", result.Diagnostics.Select(item => item.Message).Concat(result.Error is null ? [] : new[] { result.Error }));
        // Invalid patterns never rewrite user XML or interrupt the commit's other information.
        ToolTip.SetTip(_related, diagnostic.Length == 0 ? null : diagnostic);
        if (diagnostic.Length != 0)
        {
            _related.Children.Add(new TextBlock { Name = "RevisionLinksDiagnostic", Text = diagnostic, TextWrapping = TextWrapping.Wrap });
            _related.IsVisible = true;
        }
    }

    internal async Task ActivateRevisionLinkAsync(string address)
    {
        if (_disposed || _closing) { return; }
        string candidate = address;
        while (!Uri.TryCreate(candidate, UriKind.Absolute, out _))
        {
            int hash = candidate.IndexOf('#');
            if (hash < 0) { throw new ArgumentException("Invalid revision link: " + address); }
            candidate = candidate[(hash + 1)..];
        }
        Uri uri = new(candidate, UriKind.Absolute);
        if (uri.Scheme == "gitext")
        {
            string target = uri.AbsolutePath.TrimStart('/');
            if (uri.Host == "gotocommit") { CommandRequested?.Invoke(CommitInformationCommand.NavigateRevision, target); }
            else if (uri.Host is "gotobranch" or "gototag")
            { CommandRequested?.Invoke(CommitInformationCommand.NavigateReference, (uri.Host == "gototag" ? "refs/tags/" : "") + target); }
            else if (uri.Host == "showall" && target is "branches" or "tags")
            { if (target == "branches") { _showAllBranches = true; } else { _showAllTags = true; } RenderReferences(); }
            else { throw new ArgumentException("Unknown Git Extensions link: " + address); }
            return;
        }
        await RevisionLinkLauncher(uri);
    }

    private void AddOption(string name, string text, Func<CommitInformationOptions, bool> selected, Func<CommitInformationOptions, CommitInformationOptions> change)
    {
        MenuItem item = Menu(name, text, () => ApplyOptionsAsync(change(_preferences.Load()), persist: true));
        item.ToggleType = MenuItemToggleType.CheckBox; item.Tag = selected; _optionItems.Add(item);
    }

    private async Task ApplyOptionsAsync(CommitInformationOptions updated, bool persist)
    {
        if (_disposed || _closing) { return; }
        if (persist) { _preferences.Save(updated); }
        Options = updated; Cancel(); SynchronizeMenu(); _showAllBranches = _showAllTags = false;
        long generation = _generation;
        try { await LoadAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_disposed && !_closing && generation == _generation) { InteractionError?.Invoke(exception.Message); }
        }
    }

    private void SynchronizeMenu()
    {
        foreach (MenuItem item in _optionItems) { item.IsChecked = ((Func<CommitInformationOptions, bool>)item.Tag!)(Options); }
    }

    private MenuItem Menu(string name, string source, Func<Task> action)
    {
        MenuItem item = new() { Name = name, Header = new AccessText { Text = _translations.TranslateAccessKeys("CommitInfo", name, "Text", source) } };
        item.Click += async (_, _) =>
        {
            if (_disposed || _closing) { return; }
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { if (!_disposed) { InteractionError?.Invoke(exception.Message); } }
        };
        return item;
    }

    private async Task CopyAsync(string text)
    { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) { await clipboard.SetTextAsync(text); } }
    private static string LinkAddress((CommitInformationCommand Command, string Target) link)
        => link.Command == CommitInformationCommand.OpenExternalLink ? link.Target
            : link.Target.StartsWith("showall/", StringComparison.Ordinal) ? "gitext://" + link.Target
            : link.Command == CommitInformationCommand.NavigateRevision ? "gitext://gotocommit/" + link.Target
            : "gitext://" + (link.Target.StartsWith("refs/tags/", StringComparison.Ordinal) ? "gototag/" + link.Target[10..]
                : "gotobranch/" + (link.Target.StartsWith("refs/heads/", StringComparison.Ordinal) ? link.Target[11..]
                    : link.Target.StartsWith("refs/remotes/", StringComparison.Ordinal) ? link.Target[13..] : link.Target));
    private SelectableTextBlock Selectable(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Focusable = true, Background = Brushes.Transparent, ContextMenu = ContextMenu };
    private string T(string source) => _translations.Translate(source);
    private static string Identity(GitCommitIdentity identity) => identity.Name + " <" + identity.Email + ">";
    private string Date(DateTimeOffset date) => date.LocalDateTime.ToString("G", CultureInfo.CurrentCulture) + " (" + RevisionHistoryRow.FormatRelativeDate(date, DateTimeOffset.Now, _translations) + ")";
    private static GitCommitDetails KnownDetails(GitCommit commit) => new(commit.Hash, "", commit.Parents,
        new(commit.AuthorName, commit.AuthorEmail, commit.AuthoredAt), new(commit.AuthorName, commit.AuthorEmail, commit.AuthoredAt), commit.Subject, commit.Body, null);
    private void ClearContent()
    {
        _authorAvatar.SetActive(false); _authorAvatar.SetIdentity(null, null);
        _header.IsVisible = _headerLayout.IsVisible = false; _headerText = _copyHeaderText = _referenceText = ""; MessageControl.Text = ""; _supplementary.Children.Clear();
        _graphChildren = []; _headerBeforeChildren = _headerAfterChildren = ""; _revisionLinkControls.Clear();
        _contextLink = null; _pointerMenuPending = false; ClearRelatedLinks();
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        BeginClose(); _disposed = true; _revisionLinks.Dispose(); _lifetime.Dispose();
    }
}
