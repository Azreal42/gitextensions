namespace GitExtensions.Linux.Desktop;

/// <summary>Appearance draft values; the host maps these to the existing desktop and history preferences.</summary>
internal sealed record AppearanceSettingsValues
{
    public bool RelativeDate { get; init; } = true;
    public bool ShowRepoCurrentBranch { get; init; } = true;
    public bool ShowAvatar { get; init; } = true;
    public string Theme { get; init; } = "Light";
    public string? Language { get; init; }
}
