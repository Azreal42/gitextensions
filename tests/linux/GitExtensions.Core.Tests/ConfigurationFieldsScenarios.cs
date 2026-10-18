using System.Text.Json;
using GitExtensions.Linux;

internal static class ConfigurationFieldsScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("configuration fields expose immutable actual scopes origins missing empty and valueless values", SnapshotAsync),
        ("configuration fields and six tool fields share one guarded native batch with exact values", JointBatchAsync),
        ("configuration fields preserve duplicate noops and reject every changed multivalue before tools write", DuplicatesAsync),
        ("configuration fields refuse included-only unset and retain sources when adding native overrides", IncludesAsync),
        ("configuration fields preflight typed values and preserve unknown existing encoding and autocrlf aliases", ValidationAsync),
        ("configuration fields preserve explicit empty strings and distinguish scoped unset", EmptyAsync),
        ("configuration credential helper empty reset suppresses actual inherited helper execution", CredentialResetAsync),
        ("configuration fields and tools share stale include context and physical target guards", GuardsAsync),
        ("configuration fields compete with old tool API through the same shared global lock", SharedAsync),
        ("configuration fields retain mixed batch partial results after a real native global lock failure", PartialAsync),
        ("configuration fields cancellation drains the witnessed native writer without post-cancel readback", CancellationAsync)
    ];

    private static readonly GitConfigurationField[] Fields = Enum.GetValues<GitConfigurationField>();
    private static readonly string[] Keys = ["user.name", "user.email", "credential.helper", "core.editor", "commit.template", "core.autocrlf", "i18n.filesencoding"];

    private static async Task SnapshotAsync()
    {
        using Fixture f = await Fixture.Create();
        foreach (GitConfigurationField field in Fields)
        {
            await f.Git("config", "--file", f.Global, Key(field), Value(field, "global"));
            await f.Git("config", Key(field), Value(field, "local"));
        }
        await f.Git("config", "core.editor", "");
        await f.Git("config", "--unset", "commit.template");
        await File.AppendAllTextAsync(f.Config, "\n[commit]\n template\n[private]\n secret = do-not-expose\n");
        await f.Git("config", "difftool.Case.path", "/tool/path");
        GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync();
        GitToolConfigurationScopeSnapshot local = snapshot.GetScope(GitToolConfigurationView.Local);
        foreach (GitConfigurationField field in Fields)
        {
            GitToolConfigurationValue setting = local.GetSetting(field);
            Check(setting.Key == Key(field) && setting.CanChange && setting.Occurrences.Single() is { Scope: "local", IsNativeWriteTarget: true } entry
                && entry.ResolvedOriginPath == f.Config && entry.Origin.StartsWith("file:", StringComparison.Ordinal), "Each typed field retains its actual native local origin.");
            Check(snapshot.GetScope(GitToolConfigurationView.Effective).GetSetting(field).Occurrences.Select(entry => entry.Scope).SequenceEqual(["global", "local"])
                && !snapshot.GetScope(GitToolConfigurationView.Effective).GetSetting(field).CanChange
                && !snapshot.GetScope(GitToolConfigurationView.System).GetSetting(field).CanChange,
                "Effective preserves native precedence and read-only scopes cannot change fields.");
        }
        Check(local.GetSetting(GitConfigurationField.Editor).Occurrences.Single().Value == ""
            && local.GetSetting(GitConfigurationField.CommitTemplate).Occurrences.Single().Value is null
            && snapshot.GetScope(GitToolConfigurationView.System).GetSetting(GitConfigurationField.Editor).Occurrences.Count == 0,
            "Explicit empty, existing valueless, and missing entries remain distinct.");
        Check(local.Occurrences.Count == 1 && local.Occurrences[0].Key == "difftool.Case.path"
            && local.GetTool(GitToolRole.Diff, "Case").Path.Occurrences.Single().Value == "/tool/path",
            "The existing tool occurrence API contains only tool keys and never reveals common or private settings.");
        GitToolConfigurationValue captured = local.GetSetting(GitConfigurationField.UserName);
        await Expect<NotSupportedException>(() => { ((IList<GitToolConfigurationOccurrence>)captured.Occurrences)[0] = captured.Occurrences[0]; return Task.CompletedTask; });
        await Expect<ArgumentOutOfRangeException>(() => { local.GetSetting((GitConfigurationField)99); return Task.CompletedTask; });
        await f.Git("config", "user.name", "changed-externally");
        Check(captured.Occurrences.Single().Value == Value(GitConfigurationField.UserName, "local"), "A captured field lookup performs immutable memory reads.");
        await f.Git("config", "--unset", "core.editor");
        await File.AppendAllTextAsync(f.Config, "\n[core]\n editor\n");
        GitResult failedLocation = await new GitRunner().RunAsync(f.Repository.Path, ["rev-parse", "--git-path", "config"]);
        Check(failedLocation.ExitCode == 128 && failedLocation.StandardError.Contains("core.editor", StringComparison.Ordinal),
            "The real repository location query fails on a valueless core.editor setting.");
        GitToolConfigurationSnapshot unavailable = await f.Repository.GetToolConfigurationAsync();
        Check(unavailable.Scopes.All(scope => !scope.IsAvailable && !scope.CanWrite)
            && unavailable.GetScope(GitToolConfigurationView.Local).Diagnostic?.Contains(failedLocation.StandardError.Trim(), StringComparison.Ordinal) == true,
            "Actual local configuration entries distinguish a native repository error from a directory outside a repository and retain native stderr.");
        GitToolConfigurationScopeSnapshot invalidLocal = unavailable.GetScope(GitToolConfigurationView.Local);
        Check(unavailable.GitVersion == snapshot.GitVersion && unavailable.ConfigurationRevision.Length > 0
            && Fields.All(field => invalidLocal.GetSetting(field).Occurrences.Count == 1 && !invalidLocal.GetSetting(field).CanChange)
            && invalidLocal.GetSetting(GitConfigurationField.Editor).Occurrences.Single().Value is null
            && invalidLocal.GetSetting(GitConfigurationField.FilesEncoding).Occurrences.Single().Value == Value(GitConfigurationField.FilesEncoding, "local")
            && invalidLocal.GetSetting(GitConfigurationField.UserName).Occurrences.Single().Value == "changed-externally"
            && invalidLocal.GetTool(GitToolRole.Diff, "Case").Path.Occurrences.Single().Value == "/tool/path"
            && invalidLocal.GetSetting(GitConfigurationField.Editor).Occurrences.Single() is { IsNativeWriteTarget: false } invalidEntry
            && invalidEntry.ResolvedOriginPath == f.Config,
            "A failed native location query preserves actual version revision raw fields tool values and origins without inferring a native local target.");
        Check(Fields.All(field => unavailable.GetScope(GitToolConfigurationView.Effective).GetSetting(field).Occurrences.Count == 2),
            "Read-only effective fields retain both actual scopes even when the native local destination is unresolved.");
        byte[] invalidBefore = await File.ReadAllBytesAsync(f.Config);
        GitToolConfigurationSaveException refused = await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(unavailable,
            [Local(new GitConfigurationFieldEdit(GitConfigurationField.UserName, "must-not-write"))]));
        Check(refused.Result.MutationStarted == false && !refused.Result.MayHaveChanged && refused.Result.Writes.Count == 0
            && invalidBefore.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(f.Config)),
            "An unresolved native repository error cannot invent a local target or publish any field change.");
    }

    private static async Task JointBatchAsync()
    {
        using Fixture f = await Fixture.Create();
        GitToolConfigurationScopeEdit legacy = new(GitConfigScope.Local, [new(GitToolRole.Diff, "legacy", true)]);
        (GitConfigScope legacyScope, IReadOnlyList<GitToolRoleEdit> legacyRoles) = legacy;
        Check(legacyScope == GitConfigScope.Local && legacyRoles.Single().Name == "legacy" && legacy.Settings is null,
            "The existing two-argument scope edit and two-value deconstruction remain source compatible.");
        await f.Git("config", "--add", "private.values", "first"); await f.Git("config", "--add", "private.values", "second");
        await f.Git("config", "difftool.old.cmd", "keep-old");
        string head = await f.Repository.ResolveRevisionAsync("HEAD"); byte[] index = await File.ReadAllBytesAsync(f.File(".git/index"));
        string[] values = ["  λ Name '$()  ", " λ+literal@example.invalid ", "!helper --literal '$()'", "editor --wait \"λ path\"\nnext", "  /template λ\\'\"$()  ", "input", "Unrecognized-CODEPAGE-123"];
        GitToolConfigurationSaveResult saved = await f.Repository.SaveToolConfigurationAsync(await f.Repository.GetToolConfigurationAsync(),
        [
            new(GitConfigScope.Global, [], Fields.Select(field => new GitConfigurationFieldEdit(field, Value(field, "global"))).ToArray()),
            new(GitConfigScope.Local,
                [new(GitToolRole.Diff, "Diff.Final", true, new("/diff λ path"), new("diff '$LOCAL'\nnext")),
                 new(GitToolRole.Merge, "Merge.Final", true, new("/merge λ path"), new("merge '$MERGED'\nnext"))],
                Fields.Select(field => new GitConfigurationFieldEdit(field, values[(int)field])).ToArray())
        ]);
        Check(saved.Success && saved.MutationStarted == true && saved.Writes.Count == 20
            && saved.Writes.All(write => write.Outcome == GitToolConfigurationWriteOutcome.NativeCompleted && write.NativeResult?.Success == true
                && !write.Arguments.Contains("--replace-all") && !write.Arguments.Contains("--unset-all")),
            "The seven fields and six tool fields publish with ordinary scalar Git argv and individual native results.");
        Check(saved.Writes.Take(13).All(write => write.Scope == GitConfigScope.Local) && saved.Writes.Skip(13).All(write => write.Scope == GitConfigScope.Global)
            && saved.Writes.Take(6).Select(write => write.Key).SequenceEqual(["difftool.Diff.Final.path", "difftool.Diff.Final.cmd", "diff.guitool", "mergetool.Merge.Final.path", "mergetool.Merge.Final.cmd", "merge.guitool"]),
            "The existing Local-before-Global and definition-before-selector ordering remains stable in a joint batch.");
        foreach (GitConfigurationField field in Fields)
        {
            string value = values[(int)field];
            Check(saved.After!.GetScope(GitToolConfigurationView.Local).GetSetting(field).Occurrences.Single().Value == value
                && saved.After.GetScope(GitToolConfigurationView.Global).GetSetting(field).Occurrences.Single().Value == Value(field, "global"),
                "Actual readback keeps Local and Global distinct and preserves exact scalar values.");
            GitResult native = await f.Git("config", "--local", "--null", "--get", Key(field));
            Check(native.StandardOutput == value + "\0", "Independent native Git retrieval observes the exact requested value.");
        }
        Check(saved.After!.GetScope(GitToolConfigurationView.Local).GetTool(GitToolRole.Diff, "old").Command.Occurrences.Single().Value == "keep-old"
            && (await f.Repository.GetConfigAsync("^private\\.values$")).Select(entry => entry.Value).SequenceEqual(["first", "second"]),
            "Unvisited tools and unrelated multivalues survive the common-field batch.");
        Check(await f.Repository.ResolveRevisionAsync("HEAD") == head && index.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(f.File(".git/index"))),
            "Configuration saves leave commit identity and index bytes unchanged.");
    }

    private static async Task DuplicatesAsync()
    {
        using Fixture f = await Fixture.Create();
        foreach (GitConfigurationField field in Fields)
        {
            await f.Git("config", "--add", Key(field), Value(field, "first"));
            await f.Git("config", "--add", Key(field), Value(field, "last"));
        }
        byte[] before = await File.ReadAllBytesAsync(f.Config); GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync();
        Check(Fields.All(field => !snapshot.GetScope(GitToolConfigurationView.Local).GetSetting(field).CanChange), "Every duplicated common field carries its restriction.");
        GitToolConfigurationSaveResult noop = await f.Repository.SaveToolConfigurationAsync(snapshot,
            [Local(Fields.Select(field => new GitConfigurationFieldEdit(field, Value(field, "last"))).ToArray())]);
        Check(noop.Writes.Count == 7 && noop.Writes.All(write => write.Outcome == GitToolConfigurationWriteOutcome.NoOp)
            && noop.MutationStarted == false && !noop.MayHaveChanged && before.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(f.Config)),
            "All common duplicate noops retain the original bytes rather than flattening any entry.");
        foreach (GitConfigurationField field in Fields)
        {
            GitToolConfigurationSaveException error = await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(noop.After!,
                [new(GitConfigScope.Local, [new(GitToolRole.Diff, "would-publish", true, Command: new("true"))], [new(field, Value(field, "changed"))])]));
            Check(error.Result.MutationStarted == false && !error.Result.MayHaveChanged && error.Result.Writes.Count == 0
                && before.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(f.Config)), "A changed duplicate field rejects the whole mixed batch before its valid tool prefix.");
        }
    }

    private static async Task IncludesAsync()
    {
        using Fixture f = await Fixture.Create(); string included = Path.Combine(f.Root, "included fields λ.conf");
        await f.Git("config", "--unset", "user.name"); await f.Git("config", "--unset", "user.email");
        foreach (GitConfigurationField field in Fields) { await f.Git("config", "--file", included, Key(field), Value(field, "included")); }
        await f.Git("config", "include.path", included);
        byte[] bytes = await File.ReadAllBytesAsync(included), local = await File.ReadAllBytesAsync(f.Config);
        GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync();
        foreach (GitConfigurationField field in Fields)
        {
            Check(snapshot.GetScope(GitToolConfigurationView.Local).GetSetting(field).Occurrences.Single() is { IsNativeWriteTarget: false } entry
                && entry.ResolvedOriginPath == included, "Included common fields retain their physical origins.");
            GitToolConfigurationSaveException error = await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(snapshot, [Local(new GitConfigurationFieldEdit(field, null))]));
            Check(error.Result.MutationStarted == false && error.Result.Writes.Count == 0 && local.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(f.Config)),
                "A scalar scoped unset cannot erase a field that exists only in an include.");
        }
        GitToolConfigurationSaveResult saved = await f.Repository.SaveToolConfigurationAsync(snapshot,
            [Local(Fields.Select(field => new GitConfigurationFieldEdit(field, Value(field, "override"))).ToArray())]);
        foreach (GitConfigurationField field in Fields)
        {
            IReadOnlyList<GitToolConfigurationOccurrence> entries = saved.After!.GetScope(GitToolConfigurationView.Local).GetSetting(field).Occurrences;
            Check(entries.Count == 2 && entries.Any(entry => entry.Value == Value(field, "included") && !entry.IsNativeWriteTarget)
                && entries.Any(entry => entry.Value == Value(field, "override") && entry.IsNativeWriteTarget),
                "A native override exposes both actual origins and retains the included value.");
        }
        Check(bytes.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(included)), "No common-field override rewrites the include file.");
        await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(saved.After!, [Local(new GitConfigurationFieldEdit(GitConfigurationField.Editor, "would-flatten"))]));
    }

    private static async Task ValidationAsync()
    {
        using Fixture f = await Fixture.Create();
        await f.Git("config", "core.autocrlf", "TRUE"); await f.Git("config", "i18n.filesEncoding", "Unknown-Platform-Codepage");
        GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync(); byte[] before = await File.ReadAllBytesAsync(f.Config);
        GitToolConfigurationSaveResult noop = await f.Repository.SaveToolConfigurationAsync(snapshot,
            [Local(new(GitConfigurationField.AutoCrlf, "TRUE"), new(GitConfigurationField.FilesEncoding, "Unknown-Platform-Codepage"))]);
        Check(noop.Writes.All(write => write.Outcome == GitToolConfigurationWriteOutcome.NoOp) && before.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(f.Config)),
            "Unknown encoding names and existing noncanonical autocrlf values remain untouched without normalization or fallback.");
        GitConfigurationFieldEdit[][] invalid =
        [
            [new((GitConfigurationField)99, "invalid")],
            [new(GitConfigurationField.Editor, "bad\0value")],
            [new(GitConfigurationField.UserName, "first"), new(GitConfigurationField.UserName, "second")],
            [new(GitConfigurationField.AutoCrlf, "invalid")]
        ];
        foreach (GitConfigurationFieldEdit[] settings in invalid)
        {
            GitToolConfigurationSaveException error = await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(noop.After!,
                [new(GitConfigScope.Local, [new(GitToolRole.Diff, "valid-prefix", true, Command: new("true"))], settings)]));
            Check(error.Result.MutationStarted == false && error.Result.Writes.Count == 0 && before.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(f.Config)),
                "Invalid typed settings fail complete preflight before a valid tool prefix writes.");
        }
        GitToolConfigurationSaveException foreign = await Expect<GitToolConfigurationSaveException>(() => new GitRepository(f.Repository.Path).SaveToolConfigurationAsync(noop.After!, [Local(new GitConfigurationFieldEdit(GitConfigurationField.Editor, "wrong-owner"))]));
        Check(foreign.Result.MutationStarted == false, "Common fields retain snapshot repository ownership.");
        GitToolConfigurationSaveResult saved = await f.Repository.SaveToolConfigurationAsync(noop.After!,
            [Local(new(GitConfigurationField.AutoCrlf, "false"), new(GitConfigurationField.FilesEncoding, "Another-Unknown-Codepage"))]);
        Check(saved.After!.GetScope(GitToolConfigurationView.Local).GetSetting(GitConfigurationField.FilesEncoding).Occurrences.Single().Value == "Another-Unknown-Codepage"
            && saved.Writes.Single(write => write.Key == "i18n.filesencoding").NativeResult?.Success == true,
            "An explicit encoding edit stores its literal name without attempting platform codepage fallback or re-encoding files.");
    }

    private static async Task EmptyAsync()
    {
        using Fixture f = await Fixture.Create(); GitConfigurationField[] text = Fields.Where(field => field != GitConfigurationField.AutoCrlf).ToArray();
        GitToolConfigurationSaveResult saved = await f.Repository.SaveToolConfigurationAsync(await f.Repository.GetToolConfigurationAsync(),
            [Local(text.Select(field => new GitConfigurationFieldEdit(field, "")).ToArray())]);
        Check(text.All(field => saved.After!.GetScope(GitToolConfigurationView.Local).GetSetting(field).Occurrences.Single().Value == "")
            && saved.Writes.All(write => write.RequestedValue == "" && write.Arguments[^1] == "" && write.NativeResult?.Success == true),
            "Explicit empty text values are real existing entries with an empty native argv value.");
        byte[] before = await File.ReadAllBytesAsync(f.Config);
        GitToolConfigurationSaveResult noop = await f.Repository.SaveToolConfigurationAsync(saved.After!, [Local(text.Select(field => new GitConfigurationFieldEdit(field, "")).ToArray())]);
        Check(noop.MutationStarted == false && noop.Writes.All(write => write.Outcome == GitToolConfigurationWriteOutcome.NoOp)
            && before.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(f.Config)), "Untouched explicit empty values remain byte-identical.");
        GitToolConfigurationSaveResult unset = await f.Repository.SaveToolConfigurationAsync(noop.After!, [Local(text.Select(field => new GitConfigurationFieldEdit(field, null)).ToArray())]);
        Check(text.All(field => unset.After!.GetScope(GitToolConfigurationView.Local).GetSetting(field).Occurrences.Count == 0)
            && unset.Writes.All(write => write.RequestedValue is null && write.Arguments.Contains("--unset")), "Only explicit null performs scoped native unset.");
    }

    private static async Task CredentialResetAsync()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using Fixture f = await Fixture.Create(); string marker = Path.Combine(f.Root, "helper-invocations.txt");
        string helper = "!f() { printf 'called\\n' >> " + Quote(marker) + "; printf 'username=fixture-user\\npassword=fixture-password\\n'; }; f";
        await f.Git("config", "--file", f.Global, "credential.helper", helper);
        GitResult first = await Fill(); Check(first.Success && first.StandardOutput.Contains("username=fixture-user", StringComparison.Ordinal), "The native credential consumer actually invokes the inherited fixture helper.");
        byte[] calls = await File.ReadAllBytesAsync(marker);
        GitToolConfigurationSaveResult reset = await f.Repository.SaveToolConfigurationAsync(await f.Repository.GetToolConfigurationAsync(), [Local(new GitConfigurationFieldEdit(GitConfigurationField.CredentialHelper, ""))]);
        Check(reset.After!.GetScope(GitToolConfigurationView.Effective).GetSetting(GitConfigurationField.CredentialHelper).Occurrences.Select(entry => entry.Value).SequenceEqual([helper, ""]),
            "An empty local helper is retained after the inherited global helper in native precedence order.");
        GitResult suppressed = await Fill(); Check(!suppressed.Success && calls.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(marker)), "The native credential consumer honors the explicit empty reset and does not execute the inherited helper.");
        GitToolConfigurationSaveResult unset = await f.Repository.SaveToolConfigurationAsync(reset.After!, [Local(new GitConfigurationFieldEdit(GitConfigurationField.CredentialHelper, null))]);
        Check((await Fill()).Success && (await File.ReadAllBytesAsync(marker)).Length > calls.Length
            && unset.After!.GetScope(GitToolConfigurationView.Local).GetSetting(GitConfigurationField.CredentialHelper).Occurrences.Count == 0,
            "Unsetting the local reset restores the existing inherited helper without changing Global.");
        Task<GitResult> Fill() => new GitRunner().RunAsync(f.Repository.Path, ["credential", "fill"], standardInput: "protocol=https\nhost=fixture.invalid\n\n",
            environment: new Dictionary<string, string?> { ["GIT_ASKPASS"] = "/bin/false", ["SSH_ASKPASS"] = "/bin/false", ["DISPLAY"] = null, ["WAYLAND_DISPLAY"] = null });
    }

    private static async Task GuardsAsync()
    {
        using Fixture f = await Fixture.Create(); string included = Path.Combine(f.Root, "guarded.conf");
        await File.WriteAllTextAsync(included, "# empty guarded source\n"); await f.Git("config", "include.path", included);
        GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync(); await File.AppendAllTextAsync(included, "# external comment\n");
        GitToolConfigurationSaveException stale = await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(snapshot, Mixed("stale")));
        Check(stale.Result.MutationStarted == false && stale.Result.Writes.Count == 0, "Even a comment-only include change rejects the entire field/tool batch.");
        snapshot = await f.Repository.GetToolConfigurationAsync(); Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "never");
        try
        {
            GitToolConfigurationSaveException changed = await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(snapshot, Mixed("context")));
            Check(changed.Result.MutationStarted == false, "Common fields use the same guarded configuration environment.");
        }
        finally { Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always"); }
        if (!OperatingSystem.IsWindows())
        {
            string first = Path.Combine(f.Root, "first.conf"), second = Path.Combine(f.Root, "second.conf");
            File.Copy(f.Config, first); File.Copy(f.Config, second); File.Delete(f.Config); File.CreateSymbolicLink(f.Config, first);
            snapshot = await f.Repository.GetToolConfigurationAsync(); File.Delete(f.Config); File.CreateSymbolicLink(f.Config, second);
            GitToolConfigurationSaveException retargeted = await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(snapshot, Mixed("retarget")));
            Check(retargeted.Result.MutationStarted == false && (await File.ReadAllBytesAsync(first)).AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(second)),
                "Identical-byte target retargeting cannot bypass a common-field physical guard.");
        }
        static GitToolConfigurationScopeEdit[] Mixed(string value) => [new(GitConfigScope.Local, [new(GitToolRole.Diff, value, true)], [new(GitConfigurationField.Editor, value)])];
    }

    private static async Task SharedAsync()
    {
        using Fixture f = await Fixture.Create(); GitRepository other = new(f.Repository.Path);
        GitToolConfigurationSnapshot common = await f.Repository.GetToolConfigurationAsync(), tools = await other.GetToolConfigurationAsync();
        Task<bool> first = Save(f.Repository, common, new(GitConfigScope.Global, [], [new(GitConfigurationField.Editor, "common-winner")]));
        Task<bool> second = Save(other, tools, new(GitConfigScope.Global, [new(GitToolRole.Diff, "tool-winner", true)]));
        bool[] successes = await Task.WhenAll(first, second); GitToolConfigurationScopeSnapshot global = (await f.Repository.GetToolConfigurationAsync()).GetScope(GitToolConfigurationView.Global);
        Check(successes.Count(success => success) == 1 && global.GetSetting(GitConfigurationField.Editor).Occurrences.Count + global.SelectedDiffGui.Occurrences.Count == 1,
            "Old tool and new common-field saves share the same native-target semaphore and refuse the stale competitor.");
        static async Task<bool> Save(GitRepository owner, GitToolConfigurationSnapshot snapshot, GitToolConfigurationScopeEdit edit)
        {
            try { return (await owner.SaveToolConfigurationAsync(snapshot, [edit])).Success; }
            catch (GitToolConfigurationSaveException error) { Check(error.Result.MutationStarted == false && !error.Result.MayHaveChanged, "The losing mixed-API save never starts a native writer."); return false; }
        }
    }

    private static async Task PartialAsync()
    {
        using Fixture f = await Fixture.Create(); GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync();
        await File.WriteAllTextAsync(f.Global + ".lock", "foreign-lock\n");
        GitToolConfigurationSaveException error = await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(snapshot,
        [new(GitConfigScope.Local, [new(GitToolRole.Diff, "first-tool", true)], [new(GitConfigurationField.Editor, "first-editor")]),
         new(GitConfigScope.Global, [], [new(GitConfigurationField.CommitTemplate, "blocked-template")])]));
        Check(error.Result.MutationStarted == true && error.Result.MayHaveChanged && !error.Result.Success && error.Result.Writes.Count == 3
            && error.Result.Writes.Take(2).All(write => write.Outcome == GitToolConfigurationWriteOutcome.NativeCompleted && write.NativeResult?.Success == true)
            && error.Result.Writes[2].Outcome == GitToolConfigurationWriteOutcome.NativeFailed && error.Result.Writes[2].NativeResult is { Success: false },
            "The mixed batch retains each successful native prefix and the actual later failed native result.");
        Check(error.Result.After!.GetScope(GitToolConfigurationView.Local).SelectedDiffGui.Occurrences.Single().Value == "first-tool"
            && error.Result.After.GetScope(GitToolConfigurationView.Local).GetSetting(GitConfigurationField.Editor).Occurrences.Single().Value == "first-editor"
            && !File.Exists(f.Global) && await File.ReadAllTextAsync(f.Global + ".lock") == "foreign-lock\n", "Readback reports the partial publication without rollback or removal of a foreign Git lock.");
    }

    private static async Task CancellationAsync()
    {
        using Fixture f = await Fixture.Create(); GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync();
        using (CancellationTokenSource pre = new())
        {
            pre.Cancel(); GitToolConfigurationSaveCanceledException error = await Expect<GitToolConfigurationSaveCanceledException>(() => f.Repository.SaveToolConfigurationAsync(snapshot, [Local(new GitConfigurationFieldEdit(GitConfigurationField.Editor, "never"))], pre.Token));
            Check(error.Result.MutationStarted == false && !error.Result.MayHaveChanged, "Pre-cancellation cannot publish a common-field change.");
        }
        if (!OperatingSystem.IsLinux()) { return; }
        string marker = Path.Combine(f.Root, "common-writer.json"), journal = Path.Combine(f.Root, "common-writer.jsonl");
        GitRepository wrapped = await f.Wrapped("import subprocess,time\nif 'config' in a and '--list' not in a and '--' in a:\n"
            + " r=subprocess.run(['/usr/bin/git']+a)\n m=" + JsonSerializer.Serialize(marker) + ";t=m+'.tmp'\n"
            + " with open(t,'w') as f:json.dump({'pid':os.getpid(),'gitExit':r.returncode},f)\n os.replace(t,m)\n while True:time.sleep(.05)\n", journal);
        snapshot = await wrapped.GetToolConfigurationAsync(); using CancellationTokenSource cancel = new();
        Task<GitToolConfigurationSaveResult> operation = wrapped.SaveToolConfigurationAsync(snapshot, [Local(new GitConfigurationFieldEdit(GitConfigurationField.CommitTemplate, "published-before-cancel"))], cancel.Token);
        int pid = 0, lines = 0; Exception? observationFailure = null;
        try
        {
            DateTime until = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(marker))
            {
                if (operation.IsCompleted) { await operation; }
                if (DateTime.UtcNow > until) { throw new TimeoutException("The actual common-field native writer did not publish its witness."); }
                await Task.Delay(15);
            }
            using JsonDocument record = JsonDocument.Parse(await File.ReadAllTextAsync(marker)); pid = record.RootElement.GetProperty("pid").GetInt32();
            Check(record.RootElement.GetProperty("gitExit").GetInt32() == 0 && Directory.Exists("/proc/" + pid), "The real Git writer exited successfully while its owned witnessed wrapper remains live.");
            lines = File.ReadAllLines(journal).Length;
        }
        catch (Exception exception) { observationFailure = exception; }
        finally { cancel.Cancel(); }
        GitToolConfigurationSaveCanceledException cancelled = await Expect<GitToolConfigurationSaveCanceledException>(async () => await operation);
        if (observationFailure is not null) { throw new InvalidOperationException("Common native writer observation failed after the owned operation drained.", observationFailure); }
        Check(cancelled.Result.Cancelled && cancelled.Result.MutationStarted is null && cancelled.Result.MayHaveChanged && cancelled.Result.After is null
            && cancelled.Result.Writes.Single().Outcome == GitToolConfigurationWriteOutcome.Unknown && !Directory.Exists("/proc/" + pid)
            && File.ReadAllLines(journal).Length == lines, "Cancellation drains the actual process, retains uncertain outcome, and starts no post-cancel query.");
        Check((await f.Git("config", "--get", "commit.template")).StandardOutput.TrimEnd('\n') == "published-before-cancel",
            "A later independent native observer sees the published common-field effect without rollback.");
    }

    private static GitToolConfigurationScopeEdit Local(params GitConfigurationFieldEdit[] settings) => new(GitConfigScope.Local, [], settings);
    private static string Key(GitConfigurationField field) => Keys[(int)field];
    private static string Value(GitConfigurationField field, string label) => field == GitConfigurationField.AutoCrlf
        ? label is "first" or "included" ? "false" : label == "changed" ? "true" : "input" : label + "-" + Key(field);
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> Expect<T>(Func<Task> operation) where T : Exception
    { try { await operation(); } catch (T exception) { return exception; } throw new InvalidOperationException("Expected " + typeof(T).Name + "."); }

    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<string, string?> _environment;
        private Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "gitextensions-config-fields-" + Guid.NewGuid().ToString("N"));
            string home = Path.Combine(Root, "home"), xdg = Path.Combine(Root, "xdg"); Directory.CreateDirectory(home); Directory.CreateDirectory(xdg);
            Global = Path.Combine(home, ".gitconfig");
            _environment = Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(IsPrivateVariable).ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
            foreach (string name in _environment.Keys) { Environment.SetEnvironmentVariable(name, null); }
            Environment.SetEnvironmentVariable("HOME", home); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdg);
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Global); Environment.SetEnvironmentVariable("GIT_CONFIG_SYSTEM", Path.Combine(Root, "system-config"));
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1"); Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "protocol.file.allow"); Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");
        }
        public string Root { get; }
        public string Global { get; }
        public GitRepository Repository { get; private set; } = null!;
        public string Config => File(".git/config");
        public string File(string relative) => Path.Combine(Repository.Path, relative);
        public static async Task<Fixture> Create()
        {
            Fixture fixture = new();
            try
            {
                fixture.Repository = await GitRepository.InitAsync(Path.Combine(fixture.Root, "repository"));
                await RepositoryFixture.ConfigureAsync(fixture.Repository);
                await System.IO.File.WriteAllTextAsync(fixture.File("seed.txt"), "seed\n"); await fixture.Repository.StageAllAsync(); await fixture.Repository.CommitAsync("Initial");
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        public async Task<GitResult> Git(params string[] arguments)
        {
            GitResult result = await new GitRunner().RunAsync(Repository.Path, arguments);
            if (!result.Success) { throw new GitCommandException(arguments, result); } return result;
        }
        public async Task<GitRepository> Wrapped(string body, string journal)
        {
            string wrapper = Path.Combine(Root, "git-wrapper-" + Guid.NewGuid().ToString("N"));
            await System.IO.File.WriteAllTextAsync(wrapper, "#!/usr/bin/python3\nimport json,os,sys\na=sys.argv[1:]\nwith open(" + JsonSerializer.Serialize(journal) + ",'a') as f:f.write(json.dumps(a)+'\\n')\n" + body + "os.execv('/usr/bin/git',['git']+a)\n");
            if (!OperatingSystem.IsWindows()) { System.IO.File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            return new(Repository.Path, new GitRunner(wrapper));
        }
        private static bool IsPrivateVariable(string name) => name.StartsWith("GIT_CONFIG", StringComparison.Ordinal) || name is "HOME" or "XDG_CONFIG_HOME";
        public void Dispose()
        {
            foreach (string name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(IsPrivateVariable).ToArray()) { Environment.SetEnvironmentVariable(name, null); }
            foreach ((string name, string? value) in _environment) { Environment.SetEnvironmentVariable(name, value); }
            if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); }
        }
    }
}
