using System.Globalization;

namespace GitExtensions.Linux.Plugins;

public enum PluginParameterKind { Text, Multiline, Boolean, Integer, Number, Choice }

public sealed record PluginParameter(string Key, string Label, PluginParameterKind Kind, string DefaultValue = "",
    bool Required = false, string? Hint = null, IReadOnlyList<string>? Choices = null);

public sealed record PluginDescriptor(Guid Id, string Name, string Description, string IconResource,
    IReadOnlyList<PluginParameter> Parameters, bool RequiresConfirmation = false, string? ConfirmationText = null,
    string? ConfirmationParameter = null, string? ConfirmationValue = null)
{
    public bool NeedsConfirmation(IReadOnlyDictionary<string, string?> values)
        => RequiresConfirmation && (ConfirmationParameter is null
            || string.Equals(PluginValues.Text(this, values, ConfirmationParameter), ConfirmationValue, StringComparison.Ordinal));
}

public sealed record PluginContext(GitRepository Repository, bool Confirmed = false, DateTimeOffset? Now = null);
public sealed record PluginTable(IReadOnlyList<string> Columns, IReadOnlyList<PluginRow> Rows);
public sealed record PluginRow(string Key, IReadOnlyList<string> Cells);
public sealed record PluginLink(string Label, Uri Uri);
public sealed record PluginResult(string Text, PluginTable? Table = null, IReadOnlyList<PluginLink>? Links = null,
    bool RepositoryChanged = false, bool Success = true)
{
    public bool RepositoryMayHaveChanges { get; init; }
}

public interface INativePlugin
{
    PluginDescriptor Descriptor { get; }
    Task<PluginResult> ExecuteAsync(PluginContext context, IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken = default);
}

public static class NativePluginCatalog
{
    public static IReadOnlyList<INativePlugin> BuiltIn { get; } =
        [new CreateLocalBranchesPlugin(), new DeleteUnusedBranchesPlugin(), new FindLargeFilesPlugin(), new ReleaseNotesGeneratorPlugin(),
         new StatisticsPlugin(), new GitImpactPlugin(), new ProxySwitcherPlugin(), new GourcePlugin()];
}

internal static class PluginValues
{
    internal static string Text(PluginDescriptor descriptor, IReadOnlyDictionary<string, string?> values, string key)
    {
        PluginParameter parameter = descriptor.Parameters.Single(item => item.Key == key);
        string value = values.TryGetValue(key, out string? entered) && entered is not null ? entered : parameter.DefaultValue;
        if (value.Contains('\0') || (parameter.Required && string.IsNullOrWhiteSpace(value)))
        {
            throw new ArgumentException($"Provide a valid value for {parameter.Label}.", key);
        }
        if (parameter.Choices is not null && !parameter.Choices.Contains(value, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown choice for {parameter.Label}.", key);
        }
        return value;
    }

    internal static bool Boolean(PluginDescriptor descriptor, IReadOnlyDictionary<string, string?> values, string key)
        => bool.TryParse(Text(descriptor, values, key), out bool value) ? value : throw new ArgumentException($"{key} must be true or false.", key);

    internal static int Integer(PluginDescriptor descriptor, IReadOnlyDictionary<string, string?> values, string key)
        => int.TryParse(Text(descriptor, values, key), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value <= 36500
            ? value : throw new ArgumentException($"{key} must be an integer between 0 and 36500.", key);

    internal static IReadOnlyList<string> Lines(string value)
        => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())
            .Where(line => line.Length > 0).Distinct(StringComparer.Ordinal).ToArray();

    internal static void RequireConfirmation(PluginContext context)
    {
        if (!context.Confirmed)
        {
            throw new InvalidOperationException("Review the preview and confirm this action before executing it.");
        }
    }
}
