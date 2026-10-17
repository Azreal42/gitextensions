using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux;
using GitExtensions.Linux.Scripts;

internal static class ScriptRuntimeScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("script argv grammar separates raw syntax and exact opaque values", GrammarAsync),
        ("script argv null empty whitespace and incomplete syntax have explicit outcomes", InvalidAsync),
        ("script argv files providers arrays and prefix suffix retain exact value boundaries", ArraysAsync),
        ("script argv prompts and context snapshot are prepared once for preview and execution", PromptOnceAsync),
        ("script argv preserves the 17 ADR0057 value classes through a real native child", LiteralTransportAsync),
        ("script native argv distinguishes simple and double sHashes UserFiles and selected paths", CompositeTransportAsync),
        ("script original VS Code default keeps one shell program and exact one or two file arguments", VsCodeDefaultAsync),
        ("script explicit shell inline values preserve all 17 ADR0057 classes and outside argv", ShellInlineTransportAsync),
        ("script neutral runner inherits environment and captures actual cwd and separate raw streams", NeutralEnvironmentAsync),
        ("script neutral runner returns the native nonzero exit after both pipes drain", NonzeroAsync),
        ("script failed exec emits no successful started callback", StartFailureAsync),
        ("script started callback failure stops the owned process and preserves the original error", StartedFailureAsync),
        ("script cancellation captures a relay child with closed pipes before leader SIGINT exit", CancellationAsync),
    ];

    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static ScriptDefinition Script(string? arguments) => new() { Command = "/bin/sh", Arguments = arguments, Name = "&Private runtime" };
    private static ScriptContext Context(string directory = "") => new()
    {
        WorkingDir = directory, HasRevisionGrid = true, SelectedRevision = new("b"), SelectedRevisions = [new("a"), new("b")]
    };
    private static ScriptExecutionPlan Plan(ScriptExpansionResult result)
    {
        Check(result.Status == ScriptExpansionStatus.Completed, "Preparation failed: " + result.Status + "/" + result.Error + "/" + result.Option + " " + result.Diagnostic);
        return result.ExecutionPlan ?? throw new InvalidOperationException("Completed preparation did not produce an execution plan.");
    }
    private static void Equal(IReadOnlyList<string> actual, params string[] expected)
        => Check(actual.SequenceEqual(expected), "Actual argv: " + JsonSerializer.Serialize(actual) + "; expected: " + JsonSerializer.Serialize(expected));

    private static async Task GrammarAsync()
    {
        ScriptContext context = Context() with { AdditionalOptions = [new("x", ["a b\"'\\\n$HOME"]) ] };
        ScriptExecutionPlan plan = Plan(await ScriptTemplate.PrepareAsync(Script("one 'two three' \"four five\" six\\ seven pre{{x}}post '' \"\" {unknown}"), context));
        Equal(plan.ArgumentPlan.Arguments, "one", "two three", "four five", "six seven", "prea b\"'\\\n$HOMEpost", "", "", "{unknown}");
        ScriptExecutionPlan raw = Plan(await ScriptTemplate.PrepareAsync(Script("{x}"), Context() with { AdditionalOptions = [new("x", ["'one two' three"]) ] }));
        Equal(raw.ArgumentPlan.Arguments, "one two", "three");
        ScriptExecutionPlan opaque = Plan(await ScriptTemplate.PrepareAsync(Script("{{x}} {RepoName}"), Context() with
        { RepoName = "later", AdditionalOptions = [new("x", ["{RepoName}"])] }));
        Equal(opaque.ArgumentPlan.Arguments, "{RepoName}", "later");
        ScriptExecutionPlan forward = Plan(await ScriptTemplate.PrepareAsync(Script("{{early}} {late}"), Context() with
        { AdditionalOptions = [new("early", ["{late}"]), new("late", ["expanded"])] }));
        Equal(forward.ArgumentPlan.Arguments, "{late}", "expanded");
        Check(forward.ArgumentPlan.Preview == "\"expanded\" expanded", "Legacy forward substitution preview remains separate from opaque argv.");
        foreach (string shell in new[] { "bash", "/bin/sh", "dash", "zsh", "ksh" })
        {
            foreach (string flags in new[] { "-c", "--command", "-lc", "-ec", "-e -c", "-o pipefail -c" })
            {
                ScriptExecutionPlan inline = Plan(await ScriptTemplate.PrepareAsync(Script(flags + " 'printf {{x}}'") with { Command = shell },
                    Context() with { AdditionalOptions = [new("x", ["two words"])] }));
                Check(inline.ArgumentPlan.Arguments[^1] == "printf 'two words'", "Explicit shell selector must keep one quoted program: " + shell + " " + flags);
            }
        }
        ScriptContext x = Context() with { AdditionalOptions = [new("x", ["two words"])] };
        ScriptExecutionPlan nonShell = Plan(await ScriptTemplate.PrepareAsync(Script("-c 'printf {{x}}'") with { Command = "/usr/bin/python3" }, x));
        Equal(nonShell.ArgumentPlan.Arguments, "-c", "printf two words");
        ScriptExecutionPlan ungrouped = Plan(await ScriptTemplate.PrepareAsync(Script("-c printf{{x}}") with { Command = "sh" }, x));
        Equal(ungrouped.ArgumentPlan.Arguments, "-c", "printftwo words");
    }

    private static async Task InvalidAsync()
    {
        foreach (string? value in new string?[] { null, "", " \t\r\n" })
        {
            ScriptExecutionPlan plan = Plan(await ScriptTemplate.PrepareAsync(Script(value), Context()));
            Equal(plan.ArgumentPlan.Arguments); Check(plan.ArgumentPlan.Preview == value, "Preview null/blank distinctions were lost.");
        }
        foreach (string value in new[] { "unterminated'", "\"unterminated", "last\\", "nul\0value" })
        {
            ScriptExpansionResult result = await ScriptTemplate.PrepareAsync(Script(value), Context());
            Check(result.Status == ScriptExpansionStatus.Aborted && result.Error == ScriptExpansionError.InvalidArguments
                && result.ExecutionPlan is null && result.Arguments == value && !string.IsNullOrEmpty(result.Diagnostic), "Invalid syntax must refuse before launch.");
        }
        foreach (string nested in new[] { "-c 'printf \"%s\" \"{{x}}\"'", "-c \"printf '%s' '{{x}}'\"", "-c 'printf \\{{x}}'" })
        {
            ScriptExpansionResult result = await ScriptTemplate.PrepareAsync(Script(nested) with { Command = "sh" },
                Context() with { AdditionalOptions = [new("x", ["literal"])] });
            Check(result.Status == ScriptExpansionStatus.Aborted && result.Error == ScriptExpansionError.InvalidArguments
                && result.ExecutionPlan is null && result.Diagnostic?.Contains("shell program quotes or escapes", StringComparison.Ordinal) == true,
                "Nested shell quoting or an escaped inline marker must refuse explicitly, without guessing.");
        }
        foreach (ScriptDefinition textTarget in new[] { Script("unfinished'") with { IsPowerShell = true }, Script("unfinished'") with { Command = "{plugin.SomePlugin}" } })
        {
            ScriptExecutionPlan plan = Plan(await ScriptTemplate.PrepareAsync(textTarget, Context()));
            Equal(plan.ArgumentPlan.Arguments); Check(plan.ArgumentPlan.Preview == "unfinished'", "Explicit text target must retain the preview without claiming argv.");
        }
    }

    private static async Task ArraysAsync()
    {
        string[] raw = ["a\"b\\", "second path"];
        ScriptContext context = Context() with { SelectedRelativePaths = raw,
            AdditionalOptions = [new("items", ["old display one", "old display two"]) { RawValues = ["one space", "tail\\\n"] }, new("none", [])] };
        ScriptExecutionPlan plan = Plan(await ScriptTemplate.PrepareAsync(Script("pre{{items}}post {{SelectedRelativePaths}} {{none}} x{none}y"), context));
        Equal(plan.ArgumentPlan.Arguments, "preone space", "tail\\\npost", raw[0], raw[1], "xy");
        ScriptExecutionPlan folder = Plan(await ScriptTemplate.PrepareAsync(Script("{{SelectedRelativePaths}}"), context with { SelectedFolder = " folder/ " }));
        Equal(folder.ArgumentPlan.Arguments, " folder/ ");
    }

    private static async Task PromptOnceAsync()
    {
        string[] exact = ["original \"value\"\\"];
        ScriptContext context = Context() with { CurrentRevision = new("current"), AdditionalOptions = [new("custom", ["display"]) { RawValues = exact }] };
        int calls = 0;
        ScriptExpansionResult result = await ScriptTemplate.PrepareAsync(Script("{{UserInput:label=default}} {{custom}} {UserInput:label}"), context,
            (request, _) =>
            {
                Check(request.DefaultValue == "default", "Original literal default."); calls++; exact[0] = "mutated";
                return Task.FromResult(new ScriptReply(ScriptReplyKind.Accepted, "answer with spaces"));
            });
        Equal(Plan(result).ArgumentPlan.Arguments, "answer with spaces", "original \"value\"\\", "answer", "with", "spaces");
        Check(calls == 1, "A single preparation must service the labeled input exactly once.");
    }

    private static async Task<ScriptProcessResult> EchoAsync(string directory, IReadOnlyList<string> arguments)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        return await new ScriptProcessRunner().RunAsync("/bin/sh", directory,
            ["-c", "if [ \"$#\" -gt 0 ]; then printf '%s\\0' \"$@\"; fi", "private-argv-witness", .. arguments], timeout.Token);
    }
    private static void EchoEquals(ScriptProcessResult result, IReadOnlyList<string> expected)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join('\0', expected) + (expected.Count > 0 ? "\0" : ""));
        Check(result.Success && result.StandardErrorBytes.Length == 0 && result.StandardOutputBytes.SequenceEqual(bytes), "Native argv bytes differ.");
    }

    private static async Task LiteralTransportAsync()
    {
        using Fixture fixture = new(); string marker = Path.Combine(fixture.Path, "shell-must-not-run");
        string[] values = LiteralValues(marker);
        ScriptExecutionPlan plan = Plan(await ScriptTemplate.PrepareAsync(Script("{{values}}"), Context(fixture.Path) with { AdditionalOptions = [new("values", values)] }));
        Equal(plan.ArgumentPlan.Arguments, values);
        EchoEquals(await EchoAsync(fixture.Path, plan.ArgumentPlan.Arguments), values);
        Check(!File.Exists(marker), "Opaque metacharacters must remain data to the actual child.");
    }

    private static string[] LiteralValues(string marker) =>
        ["", "plain", "a b", "  exact  ", "a\tb", "café Ω 日本 😀", "first\nsecond", "first\r\nsecond", "a\"b",
            "a\\\"b", "a\\\\\"b", "a\\\\\\\"b", "tail\\", "tail\\\\", "tail\\\\\\", "a'b'c",
            "$HOME $(touch " + marker + ") `touch " + marker + "` ; && | > * ? [x] # !"];

    private static async Task VsCodeDefaultAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The original VS Code default requires a native POSIX shell."); }
        using Fixture fixture = new(); string bin = Path.Combine(fixture.Path, "bin"); Directory.CreateDirectory(bin);
        string code = Path.Combine(bin, "code");
        await File.WriteAllTextAsync(code, """
            #!/usr/bin/python3
            import os, sys
            for value in sys.argv[1:]:
                data=value.encode('utf-8')+b'\0'
                while data:
                    count=os.write(1,data)
                    data=data[count:]
            """);
        File.SetUnixFileMode(code, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string first = "one space \"quote\".txt", second = "two spaces.txt";
        await File.WriteAllTextAsync(Path.Combine(fixture.Path, first), "actual existing first file\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Path, second), "actual existing second file\n");
        ScriptDefinition original = ScriptConfiguration.GetDefaults().Single(script => script.HotkeyCommandIdentifier == 9005);
        foreach (string[] paths in new[] { new[] { first }, new[] { first, second } })
        {
            ScriptContext context = Context(fixture.Path) with { SelectedRelativePaths = paths, LineNumber = 7, ColumnNumber = 3 };
            ScriptExpansionResult prepared = await ScriptTemplate.PrepareAsync(original, context);
            ScriptExecutionPlan plan = Plan(prepared);
            Check(plan.Command == "bash" && plan.ArgumentPlan.Arguments.Count == 2 && plan.ArgumentPlan.Arguments[0] == "-c"
                && plan.RunInBackground == original.RunInBackground && plan.ArgumentPlan.Preview == (await ScriptTemplate.ExpandAsync(original.Arguments, context)).Arguments,
                "The unchanged original model must retain its preview and one complete -c program.");
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            ScriptProcessResult result = await new ScriptProcessRunner().RunAsync(plan.Command, fixture.Path, plan.ArgumentPlan.Arguments, timeout.Token,
                environment: new Dictionary<string, string?> { ["PATH"] = bin + Path.PathSeparator + "/usr/bin:/bin" });
            EchoEquals(result, paths.Length == 1 ? [".", "--goto", first + ":7:3"] : [".", "--goto", first, second]);
        }
    }

    private static async Task ShellInlineTransportAsync()
    {
        using Fixture fixture = new(); string marker = Path.Combine(fixture.Path, "inline-shell-must-not-run"); string[] values = LiteralValues(marker);
        ScriptContext context = Context(fixture.Path) with { AdditionalOptions = [new("values", values)] };
        ScriptExecutionPlan inline = Plan(await ScriptTemplate.PrepareAsync(Script("-c 'printf \"%s\\\\000\" {{values}}'") with { Command = "/bin/sh" }, context));
        Check(inline.ArgumentPlan.Arguments.Count == 2, "Inline opaque values must remain within the single shell program, not become $0/$1.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        EchoEquals(await new ScriptProcessRunner().RunAsync(inline.Command, fixture.Path, inline.ArgumentPlan.Arguments, timeout.Token), values);
        ScriptExecutionPlan outside = Plan(await ScriptTemplate.PrepareAsync(Script("-c 'printf \"%s\\\\000\" \"$@\"' private-zero {{values}}") with { Command = "/bin/sh" }, context));
        Equal(outside.ArgumentPlan.Arguments.Skip(3).ToArray(), values);
        EchoEquals(await new ScriptProcessRunner().RunAsync(outside.Command, fixture.Path, outside.ArgumentPlan.Arguments, timeout.Token), values);
        string program = "printf '%s\\000' \"$1\"";
        ScriptExecutionPlan opaqueProgram = Plan(await ScriptTemplate.PrepareAsync(Script("-c {{program}} private-zero {{value}}") with { Command = "/bin/sh" },
            Context(fixture.Path) with { AdditionalOptions = [new("program", [program]), new("value", ["literal ' \" $HOME \\"])] }));
        Equal(opaqueProgram.ArgumentPlan.Arguments, "-c", program, "private-zero", "literal ' \" $HOME \\");
        EchoEquals(await new ScriptProcessRunner().RunAsync(opaqueProgram.Command, fixture.Path, opaqueProgram.ArgumentPlan.Arguments, timeout.Token), ["literal ' \" $HOME \\"]);
        Check(!File.Exists(marker), "All 17 inline values must remain literal to printf, including shell metacharacters.");
    }

    private static async Task CompositeTransportAsync()
    {
        using Fixture fixture = new();
        ScriptContext context = Context(fixture.Path) with { SelectedRelativePaths = ["space file", "quote\"\\file"] };
        ScriptExecutionPlan plan = Plan(await ScriptTemplate.PrepareAsync(Script("{sHashes} {{sHashes}} {UserFiles} {{UserFiles}} {SelectedRelativePaths} {{SelectedRelativePaths}}"), context,
            (_, _) => Task.FromResult(new ScriptReply(ScriptReplyKind.Accepted, "\"file one\" \"file two\""))));
        string[] expected = ["a", "b", "a b", "file one", "file two", "\"file one\" \"file two\"", "space", "file", "quote\"\\file", "space file", "quote\"\\file"];
        Equal(plan.ArgumentPlan.Arguments, expected); EchoEquals(await EchoAsync(fixture.Path, plan.ArgumentPlan.Arguments), expected);
    }

    private static async Task NeutralEnvironmentAsync()
    {
        using Fixture fixture = new(); string[] keys = ["GIT_TERMINAL_PROMPT", "GIT_PAGER", "GIT_EDITOR", "GIT_SEQUENCE_EDITOR", "LC_ALL", "GIT_ASKPASS", "SSH_ASKPASS"];
        string program = "import json,os,sys; os.write(1,json.dumps([os.getcwd(),{k:os.environ.get(k) for k in sys.argv[1:]}]).encode()); os.write(2,b'\\xffnative-error\\n')";
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        ScriptProcessResult result = await new ScriptProcessRunner().RunAsync("/usr/bin/python3", fixture.Path, ["-c", program, .. keys], timeout.Token);
        Check(result.Success && result.StandardErrorBytes.SequenceEqual(new byte[] { 255 }.Concat(Encoding.UTF8.GetBytes("native-error\n"))), "Real native stderr bytes.");
        using JsonDocument json = JsonDocument.Parse(result.StandardOutputBytes);
        Check(json.RootElement[0].GetString() == fixture.Path, "Actual child cwd.");
        foreach (string key in keys) { Check(json.RootElement[1].GetProperty(key).GetString() == Environment.GetEnvironmentVariable(key), "Neutral runner changed inherited " + key); }
    }

    private static async Task NonzeroAsync()
    {
        using Fixture fixture = new(); using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)); int starts = 0;
        List<byte> output = [], error = []; object gate = new();
        ScriptProcessResult result = await new ScriptProcessRunner().RunAsync("/bin/sh", fixture.Path, ["-c", "printf out; printf err >&2; exit 7"], timeout.Token,
            outputReceived: chunk => { lock (gate) { (chunk.Stream == GitProcessOutputStream.StandardOutput ? output : error).AddRange(chunk.Bytes.ToArray()); } },
            processStarted: started => { Check(started.ProcessId > 0, "Actual started PID."); starts++; });
        Check(!result.Success && result.ExitCode == 7 && starts == 1 && Encoding.UTF8.GetString(result.StandardOutputBytes) == "out"
            && Encoding.UTF8.GetString(result.StandardErrorBytes) == "err" && output.SequenceEqual(result.StandardOutputBytes) && error.SequenceEqual(result.StandardErrorBytes),
            "Native nonzero must retain separate actual streams, their callback bytes and one real start.");
    }

    private static async Task StartFailureAsync()
    {
        using Fixture fixture = new(); using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)); int starts = 0;
        try
        {
            await new ScriptProcessRunner().RunAsync(Path.Combine(fixture.Path, "missing-executable"), fixture.Path, [], timeout.Token,
                processStarted: _ => starts++);
            throw new InvalidOperationException("Missing executable succeeded.");
        }
        catch (Win32Exception) { Check(starts == 0, "Failed native exec must not publish a successful start."); }
    }

    private static async Task StartedFailureAsync()
    {
        using Fixture fixture = new(); using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)); int pid = 0;
        InvalidOperationException original = new("private-start-observer-failure");
        try
        {
            await new ScriptProcessRunner().RunAsync("/bin/sh", fixture.Path, ["-c", "while :; do sleep 1; done"], timeout.Token,
                processStarted: started => { pid = started.ProcessId; throw original; });
            throw new InvalidOperationException("Observer failure was ignored.");
        }
        catch (InvalidOperationException error) when (ReferenceEquals(error, original)) { }
        Check(pid > 0 && !Live(pid), "Callback failure must stop and wait its owned native child.");
    }

    private static async Task CancellationAsync()
    {
        using Fixture fixture = new();
        string script = Path.Combine(fixture.Path, "relay.py");
        await File.WriteAllTextAsync(script, """
            import os, pathlib, signal, subprocess, sys, time
            root=pathlib.Path(sys.argv[1])
            def publish(name, text):
                temporary=root/(name+'.tmp')
                temporary.write_text(text)
                temporary.replace(root/name)
            def stat(pid): return pathlib.Path('/proc/'+str(pid)+'/stat').read_text()
            if len(sys.argv)>2:
                def stop(number, frame):
                    publish('child-signal', str(number))
                    raise SystemExit(0)
                signal.signal(signal.SIGINT, stop)
                publish('child-ready', stat(os.getpid()))
                while True: time.sleep(.01)
            publish('parent-ready', stat(os.getpid()))
            def interrupted(number, frame):
                publish('parent-signal', str(number))
                raise SystemExit(130)
            signal.signal(signal.SIGINT, interrupted)
            child=subprocess.Popen([sys.executable,__file__,str(root),'child'],stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
            publish('child-born', stat(child.pid))
            while True: time.sleep(.01)
            """);
        using CancellationTokenSource cancellation = new(); int parent = 0; Actor? leader = null, child = null;
        Task<ScriptProcessResult> run = new ScriptProcessRunner().RunAsync("/usr/bin/python3", fixture.Path, [script, fixture.Path], cancellation.Token,
            processStarted: started => parent = started.ProcessId);
        try
        {
            await UntilAsync(() => parent > 0 && File.Exists(Path.Combine(fixture.Path, "child-ready")), TimeSpan.FromSeconds(5));
            leader = Identity(File.ReadAllText(Path.Combine(fixture.Path, "parent-ready")));
            child = Identity(File.ReadAllText(Path.Combine(fixture.Path, "child-ready")));
            Check(leader.Value.Pid == parent && leader.Value.Parent == Environment.ProcessId && leader.Value.Group == parent && leader.Value.Session == parent
                && child.Value.Parent == parent && child.Value.Group == parent && child.Value.Session == parent
                && child.Value.Started >= leader.Value.Started && Live(leader.Value) && Live(child.Value) && !run.IsCompleted,
                "Actual private leader and closed-pipe descendant must be pending with witnessed incarnations.");
            cancellation.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Cancellation became a successful result."); }
            catch (OperationCanceledException) { }
            Check(File.Exists(Path.Combine(fixture.Path, "parent-signal")) && File.Exists(Path.Combine(fixture.Path, "child-signal")), "Both native actors received actual cancellation.");
            Check(!Live(leader.Value) && !Live(child.Value), "Cancellation must retire the relay and its closed-pipe descendant.");
        }
        finally
        {
            cancellation.Cancel();
            // Failure cleanup is separate from assertions and can never become a successful cancellation witness.
            if (leader is null && File.Exists(Path.Combine(fixture.Path, "parent-ready"))) { leader = Identity(File.ReadAllText(Path.Combine(fixture.Path, "parent-ready"))); }
            if (child is null && File.Exists(Path.Combine(fixture.Path, "child-born"))) { child = Identity(File.ReadAllText(Path.Combine(fixture.Path, "child-born"))); }
            if (leader is Actor owned && owned.Pid == parent && owned.Parent == Environment.ProcessId && owned.Group == parent && owned.Session == parent
                && child is Actor descendant && descendant.Parent == parent && descendant.Group == parent && descendant.Session == parent && descendant.Started >= owned.Started)
            {
                await CleanupAsync(descendant);
            }
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        }
    }

    private readonly record struct Actor(int Pid, int Parent, int Group, int Session, long Started);
    private static Actor Identity(string stat)
    {
        int name = stat.IndexOf('('), end = stat.LastIndexOf(')');
        string[] fields = stat[(end + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new(int.Parse(stat[..name].Trim(), CultureInfo.InvariantCulture), int.Parse(fields[1], CultureInfo.InvariantCulture),
            int.Parse(fields[2], CultureInfo.InvariantCulture), int.Parse(fields[3], CultureInfo.InvariantCulture), long.Parse(fields[19], CultureInfo.InvariantCulture));
    }
    private static bool Live(Actor expected)
    {
        try
        {
            string stat = File.ReadAllText("/proc/" + expected.Pid + "/stat");
            Actor actual = Identity(stat);
            return actual.Pid == expected.Pid && actual.Started == expected.Started && actual.Group == expected.Group && actual.Session == expected.Session
                && stat[stat.LastIndexOf(')') + 2] is not ('Z' or 'X');
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static bool Live(int pid) => LinuxProcessState.IsRunning(pid);
    private static async Task CleanupAsync(Actor actor)
    {
        if (!Live(actor)) { return; }
        try { using Process process = Process.GetProcessById(actor.Pid); if (Live(actor)) { process.Kill(); } }
        catch (InvalidOperationException) { }
        catch (Win32Exception) when (!Live(actor)) { }
        await UntilAsync(() => !Live(actor), TimeSpan.FromSeconds(3));
    }
    private static async Task UntilAsync(Func<bool> ready, TimeSpan bound)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            if (elapsed.Elapsed >= bound) { throw new TimeoutException("Private script fixture did not become ready."); }
            await Task.Delay(10);
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-script-runtime-" + Guid.NewGuid().ToString("N"));
        internal Fixture()
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Native script transport tests require Linux."); }
            Directory.CreateDirectory(Path); File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public void Dispose() { /* Preserve private fixture evidence on success and failure. */ }
    }
}
