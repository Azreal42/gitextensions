using Avalonia.Input;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ScriptHotkeysTests
{
    [Test]
    public void RenamingReorderingAndDisablingKeepTheExplicitBindingForTheSameScriptIdentity()
    {
        ScriptDefinition first = new() { Name = "&One", HotkeyCommandIdentifier = 9001 };
        ScriptDefinition second = new() { Name = "Two", HotkeyCommandIdentifier = 9002, Enabled = false };
        HotkeyBindings bindings = new([first, second]);
        KeyGesture gesture = new(Key.F10, KeyModifiers.Control);
        bindings.Set("Scripts", 9001, gesture);
        bindings.UpdateScripts([second, first with { Name = "&Renamed", Enabled = false }]);
        Assert.That(bindings.Commands("Scripts").Select(command => command.Name), Is.EqualTo(new[] { "Two", "Renamed" }));
        Assert.That(bindings.Get("Scripts", "Renamed"), Is.EqualTo(gesture));
        Assert.That(bindings.Get("Scripts", 9001), Is.EqualTo(gesture));
        Assert.That(bindings.Clone().Commands("Scripts"), Is.EqualTo(bindings.Commands("Scripts")));
    }

    [Test]
    public void UnrelatedHotkeySaveRetainsBindingsForTemporarilyAbsentScripts()
    {
        string directory = Path.Combine(Path.GetTempPath(), "script-hotkeys-" + Guid.NewGuid().ToString("N"));
        try
        {
            string profile = Path.Combine(directory, "gitextensions");
            ScriptsPreferencesStore scripts = new(Path.Combine(profile, "scripts.json"));
            ScriptDefinition script = new() { Name = "original", HotkeyCommandIdentifier = 9091, Enabled = false };
            scripts.Save([script]);
            HotkeyPreferencesStore store = new(directory); HotkeyBindings bindings = store.Load();
            bindings.Set("Scripts", 9091, new(Key.F10, KeyModifiers.Control)); store.Save(bindings);
            scripts.Save([]); HotkeyBindings withoutScript = store.Load();
            withoutScript.Set("Browse", 18, new(Key.F11)); store.Save(withoutScript);
            scripts.Save([script with { Name = "returned" }]);
            HotkeyBindings returned = store.Load();
            Assert.That(returned.Get("Scripts", "returned"), Is.EqualTo(new KeyGesture(Key.F10, KeyModifiers.Control)));
            Assert.That(returned.Get("Browse", 18), Is.EqualTo(new KeyGesture(Key.F11)));
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }
}
