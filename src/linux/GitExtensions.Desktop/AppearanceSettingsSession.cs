using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

internal sealed record AppearanceSettingsPublication(DesktopPreferences Desktop, HistoryViewPreferences History,
    AppearanceSettingsValues Values);

/// <summary>Merge Appearance intentions into the existing desktop and saved View authorities.</summary>
internal sealed class AppearanceSettingsSession
{
    private readonly PreferencesStore _desktop = new();
    private readonly HistoryViewPreferencesStore _history = new();
    private AppearanceSettingsValues _baseline;

    internal AppearanceSettingsValues Initial { get; private set; }

    internal AppearanceSettingsSession(DesktopPreferences initial, HistoryViewPreferences activeView)
    {
        ArgumentNullException.ThrowIfNull(initial); ArgumentNullException.ThrowIfNull(activeView);
        Initial = _baseline = Read(initial, activeView);
    }

    /// <summary>General's publisher performs the desktop write; unchanged Appearance fields retain peer edits.</summary>
    internal DesktopPreferences MergeDesktop(AppearanceSettingsValues draft, DesktopPreferences desired)
    {
        ArgumentNullException.ThrowIfNull(draft); ArgumentNullException.ThrowIfNull(desired);
        Validate(draft);
        DesktopPreferences latest = _desktop.Load();
        return desired with
        {
            Theme = Pick(_baseline.Theme, draft.Theme, latest.Theme),
            Language = Pick(_baseline.Language, draft.Language, latest.Language),
            ShowRepoCurrentBranch = Pick(_baseline.ShowRepoCurrentBranch, draft.ShowRepoCurrentBranch, latest.ShowRepoCurrentBranch),
            TruncatePathMethod = Pick(_baseline.TruncatePathMethod, draft.TruncatePathMethod, latest.TruncatePathMethod),
            ShowAuthorAvatarInCommitInfo = Pick(_baseline.ShowAuthorAvatarInCommitInfo, draft.ShowAuthorAvatarInCommitInfo, latest.ShowAuthorAvatarInCommitInfo),
            AvatarImageCacheDays = Pick(_baseline.AvatarImageCacheDays, draft.AvatarImageCacheDays, latest.AvatarImageCacheDays),
            AvatarProvider = Pick(_baseline.AvatarProvider, draft.AvatarProvider, latest.AvatarProvider),
            AvatarFallbackType = Pick(_baseline.AvatarFallbackType, draft.AvatarFallbackType, latest.AvatarFallbackType),
            CustomAvatarTemplate = Pick(_baseline.CustomAvatarTemplate, draft.CustomAvatarTemplate, latest.CustomAvatarTemplate)
        };
    }

    /// <summary>Acknowledge completed desktop intentions without rebasing the still-visible draft.</summary>
    internal void DesktopSaved(AppearanceSettingsValues draft)
    {
        // Called synchronously after General's real desktop write, including when a subsequent
        // workflow profile fails. This acknowledgement has no profile IO or background work.
        _baseline = _baseline with
        {
            Theme = draft.Theme, Language = draft.Language, ShowRepoCurrentBranch = draft.ShowRepoCurrentBranch,
            TruncatePathMethod = draft.TruncatePathMethod,
            ShowAuthorAvatarInCommitInfo = draft.ShowAuthorAvatarInCommitInfo, AvatarImageCacheDays = draft.AvatarImageCacheDays,
            AvatarProvider = draft.AvatarProvider, AvatarFallbackType = draft.AvatarFallbackType,
            CustomAvatarTemplate = draft.CustomAvatarTemplate
        };
    }

