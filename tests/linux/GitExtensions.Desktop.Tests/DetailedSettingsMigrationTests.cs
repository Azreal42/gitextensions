using System.Text.Json;
using System.Xml.Linq;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class DetailedSettingsMigrationTests
{
    [Test]
    public async Task AnExplicitUnsetRemovesAPresentNilXmlItemWhileUntouchedNilItemsSurvive()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(seed: false);
        string path = f.Path(GitDistributedSettingsScope.Local); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        XElement Nil(string key) => new("item", new XElement("key", new XElement("string", key)),
            new XElement("value", new XElement("string", new XAttribute(xsi + "nil", "true"))));
        new XDocument(new XElement("dictionary", new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
            Nil(DetailedRepositorySettings.AddMergeLog), Nil("future.nil"))).Save(path);
        DetailedSettingsSession session = await f.SessionAsync();
        session.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.AddMergeLog, null);
        await session.SaveFilesAsync(default);
        GitXmlSettingsSnapshot saved = await f.ReadAsync(GitDistributedSettingsScope.Local);
        Assert.That(saved.Values.ContainsKey(DetailedRepositorySettings.AddMergeLog), Is.False);
        Assert.That(saved.Values.ContainsKey("future.nil"), Is.True);
        Assert.That(saved.Values["future.nil"], Is.Null);
    }

    [Test]
    public async Task ConcurrentLegacyMarkersAndWorkflowWritesRetainEveryRepositoryAndMigratedMarker()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(seed: false);
        MergePreferencesStore store = new();
        string[] repositories = Enumerable.Range(0, 24).Select(index => "private-common-" + index).ToArray();
        foreach (string repository in repositories) { store.Save(repository, new(AddLog: true, LogCount: 42)); }
        using ManualResetEventSlim start = new();
        Task[] writers = repositories.SelectMany(repository => new[]
        {
            Task.Run(() => { start.Wait(); new MergePreferencesStore().MarkLogMigrated(repository); }),
            Task.Run(() => { start.Wait(); new MergePreferencesStore().SaveWorkflow(repository, false, true); })
        }).ToArray();
        start.Set(); await Task.WhenAll(writers);
        using JsonDocument profile = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        JsonElement entries = profile.RootElement.GetProperty("Repositories");
        Assert.That(entries.EnumerateObject().Count(), Is.EqualTo(repositories.Length));
        Assert.That(profile.RootElement.GetProperty("LogPreferencesMigrated").EnumerateArray().Select(value => value.GetString()), Is.EquivalentTo(repositories));
        foreach (string repository in repositories)
        {
            Assert.That(store.LegacyLog(repository), Is.Null);
            Assert.That(store.Load(repository), Is.EqualTo(new MergePreferences(NoFastForward: true, AddLog: true, LogCount: 42)));
        }
    }
}
