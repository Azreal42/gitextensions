using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitExtensions.Linux.Desktop;

internal sealed record GeneralSettingsPublication(DesktopPreferences Desktop, GeneralSettingsValues Values, FileHistoryPreferences FileHistory);

// General edits existing workflow preferences. It is not another persistence authority.
internal sealed class GeneralSettingsSession
{
    private readonly PreferencesStore _desktop = new();
    private readonly PullPreferencesStore _pull = new();
    private readonly ProcessDialogPreferencesStore _process = new();
    private readonly ClonePreferencesStore _clone = new();
    private readonly SubmoduleCheckoutPreferencesStore _submodules = new();
    private readonly FileHistoryPreferencesStore _history = new();
    private readonly BlamePreferencesStore _blame = new();
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private GeneralSettingsValues _baseline;

    internal GeneralSettingsSession(DesktopPreferences initial, FileHistoryPreferences? fileHistory = null)
        => Initial = _baseline = Read(initial, fileHistory ?? _history.Load());

    internal GeneralSettingsValues Initial { get; private set; }

    internal GeneralSettingsPublication Save(GeneralSettingsValues draft, DesktopPreferences desired, Action<DesktopPreferences>? desktopSaved = null)
    {
        ArgumentNullException.ThrowIfNull(draft); ArgumentNullException.ThrowIfNull(desired);
        if (draft.MaxRevisionGraphCommits is < 0 or > 1_000_000 || draft.RevisionGridQuickSearchTimeout is < 100 or > 1_000_000
            || !PullPreferencesStore.IsToolbarAction(draft.DefaultPullAction))
        { throw new ArgumentOutOfRangeException(nameof(draft)); }
        if (!draft.ShowGitStatusInBrowseToolbar && !draft.ShowGitStatusForArtificialCommits)
        { draft = draft with { ShowSubmoduleStatus = false }; }

        DesktopPreferences latest = _desktop.Load();
        DesktopPreferences accepted = desired with
        {
            CheckForUncommittedChangesInCheckoutBranch = Pick(_baseline.CheckForUncommittedChangesInCheckoutBranch, draft.CheckForUncommittedChangesInCheckoutBranch, latest.CheckForUncommittedChangesInCheckoutBranch),
            StartWithRecentWorkingDir = Pick(_baseline.StartWithRecentWorkingDir, draft.StartWithRecentWorkingDir, latest.StartWithRecentWorkingDir),
            MaxRevisionGraphCommits = Pick(_baseline.MaxRevisionGraphCommits, draft.MaxRevisionGraphCommits, latest.MaxRevisionGraphCommits),
            RevisionGridQuickSearchTimeout = Pick(_baseline.RevisionGridQuickSearchTimeout, draft.RevisionGridQuickSearchTimeout, latest.RevisionGridQuickSearchTimeout),
            ShowGitStatusInBrowseToolbar = Pick(_baseline.ShowGitStatusInBrowseToolbar, draft.ShowGitStatusInBrowseToolbar, latest.ShowGitStatusInBrowseToolbar),
            ShowGitStatusForArtificialCommits = Pick(_baseline.ShowGitStatusForArtificialCommits, draft.ShowGitStatusForArtificialCommits, latest.ShowGitStatusForArtificialCommits),
            ShowSubmoduleStatus = Pick(_baseline.ShowSubmoduleStatus, draft.ShowSubmoduleStatus, latest.ShowSubmoduleStatus),
            ShowStashCount = Pick(_baseline.ShowStashCount, draft.ShowStashCount, latest.ShowStashCount),
            ShowAheadBehindData = Pick(_baseline.ShowAheadBehindData, draft.ShowAheadBehindData, latest.ShowAheadBehindData),
            UseBrowseForFileHistory = Pick(_baseline.UseBrowseForFileHistory, draft.UseBrowseForFileHistory, latest.UseBrowseForFileHistory)
        };
        accepted = accepted with { ShowSubmoduleStatus = (accepted.ShowGitStatusInBrowseToolbar || accepted.ShowGitStatusForArtificialCommits) && accepted.ShowSubmoduleStatus };
        _desktop.Save(accepted);
        // A later profile failure must not make retry reinterpret a previous
        // unchanged control as a new intention that overwrites a concurrent edit.
        _baseline = _baseline with
        {
            CheckForUncommittedChangesInCheckoutBranch = draft.CheckForUncommittedChangesInCheckoutBranch,
            StartWithRecentWorkingDir = draft.StartWithRecentWorkingDir, MaxRevisionGraphCommits = draft.MaxRevisionGraphCommits,
            RevisionGridQuickSearchTimeout = draft.RevisionGridQuickSearchTimeout,
            ShowGitStatusInBrowseToolbar = draft.ShowGitStatusInBrowseToolbar,
            ShowGitStatusForArtificialCommits = draft.ShowGitStatusForArtificialCommits, ShowSubmoduleStatus = draft.ShowSubmoduleStatus,
            ShowStashCount = draft.ShowStashCount, ShowAheadBehindData = draft.ShowAheadBehindData,
            UseBrowseForFileHistory = draft.UseBrowseForFileHistory
        };
        // The Settings host acknowledges its already-published desktop intentions before
        // a later workflow profile can fail. The accepted value also publishes an already-existing
        // avatar runtime policy; the host owns mutation error reporting independently of profile IO.
        desktopSaved?.Invoke(accepted);
        if (draft.DefaultPullAction != _baseline.DefaultPullAction || draft.IncludeUntrackedFilesInAutoStash != _baseline.IncludeUntrackedFilesInAutoStash)
        {
            Readable<PullPreferences>(_pull.FilePath);
            PullPreferences current = _pull.Load();
            _pull.Save(current with
            {
                DefaultAction = Pick(_baseline.DefaultPullAction, draft.DefaultPullAction, current.DefaultAction),
                IncludeUntrackedFilesInAutoStash = Pick(_baseline.IncludeUntrackedFilesInAutoStash, draft.IncludeUntrackedFilesInAutoStash, current.IncludeUntrackedFilesInAutoStash)
            });
            _baseline = _baseline with { DefaultPullAction = draft.DefaultPullAction, IncludeUntrackedFilesInAutoStash = draft.IncludeUntrackedFilesInAutoStash };
        }
        if (draft.CloseProcessDialog != _baseline.CloseProcessDialog)
        {
            Readable<ProcessDialogPreferences>(_process.FilePath);
            _process.Save(_process.Load() with { CloseProcessDialog = draft.CloseProcessDialog });
            _baseline = _baseline with { CloseProcessDialog = draft.CloseProcessDialog };
        }
        if (draft.UpdateSubmodulesOnCheckout != _baseline.UpdateSubmodulesOnCheckout)
        {
            Readable<SubmoduleCheckoutPreferences>(_submodules.FilePath);
            _submodules.SaveStrict(_submodules.Load() with { UpdateSubmodulesOnCheckout = draft.UpdateSubmodulesOnCheckout });
            _baseline = _baseline with { UpdateSubmodulesOnCheckout = draft.UpdateSubmodulesOnCheckout };
        }
        if (draft.DefaultCloneDestinationPath != _baseline.DefaultCloneDestinationPath)
        {
            Readable<ClonePreferences>(_clone.FilePath);
            _clone.SaveStrict(_clone.Load() with { DefaultDestination = draft.DefaultCloneDestinationPath });
            _baseline = _baseline with { DefaultCloneDestinationPath = draft.DefaultCloneDestinationPath };
        }
        if (draft.FollowRenamesInFileHistory != _baseline.FollowRenamesInFileHistory || draft.FollowRenamesInFileHistoryExact != _baseline.FollowRenamesInFileHistoryExact)
        {
            Readable<FileHistoryPreferences>(_history.FilePath);
            FileHistoryPreferences current = _history.Load();
            _history.Save(current with
            {
                FollowRenames = Pick(_baseline.FollowRenamesInFileHistory, draft.FollowRenamesInFileHistory, current.FollowRenames),
                ExactRenames = Pick(_baseline.FollowRenamesInFileHistoryExact, draft.FollowRenamesInFileHistoryExact, current.ExactRenames)
            });
            _baseline = _baseline with { FollowRenamesInFileHistory = draft.FollowRenamesInFileHistory, FollowRenamesInFileHistoryExact = draft.FollowRenamesInFileHistoryExact };
        }
        if (draft.UseHistogramDiffAlgorithm != _baseline.UseHistogramDiffAlgorithm)
        {
            Readable<BlamePreferences>(_blame.FilePath);
            _blame.Save(_blame.Load() with { UseHistogramDiffAlgorithm = draft.UseHistogramDiffAlgorithm });
            _baseline = _baseline with { UseHistogramDiffAlgorithm = draft.UseHistogramDiffAlgorithm };
        }
        FileHistoryPreferences history = _history.Load();
        Initial = _baseline = Read(accepted, history);
        return new(accepted, Initial, history);
    }

