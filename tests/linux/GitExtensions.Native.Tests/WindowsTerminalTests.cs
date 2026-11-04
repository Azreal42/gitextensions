using System.Runtime.Versioning;
using System.Text.Json;
using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Native.Tests;

[Category("WindowsTerminal"), NonParallelizable, Platform("Win")]
[SupportedOSPlatform("windows")]
public sealed class WindowsTerminalTests
{
    [TestCase("exit", 0)]
    [TestCase("exit", 73)]
    [TestCase("cancel", 0)]
    public async Task OwnedConsolePreservesActualHandlesArgumentsInputExitAndRestoresItsModes(string scenario, int exit)
    {
        string root = Path.Combine(Path.GetTempPath(), "native windows console ' λ " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string executable = NativeProcessOwnershipTests.Executable;
            string[] literal = ["", "spaces ' λ $(literal)", "quote\"", "line\nbreak", "back\\slash\\"];
            GitExecutionEnvironment environment = GitExecutionEnvironment.Capture(new() { Executable = executable });
            await using WindowsConsoleHost host = WindowsConsoleHost.Start(executable,
                ["--native-process-probe", "windows-console-controller", root, executable, scenario,
                    exit.ToString(System.Globalization.CultureInfo.InvariantCulture), .. literal], root, environment);
            await Task.WhenAll(host.WaitForRootExitAsync(), host.WaitForEmptyAsync()).WaitAsync(TimeSpan.FromSeconds(40));
            string error = Path.Combine(root, "console-error.json");
            Assert.That(host.ExitCode, Is.Zero, File.Exists(error) ? await File.ReadAllTextAsync(error) : "The real console controller did not complete.");
            using JsonDocument ready = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "console-ready.json")));
            Assert.That(ready.RootElement.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()), Is.EqualTo(literal));
            Assert.That(ready.RootElement.GetProperty("value").GetString(), Is.EqualTo("captured console λ '$()"));
            Assert.That(ready.RootElement.GetProperty("cwd").GetString(), Is.EqualTo(root).IgnoreCase);
            Assert.That(ready.RootElement.GetProperty("modes").GetArrayLength(), Is.EqualTo(3));
            using JsonDocument result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "console-result.json")));
            JsonElement value = result.RootElement;
            Assert.That(value.GetProperty("actualChildExited").GetBoolean(), Is.True);
            Assert.That(value.GetProperty("after").EnumerateArray().Select(x => x.GetUInt32()),
                Is.EqualTo(value.GetProperty("before").EnumerateArray().Select(x => x.GetUInt32())));
            Assert.That(value.GetProperty("inputAfter").GetUInt32(), Is.EqualTo(value.GetProperty("inputBefore").GetUInt32()));
            Assert.That(value.GetProperty("outputAfter").GetUInt32(), Is.EqualTo(value.GetProperty("outputBefore").GetUInt32()));
            Assert.That(value.GetProperty("cancelled").GetBoolean(), Is.EqualTo(scenario == "cancel"));
            if (scenario == "exit")
            {
                Assert.That(value.GetProperty("exit").GetInt32(), Is.EqualTo(exit));
                using JsonDocument input = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "console-input.json")));
                Assert.That(input.RootElement.GetProperty("line").GetString(), Is.EqualTo("one ' λ [*] literal"));
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
