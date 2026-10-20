using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitExtensions.Linux.Desktop;

public sealed record DesktopPreferences
{
    public List<string> RecentRepositories { get; init; } = [];
    public string Theme { get; init; } = "Light";
    public string? Language { get; init; }
    public string Dictionary { get; init; } = GitDictionaryName.Default;
    public bool ProvideAutocompletion { get; init; } = true;
    public bool MarkIllFormedLinesInCommitMsg { get; init; } = true;
    public bool ShowRepoCurrentBranch { get; init; } = true;
    public bool MergeGraphLanesHavingCommonParent { get; init; } = true;
    public bool RenderGraphWithDiagonals { get; init; } = true;
    public bool StraightenGraphDiagonals { get; init; } = true;
    public GitCommands.TruncatePathMethod TruncatePathMethod { get; init; } = GitCommands.TruncatePathMethod.None;
    public bool ShowAuthorAvatarInCommitInfo { get; init; } = true;
    public int AvatarImageCacheDays { get; init; } = 13;
    public GitCommands.AvatarProvider AvatarProvider { get; init; } = GitCommands.AvatarProvider.None;
    public GitCommands.AvatarFallbackType AvatarFallbackType { get; init; } = GitCommands.AvatarFallbackType.AuthorInitials;
    public string CustomAvatarTemplate { get; init; } = "";
    public int HistoryLimit { get; init; } = 300;
    // HistoryLimit is the incremental batch size retained for existing profiles.
    // The Windows General policy is an independent total cap; zero is unlimited.
    public int MaxRevisionGraphCommits { get; init; } = 100_000;
    public int RevisionGridQuickSearchTimeout { get; init; } = 4000;
    public bool CheckForUncommittedChangesInCheckoutBranch { get; init; } = true;
    public bool StartWithRecentWorkingDir { get; init; }
    public bool ShowGitStatusInBrowseToolbar { get; init; } = true;
    public bool ShowGitStatusForArtificialCommits { get; init; } = true;
    public bool ShowSubmoduleStatus { get; init; }
    public bool ShowStashCount { get; init; }
    public bool ShowAheadBehindData { get; init; } = true;
    public bool AutoNormaliseBranchName { get; init; } = true;
    public string AutoNormaliseSymbol { get; init; } = "_";
    public bool AlwaysShowAdvancedOptions { get; init; }
    public bool CheckForUpdates { get; init; } = true;
    public bool CheckForReleaseCandidates { get; init; }
    public bool UseBrowseForFileHistory { get; init; } = true;
    public GitReferenceSorting ReferenceSorting { get; init; } = new();
    public string PrioritizedBranchNames { get; init; } = GitReferencePriorities.DefaultBranchPatterns;
    public string PrioritizedRemoteNames { get; init; } = GitReferencePriorities.DefaultRemotePatterns;
    public Dictionary<string, Dictionary<string, string?>> PluginSettings { get; init; } = [];
}

public sealed class PreferencesStore
{
    public string FilePath { get; }

    public PreferencesStore(string? directory = null)
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string root = directory ?? (Path.IsPathFullyQualified(xdg ?? "") ? xdg! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));
        FilePath = Path.Combine(root, "gitextensions", "preferences.json");
    }

    public DesktopPreferences Load()
    {
        if (!File.Exists(FilePath))
        {
            return new DesktopPreferences();
        }

        DesktopPreferences value = JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(FilePath)) ?? new();
        GitReferenceSorting sorting = value.ReferenceSorting ?? new();
        return value with
        {
            // Missing/null uses the Windows default; retain other unknown/invalid identities for diagnostics.
            Dictionary = GitDictionaryName.NormalizeDisabledIdentity(value.Dictionary),
            TruncatePathMethod = Enum.IsDefined(value.TruncatePathMethod) ? value.TruncatePathMethod : GitCommands.TruncatePathMethod.None,
            AvatarImageCacheDays = Math.Clamp(value.AvatarImageCacheDays, 0, 400),
            AvatarProvider = Enum.IsDefined(value.AvatarProvider) ? value.AvatarProvider : GitCommands.AvatarProvider.None,
            AvatarFallbackType = Enum.IsDefined(value.AvatarFallbackType) ? value.AvatarFallbackType : GitCommands.AvatarFallbackType.AuthorInitials,
            CustomAvatarTemplate = value.CustomAvatarTemplate ?? "",
            MaxRevisionGraphCommits = Math.Clamp(value.MaxRevisionGraphCommits, 0, 1_000_000),
            RevisionGridQuickSearchTimeout = Math.Clamp(value.RevisionGridQuickSearchTimeout, 100, 1_000_000),
            ShowSubmoduleStatus = (value.ShowGitStatusInBrowseToolbar || value.ShowGitStatusForArtificialCommits) && value.ShowSubmoduleStatus,
            ReferenceSorting = new(Enum.IsDefined(sorting.By) ? sorting.By : GitRefsSortBy.GitDefault,
                Enum.IsDefined(sorting.Order) ? sorting.Order : GitRefsSortOrder.Descending),
            PrioritizedBranchNames = value.PrioritizedBranchNames ?? GitReferencePriorities.DefaultBranchPatterns,
            PrioritizedRemoteNames = value.PrioritizedRemoteNames ?? GitReferencePriorities.DefaultRemotePatterns
        };
    }

    public void Save(DesktopPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences = preferences with { Dictionary = GitDictionaryName.NormalizeDisabledIdentity(preferences.Dictionary) };
        // A directory cannot be a profile; preserve the pre-read writer's IO failure contract.
        if (Directory.Exists(FilePath)) { throw new IOException("The preferences path is a directory: " + FilePath); }
        JsonObject document = JsonSerializer.SerializeToNode(preferences)!.AsObject();
        try
        {
            if (JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject existing)
            {
                // Retain only foreign top-level properties; typed fields keep their historical Save contract.
                foreach ((string name, JsonNode? value) in existing)
                { if (!document.ContainsKey(name)) { document[name] = value?.DeepClone(); } }
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (JsonException) { /* Explicit typed Save historically repairs invalid/non-object profiles. */ }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
