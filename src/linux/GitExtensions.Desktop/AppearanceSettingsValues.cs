namespace GitExtensions.Linux.Desktop;

/// <summary>Appearance draft values; the host maps these to the existing desktop and history preferences.</summary>
internal sealed record AppearanceSettingsValues
{
    public bool RelativeDate { get; init; } = true;
    public bool ShowRepoCurrentBranch { get; init; } = true;
    public GitCommands.TruncatePathMethod TruncatePathMethod { get; init; } = GitCommands.TruncatePathMethod.None;
    public bool ShowAvatar { get; init; } = true;
    public bool ShowAuthorAvatarInCommitInfo { get; init; } = true;
    public int AvatarImageCacheDays { get; init; } = 13;
    public GitCommands.AvatarProvider AvatarProvider { get; init; } = GitCommands.AvatarProvider.None;
    public GitCommands.AvatarFallbackType AvatarFallbackType { get; init; } = GitCommands.AvatarFallbackType.AuthorInitials;
    public string CustomAvatarTemplate { get; init; } = "";
    public string Theme { get; init; } = "Light";
    public string? Language { get; init; }
}
