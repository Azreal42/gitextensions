using System.Globalization;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Plugins;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class SimplePluginSettingsSessionTests
{
    [Test]
    public void CatalogContainsTheNineOriginalFieldsAndNoInvocationOrSecretParameters()
    {
        Assert.That(SimplePluginSettingsCatalog.All.Select(entry => entry.Definition.Fields.Count), Is.EqualTo(new[] { 3, 1, 3, 2 }));
        Assert.That(SimplePluginSettingsCatalog.All.SelectMany(entry => entry.Definition.Fields).Select(field => field.Name), Is.EqualTo(new[]
        { "Enabled", "Path to msbuild.exe", "msbuild.exe arguments", "Find large files bigger than (Mb)", "Code files", "Directories to ignore (EndsWith)", "Ignore submodules", "Path to Gource", "Arguments" }));
        Assert.That(SimplePluginSettingsCatalog.AutoCompile.Definition.Fields.Select(field => field.DefaultValue), Is.EqualTo(new[] { "false", "", "/p:Configuration=Debug" }));
        Assert.That(SimplePluginSettingsCatalog.Gource.Definition.Fields[0].DefaultValue, Is.Empty, "Do not silently substitute the Linux executable search default for the original setting.");
        Assert.That(SimplePluginSettingsCatalog.FindLargeFiles.Definition.Fields.Single().Kind, Is.EqualTo(PluginScopedSettingKind.Single));
        Assert.That(SimplePluginSettingsCatalog.All.SelectMany(entry => entry.Definition.Fields).Any(field => field.RuntimeKey is "revision" or "workingDirectory" or "password"), Is.False);
    }

    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public async Task UntouchedFutureRawAndLegacyProfilesRemainExactAcrossTheFourProviders(int index)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        SimplePluginSettingsEntry entry = SimplePluginSettingsCatalog.All[index]; PluginScopedSetting field = entry.Definition.Fields[0];
        f.Write(GitDistributedSettingsScope.Local, (entry.Definition.Key(field), "future-λ"), ("peer.future", "keep"));
        Dictionary<string, string?> legacy = new() { [field.RuntimeKey] = field.DefaultValue, ["future-json"] = "preserve" };
        SimplePluginSettingsSession session = new(entry, f.Repository, legacy); await session.InitializeAsync(default);
        Dictionary<string, byte[]> before = f.Material();
        Assert.That(session.Read(GitDistributedSettingsScope.Local, field), Is.EqualTo("future-λ"));
        Assert.That(session.EffectiveValues()["future-json"], Is.EqualTo("preserve"));
        await session.SaveFilesAsync(default); Assert.That(f.Material(), Is.EqualTo(before));
        Assert.That(legacy[field.RuntimeKey], Is.EqualTo(field.DefaultValue)); Assert.That(session.HasChanges, Is.False);
    }

    [TestCase("en")] [TestCase("fr")]
    public async Task InvalidInvariantLegacyNumbersCannotBecomeLocalizedOrThousandsSeparatedThresholds(string language)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
            using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
            SimplePluginSettingsEntry entry = SimplePluginSettingsCatalog.FindLargeFiles;
            Dictionary<string, string?> legacy = new() { ["minimumMegabytes"] = "1,5", ["future-json"] = "keep" };
            SimplePluginSettingsSession session = new(entry, f.Repository, legacy); await session.InitializeAsync(default);
            Dictionary<string, byte[]> before = f.Material();
            Assert.That(session.InvocationValues()["minimumMegabytes"], Is.EqualTo("1"), "Legacy JSON used invariant numbers; invalid text must keep the original default.");
            Assert.That(session.EffectiveValues()["future-json"], Is.EqualTo("keep"));
            await session.SaveFilesAsync(default); Assert.That(f.Material(), Is.EqualTo(before));
            Assert.That(legacy["minimumMegabytes"], Is.EqualTo("1,5")); Assert.That(session.HasChanges, Is.False);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [TestCase("en", "1.234567", "1.234567")] [TestCase("fr", "1,5", "1.5")] [TestCase("fr", "1000", "1000")]
    public async Task SingleXmlKeepsItsOriginalLocalizedTextAndFeedsOnlyInvariantRuntimeValues(string language, string raw, string runtime)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
            using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
            SimplePluginSettingsEntry entry = SimplePluginSettingsCatalog.FindLargeFiles; PluginScopedSetting field = entry.Definition.Fields.Single();
            f.Write(GitDistributedSettingsScope.Global, (entry.Definition.Key(field), raw));
            SimplePluginSettingsSession session = new(entry, f.Repository); await session.InitializeAsync(default);
            byte[] before = File.ReadAllBytes(f.Path(GitDistributedSettingsScope.Global));
            Assert.That(session.Read(GitDistributedSettingsScope.Global, field), Is.EqualTo(raw));
            Assert.That(session.InvocationValues()["minimumMegabytes"], Is.EqualTo(runtime), "A French decimal comma must never become an invariant thousands separator.");
            Assert.That(session.InvocationValues()["revision"], Is.EqualTo("HEAD")); await session.SaveFilesAsync(default);
            Assert.That(File.ReadAllBytes(f.Path(GitDistributedSettingsScope.Global)), Is.EqualTo(before));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [TestCase("-1")] [TestCase("NaN")] [TestCase("1E100")] [TestCase("1E-40")]
    public async Task SuccessfullyParsedButUnsupportedSingleFailsVisiblyWithoutClampingOrRewritingTheRaw(string raw)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
            SimplePluginSettingsEntry entry = SimplePluginSettingsCatalog.FindLargeFiles; PluginScopedSetting field = entry.Definition.Fields.Single();
            f.Write(GitDistributedSettingsScope.Global, (entry.Definition.Key(field), raw));
            SimplePluginSettingsSession session = new(entry, f.Repository); await session.InitializeAsync(default);
            byte[] before = File.ReadAllBytes(f.Path(GitDistributedSettingsScope.Global));
            Assert.That(PluginScopedSettingsDefinition.Single(raw), Is.Not.Null, "This case must exercise operation eligibility after a successful original Single parse.");
            Assert.Throws<ArgumentException>(() => session.InvocationValues());
            await session.SaveFilesAsync(default); Assert.That(File.ReadAllBytes(f.Path(GitDistributedSettingsScope.Global)), Is.EqualTo(before));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [TestCase(GitDistributedSettingsScope.Local)] [TestCase(GitDistributedSettingsScope.Distributed)] [TestCase(GitDistributedSettingsScope.Global)]
    public async Task AcceptedGourceWritesOnlyItsChangedOriginalFieldsUsingWindowsEffectiveRouting(GitDistributedSettingsScope source)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        SimplePluginSettingsEntry entry = SimplePluginSettingsCatalog.Gource;
        PluginScopedSetting path = entry.Definition.Find("_gourcePath"), args = entry.Definition.Find("_gourceArguments");
        f.Write(source, (entry.Definition.Key(path), "/old/gource"), ("peer", "keep"));
        SimplePluginSettingsSession session = new(entry, f.Repository, new Dictionary<string, string?> { ["workingDirectory"] = "/old/invocation", ["future-json"] = "keep" });
        await session.InitializeAsync(default); Dictionary<string, byte[]> before = f.Material();
        GourceOptions initial = new("/old/gource", entry.Definition.Fields.Single(field => field == args).DefaultValue, f.Repository!.Path);
        session.StageAcceptedGource(initial, new("/new/gource", "--accepted", "/temporary/workdir"));
        Assert.That(f.Material(), Is.EqualTo(before)); await session.SaveFilesAsync(default);
        GitDistributedSettingsScope destination = source == GitDistributedSettingsScope.Global ? source : GitDistributedSettingsScope.Local;
        Assert.That((await f.ReadAsync(destination)).Values[entry.Definition.Key(path)], Is.EqualTo("/new/gource"));
        Assert.That((await f.ReadAsync(GitDistributedSettingsScope.Global)).Values[entry.Definition.Key(args)], Is.EqualTo("--accepted"));
        if (source == GitDistributedSettingsScope.Distributed) { Assert.That(File.ReadAllBytes(f.Path(source)), Is.EqualTo(before[f.Path(source)])); }
        Assert.That(session.InvocationValues()["workingDirectory"], Is.Null);
        Assert.That(session.EffectiveValues()["future-json"], Is.EqualTo("keep"));
        Assert.That((await f.ReadAsync(destination)).Values.Keys.Any(key => key.EndsWith("workingDirectory", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public async Task AcceptedGourceLocalFileIsNeverReplayedWhenGlobalFailsAndARealPeerWritesBeforeRetry()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        SimplePluginSettingsEntry entry = SimplePluginSettingsCatalog.Gource; PluginScopedSetting path = entry.Definition.Find("_gourcePath");
        f.Write(GitDistributedSettingsScope.Local, (entry.Definition.Key(path), "/before"));
        SimplePluginSettingsSession session = new(entry, f.Repository); await session.InitializeAsync(default);
        GourceOptions before = new("/before", "--old"), accepted = new("/accepted", "--accepted");
        session.StageAcceptedGource(before, accepted);
        using DetailedSettingsTestScope.BlockedFile block = new(f.Path(GitDistributedSettingsScope.Global));
        Assert.CatchAsync<IOException>(async () => await session.SaveFilesAsync(default));
        GitXmlSettingsSnapshot local = await f.ReadAsync(GitDistributedSettingsScope.Local);
        Assert.That(local.Values[entry.Definition.Key(path)], Is.EqualTo("/accepted"));
        await GitXmlSettingsStore.SaveChangesAsync(local, new Dictionary<string, string?> { ["peer.after-accepted"] = "preserve" });
        byte[] peer = File.ReadAllBytes(local.Path); block.Restore(); session.StageAcceptedGource(before, accepted);
        await session.SaveFilesAsync(default); Assert.That(File.ReadAllBytes(local.Path), Is.EqualTo(peer)); Assert.That(session.HasChanges, Is.False);
    }
}