    /// <summary>Save only Appearance's edited View fields; an unchanged active View remains session-only.</summary>
    internal AppearanceSettingsPublication Save(AppearanceSettingsValues draft, DesktopPreferences acceptedDesktop,
        HistoryViewPreferences activeView, GitLogSort? sort = null)
    {
        ArgumentNullException.ThrowIfNull(draft); ArgumentNullException.ThrowIfNull(acceptedDesktop);
        ArgumentNullException.ThrowIfNull(activeView); Validate(draft);
        if (sort is { } selectedSort && !Enum.IsDefined(selectedSort)) { throw new ArgumentOutOfRangeException(nameof(sort)); }
        // General has already published the merged desktop values. If History fails below,
        // retry must not reinterpret those completed intentions as new edits over a peer's save.
        // Retain the draft identities here: the visible page has not yet adopted merged peer values.
        DesktopSaved(draft);
        bool relativeChanged = draft.RelativeDate != _baseline.RelativeDate;
        bool avatarChanged = draft.ShowAvatar != _baseline.ShowAvatar;
        if (relativeChanged || avatarChanged || sort is not null)
        {
            // The legacy store returns defaults on read failure. An explicit Settings write must
            // report a corrupt/unreadable profile rather than overwrite it with those defaults.
            ReadableHistory();
            HistoryViewPreferences latest = _history.Load();
            HistoryViewPreferences saved = latest with
            {
                RelativeDate = relativeChanged ? draft.RelativeDate : latest.RelativeDate,
                ShowAvatar = avatarChanged ? draft.ShowAvatar : latest.ShowAvatar,
                Sort = sort ?? latest.Sort
            };
            if (saved != latest) { _history.Save(saved); }
        }
        HistoryViewPreferences active = activeView with
        {
            RelativeDate = relativeChanged ? draft.RelativeDate : activeView.RelativeDate,
            ShowAvatar = avatarChanged ? draft.ShowAvatar : activeView.ShowAvatar,
            Sort = sort ?? activeView.Sort
        };
        Initial = _baseline = Read(acceptedDesktop, active);
        return new(acceptedDesktop, active, Initial);
    }

    private void ReadableHistory()
    {
        if (Directory.Exists(_history.FilePath)) { throw new IOException("A settings profile is a directory: " + _history.FilePath); }
        if (!File.Exists(_history.FilePath)) { return; }
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(_history.FilePath));
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || JsonSerializer.Deserialize<HistoryViewPreferences>(document.RootElement) is null)
        { throw new JsonException("Invalid history view settings profile: " + _history.FilePath); }
    }

    private static void Validate(AppearanceSettingsValues draft)
    {
        if (!Enum.IsDefined(draft.TruncatePathMethod)) { throw new ArgumentException("Select a valid path truncation method.", nameof(draft)); }
        if (draft.Theme is not ("Light" or "Dark" or "System")) { throw new ArgumentException("Select a valid theme.", nameof(draft)); }
        if (draft.AvatarImageCacheDays is < 0 or > 400 || !Enum.IsDefined(draft.AvatarProvider) || !Enum.IsDefined(draft.AvatarFallbackType))
        { throw new ArgumentException("Select valid author image settings.", nameof(draft)); }
    }
    private static T Pick<T>(T baseline, T draft, T current)
        => EqualityComparer<T>.Default.Equals(baseline, draft) ? current : draft;
    private static AppearanceSettingsValues Read(DesktopPreferences desktop, HistoryViewPreferences active) => new()
    {
        Theme = desktop.Theme, Language = desktop.Language, ShowRepoCurrentBranch = desktop.ShowRepoCurrentBranch,
        TruncatePathMethod = desktop.TruncatePathMethod,
        RelativeDate = active.RelativeDate, ShowAvatar = active.ShowAvatar,
        ShowAuthorAvatarInCommitInfo = desktop.ShowAuthorAvatarInCommitInfo, AvatarImageCacheDays = desktop.AvatarImageCacheDays,
        AvatarProvider = desktop.AvatarProvider, AvatarFallbackType = desktop.AvatarFallbackType,
        CustomAvatarTemplate = desktop.CustomAvatarTemplate
    };
}
