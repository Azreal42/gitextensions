using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

internal sealed class DeleteUnusedBranchesUiFixture : IAsyncDisposable
{
    internal ReferenceDialogTests.ReferenceFixture Git { get; }
    internal GitRepository Repository => Git.Repository;
    internal string Old { get; private set; } = "";
    internal string Recent { get; private set; } = "";
    internal string Side { get; private set; } = "";
    internal string? Bare { get; private set; }
    internal Window Owner { get; } = new() { Width = 1000, Height = 740 };
    internal DeleteUnusedBranchesDialog Dialog { get; private set; } = null!;
    private readonly CultureInfo _culture, _uiCulture;
    private Task? _modal;
    private readonly List<string> _release = [];
    private DeleteUnusedBranchesUiFixture(ReferenceDialogTests.ReferenceFixture git, CultureInfo culture, CultureInfo uiCulture)
    { Git = git; _culture = culture; _uiCulture = uiCulture; }

    internal static async Task<DeleteUnusedBranchesUiFixture> Create(bool remote = false)
    {
        CultureInfo culture = CultureInfo.CurrentCulture, uiCulture = CultureInfo.CurrentUICulture;
        ReferenceDialogTests.ReferenceFixture git = await ReferenceDialogTests.ReferenceFixture.CreateAsync(unborn: true);
        DeleteUnusedBranchesUiFixture f = new(git, culture, uiCulture);
        git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0");
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            f.Date(now.AddDays(-60)); await git.ChangeAsync("old\n", "Old merged tip");
            f.Old = await f.Revision("HEAD");
            await git.GitAsync("branch", "old/one"); await git.GitAsync("branch", "old/two");
            f.Date(now.AddDays(-1)); await git.ChangeAsync("recent\n", "Recent main tip");
            f.Recent = await f.Revision("HEAD"); await git.GitAsync("branch", "recent/topic");
            string tree = await f.Revision("HEAD^{tree}");
            f.Side = (await git.GitAsync("commit-tree", tree, "-p", f.Old, "-m", "Unmerged sibling")).StandardOutput.Trim();
            await git.GitAsync("branch", "unmerged/topic", f.Side);
            if (remote)
            {
                f.Bare = await git.CreateRemoteAsync();
                await git.GitAsync("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
            }
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
    private void Date(DateTimeOffset value)
    {
        string date = value.ToString("O", CultureInfo.InvariantCulture);
        Git.SetEnvironment("GIT_AUTHOR_DATE", date); Git.SetEnvironment("GIT_COMMITTER_DATE", date);
    }
    internal async Task<string> Revision(string name) => (await Git.GitAsync("rev-parse", name)).StandardOutput.Trim();
    internal Dictionary<string, string?> Values(bool remote = false) => new()
    {
        ["mergedInto"] = "HEAD", ["olderThanDays"] = "30", ["remoteBranches"] = remote.ToString(), ["remote"] = "origin",
        ["includeUnmerged"] = "false", ["useRegex"] = "false", ["pattern"] = "/(feature|develop)/", ["ignoreCase"] = "false", ["invertPattern"] = "false"
    };
    internal async Task<DeleteUnusedBranchesDialog> Show(bool remote = false, string language = "en", bool dark = false,
        IReadOnlyDictionary<string, string?>? values = null, GitRepository? repository = null,
        Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task>? apply = null, bool initialize = true)
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        Dialog = new(repository ?? Repository, values ?? Values(remote), apply) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        Owner.Show(); _modal = Dialog.ShowDialog(Owner);
        if (initialize) { await Dialog.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        Pump(); return Dialog;
    }
    internal T Field<T>(string name) where T : Control => Field<T>(Dialog, name);
    internal static T Field<T>(Window owner, string name) where T : Control
        => owner.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    internal void Click(string name) => Pointer(Field<Control>(name));
    internal static void Pointer(Control item) => PushProcessTestDriver.Pointer(item);
    internal void Pump()
    { Dispatcher.UIThread.RunJobs(); if (Dialog is { IsVisible: true }) Dialog.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    internal async Task Until(Func<bool> ready, string reason = "The real delete-obsolete-branches workflow did not reach its expected state.")
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!ready() && clock.Elapsed < TimeSpan.FromSeconds(15)) { Pump(); await Task.Delay(10); }
        Pump(); Assert.That(ready(), Is.True, reason);
    }
    internal async Task Idle()
    { await Until(() => !Dialog.IsBusy); await Dialog.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Pump(); }
    internal async Task<Window> Decision(string text)
    {
        await Until(() => Dialog.OwnedWindows.Any(window => window.IsVisible && window.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text?.Contains(text, StringComparison.Ordinal) == true)));
        return Dialog.OwnedWindows.Single(window => window.IsVisible && window.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text?.Contains(text, StringComparison.Ordinal) == true));
    }
    internal static void Answer(Window owner, bool yes)
    {
        Button button = owner.GetVisualDescendants().OfType<Button>().Single(item => item.IsDefault == yes);
        // Every source Yes/No decision defaults to Yes; No remains an explicit pointer gesture.
        Assert.That(owner.GetVisualDescendants().OfType<Button>().Single(item => item.IsDefault).Content?.ToString(), Is.AnyOf("Yes", "Oui"));
        Pointer(button);
    }
    internal async Task Check(string fullName)
    {
        ListBox list = Field<ListBox>("BranchesGrid");
        int index = Dialog.VisibleBranches.ToList().FindIndex(item => item.Reference.Name == fullName);
        Assert.That(index, Is.GreaterThanOrEqualTo(0), fullName); list.ScrollIntoView(index);
        await Until(() => list.ContainerFromIndex(index) is Control container && container.GetVisualDescendants().OfType<CheckBox>().Any(item => item.Tag as string == fullName));
        Control row = (Control)list.ContainerFromIndex(index)!;
        Pointer(row.GetVisualDescendants().OfType<CheckBox>().Single(item => item.Tag as string == fullName));
    }
    internal void Press(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        TopLevel target = Dialog.OwnedWindows.LastOrDefault(window => window.IsVisible) ?? Dialog;
        target.KeyPressQwerty(key, modifiers); target.KeyReleaseQwerty(key, modifiers); Pump();
    }
    internal void Edit(string name, string text)
    {
        Click(name); Press(PhysicalKey.A, RawInputModifiers.Control); Press(PhysicalKey.Backspace);
        if (text.Length > 0) Dialog.KeyTextInput(text); Pump(); Assert.That(Field<TextBox>(name).Text, Is.EqualTo(text));
    }
    internal async Task<string> Materials()
    {
        string refs = (await Git.GitAsync("for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput;
        return refs + "\n" + string.Join('\n', new[] { ".git/HEAD", ".git/index", ".git/config", "file.txt" }
            .Select(path => path + ":" + Convert.ToHexString(System.IO.File.ReadAllBytes(Git.PathInRepository(path)))));
    }
    internal async Task<IReadOnlyDictionary<string, string>> RemoteRefs()
    {
        Assert.That(Bare, Is.Not.Null);
        string output = (await Git.GitAsync("--git-dir=" + Bare, "for-each-ref", "--format=%(refname) %(objectname)", "refs/heads")).StandardOutput;
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(' ', 2)).ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
    }
    internal async Task AdvanceRemote(string name)
    { await Git.GitAsync("--git-dir=" + Bare, "update-ref", "refs/heads/" + name, Recent, Old); }
    internal string Gate(string command)
    {
        string script = Git.Output("gate-" + command + ".sh"); _release.Add(script + ".release");
        // Written as fixture input only; it executes real Git once the bounded test releases it.
        System.IO.File.WriteAllText(script, "#!/bin/sh\n" + NativeGitCommandProbe + "\nif [ \"$ge_command\" = " + Quote(command) + " ]; then\n printf '%s' \"$$\" > " + Quote(script + ".ready")
            + "\n while [ ! -f " + Quote(script + ".release") + " ]; do sleep 0.02; done\nfi\nexec git \"$@\"\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) { System.IO.File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } return script;
    }
    internal string GateSecondPush()
    {
        string script = Git.Output("second-push.sh"); _release.Add(script + ".release");
        System.IO.File.WriteAllText(script, "#!/bin/sh\n" + NativeGitCommandProbe + "\nif [ \"$ge_command\" = push ]; then\n if [ -f " + Quote(script + ".first")
            + " ]; then\n  printf '%s' \"$$\" > " + Quote(script + ".ready")
            + "\n  while [ ! -f " + Quote(script + ".release") + " ]; do sleep 0.02; done\n else\n  : > " + Quote(script + ".first")
            + "\n fi\nfi\nexec git \"$@\"\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) { System.IO.File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } return script;
    }
    internal string DeleteThenFail()
    {
        string script = Git.Output("delete-then-fail.sh");
        // Real private Git applies its effect, then this fixture reports a native failure.
        // This is input to a future test, never executed during preparation.
        System.IO.File.WriteAllText(script, "#!/bin/sh\n" + NativeGitCommandProbe + "\nif [ \"$ge_command\" = branch ]; then\n git \"$@\"\n result=$?\n if [ \"$result\" -eq 0 ]; then\n"
            + "  printf '%s\\n' 'private failure after applied deletion' >&2\n  exit 73\n fi\n exit \"$result\"\nfi\nexec git \"$@\"\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) { System.IO.File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } return script;
    }
    internal static bool Running(int pid)
    {
        try { using Process process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    // Inspect without shifting $@: GitRepository prepends global options before the verb.
    // Unknown options leave the gate unmatched; actual Git still receives the exact original argv.
    private const string NativeGitCommandProbe = """
        ge_command=
        ge_value=0
        for ge_argument in "$@"; do
          if [ "$ge_value" = 1 ]; then ge_value=0; continue; fi
          case "$ge_argument" in
            -c|-C|--git-dir|--work-tree|--namespace|--super-prefix|--config-env) ge_value=1 ;;
            --no-pager|--paginate|--bare|--literal-pathspecs|--no-replace-objects|--no-optional-locks) ;;
            --git-dir=*|--work-tree=*|--namespace=*|--super-prefix=*|--config-env=*|-c?*|-C?*) ;;
            --|-*) break ;;
            *) ge_command=$ge_argument; break ;;
          esac
        done
        """;
    private static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    internal void Capture(string name)
    {
        Pump(); using Bitmap? frame = Dialog.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    public async ValueTask DisposeAsync()
    {
        foreach (string release in _release) System.IO.File.WriteAllText(release, "release");
        if (Dialog is not null)
        {
            if (Dialog.IsVisible) Dialog.Close();
            await Until(() => !Dialog.IsBusy && !Dialog.IsVisible);
            await Dialog.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Owner.Close(); if (_modal is not null) await _modal.WaitAsync(TimeSpan.FromSeconds(5));
        Git.Dispose(); CultureInfo.CurrentCulture = _culture; CultureInfo.CurrentUICulture = _uiCulture;
    }
}
