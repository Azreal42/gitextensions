using System.Globalization;
using System.Xml.Linq;
using GitExtensions.Linux.Scripts;

internal static class ScriptScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("script events model defaults and disabled examples match Windows source", ModelAsync),
        ("script Windows XML preserves every property order null and empty values", XmlAsync),
        ("script explicit old-format import keeps flags and repairs duplicate identifiers", LegacyAsync),
        ("script corrupt XML invalid enums and external entities remain unmodified errors", CorruptionAsync),
        ("script simple and double braces preserve Windows quote oracles", QuotesAsync),
        ("script all selected and current variables remain distinct materialized facts", VariablesAsync),
        ("script author and committer dates use Windows local DateTime cultures rather than raw offsets", DatesAsync),
        ("script absent author committer and body differ from explicitly empty metadata", NullMetadataAsync),
        ("script multiple ref and remote choices preserve candidates and source expansion order", ChoicesAsync),
        ("script closed ref selector produces empty value unlike cancelled input", ClosedChoiceAsync),
        ("script missing grid selected or current revision abort distinctly", MissingRevisionAsync),
        ("script custom provider multiple values and unknown placeholders retain source semantics", ProviderAsync),
        ("script provider declared order and global precedence match the original union", ProviderOrderAsync),
        ("script folder paths lines and columns use explicit Windows or POSIX escaping", FilesAsync),
        ("script user input labels and repeated placeholders match Windows prompt oracles", InputsAsync),
        ("script labeled input preserves exact whitespace and consumes each recognized token once", ExactInputTokensAsync),
        ("script quoted user input retains quotes trailing backslash and ordinary substitutions", LiteralInputValuesAsync),
        ("script prompt defaults resolve HEAD and failures before input", DefaultsAsync),
        ("script UserFiles and anonymous input distinguish empty accepted from cancelled", InputCancellationAsync),
        ("script confirmation refusal and unavailable input never produce a command", ConfirmationAsync),
        ("script command aliases targets and PowerShell remain pure preparation", TargetsAsync),
        ("script null empty whitespace arguments and empty command retain their differences", EmptyAsync),
        ("script introduced options are expanded once in original order without evaluation", ExpansionOrderAsync),
        ("script contexts and provider arrays are snapshotted before any awaited prompt", SnapshotAsync),
        ("script cancellation before and during prompts stops all later requests", CancellationAsync),
    ];

    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static ScriptDefinition Script(string? arguments, string command = "git") => new() { Name = "&Private script", Command = command, Arguments = arguments };
    private static ScriptContext Facts() => new()
    {
        HasRevisionGrid = true,
        SelectedRevision = new("selected", "Selected Café", "Selected Café\nbody", "Selected Ω", "Selected Committer", new(2020, 1, 2, 3, 4, 5), new(2020, 1, 3, 4, 5, 6)),
        SelectedRevisions = [new("older"), new("selected")],
        SelectedReferences = [new("v1", ScriptReferenceKind.Tag), new("topic", ScriptReferenceKind.LocalBranch), new("origin/topic", ScriptReferenceKind.RemoteBranch, "origin")],
        CurrentRevision = new("current", "Current Café", "Current Café\r\nbody", "Current Ω", "Current Committer", new(2021, 2, 3, 4, 5, 6), new(2021, 2, 4, 5, 6, 7)),
        CurrentReferences = [new("v2", ScriptReferenceKind.Tag), new("main", ScriptReferenceKind.LocalBranch), new("upstream/main", ScriptReferenceKind.RemoteBranch, "upstream")],
        CurrentBranch = "main", CurrentRemote = "ignored",
        BranchRemotes = new Dictionary<string, string> { ["main"] = "upstream" },
        RemoteUrls = new Dictionary<string, string> { ["origin"] = "git@host.invalid:org/topic.git", ["upstream"] = "https://host.invalid/org/current.git" },
        WorkingDir = "/literal repo/", RepoName = "Friendly description",
        Culture = CultureInfo.GetCultureInfo("en-US"), ArgumentPlatform = ScriptArgumentPlatform.Windows
    };

    private static Task ModelAsync()
    {
        string[] names = ["None", "BeforeCommit", "AfterCommit", "BeforePull", "AfterPull", "BeforePush", "AfterPush", "ShowInUserMenuBar",
            "BeforeCheckout", "AfterCheckout", "BeforeMerge", "AfterMerge", "BeforeFetch", "AfterFetch", "ShowInFileList"];
        Check(Enum.GetNames<ScriptEvent>().SequenceEqual(names) && Enum.GetValues<ScriptEvent>().Select(value => (int)value).SequenceEqual(Enumerable.Range(0, names.Length)), "Exact original event names and serialized ordinals, with no Clone/Rebase hooks.");
        string[] properties = ["Enabled", "Name", "Command", "Arguments", "AddToRevisionGridContextMenu", "OnEvent", "AskConfirmation", "RunInBackground", "IsPowerShell", "HotkeyCommandIdentifier", "Icon", "IconFilePath"];
        Check(typeof(ScriptDefinition).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(properties.Order(StringComparer.Ordinal)), "All original serialized properties are represented.");
        ScriptDefinition empty = new();
        Check(empty.Enabled && !empty.AskConfirmation && !empty.RunInBackground && !empty.IsPowerShell && !empty.AddToRevisionGridContextMenu && empty.OnEvent == ScriptEvent.None, "New model defaults differ from disabled samples exactly as in ScriptInfo.");
        IReadOnlyList<ScriptDefinition> defaults = ScriptConfiguration.Read(null).Scripts;
        Check(defaults.Count == 8 && defaults.All(script => !script.Enabled) && defaults.Select(script => script.HotkeyCommandIdentifier).SequenceEqual(new[] { 9000, 9001, 9005, 9002, 9003, 9004, 9006, 9007 }), "Samples preserve source order/IDs; none are enabled.");
        Check(defaults[0].OnEvent == ScriptEvent.AfterCommit && defaults[0].AskConfirmation && defaults[1].OnEvent == ScriptEvent.AfterPull && defaults[1].AskConfirmation, "Original after samples are configured but inactive.");
        Check(defaults[2].Arguments!.Contains("{{SelectedRelativePaths}}:{LineNumber}:{ColumnNumber}", StringComparison.Ordinal) && defaults[2].RunInBackground && defaults[2].Icon == "EditFile", "VS Code retains position placeholders.");
        Check(defaults[4].AddToRevisionGridContextMenu && defaults[5].AddToRevisionGridContextMenu && defaults[6].Command == "bash.exe" && defaults[3].Command == @"c:\windows\system32\calc.exe", "No import rewrites Windows commands into running Linux commands.");
        Check((new ScriptDefinition { Name = "&One && two" }).GetDisplayName() == "One & two" && ScriptConfiguration.NextIdentifier([]) == 9001, "Mnemonic display and new ID floor match source UI.");
        return Task.CompletedTask;
    }

    private static Task XmlAsync()
    {
        ScriptDefinition first = new() { Enabled = false, Name = " &Café \nΩ", Command = "/literal exe", Arguments = "{{WorkingDir}}\r\n{UserInput:msg=x}",
            OnEvent = ScriptEvent.BeforeFetch, AskConfirmation = true, RunInBackground = true, IsPowerShell = true,
            AddToRevisionGridContextMenu = true, Icon = "Console", IconFilePath = " leading.ico ", HotkeyCommandIdentifier = 9012 };
        ScriptDefinition second = new() { Name = "empty", Command = "", Arguments = null, Icon = "", IconFilePath = null, HotkeyCommandIdentifier = 9011 };
        string xml = ScriptConfiguration.WriteXml([first, second]);
        XDocument parsed = XDocument.Parse(xml);
        Check(parsed.Root!.Name.LocalName == "ArrayOfScriptInfo" && parsed.Root!.Elements().All(item => item.Name.LocalName == "ScriptInfo"), "Windows XML element names stay compatible.");
        ScriptConfigurationResult read = ScriptConfiguration.Read(xml);
        Check(read.Success && read.Source == ScriptConfigurationSource.Xml && read.OriginalText == xml && read.Scripts.SequenceEqual(new[] { first, second }), "Every field, newline, null/empty value and row order round-trips.");
        ScriptConfigurationResult savedEmpty = ScriptConfiguration.Read("<ArrayOfScriptInfo />");
        Check(savedEmpty.Success && savedEmpty.Source == ScriptConfigurationSource.Xml && savedEmpty.Scripts.Count == 0, "A saved empty list does not recreate samples.");
        ScriptConfigurationResult old = ScriptConfiguration.Read("<ArrayOfScriptInfo><ScriptInfo><Name>old</Name><Command>git</Command></ScriptInfo></ArrayOfScriptInfo>");
        Check(old.Success && old.Scripts.Single().Enabled && old.Scripts.Single().Arguments is null && old.Scripts.Single().OnEvent == ScriptEvent.None, "Absent fields retain the original defaults.");
        return Task.CompletedTask;
    }

    private static Task LegacyAsync()
    {
        const string p = "<_PARAM_SEPARATOR_>", s = "<_SCRIPT_SEPARATOR_>";
        string input = " leading name " + p + "git" + p + " literal args " + p + "yes" + s + "second" + p + "exe" + p + "" + p + "no";
        ScriptConfigurationResult result = ScriptConfiguration.Read(input);
        Check(result.Success && result.Source == ScriptConfigurationSource.Legacy && result.OriginalText == input && result.Scripts.Count == 2, "Old format is an explicit parse result retaining its preimage.");
        Check(result.Scripts[0].Name == " leading name " && result.Scripts[0].Arguments == " literal args " && result.Scripts[0].AddToRevisionGridContextMenu && result.Scripts.All(script => script.Enabled && !script.RunInBackground), "Legacy values are not trimmed and flags match.");
        Check(result.Scripts.Select(script => script.HotkeyCommandIdentifier).SequenceEqual(new[] { 0, 1 }), "Legacy duplicate zero IDs are repaired like Windows.");
        string xml = "<ArrayOfScriptInfo><ScriptInfo><Name>a</Name><HotkeyCommandIdentifier>12</HotkeyCommandIdentifier></ScriptInfo><ScriptInfo><Name>b</Name><HotkeyCommandIdentifier>12</HotkeyCommandIdentifier></ScriptInfo><ScriptInfo><Name>c</Name><HotkeyCommandIdentifier>40</HotkeyCommandIdentifier></ScriptInfo></ArrayOfScriptInfo>";
        Check(ScriptConfiguration.Read(xml).Scripts.Select(script => script.HotkeyCommandIdentifier).SequenceEqual(new[] { 12, 41, 40 }), "Duplicate IDs use list maximum without reordering.");
        return Task.CompletedTask;
    }

    private static Task CorruptionAsync()
    {
        foreach (string input in new[] { "<ArrayOfScriptInfo>", "not XML", "name<_PARAM_SEPARATOR_>too short",
            "<ArrayOfScriptInfo><ScriptInfo><OnEvent>AfterClone</OnEvent></ScriptInfo></ArrayOfScriptInfo>",
            "<!DOCTYPE ArrayOfScriptInfo [<!ENTITY secret SYSTEM 'file:///must-not-be-read'>]><ArrayOfScriptInfo><ScriptInfo><Name>&secret;</Name></ScriptInfo></ArrayOfScriptInfo>" })
        {
            ScriptConfigurationResult result = ScriptConfiguration.Read(input);
            Check(!result.Success && result.Source == ScriptConfigurationSource.Invalid && result.Scripts.Count == 0 && result.OriginalText == input && result.Error is not null, "Corruption stays an error/preimage, not enabled defaults or an entity read.");
        }
        return Task.CompletedTask;
    }

    private static Task QuotesAsync()
    {
        Check(ScriptTemplate.ReplaceOption("WorkingDir", "{{WorkingDir}} \"{WorkingDir}\"", ["C:\\test path with whitespaces\\"])
            == "\"C:\\test path with whitespaces\\\\\" \"C:\\test path with whitespaces\\\"", "Exact Windows trailing-backslash quote oracle.");
        const string value = "test string with \"double quotes\" and escaped \\\"double quotes\\\"";
        Check(ScriptTemplate.ReplaceOption("sMessage", "{{sMessage}}", [value]) == "\"test string with \\\"double quotes\\\" and escaped \\\"double quotes\\\"\"", "Exact Windows preescaped quote oracle.");
        Check(ScriptTemplate.ReplaceOption("sMessage", "{sMessage}", [value]) == value, "Simple form is literal unquoted replacement.");
        Check(ScriptTemplate.ReplaceOption("x", "{{x}}:{x}", ["a b", "", "Ω"]) == "\"a b\" \"\" \"Ω\":a b  Ω", "Quoted replacement is per value, not one composite quote.");
        Check(ScriptTemplate.CreateOption(" a\ta ", true) == "{{a\ta}}" && ScriptTemplate.Contains("", " ") && ScriptTemplate.DependsOnSelectedRevision("s"), "Trim/empty Contains/lowercase-s match Windows unit oracles.");
        return Task.CompletedTask;
    }

    private static async Task VariablesAsync()
    {
        Dictionary<string, string> expected = new()
        {
            ["sHashes"] = "older selected", ["sTag"] = "v1", ["sBranch"] = "topic", ["sLocalBranch"] = "topic", ["sRemoteBranch"] = "origin/topic",
            ["sRemoteBranchName"] = "topic", ["sRemote"] = "origin", ["sRemoteUrl"] = "git@host.invalid:org/topic.git", ["sRemotePathFromUrl"] = "/org/topic",
            ["sHash"] = "selected", ["sMessage"] = "Selected Café\\nbody", ["sSubject"] = "Selected Café", ["sAuthor"] = "Selected Ω", ["sCommitter"] = "Selected Committer",
            ["sAuthorDate"] = new DateTime(2020, 1, 2, 3, 4, 5).ToString(CultureInfo.GetCultureInfo("en-US")), ["sCommitDate"] = new DateTime(2020, 1, 3, 4, 5, 6).ToString(CultureInfo.GetCultureInfo("en-US")), ["HEAD"] = "main", ["cTag"] = "v2",
            ["cBranch"] = "main", ["cLocalBranch"] = "main", ["cRemoteBranch"] = "upstream/main", ["cRemoteBranchName"] = "main", ["cHash"] = "current",
            ["cMessage"] = "Current Café\r\\nbody", ["cSubject"] = "Current Café", ["cAuthor"] = "Current Ω", ["cCommitter"] = "Current Committer",
            ["cAuthorDate"] = new DateTime(2021, 2, 3, 4, 5, 6).ToString(CultureInfo.GetCultureInfo("en-US")), ["cCommitDate"] = new DateTime(2021, 2, 4, 5, 6, 7).ToString(CultureInfo.GetCultureInfo("en-US")), ["cDefaultRemote"] = "upstream",
            ["cDefaultRemoteUrl"] = "https://host.invalid/org/current.git", ["cDefaultRemotePathFromUrl"] = "/org/current", ["RepoName"] = "Friendly description", ["WorkingDir"] = "/literal repo/"
        };
        foreach ((string option, string value) in expected)
        {
            ScriptExpansionResult result = await ScriptTemplate.ExpandAsync("{" + option + "}", Facts(), (request, _) => Task.FromResult(new ScriptReply(ScriptReplyKind.Accepted,
                request.Option == "sBranch" ? "topic" : "main")));
            Check(result.Status == ScriptExpansionStatus.Completed && result.Arguments == value, "Independent source oracle for " + option + ": " + result.Arguments);
        }
        Check((await ScriptTemplate.ExpandAsync("{HEAD}", Facts() with { CurrentBranch = "" })).Arguments == "current", "Detached HEAD is current, never selected.");
    }

    private static async Task DatesAsync()
    {
        DateTimeOffset author = new(2024, 4, 5, 6, 7, 8, TimeSpan.FromHours(5.5));
        DateTimeOffset committer = new(2024, 4, 6, 9, 10, 11, TimeSpan.FromHours(-4));
        long authorUnix = author.ToUnixTimeSeconds(), commitUnix = committer.ToUnixTimeSeconds();
        DateTime sourceAuthor = DateTimeOffset.FromUnixTimeSeconds(authorUnix).LocalDateTime;
        DateTime sourceCommitter = DateTimeOffset.FromUnixTimeSeconds(commitUnix).LocalDateTime;
        Check(ScriptRevision.FromUnixTimeSeconds(authorUnix) == sourceAuthor && ScriptRevision.FromUnixTimeSeconds(commitUnix) == sourceCommitter,
            "The Windows source converts Unix seconds to local DateTime; a raw object's timezone offset is not appended.");
        Check(ScriptRevision.FromUnixTimeSeconds(0) == DateTime.MaxValue, "Windows zero timestamp sentinel is preserved.");
        foreach (string cultureName in new[] { "en-US", "fr-FR" })
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
            ScriptContext context = Facts() with
            {
                Culture = culture,
                SelectedRevision = new("selected", AuthorDate: ScriptRevision.FromUnixTimeSeconds(authorUnix), CommitDate: ScriptRevision.FromUnixTimeSeconds(commitUnix)),
                CurrentRevision = new("current", AuthorDate: DateTime.MaxValue, CommitDate: sourceCommitter)
            };
            string expected = sourceAuthor.ToString(culture) + "|" + sourceCommitter.ToString(culture) + "|" + DateTime.MaxValue.ToString(culture) + "|" + sourceCommitter.ToString(culture);
            ScriptExpansionResult expanded = await ScriptTemplate.ExpandAsync("{sAuthorDate}|{sCommitDate}|{cAuthorDate}|{cCommitDate}", context);
            Check(expanded.Arguments == expected, "Exact DateTime.ToString source oracle for " + cultureName + ": " + expanded.Arguments);
            Check(!expanded.Arguments!.Contains("+05:30", StringComparison.Ordinal) && !expanded.Arguments!.Contains("-04:00", StringComparison.Ordinal),
                "Neither raw offset appears in a DateTime script variable.");
        }
    }

    private static async Task NullMetadataAsync()
    {
        ScriptContext context = Facts() with
        {
            SelectedRevision = new("selected", "fallback subject", Body: null, Author: null, Committer: ""),
            CurrentRevision = new("current", "current subject", Body: "", Author: "", Committer: null)
        };
        ScriptExpansionResult result = await ScriptTemplate.ExpandAsync("{sAuthor}|{{sAuthor}}|{sCommitter}|{{sCommitter}}|{sMessage}|{cAuthor}|{cCommitter}|{cMessage}", context);
        Check(result.Status == ScriptExpansionStatus.Completed && result.Arguments == "{sAuthor}|{{sAuthor}}||\"\"|fallback subject||{cCommitter}|",
            "Missing nullable scalars remain placeholders; empty metadata is replaced; only null body falls back to Subject.");
    }

    private static async Task ChoicesAsync()
    {
        List<ScriptRequest> requests = [];
        ScriptContext context = Facts() with
        {
            SelectedReferences = [new("r/tag", ScriptReferenceKind.Tag), new("two", ScriptReferenceKind.LocalBranch), new("one", ScriptReferenceKind.LocalBranch),
                new("origin/topic", ScriptReferenceKind.RemoteBranch, "origin"), new("upstream/topic", ScriptReferenceKind.RemoteBranch, "upstream")],
            CurrentReferences = [new("branchB", ScriptReferenceKind.LocalBranch), new("branchA", ScriptReferenceKind.LocalBranch)], CurrentRemote = "",
            BranchRemotes = new Dictionary<string, string> { ["branchA"] = "upstream" }
        };
        ScriptExpansionResult result = await ScriptTemplate.ExpandAsync("{cHash}|{sLocalBranch}|{sRemote}|{cDefaultRemote}", context, (request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(new ScriptReply(ScriptReplyKind.Accepted, request.Option switch { "sLocalBranch" => "one", "sRemote" => "origin", _ => "branchA" }));
        });
        Check(result.Arguments == "current|one|origin|upstream", "Current remote comes from selected branch config.");
        Check(requests.Select(request => request.Option).SequenceEqual(new[] { "sLocalBranch", "sRemote", "cDefaultRemote" }), "Options order overrides textual placeholder order.");
        Check(requests[0].Candidates!.SequenceEqual(new[] { "two", "one" }) && requests[1].Candidates!.SequenceEqual(new[] { "origin", "upstream" })
            && requests[2].Candidates!.SequenceEqual(new[] { "branchB", "branchA" }), "Candidates retain names/source order.");
    }

    private static async Task ClosedChoiceAsync()
    {
        ScriptContext context = Facts() with { SelectedReferences = [new("a", ScriptReferenceKind.Tag), new("b", ScriptReferenceKind.Tag)] };
        ScriptExpansionResult closed = await ScriptTemplate.ExpandAsync("before{sTag}after", context, (_, _) => Task.FromResult(new ScriptReply(ScriptReplyKind.Cancelled)));
        Check(closed.Status == ScriptExpansionStatus.Completed && closed.Arguments == "beforeafter", "Closed selector supplies empty, unlike cancelled input.");
        ScriptExpansionResult zero = await ScriptTemplate.ExpandAsync("{sTag}|{sRemote}|{cTag}", Facts() with { SelectedReferences = [], CurrentReferences = [], CurrentRemote = "" });
        Check(zero.Status == ScriptExpansionStatus.Completed && zero.Arguments == "||" && zero.Requests.Count == 0, "Zero candidates means empty and no prompt.");
    }

    private static async Task MissingRevisionAsync()
    {
        ScriptExpansionResult grid = await ScriptTemplate.PrepareAsync(Script("{sHash}") with { AskConfirmation = true }, new(), (_, _) => throw new InvalidOperationException("No prompt before missing grid."));
        Check(grid.Status == ScriptExpansionStatus.Aborted && grid.Error == ScriptExpansionError.NoRevisionGrid && grid.Option == "sHash" && grid.Requests.Count == 0, "Missing grid precedes AskConfirmation.");
        ScriptExpansionResult selection = await ScriptTemplate.ExpandAsync("{sHashes}", new() { HasRevisionGrid = true });
        Check(selection.Error == ScriptExpansionError.MissingSelectedRevision && selection.Status == ScriptExpansionStatus.Aborted, "No latest selection is not a successful empty hash.");
        foreach (string option in new[] { "HEAD", "cHash", "cDefaultRemote" })
        {
            ScriptExpansionResult unborn = await ScriptTemplate.ExpandAsync("{" + option + "}", new());
            Check(unborn.Error == ScriptExpansionError.MissingCurrentRevision && unborn.Status == ScriptExpansionStatus.Aborted, "Missing current revision aborts " + option);
        }
        Check((await ScriptTemplate.ExpandAsync("{WorkingDir} {RepoName}", new() { WorkingDir = "/empty/", RepoName = "Empty" })).Arguments == "/empty/ Empty", "Independent facts need no fabricated commit.");
    }

    private static async Task ProviderAsync()
    {
        ScriptContext context = new() { AdditionalOptions = [new("option1", ["value1a", "value1b"]), new("option2", ["value2"])] };
        ScriptExpansionResult result = await ScriptTemplate.ExpandAsync("foo {{option1}} bar {option2} {unknown}", context);
        Check(result.Arguments == "foo \"value1a\" \"value1b\" bar value2 {unknown}", "Exact multiple-value Windows provider oracle; unknown stays literal.");
        Check((await ScriptTemplate.ExpandAsync("before{SelectedRelativePaths}{LineNumber}{ColumnNumber}after", new())).Arguments == "beforeafter", "Default provider clears known file options.");
    }

    private static async Task ProviderOrderAsync()
    {
        ScriptContext context = new()
        {
            WorkingDir = "/global/",
            ProviderOptions = ["first", "SelectedRelativePaths", "last", "first", "WorkingDir"],
            AdditionalOptions = [new("first", ["{SelectedRelativePaths}"]), new("SelectedRelativePaths", ["{last}"]), new("last", ["literal \"'"]),
                new("WorkingDir", ["provider must not override global"]), new("undeclared", ["not expanded"])]
        };
        ScriptExpansionResult result = await ScriptTemplate.ExpandAsync("{first}|{SelectedRelativePaths}|{last}|{WorkingDir}|{undeclared}|{LineNumber}", context);
        Check(result.Arguments == "literal \"'|literal \"'|literal \"'|/global/|{undeclared}|{LineNumber}",
            "Declared provider order follows globals, is union-deduplicated, and consumes supplied values once without extra escaping.");
        ScriptExpansionResult empty = await ScriptTemplate.ExpandAsync("{SelectedRelativePaths}|{LineNumber}|{first}", context with { ProviderOptions = [] });
        Check(empty.Arguments == "{SelectedRelativePaths}|{LineNumber}|{first}", "An explicitly empty provider leaves its undeclared keys alone.");
    }

    private static async Task FilesAsync()
    {
        const string raw = "a\\b\"' Ω\n";
        ScriptContext context = new() { SelectedRelativePaths = [raw, "second file"], LineNumber = 180, ColumnNumber = 7 };
        Check((await ScriptTemplate.ExpandAsync("{SelectedRelativePaths}|{LineNumber}:{ColumnNumber}", context)).Arguments == "a\\\\b\\\"\\' Ω\n second file|180:7", "Explicit POSIX escape preserves LF/raw paths.");
        Check((await ScriptTemplate.ExpandAsync("{SelectedRelativePaths}|{LineNumber}:{ColumnNumber}", context with { ArgumentPlatform = ScriptArgumentPlatform.Windows })).Arguments == "a\\b\"\"' Ω\n second file|180:7", "Windows provider doubles quotes only.");
        Check((await ScriptTemplate.ExpandAsync("{{SelectedRelativePaths}}", context with { SelectedFolder = " chosen folder/" })).Arguments == "\" chosen folder/\"", "Folder replaces leaf selection without normalization.");
    }

    private static async Task InputsAsync()
    {
        List<(string? Label, string? Default)> seen = [];
        Queue<string> values = new(new[] { "foo1", "bar1", "foo2" });
        ScriptExpansionResult result = await ScriptTemplate.PrepareAsync(Script("{UserInput:input label1=value}_{{UserInput:input label2=value2}}_{UserInput}_{{UserInput}}"), new(), (request, _) =>
        { seen.Add((request.Label, request.DefaultValue)); return Task.FromResult(new ScriptReply(ScriptReplyKind.Accepted, values.Dequeue())); });
        Check(result.Arguments == "foo1_\"bar1\"_foo2_\"foo2\"" && seen.SequenceEqual(new (string?, string?)[] { ("input label1", "value"), ("input label2", "value2"), (null, "") }), "Exact distinct/anonymous Windows input oracle.");
        ScriptExpansionResult repeated = await ScriptTemplate.PrepareAsync(Script("{UserInput:input label1=value}_{UserInput:input label1}_{{UserInput:input label1}}"), new(), (request, _) =>
        { Check(request.Label == "input label1" && request.DefaultValue == "value", "First labeled default."); return Task.FromResult(new ScriptReply(ScriptReplyKind.Accepted, "foo1")); });
        Check(repeated.Arguments == "foo1_foo1_\"foo1\"" && repeated.Requests.Count == 1, "Same label reuses one answer.");
        ScriptExpansionResult noDefault = await ScriptTemplate.PrepareAsync(Script("{UserInput:input label1}"), new(), (request, _) =>
        { Check(request.DefaultValue == "", "Missing default is empty, not null."); return Task.FromResult(new ScriptReply(ScriptReplyKind.Accepted, "foo1")); });
        Check(noDefault.Arguments == "foo1", "No-default labeled input.");
    }

    private static async Task ExactInputTokensAsync()
    {
        foreach (bool quoted in new[] { false, true })
        {
            const string label = " Label \t ";
            const string value = "  exact  ";
            string token = "{UserInput:" + label + "=" + value + "}";
            if (quoted) { token = "{" + token + "}"; }
            List<ScriptRequest> seen = [];
            ScriptExpansionResult result = await ScriptTemplate.PrepareAsync(Script(token + "|{UserInput:" + label + "}|{{UserInput:" + label + "}}"), new(), (request, _) =>
            {
                seen.Add(request);
                return Task.FromResult(seen.Count == 1 ? new ScriptReply(ScriptReplyKind.Accepted, "  accepted  ") : new ScriptReply(ScriptReplyKind.Cancelled));
            });
            string expected = (quoted ? "\"  accepted  \"" : "  accepted  ") + "|  accepted  |\"  accepted  \"";
            Check(result.Status == ScriptExpansionStatus.Completed && result.Arguments == expected && seen.Count == 1,
                $"Exact UserInput token must terminate after one request (quoted={quoted}, status={result.Status}, calls={seen.Count}).");
            Check(seen[0].Label == label && seen[0].Option == "UserInput:" + label && seen[0].DefaultValue == value,
                "Label, option and leading/trailing default spaces are literal prompt text.");
        }
        int calls = 0;
        ScriptExpansionResult distinct = await ScriptTemplate.PrepareAsync(Script("{UserInput:Label=one}|{UserInput:Label =two}|{UserInput:Label}"), new(), (request, _) =>
        {
            calls++;
            return Task.FromResult(calls > 2 ? new ScriptReply(ScriptReplyKind.Cancelled) : new ScriptReply(ScriptReplyKind.Accepted, request.Label == "Label" ? "first" : "second"));
        });
        Check(distinct.Status == ScriptExpansionStatus.Completed && distinct.Arguments == "first|second|first" && calls == 2,
            "Labels differing by trailing space stay distinct and an exact repeated label reuses its answer.");
    }

    private static async Task LiteralInputValuesAsync()
    {
        const string value = "  say \"hi\" and \\\"kept\\\" tail\\";
        const string quotedValue = "\"  say \\\"hi\\\" and \\\"kept\\\" tail\\\\\"";
        foreach (bool quoted in new[] { false, true })
        {
            string token = "{UserInput:Exact label=  \"default\" C:\\dir\\  }";
            if (quoted) { token = "{" + token + "}"; }
            int calls = 0;
            ScriptExpansionResult result = await ScriptTemplate.PrepareAsync(Script("{{WorkingDir}}|" + token + "|{RepoName}"), Facts(), (request, _) =>
            {
                calls++;
                Check(request.Label == "Exact label" && request.DefaultValue == "  \"default\" C:\\dir\\  ", "Quotes, backslashes and trailing spaces in defaults are not normalized.");
                return Task.FromResult(calls == 1 ? new ScriptReply(ScriptReplyKind.Accepted, value) : new ScriptReply(ScriptReplyKind.Cancelled));
            });
            Check(result.Status == ScriptExpansionStatus.Completed && calls == 1 && result.Arguments == "\"/literal repo/\"|" + (quoted ? quotedValue : value) + "|Friendly description",
                $"Input quoting and ordinary variables retain their established forms (quoted={quoted}, status={result.Status}, calls={calls}, arguments={result.Arguments}).");
        }
    }

    private static async Task DefaultsAsync()
    {
        ScriptExpansionResult result = await ScriptTemplate.PrepareAsync(Script("{UserInput:input label1=file_{HEAD}.bak}"), Facts() with { CurrentBranch = "" }, (request, _) =>
        { Check(request.DefaultValue == "file_current.bak" && request.Label == "input label1", "Default uses checkout, not selection."); return Task.FromResult(new ScriptReply(ScriptReplyKind.Accepted, "file_foo.bak")); });
        Check(result.Arguments == "file_foo.bak", "Exact nested-default Windows oracle.");
        ScriptExpansionResult failure = await ScriptTemplate.PrepareAsync(Script("{UserInput:label={HEAD}}"), new(), (_, _) => throw new InvalidOperationException("No input after default failure."));
        Check(failure.Error == ScriptExpansionError.MissingCurrentRevision && failure.Status == ScriptExpansionStatus.Aborted && failure.Requests.Count == 0, "Default failure is aborted, not cancelled/empty.");
    }

    private static async Task InputCancellationAsync()
    {
        ScriptExpansionResult files = await ScriptTemplate.PrepareAsync(Script("unquoted path:{UserFiles} - quoted path:{{UserFiles}}"), new(), (request, _) =>
        { Check(request.Kind == ScriptRequestKind.UserFiles, "Typed file request."); return Task.FromResult(new ScriptReply(ScriptReplyKind.Accepted, @"C:\a_file_path\file.txt")); });
        Check(files.Arguments == "unquoted path:C:\\a_file_path\\file.txt - quoted path:\"C:\\a_file_path\\file.txt\"", "Exact Windows UserFiles oracle.");
        ScriptExpansionResult cancelled = await ScriptTemplate.PrepareAsync(Script("{UserInput} {UserFiles}"), new(), (_, _) => Task.FromResult(new ScriptReply(ScriptReplyKind.Cancelled)));
        Check(cancelled.Status == ScriptExpansionStatus.Cancelled && cancelled.Requests.Count == 1 && cancelled.Arguments is null, "Cancellation stops the next prompt.");
        ScriptExpansionResult empty = await ScriptTemplate.PrepareAsync(Script("{{UserInput}}"), new(), (_, _) => Task.FromResult(new ScriptReply(ScriptReplyKind.Accepted, "")));
        Check(empty.Status == ScriptExpansionStatus.Completed && empty.Arguments == "\"\"", "Accepted empty is not cancelled.");
    }

    private static async Task ConfirmationAsync()
    {
        ScriptDefinition script = Script("{UserInput}") with { AskConfirmation = true };
        foreach (ScriptReplyKind choice in new[] { ScriptReplyKind.Rejected, ScriptReplyKind.Cancelled })
        {
            ScriptExpansionResult result = await ScriptTemplate.PrepareAsync(script, new(), (request, _) =>
            { Check(request.Kind == ScriptRequestKind.Confirmation && request.ScriptName == "Private script", "First request is confirmation with display name."); return Task.FromResult(new ScriptReply(choice)); });
            Check(result.Status == (choice == ScriptReplyKind.Cancelled ? ScriptExpansionStatus.Cancelled : ScriptExpansionStatus.Declined) && result.Command is null && result.Requests.Count == 1, "Refusal has no prepared command/next input.");
        }
        ScriptExpansionResult unavailable = await ScriptTemplate.PrepareAsync(script, new());
        Check(unavailable.Error == ScriptExpansionError.InputUnavailable && unavailable.Status == ScriptExpansionStatus.Aborted && unavailable.Requests.Count == 1, "No handler never implies Yes.");
    }

    private static async Task TargetsAsync()
    {
        ScriptContext context = new() { WorkingDir = "/literal ", GitExecutable = "/chosen git", ApplicationExecutable = "/chosen app", OpenUrlExecutable = "xdg-open", HasRevisionGrid = true };
        foreach (string alias in new[] { "git", "{git}", "GIT" })
        { Check((await ScriptTemplate.PrepareAsync(Script(null, alias), context)).Command == "/chosen git", "Git alias is a host fact."); }
        foreach (string alias in new[] { "gitextensions", "{gitextensions}", "gitex", "{gitex}" })
        { Check((await ScriptTemplate.PrepareAsync(Script("", alias), context)).Command == "/chosen app", "All app aliases remain."); }
        ScriptExpansionResult open = await ScriptTemplate.PrepareAsync(Script("https://host.invalid", "{openurl}") with { RunInBackground = true }, context);
        Check(open.Target == ScriptTargetKind.OpenUrl && open.Command == "xdg-open", "OpenURL prepares but never launches.");
        Check((await ScriptTemplate.PrepareAsync(Script("", "{openurl}"), context)).Target == ScriptTargetKind.External, "Foreground openurl stays external.");
        ScriptExpansionResult plugin = await ScriptTemplate.PrepareAsync(Script("", "{plugin:Some Plugin}"), context);
        Check(plugin.Target == ScriptTargetKind.Plugin && plugin.Command == "some plugin", "Plugin is a target, not an execution.");
        ScriptExpansionResult navigate = await ScriptTemplate.PrepareAsync(Script("", "navigateTo:{WorkingDir}picker"), context);
        Check(navigate.Target == ScriptTargetKind.NavigateTo && navigate.Command == "/literal picker", "Navigation materializes executable only.");
        ScriptExpansionResult powershell = await ScriptTemplate.PrepareAsync(Script("", "{plugin:Some Plugin}") with { IsPowerShell = true }, context);
        Check(powershell.Target == ScriptTargetKind.PowerShell && powershell.Command == "plugin:some plugin", "PowerShell precedes plugin dispatch.");
    }

    private static async Task EmptyAsync()
    {
        foreach (string? input in new string?[] { null, "", " ", "\t" })
        {
            ScriptExpansionResult expanded = await ScriptTemplate.ExpandAsync(input, new());
            ScriptExpansionResult prepared = await ScriptTemplate.PrepareAsync(Script(input), new());
            Check(expanded.Status == ScriptExpansionStatus.Completed && expanded.Arguments == input && prepared.Arguments == input, "Null/empty/whitespace remain distinct.");
        }
        ScriptExpansionResult missing = await ScriptTemplate.PrepareAsync(new() { Command = null, AskConfirmation = true }, new(), (_, _) => throw new InvalidOperationException("Empty command precedes confirmation."));
        Check(missing.Status == ScriptExpansionStatus.Aborted && missing.Error == ScriptExpansionError.EmptyCommand && missing.Requests.Count == 0, "No command never reaches prompts.");
    }

    private static async Task ExpansionOrderAsync()
    {
        ScriptContext context = new() { AdditionalOptions = [new("early", ["{late}"]), new("late", ["{early};$(literal)$HOME"])] };
        ScriptExpansionResult result = await ScriptTemplate.ExpandAsync("{early} {late} {unrecognised}", context);
        Check(result.Arguments == "{early};$(literal)$HOME {early};$(literal)$HOME {unrecognised}", "One source-ordered pass, no recursive/shell/environment evaluation.");
        Check((await ScriptTemplate.ExpandAsync("{sMessage}", Facts() with { SelectedRevision = new("selected", "fallback", "") })).Arguments == "", "Empty body differs from null body.");
    }

    private static async Task SnapshotAsync()
    {
        string[] paths = ["before"], values = ["value before"], providerOrder = ["SelectedRelativePaths", "custom"];
        ScriptReference[] refs = [new("tag before", ScriptReferenceKind.Tag)];
        ScriptContext context = Facts() with { SelectedRelativePaths = paths, SelectedReferences = refs, ProviderOptions = providerOrder, AdditionalOptions = [new("custom", values)] };
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ScriptExpansionResult> pending = ScriptTemplate.PrepareAsync(Script("{sTag}|{SelectedRelativePaths}|{custom}") with { AskConfirmation = true }, context, async (_, token) =>
        { ready.TrySetResult(); await release.Task.WaitAsync(token); return new(ScriptReplyKind.Accepted); });
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            paths[0] = "after"; values[0] = "value after"; refs[0] = new("tag after", ScriptReferenceKind.Tag); providerOrder[1] = "not custom";
            release.TrySetResult();
            Check((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Arguments == "tag before|before|value before", "Snapshot holds host facts and provider order through an await.");
        }
        finally { release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task CancellationAsync()
    {
        using CancellationTokenSource stopped = new(); stopped.Cancel();
        try { await ScriptTemplate.PrepareAsync(Script("{UserInput}"), new(), (_, _) => throw new InvalidOperationException("No input after cancellation."), stopped.Token); throw new InvalidOperationException("Pre-cancellation ignored."); }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        using CancellationTokenSource live = new(); int calls = 0;
        try
        {
            await ScriptTemplate.PrepareAsync(Script("{UserInput:first}_{UserInput:second}"), new(), (_, _) =>
            { calls++; live.Cancel(); return Task.FromResult(new ScriptReply(ScriptReplyKind.Accepted, "first")); }, live.Token);
            throw new InvalidOperationException("Cancellation during prompt ignored.");
        }
        catch (OperationCanceledException) when (live.IsCancellationRequested) { }
        Check(calls == 1, "Cancellation prevents subsequent prompts/prepared command.");
    }
}
