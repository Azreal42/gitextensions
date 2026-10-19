using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GitCommands.ExternalLinks;
using GitExtensions.Linux;

// Prepared only: Program registration and all execution belong to the integrating owner.
internal static class RevisionLinksScenarios
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";
    private const string ResourcePrefix = "RevisionLinksWindows.";
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("revision links encode the actual Windows golden XML and retain its manual Add defaults", WindowsGoldenAsync),
        ("revision links decode all three actual Windows XML fixtures without flattening their lists", WindowsFixturesAsync),
        ("revision links add explicit scopes even for identical names and independently filter Enabled", AdditiveAsync),
        ("revision links retain opaque raw documents and mutable callers cannot edit captured snapshots", CaptureAsync),
        ("revision links preserve Windows origin upstream all-remotes and empty-remote parser oracles", WindowsRemotesAsync),
        ("revision links consume real configured raw last pushurl without a fetch fallback or network", RawPushAsync),
        ("revision links order local tags remote message then formats and deduplicate only final URIs", OrderAsync),
        ("revision links combine numeric named optional repeated and nested groups using Windows numbering", CapturesAsync),
        ("revision links substitute exact hash only in URI and preserve Unicode braces captions and raw targets", FormattingAsync),
        ("revision links retain each Windows invalid-regex fallback and expose separate diagnostics", InvalidPatternsAsync),
        ("revision links keep invalid-format error links in the Windows output instead of hiding them", InvalidFormatsAsync),
        ("revision links reject malformed or DTD inner XML visibly without replacing its raw value", InvalidXmlAsync),
        ("revision links use real exact attached refs including tags rather than containing branches", NativeReferencesAsync),
        ("revision links consume the native complete message including subject but excluding notes", NativeMessageAsync),
        ("revision links plan guarded explicit saves and removal while retaining Dictionary Detailed index and HEAD", StorageAsync),
        ("revision links retain completed scope effects and refuse stale peer destinations before retry", StaleAsync),
        ("revision links aggregate real linked worktree bare and no-repository endpoints", EndpointsAsync),
        ("revision links observe cancellation and an explicitly bounded regex never publishes partial output", CancellationAsync),
        ("revision link templates preserve Windows provider patterns placeholders and upstream fork origin preference", TemplatesAsync)
    ];

    private static Task TemplatesAsync()
    {
        GitRevisionLinkRemote[] remotes = [new("origin", "https://github.com/origin/repo.git", null),
            new("fork", "git@github.com:fork/repo.git", null), new("upstream", "https://github.com/upstream/repo.git", null)];
        IReadOnlyList<ExternalLinkDefinition> github = GitRevisionLinkTemplates.Create(GitRevisionLinkTemplate.GitHub, remotes);
        Check(github.Count == 3 && github[0].LinkFormats[0].Format == "https://github.com/upstream/repo/commit/%COMMIT_HASH%", "Templates select upstream before fork before origin.");
        Check(GitRevisionLinkTemplates.Create(GitRevisionLinkTemplate.GitHub, remotes[..2])[0].LinkFormats[0].Format!.Contains("/fork/repo/", StringComparison.Ordinal), "Fork precedes origin independent of enumeration order.");
        Check(github[1].NestedSearchPattern == @"\d+" && github[1].SearchInParts.Contains(ExternalLinkDefinition.RevisionPart.LocalBranches)
            && github[2].SearchInParts.Contains(ExternalLinkDefinition.RevisionPart.RemoteBranches), "Use the actual extractor's patterns and search parts.");
        IReadOnlyList<ExternalLinkDefinition> azure = GitRevisionLinkTemplates.Create(GitRevisionLinkTemplate.AzureDevOps,
            [new("origin", "git@ssh.dev.azure.com:v3/account/project/repo", null)]);
        Check(azure.Count == 2 && azure[0].LinkFormats[0].Format == "https://dev.azure.com/account/_git/repo/commit/%COMMIT_HASH%"
            && azure[1].LinkFormats[0].Format == "https://dev.azure.com/account/repo/_workitems/edit/{0}", "Preserve Windows extractor's account/repository URLs, including its project omission.");
        Check(GitRevisionLinkTemplates.Create(GitRevisionLinkTemplate.GitHub, [new("origin", null, "https://github.com/push/only")])[0].LinkFormats[0].Format!
            .Contains("ORGANIZATION_NAME/REPO_NAME", StringComparison.Ordinal), "Template detection considers fetch URL only and retains the Windows placeholders.");
        return Task.CompletedTask;
    }

    private static Task WindowsGoldenAsync()
    {
        string expected = Inner(Resource("golden.xml"));
        ExternalLinkDefinition definition = GitRevisionLinkTemplates.CreateManual();
        string encoded = GitRevisionLinkCodec.Encode([definition])!;
        Check(encoded.StartsWith("<?xml version=\"1.0\" encoding=\"utf-16\"?>", StringComparison.Ordinal)
            && XNode.DeepEquals(XDocument.Parse(expected), XDocument.Parse(encoded)), "The original Windows golden is the serialization oracle, including XML type names and omitted null fields.");
        GitRevisionLinkDocument read = GitRevisionLinkCodec.Decode(expected);
        ExternalLinkDefinition restored = read.Definitions.Single();
        Check(read.Diagnostic is null && restored.Name == "<new>" && restored.Enabled && restored.UseOnlyFirstRemote
            && restored.SearchInParts.SetEquals([ExternalLinkDefinition.RevisionPart.Message])
            && restored.RemoteSearchInParts.SetEquals([ExternalLinkDefinition.RemotePart.URL]) && restored.LinkFormats.Count == 0,
            "Manual Add defaults come from the actual saved Windows fixture, not defaults invented by the codec.");
        Check(!new ExternalLinkDefinition().Enabled && !new ExternalLinkDefinition().UseOnlyFirstRemote
            && GitRevisionLinkCodec.Encode([]) is null, "A naked XML model is not a manual Add action; empty lists remove the key.");
        definition.LinkFormats.Add(new() { Caption = "  ", Format = "\t" });
        definition.LinkFormats.Add(new() { Caption = "label", Format = "" });
        string pruned = GitRevisionLinkCodec.Encode([definition])!;
        Check(definition.LinkFormats.Count == 2 && GitRevisionLinkCodec.Decode(pruned).Definitions.Single().LinkFormats.Count == 1,
            "Only rows with BOTH fields blank are removed, without mutating a caller's editable list.");
        return Task.CompletedTask;
    }

    private static Task WindowsFixturesAsync()
    {
        foreach ((string file, int count) in new[] { ("local.xml", 1), ("distributed.xml", 3), ("global.xml", 1) })
        {
            string raw = Inner(Resource(file)); GitRevisionLinkDocument document = GitRevisionLinkCodec.Decode(raw);
            Check(document.Diagnostic is null && document.Definitions.Count == count && document.RawXml == raw,
                "Decode must retain the independent actual Windows " + file + " list and raw value.");
            string encoded = GitRevisionLinkCodec.Encode(document.Definitions)!;
            Check(GitRevisionLinkCodec.Decode(encoded).Definitions.Select(definition => definition.Name)
                .SequenceEqual(document.Definitions.OrderBy(definition => definition.Name).Select(definition => definition.Name)),
                "Windows sorts category names for serialization without changing the number of categories.");
        }
        return Task.CompletedTask;
    }

    private static async Task AdditiveAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        GitRepositoryInfo info = await f.Repository.GetInfoAsync();
        GitDistributedSettingsSnapshot snapshot = await GitDistributedSettingsSnapshot.LoadAsync(Global(f), info);
        foreach ((GitDistributedSettingsScope scope, string target) in new[]
        {
            (GitDistributedSettingsScope.Local, "local"), (GitDistributedSettingsScope.Distributed, "distributed"), (GitDistributedSettingsScope.Global, "global")
        })
        {
            ExternalLinkDefinition definition = Definition("same", "#(\\d+)", target + ":{0}");
            if (scope == GitDistributedSettingsScope.Local) { definition.Enabled = false; }
            snapshot = snapshot.WithSaved(scope, await GitXmlSettingsStore.SaveChangesAsync(snapshot.GetSnapshot(scope),
                new Dictionary<string, string?> { [GitRevisionLinkCodec.SettingName] = GitRevisionLinkCodec.Encode([definition]) }));
        }
        GitRevisionLinkSettings settings = GitRevisionLinkSettings.From(snapshot);
        Check(settings.GetDefinitions().Count == 3 && settings.GetDefinitions().All(definition => definition.Name == "same"),
            "Collection resolution adds all three explicit documents, even with identical names.");
        Check(Uris(Parse(settings.GetDefinitions(), "#7")).SequenceEqual(["distributed:7", "global:7"]),
            "A disabled Local category does not suppress enabled same-name definitions in lower scopes.");
        Expect<InvalidOperationException>(() => settings.PlanChanges(GitDistributedSettingsScope.Effective, []));
        Expect<ArgumentException>(() => settings.GetDocument(GitDistributedSettingsScope.Effective));
    }

    private static async Task CaptureAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        string raw = GitRevisionLinkCodec.Encode([Definition("captured", "\\d+", "literal:{0}")])!;
        GitDistributedSettingsSnapshot snapshot = await GitDistributedSettingsSnapshot.LoadAsync(Global(f));
        snapshot = snapshot.WithSaved(GitDistributedSettingsScope.Global, await GitXmlSettingsStore.SaveChangesAsync(snapshot.Global,
            new Dictionary<string, string?> { [GitRevisionLinkCodec.SettingName] = raw }));
        GitRevisionLinkSettings settings = GitRevisionLinkSettings.From(snapshot);
        IReadOnlyList<ExternalLinkDefinition> first = settings.GetDefinitions(); first[0].Name = "caller mutation"; first[0].LinkFormats.Clear();
        Check(settings.GetDefinitions().Single().Name == "captured" && settings.GetDefinitions().Single().LinkFormats.Count == 1
            && settings.GetDocument(GitDistributedSettingsScope.Global).RawXml == raw, "Editable models returned to one caller cannot mutate a captured document or another draft.");
        GitRevisionLinkReference[] refs = [new(false, "42")]; GitRevisionLinkRemote[] remotes = [new("origin", "fetch", null)];
        GitRevisionLinkContext context = new(Hash, null, refs, remotes);
        refs[0] = new(false, "99"); remotes[0] = new("other", "later", "later");
        Check(context.AttachedRefs.Single().LocalName == "42" && context.RawRemotes.Single().Name == "origin", "Revision/remote arrays are captured once, not reused as live producer collections.");
        Expect<NotSupportedException>(() => ((IList<GitRevisionLinkReference>)context.AttachedRefs)[0] = new(false, "injected"));
    }

    private static Task WindowsRemotesAsync()
    {
        GitRevisionLinkRemote[] remotes =
        [
            new("origin", "https://github.com/jbialobr/gitextensions.git", null),
            new("upstream", "https://github.com/gitextensions/gitextensions.git", null),
            new("RussKie", "https://github.com/russkie/gitextensions.git", null)
        ];
        ExternalLinkDefinition definition = Definition("GitHub - issues", @"(\s*(,|and)?\s*#\d+)+", "https://github.com/{0}/issues/{1}");
        definition.LinkFormats[0].Caption = "Issue {1}";
        definition.NestedSearchPattern = @"(\d+)+";
        definition.RemoteSearchPattern = @"github.com[:/](.+)\.git";
        definition.UseRemotesPattern = "upstream|origin"; definition.UseOnlyFirstRemote = true;
        definition.RemoteSearchInParts.Add(ExternalLinkDefinition.RemotePart.URL);
        const string message = "Merge pull request #3657 from RussKie/tweak_FormRemotes_tooltips";
        Check(Uris(Parse([definition], message, remotes)).SequenceEqual(["https://github.com/gitextensions/gitextensions/issues/3657"]),
            "Windows ExternalLinkRevisionParserTests.ParseGitHubIssueForUpstreamLink oracle.");
        definition.UseRemotesPattern = "origin|upstream";
        Check(Uris(Parse([definition], message, remotes)).SequenceEqual(["https://github.com/jbialobr/gitextensions/issues/3657"]), "Windows origin-first oracle.");
        definition.UseRemotesPattern = "upstream|origin"; definition.UseOnlyFirstRemote = false;
        Check(Uris(Parse([definition], message, remotes)).SequenceEqual(["https://github.com/gitextensions/gitextensions/issues/3657", "https://github.com/jbialobr/gitextensions/issues/3657"]), "Windows two matching remotes oracle.");
        definition.UseRemotesPattern = ""; definition.UseOnlyFirstRemote = true;
        Check(Uris(Parse([definition], message, remotes)).SequenceEqual(["https://github.com/jbialobr/gitextensions/issues/3657", "https://github.com/gitextensions/gitextensions/issues/3657", "https://github.com/russkie/gitextensions/issues/3657"]),
            "Windows blank-name-pattern early return keeps all input remotes, even if OnlyFirst is true.");
        definition.RemoteSearchPattern = ""; definition.LinkFormats[0].Caption = "Issue {0}"; definition.LinkFormats[0].Format = "https://example.invalid/issues/{0}";
        Check(Uris(Parse([definition], message, [])).SequenceEqual(["https://example.invalid/issues/3657"]), "Windows empty-remote-part oracle needs no configured remotes.");
        return Task.CompletedTask;
    }

    private static async Task RawPushAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        await f.Repository.SetConfigAsync("remote.origin.url", "https://example.invalid/repos/fetch");
        ExternalLinkDefinition definition = Definition("raw push", "#(\\d+)", "{0}:{1}");
        definition.RemoteSearchPattern = @"repos/(.+)";
        definition.RemoteSearchInParts.Add(ExternalLinkDefinition.RemotePart.PushURL);
        Check(Uris(Parse([definition], "#8", await f.Repository.GetRevisionLinkRemotesAsync())).Length == 0
            && (await f.Repository.GetRemotesAsync()).Single().PushUrls.Count > 0, "The pure context uses absence of a raw pushurl, never GetRemotesAsync's synthesized fetch fallback.");
        await f.Repository.RunAsync(["config", "--local", "--add", "remote.origin.pushurl", "https://example.invalid/repos/first"]);
        await f.Repository.RunAsync(["config", "--local", "--add", "remote.origin.pushurl", "https://example.invalid/repos/last-été-雪"]);
        byte[] config = await File.ReadAllBytesAsync(f.File(".git/config"));
        Check(Uris(Parse([definition], "#8", await f.Repository.GetRevisionLinkRemotesAsync())).SequenceEqual(["last-été-雪:8"]), "Windows ConfigFileRemote supplies the last configured pushurl. Selecting that native raw field belongs to the producer.");
        byte[] configAfterExtraction = await File.ReadAllBytesAsync(f.File(".git/config"));
        Check(config.SequenceEqual(configAfterExtraction), "Extraction performs no Git/config/network action.");
    }

    private static Task OrderAsync()
    {
        ExternalLinkDefinition first = Definition("first", "#(\\d+)", "target:{0}");
        first.LinkFormats[0].Caption = "first caption {0}";
        first.LinkFormats.Add(new() { Caption = "second caption {0}", Format = "target:{0}" });
        first.SearchInParts.UnionWith([ExternalLinkDefinition.RevisionPart.LocalBranches, ExternalLinkDefinition.RevisionPart.RemoteBranches]);
        GitRevisionLinkReference[] refs = [new(true, "#3"), new(false, "#1"), new(false, "tag/#2"), new(true, "#4")];
        GitRevisionLinkParseResult result = new GitRevisionLinkParser().Parse(new(Hash, "#9", refs, []), [first, first]);
        Check(Uris(result).SequenceEqual(new[] { "target:1", "target:1", "target:2", "target:2", "target:3", "target:3", "target:4", "target:4", "target:9", "target:9" }
            .Concat(new[] { "target:1", "target:1", "target:2", "target:2", "target:3", "target:3", "target:4", "target:4", "target:9", "target:9" })),
            "Definitions, local/nonremote refs, remote refs, message, matches and formats retain Windows order.");
        Check(result.DistinctLinks.Select(link => link.Uri).SequenceEqual(["target:1", "target:2", "target:3", "target:4", "target:9"])
            && result.DistinctLinks.All(link => link.Caption!.StartsWith("first caption", StringComparison.Ordinal)), "Only the final URI projection removes duplicates and retains the first caption.");
        ExternalLinkDefinition ordinal = Definition("ordinal", "#(\\d+)", "Target:{0}");
        Check(Parse([first, ordinal], "#9").DistinctLinks.Count == 2, "URI identity is ordinal; schemes/hosts are not normalized by the parser.");
        return Task.CompletedTask;
    }

    private static Task CapturesAsync()
    {
        ExternalLinkDefinition definition = Definition("groups", @"(?<ticket>\d+)-(X)?", "{0}|{1}|{2}|{3}");
        definition.RemoteSearchPattern = @"(org)/(repo)"; definition.RemoteSearchInParts.Add(ExternalLinkDefinition.RemotePart.URL);
        Check(Uris(Parse([definition], "42-X", [new("origin", "org/repo", null)])).SequenceEqual(["org|repo|X|42"]), "Remote captures precede .NET's numeric numbering of unnamed then named revision groups.");
        Check(Uris(Parse([definition], "42-", [new("origin", "org/repo", null)])).SequenceEqual(["org|repo||42"]), "Unmatched optional groups retain their empty parameter position.");
        ExternalLinkDefinition whole = Definition("whole", @"\d+", "{0}");
        Check(Uris(Parse([whole], "123")).SequenceEqual(["123"]), "A regex without captures uses its complete match as parameter zero.");
        whole.SearchPattern = @"(\d)+";
        Check(Uris(Parse([whole], "123")).SequenceEqual(["3"]), "A repeated group uses Group.Value, not all captures or the full match.");
        whole.SearchPattern = @"(Ticket#(\d+))"; whole.NestedSearchPattern = @"#(\d+)";
        Check(Uris(Parse([whole], "Ticket#42")).SequenceEqual(["42"]), "Nested matches replace primary captures and run only inside match.Value.");
        return Task.CompletedTask;
    }

    private static Task FormattingAsync()
    {
        ExternalLinkDefinition definition = Definition("unicode", @"雪/(\d+)", "https://example.invalid/%COMMIT_HASH%/{0}/{{literal}}/été 雪");
        definition.LinkFormats[0].Caption = "%COMMIT_HASH%:{0}";
        ExternalLink link = Parse([definition], "雪/42").Links.Single();
        Check(link.Caption == "%COMMIT_HASH%:42" && link.Uri == "https://example.invalid/" + Hash + "/42/{literal}/été 雪",
            "Only the exact URI token expands. Composite-format braces and literal Unicode are preserved without URI encoding.");
        definition.LinkFormats[0].Format = "gitext://gotocommit/%COMMIT_HASH%";
        Check(Parse([definition], "雪/42").Links.Single().Uri == "gitext://gotocommit/" + Hash, "Core does not launch or restrict an absolute/internal/custom scheme.");
        ExternalLinkDefinition sensitive = Definition("case", "ABC", "{0}");
        Check(Parse([sensitive], "abc").Links.Count == 0, "Compiled is the only implicit regex option.");
        sensitive.SearchPattern = "(?i)ABC";
        Check(Parse([sensitive], "abc").Links.Single().Uri == "abc", "Inline regex options remain authoritative.");
        sensitive.SearchPattern = "A.B";
        Check(Parse([sensitive], "A\nB").Links.Count == 0, "Singleline and Multiline are not silently enabled.");
        return Task.CompletedTask;
    }

    private static Task InvalidPatternsAsync()
    {
        ExternalLinkDefinition definition = Definition("invalid", "[", "{0}");
        GitRevisionLinkParseResult result = Parse([definition], "42");
        Check(result.Links.Count == 0 && result.Diagnostics.Single().Property == nameof(definition.SearchPattern), "Invalid primary regex yields no links and a visible diagnostic.");
        definition.SearchPattern = @"\d+"; definition.NestedSearchPattern = "[";
        result = Parse([definition], "42");
        Check(result.Links.Count == 0 && result.Diagnostics.Single().Property == nameof(definition.NestedSearchPattern), "Invalid nonempty nested pattern removes primary matches instead of reverting to them.");
        definition.NestedSearchPattern = null; definition.RemoteSearchPattern = "[";
        result = Parse([definition], "42");
        Check(Uris(result).SequenceEqual(["42"]) && result.Diagnostics.Single().Property == nameof(definition.RemoteSearchPattern), "Invalid remote pattern gives Windows' null remote match, not no links.");
        definition.RemoteSearchPattern = @"repo/(\w+)"; definition.UseRemotesPattern = "["; definition.UseOnlyFirstRemote = true;
        definition.RemoteSearchInParts.Add(ExternalLinkDefinition.RemotePart.URL); definition.LinkFormats[0].Format = "{0}:{1}";
        result = Parse([definition], "42", [new("origin", "repo/a", null), new("upstream", "repo/b", null)]);
        Check(Uris(result).SequenceEqual(["a:42", "b:42"]) && result.Diagnostics.Single().Property == nameof(definition.UseRemotesPattern), "Invalid name filter admits all remotes and bypasses OnlyFirst, as Windows does.");
        definition.UseRemotesPattern = "or.*|upstream";
        Check(Uris(Parse([definition], "42", [new("origin", "repo/a", null), new("upstream", "repo/b", null)])).SequenceEqual(["a:42"]), "A regex-matching name with IndexOf=-1 sorts before a name literally present in the preference string.");
        definition.UseRemotesPattern = null; definition.RemoteSearchInParts.Add(ExternalLinkDefinition.RemotePart.PushURL);
        Check(Parse([definition], "42", [new("origin", "repo/a", "repo/a")]).Links.Count == 1, "Equal selected fetch/push strings are deduplicated before matching.");
        definition.SearchPattern = "";
        Check(Parse([definition], "42").Links.Count == 0, "Empty primary is not an unconditional link, despite the historical model comment.");
        definition.SearchPattern = " "; definition.RemoteSearchPattern = null; definition.LinkFormats[0].Format = "[{0}]";
        Check(Parse([definition], " ").Links.Single().Uri == "[ ]", "Whitespace primary is a valid literal regex, unlike whitespace remote/name filters.");
        return Task.CompletedTask;
    }

    private static Task InvalidFormatsAsync()
    {
        ExternalLinkFormat format = new() { Caption = "Issue {0}", Format = "https://example.invalid/{2}" };
        ExternalLink failure = format.Apply(null, Regex.Match("42", @"\d+"), Hash);
        Check(!format.IsValid && failure.Caption == "Issue 42" && failure.Uri.Contains("https://example.invalid/{2} System.Object[]", StringComparison.Ordinal), "URI format failure preserves its successfully rendered caption and the Windows error-link shape.");
        format.Format = "https://example.invalid/{0}"; ExternalLink valid = format.Apply(null, Regex.Match("42", @"\d+"), Hash);
        Check(format.IsValid && valid.Uri == "https://example.invalid/42", "The mutable format's validity updates after an actual successful application.");
        ExternalLinkDefinition definition = Definition("broken format", @"\d+", "{2}");
        definition.LinkFormats[0].Caption = "{2}";
        GitRevisionLinkParseResult result = Parse([definition], "42");
        Check(result.Links.Count == 1 && result.Links[0].Caption is null && result.Diagnostics.Single().Kind == GitRevisionLinkDiagnosticKind.InvalidFormat,
            "Format errors remain in Windows output and also report a separate diagnostic; Core never tries opening them.");
        return Task.CompletedTask;
    }

    private static async Task InvalidXmlAsync()
    {
        foreach (string? raw in new string?[] { null, "", " \n\t" })
        { GitRevisionLinkDocument empty = GitRevisionLinkCodec.Decode(raw); Check(empty.Definitions.Count == 0 && empty.Diagnostic is null && empty.RawXml == raw, "Absent and blank inner values retain Windows' empty collection without a fabricated diagnostic."); }
        foreach (string raw in new[] { "<broken>", "<wrong/>", "<!DOCTYPE x [<!ENTITY y 'hidden'>]><ArrayOfGitExtLinkDef/>" })
        {
            GitRevisionLinkDocument invalid = GitRevisionLinkCodec.Decode(raw);
            Check(invalid.RawXml == raw && invalid.Definitions.Count == 0 && invalid.Diagnostic?.Kind == GitRevisionLinkDiagnosticKind.InvalidXml,
                "Malformed inner XML and rejected DTDs retain their exact raw value; no empty replacement is generated.");
        }
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepositoryInfo info = await f.Repository.GetInfoAsync();
        GitDistributedSettingsSnapshot snapshot = await GitDistributedSettingsSnapshot.LoadAsync(Global(f), info);
        snapshot = snapshot.WithSaved(GitDistributedSettingsScope.Local, await GitXmlSettingsStore.SaveChangesAsync(snapshot.Local!,
            new Dictionary<string, string?> { [GitRevisionLinkCodec.SettingName] = "<broken>", ["dictionary"] = "literal-invalid/identity" }));
        snapshot = snapshot.WithSaved(GitDistributedSettingsScope.Global, await GitXmlSettingsStore.SaveChangesAsync(snapshot.Global,
            new Dictionary<string, string?> { [GitRevisionLinkCodec.SettingName] = GitRevisionLinkCodec.Encode([Definition("valid global", "42", "global")]) }));
        byte[] local = await File.ReadAllBytesAsync(snapshot.Local!.Path); GitRevisionLinkSettings settings = GitRevisionLinkSettings.From(snapshot);
        byte[] localAfterExtraction = await File.ReadAllBytesAsync(snapshot.Local!.Path);
        Check(settings.GetDefinitions().Single().Name == "valid global" && settings.GetDiagnostics().Single().Scope == GitDistributedSettingsScope.Local
            && local.SequenceEqual(localAfterExtraction), "An invalid Local document does not suppress valid lower collections or rewrite any outer key.");
    }

    private static async Task NativeReferencesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string initial = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.Repository.RunAsync(["branch", "fix/42-été", initial]);
        await f.Repository.RunAsync(["tag", "issue/43", initial]);
        await f.Repository.SetConfigAsync("remote.team/repo.url", "https://example.invalid/private");
        await f.Repository.RunAsync(["update-ref", "refs/remotes/team/repo/PR-44", initial]);
        f.Write("seed.txt", "advance\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Later #99");
        await f.Repository.RunAsync(["branch", "containing/88"]);
        GitReference[] exact = (await f.Repository.GetReferencesAsync()).Where(reference => reference.RevisionHash == initial).ToArray();
        ExternalLinkDefinition definition = Definition("attached", @"(?:fix/|issue/|PR-)(\d+)", "id:{0}");
        definition.SearchInParts.Clear(); definition.SearchInParts.UnionWith([ExternalLinkDefinition.RevisionPart.LocalBranches, ExternalLinkDefinition.RevisionPart.RemoteBranches]);
        GitRevisionLinkParseResult result = new GitRevisionLinkParser().Parse(await f.Repository.GetRevisionLinkContextAsync(initial), [definition]);
        Check(result.DistinctLinks.Select(link => link.Uri).ToHashSet(StringComparer.Ordinal).SetEquals(["id:42", "id:43", "id:44"])
            && exact.All(reference => reference.ShortName != "containing/88"), "Native exact refs include the attached tag and remove the full slash-containing remote name; a containing branch at a later commit is not a candidate.");
    }

    private static async Task NativeMessageAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        f.Write("seed.txt", "message\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Subject #101\n\nBody #102 été 雪");
        string head = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.Repository.RunAsync(["notes", "add", "-m", "Notes #103", head]);
        GitCommitDetails details = await f.Repository.GetCommitDetailsAsync(head);
        Check(details.Notes?.Contains("#103", StringComparison.Ordinal) == true
            && Uris(new GitRevisionLinkParser().Parse(await f.Repository.GetRevisionLinkContextAsync(head), [Definition("native message", "#(\\d+)", "id:{0}")]))
                .SequenceEqual(["id:101", "id:102"]), "Only the original complete message feeds revision-link extraction; notes remain a separate input.");
    }

    private static async Task StorageAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepositoryInfo info = await f.Repository.GetInfoAsync();
        byte[] index = await File.ReadAllBytesAsync(f.File(".git/index")); string head = await f.Repository.ResolveRevisionAsync("HEAD");
        GitDistributedSettingsSnapshot snapshot = await GitDistributedSettingsSnapshot.LoadAsync(Global(f), info);
        foreach (GitDistributedSettingsScope scope in new[] { GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed, GitDistributedSettingsScope.Global })
        {
            snapshot = snapshot.WithSaved(scope, await GitXmlSettingsStore.SaveChangesAsync(snapshot.GetSnapshot(scope),
                new Dictionary<string, string?> { ["dictionary"] = "../opaque λ", ["Detailed.MergeLogMessagesCount"] = "-901", ["future"] = "keep <&>" }));
        }
        GitRevisionLinkSettings settings = GitRevisionLinkSettings.From(snapshot);
        foreach (GitDistributedSettingsScope scope in new[] { GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed, GitDistributedSettingsScope.Global })
        { settings = await Apply(settings, scope, [Definition(scope.ToString(), "42", scope.ToString())]); }
        byte[] global = await File.ReadAllBytesAsync(settings.Snapshot.Global.Path);
        byte[] globalAfterPlanning = await File.ReadAllBytesAsync(settings.Snapshot.Global.Path);
        Check(settings.PlanChanges(GitDistributedSettingsScope.Global, settings.GetDefinitions(GitDistributedSettingsScope.Global)).Count == 0
            && global.SequenceEqual(globalAfterPlanning), "Exact serialized no-op intents produce no writer destination.");
        settings = await Apply(settings, GitDistributedSettingsScope.Distributed, null);
        Check(!settings.Snapshot.Distributed!.Values.ContainsKey(GitRevisionLinkCodec.SettingName) && settings.GetDefinitions().Count == 2,
            "Null removes only that explicit collection and immediately reveals the other additive scopes.");
        byte[] indexAfterSave = await File.ReadAllBytesAsync(f.File(".git/index"));
        string headAfterSave = await f.Repository.ResolveRevisionAsync("HEAD");
        Check(new[] { settings.Snapshot.Local!, settings.Snapshot.Distributed!, settings.Snapshot.Global }.All(file => file.Values["dictionary"] == "../opaque λ"
                && file.Values["Detailed.MergeLogMessagesCount"] == "-901" && file.Values["future"] == "keep <&>")
            && index.SequenceEqual(indexAfterSave) && head == headAfterSave,
            "The existing guarded writer preserves unrelated business keys and does not stage the distributed file or mutate HEAD/index.");
    }

    private static async Task StaleAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        GitRevisionLinkSettings settings = GitRevisionLinkSettings.From(await GitDistributedSettingsSnapshot.LoadAsync(Global(f), await f.Repository.GetInfoAsync()));
        GitDistributedSettingsDestination local = settings.PlanChanges(GitDistributedSettingsScope.Local, [Definition("local", "42", "local")]).Single();
        GitDistributedSettingsDestination global = settings.PlanChanges(GitDistributedSettingsScope.Global, [Definition("global", "42", "global")]).Single();
        GitXmlSettingsSnapshot localSaved = await GitXmlSettingsStore.SaveChangesAsync(local.Snapshot, local.Intentions);
        settings = settings.WithSaved(local.Scope, localSaved);
        GitXmlSettingsSnapshot peer = await GitXmlSettingsStore.SaveChangesAsync(global.Snapshot, new Dictionary<string, string?> { ["peer"] = "retained" });
        await ExpectAsync<IOException>(() => GitXmlSettingsStore.SaveChangesAsync(global.Snapshot, global.Intentions));
        Check(settings.GetDefinitions(GitDistributedSettingsScope.Local).Single().Name == "local" && peer.Values["peer"] == "retained", "An earlier accepted Local write and another actor's Global effect survive the stale refusal.");
        Expect<ArgumentException>(() => settings.WithSaved(GitDistributedSettingsScope.Global, localSaved));
        settings = settings.WithSaved(GitDistributedSettingsScope.Global, await GitXmlSettingsStore.LoadAsync(global.Snapshot.Path));
        settings = await Apply(settings, GitDistributedSettingsScope.Global, [Definition("global", "42", "global")]);
        Check(settings.GetDefinitions().Select(definition => definition.Name).SequenceEqual(["local", "global"])
            && settings.Snapshot.Global.Values["peer"] == "retained", "Refreshing only the refused file permits a bounded retry without replaying or flattening Local.");
    }

    private static async Task EndpointsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepositoryInfo main = await f.Repository.GetInfoAsync();
        GitRevisionLinkSettings a = GitRevisionLinkSettings.From(await GitDistributedSettingsSnapshot.LoadAsync(Global(f), main));
        a = await Apply(a, GitDistributedSettingsScope.Local, [Definition("shared local", "42", "local")]);
        a = await Apply(a, GitDistributedSettingsScope.Distributed, [Definition("main distributed", "42", "main")]);
        string linkedPath = Path.Combine(f.Root, "linked été 雪");
        await f.Repository.RunAsync(["worktree", "add", "--detach", "--", linkedPath, "HEAD"]);
        GitRepository linked = new(linkedPath);
        GitRevisionLinkSettings b = GitRevisionLinkSettings.From(await GitDistributedSettingsSnapshot.LoadAsync(Global(f), await linked.GetInfoAsync()));
        Check(b.GetDefinitions().Select(definition => definition.Name).SequenceEqual(["shared local"]), "A linked worktree reads CommonDirectory Local but does not borrow another working tree's Distributed list.");
        b = await Apply(b, GitDistributedSettingsScope.Distributed, [Definition("linked distributed", "42", "linked")]);
        Check(a.GetDefinitions().Select(definition => definition.Name).SequenceEqual(["shared local", "main distributed"])
            && b.GetDefinitions().Select(definition => definition.Name).SequenceEqual(["shared local", "linked distributed"]), "Each captured linked/main distributed endpoint retains its own additive list.");
        GitRepository bare = await GitRepository.InitAsync(Path.Combine(f.Root, "bare.git"), bare: true);
        GitRevisionLinkSettings bareSettings = GitRevisionLinkSettings.From(await GitDistributedSettingsSnapshot.LoadAsync(Global(f), await bare.GetInfoAsync()));
        Expect<InvalidOperationException>(() => bareSettings.PlanChanges(GitDistributedSettingsScope.Distributed, []));
        GitRevisionLinkSettings outside = GitRevisionLinkSettings.From(await GitDistributedSettingsSnapshot.LoadAsync(Global(f)));
        Expect<InvalidOperationException>(() => outside.PlanChanges(GitDistributedSettingsScope.Local, []));
        Check(outside.GetDefinitions().Count == 0 && outside.GetDocument(GitDistributedSettingsScope.Global).RawXml is null, "A no-repository profile reads Global alone without creating any settings file.");
    }

    private static Task CancellationAsync()
    {
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        ExternalLinkDefinition definition = Definition("cancel", @"\d+", "{0}");
        Expect<OperationCanceledException>(() => new GitRevisionLinkParser().Parse(new(Hash, "42", [], []), [definition], cancelled.Token));
        Expect<ArgumentOutOfRangeException>(() => new GitRevisionLinkParser(TimeSpan.Zero));
        ExternalLinkDefinition beforeTimeout = Definition("before timeout", "^", "first");
        ExternalLinkDefinition pathological = Definition("bounded", "(a+)+$", "{0}");
        Expect<RegexMatchTimeoutException>(() => new GitRevisionLinkParser(TimeSpan.FromMilliseconds(1))
            .Parse(new(Hash, new string('a', 70_000) + "!", [], []), [beforeTimeout, pathological]));
        Check(!definition.LinkFormats.Single().IsValid && !beforeTimeout.LinkFormats.Single().IsValid,
            "Parser cloning leaves the caller's format state unchanged, even after an accepted earlier link and a later timeout.");
        return Task.CompletedTask;
    }

    private static string Global(RepositoryFixture f) => Path.Combine(f.Root, "revision-links-profile", GitDistributedSettingsSnapshot.FileName);
    private static ExternalLinkDefinition Definition(string name, string pattern, string target)
        => new() { Name = name, Enabled = true, SearchPattern = pattern, SearchInParts = { ExternalLinkDefinition.RevisionPart.Message },
            LinkFormats = { new ExternalLinkFormat { Caption = "{0}", Format = target } } };
    private static GitRevisionLinkParseResult Parse(IEnumerable<ExternalLinkDefinition> definitions, string? message, IReadOnlyList<GitRevisionLinkRemote>? remotes = null)
        => new GitRevisionLinkParser().Parse(new(Hash, message, [], remotes ?? []), definitions);
    private static string[] Uris(GitRevisionLinkParseResult result) => result.Links.Select(link => link.Uri).ToArray();
    private static string Resource(string suffix)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + suffix)
            ?? throw new InvalidOperationException("Missing original Windows resource " + suffix);
        using StreamReader reader = new(stream, Encoding.UTF8); return reader.ReadToEnd();
    }
    private static string Inner(string outer) => XDocument.Parse(outer).Root!.Elements("item")
        .Single(item => item.Element("key")!.Element("string")!.Value == GitRevisionLinkCodec.SettingName).Element("value")!.Element("string")!.Value;
    private static async Task<GitRevisionLinkSettings> Apply(GitRevisionLinkSettings settings, GitDistributedSettingsScope scope, IReadOnlyList<ExternalLinkDefinition>? definitions)
    {
        foreach (GitDistributedSettingsDestination destination in settings.PlanChanges(scope, definitions))
        { settings = settings.WithSaved(destination.Scope, await GitXmlSettingsStore.SaveChangesAsync(destination.Snapshot, destination.Intentions)); }
        return settings;
    }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
