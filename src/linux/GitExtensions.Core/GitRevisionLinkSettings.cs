using System.Xml;
using System.Xml.Serialization;
using GitCommands.ExternalLinks;

namespace GitExtensions.Linux;

/// <summary>Retain the raw inner XML and its diagnostic; expose fresh editable model copies to each caller.</summary>
public sealed class GitRevisionLinkDocument
{
    private readonly ExternalLinkDefinition[] _definitions;
    internal GitRevisionLinkDocument(string? rawXml, IEnumerable<ExternalLinkDefinition> definitions, GitRevisionLinkDiagnostic? diagnostic)
    {
        RawXml = rawXml;
        _definitions = definitions.Select(GitRevisionLinkCodec.Clone).ToArray();
        Diagnostic = diagnostic;
    }
    public string? RawXml { get; }
    public GitRevisionLinkDiagnostic? Diagnostic { get; }
    public IReadOnlyList<ExternalLinkDefinition> Definitions
        => Array.AsReadOnly(_definitions.Select(GitRevisionLinkCodec.Clone).ToArray());
}

/// <summary>The Windows nested XML value, independent of its outer guarded file store.</summary>
public static class GitRevisionLinkCodec
{
    public const string SettingName = "RevisionLinkDefs";
    private static readonly XmlSerializer Serializer = new(typeof(List<ExternalLinkDefinition>));

    public static GitRevisionLinkDocument Decode(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) { return new(xml, [], null); }
        try
        {
            using StringReader text = new(xml);
            // The inner value must not resolve entities or external documents. Unlike Windows, report the rejected value.
            using XmlReader reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            List<ExternalLinkDefinition> definitions = Serializer.Deserialize(reader) as List<ExternalLinkDefinition>
                ?? throw new XmlException("The revision links value is not a list of GitExtLinkDef elements.");
            return new(xml, definitions, null);
        }
        catch (Exception error) when (error is XmlException or InvalidOperationException)
        { return new(xml, [], new(GitRevisionLinkDiagnosticKind.InvalidXml, null, SettingName, error.Message)); }
    }

    /// <summary>Null is Windows' empty-list removal. Sorting/empty-row pruning never mutate the caller's draft.</summary>
    public static string? Encode(IReadOnlyList<ExternalLinkDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (definitions.Count == 0) { return null; }
        List<ExternalLinkDefinition> captured = definitions.Select(Clone).ToList();
        foreach (ExternalLinkDefinition definition in captured) { definition.RemoveEmptyFormats(); }
        XmlSerializerNamespaces namespaces = new();
        namespaces.Add(string.Empty, string.Empty);
        using StringWriter text = new();
        using XmlWriter writer = XmlWriter.Create(text, new XmlWriterSettings { Indent = true });
        Serializer.Serialize(writer, captured.OrderBy(definition => definition.Name).ToList(), namespaces);
        writer.Flush();
        return text.ToString();
    }

    public static ExternalLinkDefinition Clone(ExternalLinkDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ExternalLinkDefinition copy = new()
        {
            Name = definition.Name, Enabled = definition.Enabled, SearchPattern = definition.SearchPattern,
            NestedSearchPattern = definition.NestedSearchPattern, RemoteSearchPattern = definition.RemoteSearchPattern,
            UseRemotesPattern = definition.UseRemotesPattern, UseOnlyFirstRemote = definition.UseOnlyFirstRemote
        };
        copy.SearchInParts.UnionWith(definition.SearchInParts);
        copy.RemoteSearchInParts.UnionWith(definition.RemoteSearchInParts);
        foreach (ExternalLinkFormat format in definition.LinkFormats)
        { copy.LinkFormats.Add(new() { Caption = format.Caption, Format = format.Format }); }
        return copy;
    }
}

/// <summary>Additive link definitions from explicit file snapshots, never a scalar Effective lookup.</summary>
public sealed class GitRevisionLinkSettings
{
    private static readonly GitDistributedSettingsScope[] Order =
        [GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed, GitDistributedSettingsScope.Global];
    private readonly IReadOnlyDictionary<GitDistributedSettingsScope, GitRevisionLinkDocument> _documents;
    private GitRevisionLinkSettings(GitDistributedSettingsSnapshot snapshot)
    {
        Snapshot = snapshot;
        Dictionary<GitDistributedSettingsScope, GitRevisionLinkDocument> documents = [];
        foreach (GitDistributedSettingsScope scope in Order.Where(snapshot.IsAvailable))
        {
            GitXmlSettingsSnapshot file = snapshot.GetSnapshot(scope);
            documents.Add(scope, GitRevisionLinkCodec.Decode(file.Values.GetValueOrDefault(GitRevisionLinkCodec.SettingName)));
        }
        _documents = documents;
    }

    public GitDistributedSettingsSnapshot Snapshot { get; }
    public static GitRevisionLinkSettings From(GitDistributedSettingsSnapshot snapshot)
        => new(snapshot ?? throw new ArgumentNullException(nameof(snapshot)));

    public GitRevisionLinkDocument GetDocument(GitDistributedSettingsScope scope)
    {
        if (scope == GitDistributedSettingsScope.Effective)
        { throw new ArgumentException("Effective revision links aggregate independent file documents.", nameof(scope)); }
        _ = Snapshot.GetSnapshot(scope);
        return _documents[scope];
    }

    public IReadOnlyList<ExternalLinkDefinition> GetDefinitions(GitDistributedSettingsScope scope = GitDistributedSettingsScope.Effective)
        => scope == GitDistributedSettingsScope.Effective
            ? Array.AsReadOnly(Order.Where(_documents.ContainsKey).SelectMany(part => _documents[part].Definitions).ToArray())
            : GetDocument(scope).Definitions;

    public IReadOnlyList<GitRevisionLinkDiagnostic> GetDiagnostics(GitDistributedSettingsScope scope = GitDistributedSettingsScope.Effective)
    {
        if (scope != GitDistributedSettingsScope.Effective) { _ = GetDocument(scope); }
        IEnumerable<GitDistributedSettingsScope> scopes = scope == GitDistributedSettingsScope.Effective
            ? Order.Where(_documents.ContainsKey) : [scope];
        return Array.AsReadOnly(scopes.Where(part => _documents[part].Diagnostic is not null)
            .Select(part => _documents[part].Diagnostic! with { Scope = part }).ToArray());
    }

    /// <summary>Only explicit edit intentions create plans. An untouched document is never re-serialized or repaired.</summary>
    public IReadOnlyList<GitDistributedSettingsDestination> PlanChanges(GitDistributedSettingsScope scope,
        IReadOnlyList<ExternalLinkDefinition>? definitions)
    {
        if (scope == GitDistributedSettingsScope.Effective)
        { throw new InvalidOperationException("Effective revision links are read-only. Choose an explicit scope."); }
        string? xml = definitions is null ? null : GitRevisionLinkCodec.Encode(definitions);
        return Snapshot.PlanChanges(scope, new Dictionary<string, string?> { [GitRevisionLinkCodec.SettingName] = xml });
    }

    /// <summary>Acknowledge a real completed destination; this class never performs a write itself.</summary>
    public GitRevisionLinkSettings WithSaved(GitDistributedSettingsScope scope, GitXmlSettingsSnapshot saved)
        => From(Snapshot.WithSaved(scope, saved));
}
