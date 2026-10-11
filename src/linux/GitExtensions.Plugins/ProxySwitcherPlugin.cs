namespace GitExtensions.Linux.Plugins;

public sealed record ProxySwitcherOptions(string Host, string Port = "8080", string Username = "", string Password = "")
{
    public string BuildProxy()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        if (new[] { Host, Port, Username, Password }.Any(value => value.Contains('\0')))
        { throw new ArgumentException("Proxy settings cannot contain NUL."); }
        if (Port.Length > 0 && (!int.TryParse(Port, out int port) || port is < 1 or > 65535))
        { throw new ArgumentException("Proxy port must be between 1 and 65535, or empty."); }
        string credentials = Username.Length == 0 ? "" : Username + (Password.Length == 0 ? "" : ":" + Password) + "@";
        return credentials + Host + (Port.Length == 0 ? "" : ":" + Port);
    }
}

public sealed record ProxySwitcherState(string EffectiveProxy, string GlobalProxy, bool ApplyGlobally);

public sealed class ProxySwitcherPlugin : INativePlugin
{
    public PluginDescriptor Descriptor { get; } = new(new Guid("C2A1C7A4-D519-4BD1-859B-6CE7DB9325FB"),
        "Proxy Switcher", "Inspect, set or unset Git's HTTP proxy in the repository or global configuration.", "IconProxySwitcher",
        [new("operation", "Operation", PluginParameterKind.Choice, "inspect", Choices: ["inspect", "set", "unset"]),
         new("host", "HTTP proxy", PluginParameterKind.Text), new("port", "HTTP proxy port", PluginParameterKind.Text, "8080"),
         new("username", "Username", PluginParameterKind.Text), new("password", "Password", PluginParameterKind.Text),
         new("global", "Apply globally", PluginParameterKind.Boolean, "false")]);

    public async Task<PluginResult> ExecuteAsync(PluginContext context, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        string operation = PluginValues.Text(Descriptor, values, "operation");
        GitConfigScope scope = PluginValues.Boolean(Descriptor, values, "global") ? GitConfigScope.Global : GitConfigScope.Local;
        if (operation == "set")
        {
            ProxySwitcherOptions options = new(PluginValues.Text(Descriptor, values, "host"), PluginValues.Text(Descriptor, values, "port"),
                PluginValues.Text(Descriptor, values, "username"), PluginValues.Text(Descriptor, values, "password"));
            string proxy = options.BuildProxy();
            try { await context.Repository.SetConfigAsync("http.proxy", proxy, scope, cancellationToken).ConfigureAwait(false); }
            catch (GitCommandException exception)
            {
                string message = exception.Result.StandardError.Replace(proxy, HidePassword(proxy), StringComparison.Ordinal);
                if (options.Password.Length > 0) { message = message.Replace(options.Password, "****", StringComparison.Ordinal); }
                throw new InvalidOperationException("Cannot update the HTTP proxy. " + message.Trim());
            }
        }
        else if (operation == "unset")
        { await context.Repository.UnsetConfigAsync("http.proxy", scope, cancellationToken).ConfigureAwait(false); }
        ProxySwitcherState state = await GetStateAsync(context.Repository, cancellationToken).ConfigureAwait(false);
        return new("HTTP proxy configuration", new(["Scope", "HTTP proxy"],
            [new("effective", ["Effective repository setting", state.EffectiveProxy]), new("global", ["Global setting", state.GlobalProxy])]),
            RepositoryChanged: operation != "inspect");
    }

    public static async Task<ProxySwitcherState> GetStateAsync(GitRepository repository, CancellationToken cancellationToken = default)
    {
        string effective = (await repository.GetConfigAsync("^http\\.proxy$", cancellationToken: cancellationToken).ConfigureAwait(false)).LastOrDefault().Value ?? "";
        string global = (await repository.GetConfigAsync("^http\\.proxy$", GitConfigScope.Global, cancellationToken).ConfigureAwait(false)).LastOrDefault().Value ?? "";
        return new(HidePassword(effective), HidePassword(global), string.Equals(effective, global, StringComparison.Ordinal));
    }

    public static string HidePassword(string proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        int userStart = proxy.IndexOf("://", StringComparison.Ordinal) is int scheme && scheme >= 0 ? scheme + 3 : 0;
        int at = proxy.LastIndexOf('@');
        int colon = proxy.IndexOf(':', userStart);
        return at > colon && colon >= userStart ? proxy[..(colon + 1)] + "****" + proxy[at..] : proxy;
    }
}