    private GeneralSettingsValues Read(DesktopPreferences desktop, FileHistoryPreferences history)
    {
        PullPreferences pull = _pull.Load();
        return new()
        {
            CheckForUncommittedChangesInCheckoutBranch = desktop.CheckForUncommittedChangesInCheckoutBranch,
            StartWithRecentWorkingDir = desktop.StartWithRecentWorkingDir, MaxRevisionGraphCommits = desktop.MaxRevisionGraphCommits,
            RevisionGridQuickSearchTimeout = desktop.RevisionGridQuickSearchTimeout,
            ShowGitStatusInBrowseToolbar = desktop.ShowGitStatusInBrowseToolbar,
            ShowGitStatusForArtificialCommits = desktop.ShowGitStatusForArtificialCommits, ShowSubmoduleStatus = desktop.ShowSubmoduleStatus,
            ShowStashCount = desktop.ShowStashCount, ShowAheadBehindData = desktop.ShowAheadBehindData,
            UseBrowseForFileHistory = desktop.UseBrowseForFileHistory,
            DefaultPullAction = pull.DefaultAction, IncludeUntrackedFilesInAutoStash = pull.IncludeUntrackedFilesInAutoStash,
            CloseProcessDialog = _process.Load().CloseProcessDialog, UpdateSubmodulesOnCheckout = _submodules.Load().UpdateSubmodulesOnCheckout,
            DefaultCloneDestinationPath = _clone.Load().EffectiveDestination,
            FollowRenamesInFileHistory = history.FollowRenames, FollowRenamesInFileHistoryExact = history.ExactRenames,
            UseHistogramDiffAlgorithm = _blame.Load().UseHistogramDiffAlgorithm
        };
    }

    private static T Pick<T>(T baseline, T draft, T current) => EqualityComparer<T>.Default.Equals(baseline, draft) ? current : draft;

    private static void Readable<T>(string path)
    {
        if (Directory.Exists(path)) { throw new IOException("A settings profile is a directory: " + path); }
        if (!File.Exists(path)) { return; }
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object || JsonSerializer.Deserialize<T>(document.RootElement, Json) is null)
        { throw new JsonException("Invalid settings profile: " + path); }
    }
}
