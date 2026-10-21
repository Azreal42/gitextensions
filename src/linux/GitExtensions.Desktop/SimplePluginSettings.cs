using System.Globalization;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop;

internal sealed record SimplePluginSettingsEntry(PluginScopedSettingsDefinition Definition, string Icon)
{
    internal string PageId => "Plugin" + Definition.ControlPrefix;
}

/// <summary>Actual Windows GetSettings fields only; invocation parameters are not defaults.</summary>
internal static class SimplePluginSettingsCatalog
{
    internal static SimplePluginSettingsEntry AutoCompile { get; } = new(new(
        AutoCompileSubmodulesService.Descriptor.Id, "Auto compile submodules", "AutoCompileSubModulesPlugin", "AutoCompileSubmodules",
        [new("_msBuildEnabled", "Enabled", "Enabled", "enabled", PluginScopedSettingKind.Boolean, "false"),
         new("_msBuildPath", "Path to msbuild.exe", "Path to msbuild.exe", "executable", PluginScopedSettingKind.Text, ""),
         new("_msBuildArguments", "msbuild.exe arguments", "msbuild.exe arguments", "arguments", PluginScopedSettingKind.Text, "/p:Configuration=Debug")]),
        "IconAutoCompileSubmodules");
    internal static SimplePluginSettingsEntry FindLargeFiles { get; } = new(new(
        new FindLargeFilesPlugin().Descriptor.Id, "Find large files", "FindLargeFilesPlugin", "FindLargeFiles",
        [new("_sizeLargeFile", "Find large files bigger than (Mb)", "Find large files bigger than (Mb)", "minimumMegabytes", PluginScopedSettingKind.Single, "1")]),
        "IconFindLargeFiles");
    internal static SimplePluginSettingsEntry Statistics { get; } = new(new(
        new StatisticsPlugin().Descriptor.Id, "Statistics", "GitStatisticsPlugin", "Statistics",
        [new("_codeFiles", "Code files", "Code files", "codeFiles", PluginScopedSettingKind.Text, StatisticsPlugin.DefaultCodeFiles),
         new("_ignoreDirectories", "Directories to ignore (EndsWith)", "Directories to ignore (EndsWith)", "directoriesToIgnore", PluginScopedSettingKind.Text, StatisticsPlugin.DefaultDirectoriesToIgnore),
         new("_ignoreSubmodules", "Ignore submodules", "Ignore submodules", "ignoreSubmodules", PluginScopedSettingKind.Boolean, "true")]),
        "IconGitStatistics");
    internal static SimplePluginSettingsEntry Gource { get; } = new(new(
        new GourcePlugin().Descriptor.Id, "Gource", "GourcePlugin", "Gource",
        [new("_gourcePath", "Path to Gource", "Path to Gource", "executable", PluginScopedSettingKind.Text, ""),
         new("_gourceArguments", "Arguments", "Arguments", "arguments", PluginScopedSettingKind.Text, "--hide filenames --user-image-dir \"$(AVATARS)\"")]),
        "IconGource");
    internal static IReadOnlyList<SimplePluginSettingsEntry> All { get; } = Array.AsReadOnly<SimplePluginSettingsEntry>(
        [AutoCompile, FindLargeFiles, Statistics, Gource]);
    internal static SimplePluginSettingsEntry Find(Guid id) => All.Single(entry => entry.Definition.Id == id);
}

internal sealed class SimplePluginSettingsSession : PluginScopedSettingsSession
{
    internal const string UnsupportedThresholdMessage = "The configured file size cannot be used by the large-file scanner. Choose a finite nonnegative supported MiB value in plugin settings.";
    internal SimplePluginSettingsEntry Entry { get; }
    internal SimplePluginSettingsSession(SimplePluginSettingsEntry entry, GitRepository? repository,
        IReadOnlyDictionary<string, string?>? legacyValues = null, DistributedSettingsWorkspace? workspace = null)
        : base(entry.Definition, repository, legacyValues, workspace) => Entry = entry;

    internal AutoCompileSubmodulesOptions AutoCompileOptions() => AutoCompileSubmodulesOptions.FromValues(EffectiveValues());

    internal IReadOnlyDictionary<string, string?> InvocationValues()
    {
        Dictionary<string, string?> values = new(EffectiveValues(), StringComparer.Ordinal);
        if (ReferenceEquals(Entry, SimplePluginSettingsCatalog.FindLargeFiles))
        {
            string text = values["minimumMegabytes"]!; // Typed Single runtime text is invariant, never a localized raw field.
            float single = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (!float.IsFinite(single) || single < 0 || !decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number)
                || number < 0 || number > long.MaxValue / 1048576m || single != 0 && number == 0)
            { throw new ArgumentException(UnsupportedThresholdMessage, "minimumMegabytes"); }
            values["minimumMegabytes"] = number.ToString(CultureInfo.InvariantCulture);
            values["revision"] = "HEAD";
        }
        else if (ReferenceEquals(Entry, SimplePluginSettingsCatalog.Gource))
        { values["workingDirectory"] = null; }
        return values;
    }

    internal void StageAcceptedGource(GourceOptions initial, GourceOptions accepted)
    {
        if (!ReferenceEquals(Entry, SimplePluginSettingsCatalog.Gource)) { throw new InvalidOperationException("This is not the Gource settings session."); }
        if (initial.Executable != accepted.Executable) { StageEffectiveValue(Definition.Find("_gourcePath"), accepted.Executable); }
        if (initial.Arguments != accepted.Arguments) { StageEffectiveValue(Definition.Find("_gourceArguments"), accepted.Arguments); }
    }
}
