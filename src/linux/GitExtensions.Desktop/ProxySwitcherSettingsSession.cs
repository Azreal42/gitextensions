using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop;

internal static class ProxySwitcherSettingsCatalog
{
    internal const string PageId = "PluginsProxy";
    internal static PluginScopedSettingsDefinition Definition { get; } = new(
        new ProxySwitcherPlugin().Descriptor.Id, "Proxy Switcher", "ProxySwitcherPlugin", "Proxy",
        [new("Username", "Username", "Username", "username", PluginScopedSettingKind.Text, ""),
         new("Password", "Password", "Password", "password", PluginScopedSettingKind.Password, ""),
         new("HttpProxy", "HTTP proxy", "HTTP proxy", "host", PluginScopedSettingKind.Text, ""),
         new("HttpProxyPort", "HTTP proxy port", "HTTP proxy port", "port", PluginScopedSettingKind.Text, "8080")]);
}

/// <summary>The original four string defaults; only explicit scoped XML intentions can persist a password.</summary>
internal sealed class ProxySwitcherSettingsSession : PluginScopedSettingsSession
{
    internal ProxySwitcherSettingsSession(GitRepository? repository, IReadOnlyDictionary<string, string?>? legacyValues = null,
        DistributedSettingsWorkspace? workspace = null)
        : base(ProxySwitcherSettingsCatalog.Definition, repository, LegacyDefaults(legacyValues), workspace) { }

    internal IReadOnlyDictionary<string, string?> RuntimeValues()
    {
        IReadOnlyDictionary<string, string?> accepted = EffectiveValues();
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["host"] = accepted["host"], ["port"] = accepted["port"],
            ["username"] = accepted["username"], ["password"] = accepted["password"]
        };
    }

    // Preserve nonsecret old defaults, never adopt an old JSON password or inline host credentials.
    private static IReadOnlyDictionary<string, string?> LegacyDefaults(IReadOnlyDictionary<string, string?>? values)
    {
        Dictionary<string, string?> result = new(StringComparer.Ordinal);
        if (values is null) { return result; }
        foreach (string key in new[] { "host", "port", "username" })
        {
            if (!values.TryGetValue(key, out string? value)) { continue; }
            result[key] = key == "host" && value is not null ? WithoutCredentials(value) : value;
        }
        return result;
    }

    private static string WithoutCredentials(string host)
    {
        int at = host.LastIndexOf('@');
        if (at < 0) { return host; }
        int scheme = host.IndexOf("://", StringComparison.Ordinal);
        return (scheme >= 0 ? host[..(scheme + 3)] : "") + host[(at + 1)..];
    }
}
