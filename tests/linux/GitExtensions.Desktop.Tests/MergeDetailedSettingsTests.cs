using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class MergeDetailedSettingsTests
{
    [AvaloniaTest]
    public async Task RawLocalUnknownAndEmptyValuesMaskLowerXmlAndLegacyBeforeTypedDefaultsWithoutCancelWrites()
    {
        foreach (string raw in new[] { "future-value", "" })
        {
            using F f = await F.CreateAsync();
            f.Profile.Write(GitDistributedSettingsScope.Global, (DetailedRepositorySettings.AddMergeLog, "true"), (DetailedRepositorySettings.MergeLogCount, "31"));
            f.Profile.Write(GitDistributedSettingsScope.Distributed, (DetailedRepositorySettings.AddMergeLog, "true"), (DetailedRepositorySettings.MergeLogCount, "17"));
            f.Profile.Write(GitDistributedSettingsScope.Local, (DetailedRepositorySettings.AddMergeLog, raw), (DetailedRepositorySettings.MergeLogCount, raw), ("future.λ", "keep"));
            await f.LegacyAsync(addLog: true, count: 9); string before = f.Materials();
            MergeBranchDialog dialog = await f.ShowAsync();
            Assert.That(Field<CheckBox>(dialog, "MergeAddLog").IsChecked, Is.False);
            Assert.That(Field<NumericUpDown>(dialog, "MergeLogCount").Value, Is.EqualTo(20));
            Assert.That(dialog.ReadOptions().LogCount, Is.Null); Assert.That(File.Exists(f.Arguments), Is.False);
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelMerge"));
            Assert.That(f.Materials(), Is.EqualTo(before), "Opening/Cancel cannot normalize unknown raw XML, import legacy JSON or start a merge.");
        }
    }

    [AvaloniaTest]
    public async Task LogAndCountEditsPersistImmediatelyAcrossCancelAndRouteOnlyToEffectiveLocalOrGlobal()
    {
        foreach (bool inheritedFromDistributed in new[] { true, false })
        {
            using F f = await F.CreateAsync();
            f.Profile.Write(GitDistributedSettingsScope.Global, (DetailedRepositorySettings.AddMergeLog, "false"), (DetailedRepositorySettings.MergeLogCount, "12"), ("future.λ", "global keep"));
            f.Profile.Write(GitDistributedSettingsScope.Distributed, inheritedFromDistributed
                ? [(DetailedRepositorySettings.AddMergeLog, "false"), (DetailedRepositorySettings.MergeLogCount, "12"), ("future.λ", "distributed keep")]
                : [("future.λ", "distributed keep")]);
            f.Profile.Write(GitDistributedSettingsScope.Local, ("future.λ", "local keep"));
            byte[] distributed = File.ReadAllBytes(f.Profile.Path(GitDistributedSettingsScope.Distributed));
            GitDistributedSettingsScope destination = inheritedFromDistributed ? GitDistributedSettingsScope.Local : GitDistributedSettingsScope.Global;
            GitDistributedSettingsScope untouched = inheritedFromDistributed ? GitDistributedSettingsScope.Global : GitDistributedSettingsScope.Local;
            byte[] other = File.ReadAllBytes(f.Profile.Path(untouched)); byte[] index = f.Index(); string head = await f.Head();
            MergeBranchDialog dialog = await f.ShowAsync(); PushProcessTestDriver.Pointer(Field<CheckBox>(dialog, "MergeAdvanced"));
            PushProcessTestDriver.Pointer(Field<CheckBox>(dialog, "MergeAddLog")); await Settled(dialog);
            Assert.That((await f.Profile.ReadAsync(destination)).Values[DetailedRepositorySettings.AddMergeLog], Is.EqualTo("true"), "The checkbox edit saves before Merge or Cancel.");
            Field<NumericUpDown>(dialog, "MergeLogCount").Value = 37; await Settled(dialog);
            Assert.That((await f.Profile.ReadAsync(destination)).Values[DetailedRepositorySettings.MergeLogCount], Is.EqualTo("37"));
            Assert.That(File.ReadAllBytes(f.Profile.Path(GitDistributedSettingsScope.Distributed)), Is.EqualTo(distributed));
            Assert.That(File.ReadAllBytes(f.Profile.Path(untouched)), Is.EqualTo(other));
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelMerge"));
            MergeBranchDialog reopened = await f.ShowAsync();
            Assert.That(Field<CheckBox>(reopened, "MergeAddLog").IsChecked, Is.True); Assert.That(Field<NumericUpDown>(reopened, "MergeLogCount").Value, Is.EqualTo(37));
            Assert.That(await f.Head(), Is.EqualTo(head)); Assert.That(f.Index(), Is.EqualTo(index)); Assert.That(File.Exists(f.Arguments), Is.False);
            Assert.That((await f.Profile.ReadAsync(destination)).Values["future.λ"], Is.EqualTo(inheritedFromDistributed ? "local keep" : "global keep"));
        }
    }

    [AvaloniaTest]
    public async Task InheritedZeroIsPreservedAndActuallyOmitsTheNativeMergeLogArgument()
    {
        using F f = await F.CreateAsync();
        f.Profile.Write(GitDistributedSettingsScope.Distributed, (DetailedRepositorySettings.AddMergeLog, "true"), (DetailedRepositorySettings.MergeLogCount, "0"), ("future.λ", "keep"));
        Dictionary<string, byte[]> xml = f.Profile.Material(); MergeBranchDialog dialog = await f.ShowAsync();
        Assert.That(dialog.ReadOptions().LogCount, Is.EqualTo(0));
        PushProcessTestDriver.Pointer(Field<RadioButton>(dialog, "MergeNoFastForward"));
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "ExecuteMerge")); await UiUntil(() => !dialog.IsBusy && !dialog.IsVisible);
        Assert.That(dialog.Result?.Result.Success, Is.True, dialog.LastError);
        Assert.That(f.NativeArguments().Any(argument => argument == "--log" || argument.StartsWith("--log=", StringComparison.Ordinal)), Is.False);
        Assert.That(f.NativeArguments(), Does.Contain("--no-ff")); Assert.That((await f.Text("rev-list", "--parents", "-n", "1", "HEAD")).Split(' ').Length, Is.EqualTo(3));
        Assert.That(await f.Text("show", "-s", "--format=%B", "HEAD"), Does.Not.Contain("Topic log message λ"));
        f.Profile.AssertMaterial(xml); Assert.That(File.Exists(f.Profile.Path(GitDistributedSettingsScope.Local)), Is.False, "Inherited zero does not trigger an XML rewrite or local override during Merge.");
    }

    [AvaloniaTest]
    public async Task EverySavedInt32RemainsUnclampedAndBlocksMergeUntilADeliberateValidCorrection()
    {
        foreach (int saved in new[] { -1, int.MaxValue })
        {
            using F f = await F.CreateAsync();
            f.Profile.Write(GitDistributedSettingsScope.Local, (DetailedRepositorySettings.AddMergeLog, "true"), (DetailedRepositorySettings.MergeLogCount, saved.ToString(CultureInfo.InvariantCulture)), ("future.λ", "keep"));
            string before = f.Materials(); MergeBranchDialog dialog = await f.ShowAsync();
            NumericUpDown count = Field<NumericUpDown>(dialog, "MergeLogCount");
            Assert.That(count.Value, Is.EqualTo(saved)); Assert.That(Field<Button>(dialog, "ExecuteMerge").IsEnabled, Is.False);
            Assert.That(f.Materials(), Is.EqualTo(before), "Initialization cannot clamp or persist any saved Int32 outside the dialog's 0–100 range.");
            dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); dialog.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
            Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.Result, Is.Null); Assert.That(File.Exists(f.Arguments), Is.False); Assert.That(f.Materials(), Is.EqualTo(before));
            PushProcessTestDriver.Pointer(Field<CheckBox>(dialog, "MergeAdvanced")); Assert.That(count.IsEnabled, Is.True);
            count.Value = 42; await Settled(dialog);
            Assert.That(count.Minimum, Is.EqualTo(0)); Assert.That(count.Maximum, Is.EqualTo(100));
            Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Local)).Values[DetailedRepositorySettings.MergeLogCount], Is.EqualTo("42"));
            Assert.That(Field<Button>(dialog, "ExecuteMerge").IsEnabled, Is.True);
            PushProcessTestDriver.Pointer(Field<RadioButton>(dialog, "MergeNoFastForward")); PushProcessTestDriver.Pointer(Field<Button>(dialog, "ExecuteMerge"));
            await UiUntil(() => !dialog.IsBusy && !dialog.IsVisible);
            Assert.That(dialog.Result?.Result.Success, Is.True, dialog.LastError); Assert.That(f.NativeArguments(), Does.Contain("--log=42"));
            Assert.That(await f.Text("show", "-s", "--format=%B", "HEAD"), Does.Contain("Topic log message λ"));
        }
    }

    [AvaloniaTest]
    public async Task LegacyJsonIsReadWithoutWritesOnOpenCancelAndImportsLocallyOnlyAtExplicitEditOrMerge()
    {
        foreach (bool editInsteadOfMerge in new[] { true, false })
        {
            using F f = await F.CreateAsync(); await f.LegacyAsync(addLog: true, count: 9);
            string before = f.Materials(); MergeBranchDialog first = await f.ShowAsync();
            Assert.That(Field<CheckBox>(first, "MergeAddLog").IsChecked, Is.True); Assert.That(Field<NumericUpDown>(first, "MergeLogCount").Value, Is.EqualTo(9));
            Assert.That(f.Profile.XmlPaths.Any(File.Exists), Is.False); Assert.That(f.Materials(), Is.EqualTo(before));
            PushProcessTestDriver.Pointer(Field<Button>(first, "CancelMerge")); Assert.That(f.Materials(), Is.EqualTo(before));
            MergeBranchDialog dialog = await f.ShowAsync(); Assert.That(f.Materials(), Is.EqualTo(before));
            if (editInsteadOfMerge)
            {
                PushProcessTestDriver.Pointer(Field<CheckBox>(dialog, "MergeAdvanced")); Field<NumericUpDown>(dialog, "MergeLogCount").Value = 11; await Settled(dialog);
                Assert.That(File.Exists(f.Arguments), Is.False); PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelMerge"));
            }
            else
            {
                PushProcessTestDriver.Pointer(Field<Button>(dialog, "ExecuteMerge")); await UiUntil(() => !dialog.IsBusy && !dialog.IsVisible);
                Assert.That(dialog.Result?.Result.Success, Is.True, dialog.LastError); Assert.That(f.NativeArguments(), Does.Contain("--log=9"));
            }
            GitXmlSettingsSnapshot local = await f.Profile.ReadAsync(GitDistributedSettingsScope.Local);
            Assert.That(local.Values[DetailedRepositorySettings.AddMergeLog], Is.EqualTo("true"));
            Assert.That(local.Values[DetailedRepositorySettings.MergeLogCount], Is.EqualTo(editInsteadOfMerge ? "11" : "9"));
            Assert.That(File.Exists(f.Profile.Path(GitDistributedSettingsScope.Global)) || File.Exists(f.Profile.Path(GitDistributedSettingsScope.Distributed)), Is.False);
            Assert.That(new MergePreferencesStore().LegacyLog(await f.CommonDirectory()), Is.Null, "The durable import marker prevents removed XML overrides from reviving old JSON.");
        }
    }

    [Test]
    public async Task ExplicitLocalUnsetsAreNotReimportedFromLegacyAndAnEditRevertedToBaselineDoesNotMigrate()
    {
        using (DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync(seed: false))
        {
            string common = (await profile.Repository!.GetInfoAsync()).CommonDirectory; MergePreferencesStore legacy = new();
            legacy.Save(common, new(AddLog: true, LogCount: 42));
            profile.Write(GitDistributedSettingsScope.Local, (DetailedRepositorySettings.AddMergeLog, "true"), (DetailedRepositorySettings.MergeLogCount, "42"), ("future.λ", "retained"));
            profile.Write(GitDistributedSettingsScope.Global, (DetailedRepositorySettings.AddMergeLog, "false"), (DetailedRepositorySettings.MergeLogCount, "7"));
            byte[] global = File.ReadAllBytes(profile.Path(GitDistributedSettingsScope.Global)); DetailedSettingsSession session = await profile.SessionAsync();
            session.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.AddMergeLog, null);
            session.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.MergeLogCount, null); await session.SaveFilesAsync(default);
            GitXmlSettingsSnapshot local = await profile.ReadAsync(GitDistributedSettingsScope.Local);
            Assert.That(local.Values.ContainsKey(DetailedRepositorySettings.AddMergeLog) || local.Values.ContainsKey(DetailedRepositorySettings.MergeLogCount), Is.False);
            Assert.That(local.Values["future.λ"], Is.EqualTo("retained")); Assert.That(File.ReadAllBytes(profile.Path(GitDistributedSettingsScope.Global)), Is.EqualTo(global));
            Assert.That(File.Exists(profile.Path(GitDistributedSettingsScope.Distributed)), Is.False); Assert.That(legacy.LegacyLog(common), Is.Null);
            Assert.That((await profile.SessionAsync()).Read(GitDistributedSettingsScope.Effective), Is.EqualTo(new DetailedScopeValues(false, false, "7")), "Explicit unsets reveal XML inheritance, even in a fresh consumer after legacy migration.");
        }
        using (DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync(seed: false))
        {
            string common = (await profile.Repository!.GetInfoAsync()).CommonDirectory; MergePreferencesStore legacy = new(); legacy.Save(common, new(AddLog: true, LogCount: 42));
            profile.Write(GitDistributedSettingsScope.Local, (DetailedRepositorySettings.AddMergeLog, "true"), (DetailedRepositorySettings.MergeLogCount, "42"));
            Dictionary<string, byte[]> before = profile.Material(); byte[] json = File.ReadAllBytes(legacy.FilePath); DetailedSettingsSession session = await profile.SessionAsync();
            session.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.AddMergeLog, "false"); session.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.AddMergeLog, "true");
            session.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.MergeLogCount, "41"); session.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.MergeLogCount, "42");
            await session.SaveFilesAsync(default); profile.AssertMaterial(before);
            Assert.That(File.ReadAllBytes(legacy.FilePath), Is.EqualTo(json)); Assert.That(legacy.LegacyLog(common), Is.Not.Null);
            Assert.That(File.Exists(profile.Path(GitDistributedSettingsScope.Global)) || File.Exists(profile.Path(GitDistributedSettingsScope.Distributed)), Is.False);
        }
    }

    private static T Field<T>(TopLevel dialog, string name) where T : Control => PushProcessTestDriver.Field<T>(dialog, name);
    private static async Task Settled(MergeBranchDialog dialog)
    { await dialog.LogPreferencesSettled.WaitAsync(TimeSpan.FromSeconds(15)); await UiUntil(() => !dialog.IsBusy); Assert.That(dialog.LastError, Is.Null); }
    private static async Task UiUntil(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "Detailed Merge workflow did not settle.");
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string Tree(string root) => string.Join('\n', Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(file => Path.GetRelativePath(root, file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))));

    private sealed class F : IDisposable
    {
        internal DetailedSettingsTestScope Profile { get; }
        internal GitRepository Repository { get; }
        internal string Arguments { get; }
        private readonly List<MergeBranchDialog> _dialogs = [];
        private F(DetailedSettingsTestScope profile, string executable, string arguments)
        { Profile = profile; Arguments = arguments; Repository = new(profile.Repository!.Path, new GitRunner(executable)); }
        internal static async Task<F> CreateAsync()
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Native merge argument witness requires Linux."); }
            DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync(seed: false);
            try
            {
                profile.Git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0");
                await profile.Git.GitAsync("checkout", "-b", "topic"); await File.WriteAllTextAsync(profile.Git.PathInRepository("topic.txt"), "topic content\n");
                await profile.Git.GitAsync("add", "--", "topic.txt"); await profile.Git.GitAsync("commit", "-m", "Topic log message λ"); await profile.Git.GitAsync("checkout", "main");
                string executable = profile.Git.Output("merge-native-witness"), arguments = executable + ".arguments";
                await File.WriteAllTextAsync(executable, "#!/bin/sh\nset -eu\nfor arg do\n  if test \"$arg\" = merge; then printf '%s\\0' \"$@\" > " + Quote(arguments) + "; fi\ndone\nexec /usr/bin/git \"$@\"\n");
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return new(profile, executable, arguments);
            }
            catch { profile.Dispose(); throw; }
        }
        internal async Task<MergeBranchDialog> ShowAsync()
        {
            MergeBranchDialog dialog = new(Repository, "topic"); _dialogs.Add(dialog); dialog.Show(); await dialog.InitializeAsync();
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); Assert.That(dialog.IsBusy, Is.False); return dialog;
        }
        internal async Task LegacyAsync(bool addLog, int count)
        { new MergePreferencesStore().Save(await CommonDirectory(), new(NoFastForward: true, AddLog: addLog, LogCount: count)); }
        internal async Task<string> CommonDirectory() => (await Repository.GetInfoAsync()).CommonDirectory;
        internal async Task<string> Text(params string[] args) => (await Profile.Git.GitAsync(args)).StandardOutput.TrimEnd('\r', '\n');
        internal Task<string> Head() => Text("rev-parse", "HEAD");
        internal byte[] Index() => File.ReadAllBytes(Profile.Git.PathInRepository(".git/index"));
        internal string[] NativeArguments() => Encoding.UTF8.GetString(File.ReadAllBytes(Arguments)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        internal string Materials()
        {
            string legacy = new MergePreferencesStore().FilePath;
            return Tree(Repository.Path) + "\nPROFILES\n" + string.Join('\n', Profile.Material().OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + ":" + Convert.ToHexString(SHA256.HashData(pair.Value))))
                + "\nLEGACY\n" + (File.Exists(legacy) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(legacy))) : "<absent>");
        }
        public void Dispose()
        { foreach (MergeBranchDialog dialog in _dialogs.AsEnumerable().Reverse()) { if (dialog.IsVisible) { dialog.Close(); } } Profile.Dispose(); }
    }
}
