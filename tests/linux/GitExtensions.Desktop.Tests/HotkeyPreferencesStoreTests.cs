using System.Text;
using System.Text.Json;
using Avalonia.Input;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class HotkeyPreferencesStoreTests
{
    [Test]
    public void MissingProfileSharesXdgRootWithoutCreatingOrImportingWindowsBindings()
    {
        using TemporaryProfile temporary = new();
        string? original = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", temporary.Directory);
            string mainFile = new PreferencesStore().FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(mainFile)!);
            byte[] otherSettings = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Theme = "Dark",
                SerializedHotkeys = "<ArrayOfHotkeySettings><HotkeySettings Name=\"Browse\" /></ArrayOfHotkeySettings>",
                OwnScripts = "<ArrayOfScriptInfo><ScriptInfo><Enabled>true</Enabled><Name>Never imported</Name></ScriptInfo></ArrayOfScriptInfo>"
            });
            File.WriteAllBytes(mainFile, otherSettings);
            HotkeyPreferencesStore store = new();
            HotkeyBindings bindings = store.Load();
            Assert.That(store.FilePath, Is.EqualTo(Path.Combine(temporary.Directory, "gitextensions", "hotkeys.json")));
            Assert.That(bindings.Get("Browse", 43)!.Key, Is.EqualTo(Key.T));
            Assert.That(bindings.Get("Browse", 43)!.KeyModifiers, Is.EqualTo(KeyModifiers.Control));
            Assert.That(HotkeyCatalog.For("Scripts"), Is.Empty);
            Assert.That(File.Exists(store.FilePath), Is.False);
            Assert.That(File.ReadAllBytes(mainFile), Is.EqualTo(otherSettings));
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(mainFile)!), Is.EqualTo(new[] { mainFile }));
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", original); }
    }

    [Test]
    public void ExplicitDirectoryWinsAndRelativeXdgFallsBackExactlyLikePreferencesStore()
    {
        using TemporaryProfile temporary = new();
        string? original = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(temporary.Directory, "other-xdg"));
            HotkeyPreferencesStore explicitStore = new(temporary.Directory);
            Assert.That(explicitStore.FilePath, Is.EqualTo(Path.Combine(temporary.Directory, "gitextensions", "hotkeys.json")));
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "relative-xdg-is-not-an-absolute-profile");
            HotkeyPreferencesStore fallback = new();
            string expected = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "hotkeys.json"));
            Assert.That(fallback.FilePath, Is.EqualTo(expected));
            Assert.That(Path.IsPathFullyQualified(fallback.FilePath), Is.True);
            Assert.That(Directory.GetFileSystemEntries(temporary.Directory), Is.Empty, "Resolving a store path does not create a profile.");
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", original); }
    }

    [Test]
    public void EveryFixedIdentityRoundTripsWithCollisionsAllFourModifiersAndExplicitNone()
    {
        using TemporaryProfile temporary = new();
        HotkeyPreferencesStore store = temporary.Store;
        HotkeyBindings bindings = new();
        KeyModifiers[] modifiers =
        [
            KeyModifiers.None, KeyModifiers.Control, KeyModifiers.Shift, KeyModifiers.Alt, KeyModifiers.Meta,
            KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta
        ];
        int index = 0;
        foreach (string context in HotkeyCatalog.Contexts)
        {
            foreach (HotkeyCommand command in HotkeyCatalog.For(context))
            {
                bindings.Set(context, command.Code, index % 7 == 0 ? null : new(Key.F11, modifiers[index % modifiers.Length]));
                index++;
            }
        }

        string unrelated = Path.Combine(temporary.Directory, "browse-preferences.json");
        byte[] unrelatedBytes = Encoding.UTF8.GetBytes("{\"Theme\":\"Dark\",\"literal\":\"Café Ω\"}");
        File.WriteAllBytes(unrelated, unrelatedBytes);
        store.Save(bindings);
        HotkeyBindings loaded = new HotkeyPreferencesStore(temporary.Directory).Load();
        Assert.That(index, Is.EqualTo(171));
        foreach (string context in HotkeyCatalog.Contexts)
        {
            foreach (HotkeyCommand command in HotkeyCatalog.For(context))
            {
                Assert.That(Describe(loaded.Get(context, command.Code)), Is.EqualTo(Describe(bindings.Get(context, command.Name))), $"{context}:{command.Code}");
            }
        }

        Assert.That(File.ReadAllBytes(unrelated), Is.EqualTo(unrelatedBytes));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"), Is.Empty);
        Assert.That(HotkeyCatalog.For("Scripts"), Is.Empty);
    }

    [Test]
    public void PartialOverridesUseStableIdsIgnoreUnknownEntriesAndKeepAllOtherDefaults()
    {
        using TemporaryProfile temporary = new();
        temporary.Write("""
            {
              "Version": 1,
              "FutureRootProperty": true,
              "Bindings": [
                {"Context":"Browse","Code":43,"Name":"NotTheCommandName","Key":"F11","Modifiers":["Meta"]},
                {"Context":"Commit","Code":11,"Key":null,"Modifiers":[]},
                {"Context":"FutureContext","Code":"not-our-schema","Key":{"future":true}},
                {"Context":"Browse","Code":99999,"Key":{"future":true},"Modifiers":77},
                {"Context":"Scripts","Code":9000,"Key":"F12","Modifiers":[]}
              ]
            }
            """);
        byte[] before = File.ReadAllBytes(temporary.Store.FilePath);
        HotkeyBindings loaded = temporary.Store.Load();
        Assert.That(loaded.Get("Browse", "CreateTag")!.Key, Is.EqualTo(Key.F11));
        Assert.That(loaded.Get("Browse", 43)!.KeyModifiers, Is.EqualTo(KeyModifiers.Meta));
        Assert.That(loaded.Get("Commit", "StageAll"), Is.Null);
        Assert.That(loaded.Get("Commit", 25)!.Key, Is.EqualTo(Key.T));
        Assert.That(loaded.Get("FileViewer", 19)!.KeyModifiers, Is.EqualTo(KeyModifiers.Control));
        Assert.That(loaded.Get("Browse", 99999), Is.Null);
        Assert.That(loaded.Get("Scripts", 9000), Is.EqualTo(new KeyGesture(Key.F12)), "The named disabled example is part of the dynamic Scripts catalog, as in Windows.");
        foreach (string context in HotkeyCatalog.Contexts)
        {
            foreach (HotkeyCommand command in HotkeyCatalog.For(context))
            {
                if ((context, command.Code) is ("Browse", 43) or ("Commit", 11))
                {
                    continue;
                }

                Assert.That(Describe(loaded.Get(context, command.Code)), Is.EqualTo(Describe(command.DefaultGesture)), $"Absent override {context}:{command.Code} keeps its default.");
            }
        }

        Assert.That(File.ReadAllBytes(temporary.Store.FilePath), Is.EqualTo(before), "Load does not normalize or rewrite unknown data.");
    }

    [Test]
    public void RepeatedKnownOverrideUsesTheLastValueLikeTheWindowsMergeAndKeepsExplicitNone()
    {
        using TemporaryProfile temporary = new();
        temporary.Write("""
            {"Version":1,"Bindings":[
              {"Context":"Browse","Code":43,"Key":"F11","Modifiers":["Meta"]},
              {"Context":"Browse","Code":43,"Key":null,"Modifiers":[]}
            ]}
            """);
        HotkeyBindings loaded = temporary.Store.Load();
        Assert.That(loaded.Get("Browse", "CreateTag"), Is.Null);
        Assert.That(loaded.Match("Browse", new() { Key = Key.T, KeyModifiers = KeyModifiers.Control }), Is.Null);
        temporary.Store.Save(loaded);
        Assert.That(temporary.Store.Load().Get("Browse", 43), Is.Null);
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllBytes(temporary.Store.FilePath));
        Assert.That(saved.RootElement.GetProperty("Bindings").EnumerateArray().Count(entry => entry.GetProperty("Context").GetString() == "Browse" && entry.GetProperty("Code").GetInt32() == 43), Is.EqualTo(1));
    }

    [Test]
    public void KnownMalformedEntriesAndDocumentVersionsAreVisibleErrorsAndCannotBeOverwritten()
    {
        using TemporaryProfile temporary = new();
        List<string> invalid =
        [
            "{broken", "null", "[]", "{}", """{"Version":"1","Bindings":[]}""",
            """{"Version":2,"Bindings":[]}""", """{"Version":1,"Bindings":null}""",
            """{"Version":1,"Bindings":[null]}""", """{"Version":1}"""
        ];
        string[] invalidEntries =
        [
            """{"Context":null,"Code":43,"Key":"A","Modifiers":[]}""",
            """{"Context":"Browse","Code":"43","Key":"A","Modifiers":[]}""",
            """{"Context":"Browse","Code":43,"Modifiers":[]}""",
            """{"Context":"Browse","Code":43,"Key":"A"}""",
            """{"Context":"Browse","Code":43,"Key":23,"Modifiers":[]}""",
            """{"Context":"Browse","Code":43,"Key":"NoSuchKey","Modifiers":[]}""",
            """{"Context":"Browse","Code":43,"Key":"None","Modifiers":[]}""",
            """{"Context":"Browse","Code":43,"Key":"a","Modifiers":[]}""",
            """{"Context":"Browse","Code":43,"Key":"65","Modifiers":[]}""",
            """{"Context":"Browse","Code":43,"Key":null,"Modifiers":["Control"]}""",
            """{"Context":"Browse","Code":43,"Key":"A","Modifiers":8}""",
            """{"Context":"Browse","Code":43,"Key":"A","Modifiers":["Super"]}""",
            """{"Context":"Browse","Code":43,"Key":"A","Modifiers":["Control","Control"]}""",
            """{"Context":"Browse","Code":43,"Key":"A","Modifiers":[1]}"""
        ];
        invalid.AddRange(invalidEntries.Select(entry => "{\"Version\":1,\"Bindings\":[" + entry + "]}"));
        List<string> notifications = [];
        Action<string> handler = notifications.Add;
        HotkeyPreferencesStore.Saved += handler;
        try
        {
            foreach (string text in invalid)
            {
                temporary.Write(text);
                byte[] before = File.ReadAllBytes(temporary.Store.FilePath);
                Assert.Catch<JsonException>(() => temporary.Store.Load(), text);
                Assert.Catch<JsonException>(() => temporary.Store.Save(new()), text);
                Assert.That(File.ReadAllBytes(temporary.Store.FilePath), Is.EqualTo(before), text);
                Assert.That(Directory.GetFiles(Path.GetDirectoryName(temporary.Store.FilePath)!), Is.EqualTo(new[] { temporary.Store.FilePath }));
            }

            Assert.That(notifications, Is.Empty, "No rejected document publishes a successful save.");
        }
        finally { HotkeyPreferencesStore.Saved -= handler; }
    }

    [Test]
    public void InvalidUtf8BytesRemainExactAndAreNotDecodedWithReplacementCharacters()
    {
        using TemporaryProfile temporary = new();
        byte[][] damaged =
        [
            [0xff, 0xfe, 0x00],
            [0x7b, 0x22, 0x80, 0xff, 0x22, 0x7d]
        ];
        foreach (byte[] bytes in damaged)
        {
            temporary.Write(bytes);
            Assert.Throws<InvalidDataException>(() => temporary.Store.Load());
            Assert.Throws<InvalidDataException>(() => temporary.Store.Save(new()));
            Assert.That(File.ReadAllBytes(temporary.Store.FilePath), Is.EqualTo(bytes));
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(temporary.Store.FilePath)!, "*.tmp"), Is.Empty);
        }
    }

    [Test]
    public void SaveRereadsTheActualFileInsteadOfTrustingAPreviouslySuccessfulLoad()
    {
        using TemporaryProfile temporary = new();
        HotkeyPreferencesStore store = temporary.Store;
        store.Save(new());
        HotkeyBindings draft = store.Load();
        draft.Set("FileViewer", 0, new(Key.F11, KeyModifiers.Meta));
        byte[] damaged = Encoding.UTF8.GetBytes("{changed after the good load");
        temporary.Write(damaged);
        int notifications = 0;
        Action<string> handler = _ => notifications++;
        HotkeyPreferencesStore.Saved += handler;
        try
        {
            Assert.Catch<JsonException>(() => store.Save(draft));
            Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(damaged));
            Assert.That(notifications, Is.Zero);
            Assert.That(draft.Get("FileViewer", 0)!.KeyModifiers, Is.EqualTo(KeyModifiers.Meta));
        }
        finally { HotkeyPreferencesStore.Saved -= handler; }
    }

    [Test]
    public void SavedPublishesTheFullAbsolutePathOnlyAfterTheNewFileCanBeLoadedAndNoTemporaryFileRemains()
    {
        using TemporaryProfile temporary = new();
        HotkeyPreferencesStore store = temporary.Store;
        HotkeyBindings draft = new();
        draft.Set("FileViewer", 0, new(Key.F11, KeyModifiers.Meta));
        List<string> notifications = [];
        Action<string> handler = path =>
        {
            notifications.Add(path);
            Assert.That(path, Is.EqualTo(store.FilePath));
            Assert.That(Path.IsPathFullyQualified(path), Is.True);
            HotkeyBindings observed = new HotkeyPreferencesStore(temporary.Directory).Load();
            Assert.That(observed.Get("FileViewer", 0)!.Key, Is.EqualTo(Key.F11));
            Assert.That(observed.Get("FileViewer", 0)!.KeyModifiers, Is.EqualTo(KeyModifiers.Meta));
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"), Is.Empty);
        };
        HotkeyPreferencesStore.Saved += handler;
        try
        {
            store.Save(draft);
            Assert.That(notifications, Is.EqualTo(new[] { store.FilePath }));
            store.Load();
            Assert.That(notifications.Count, Is.EqualTo(1), "A read is not a save event.");
            store.Save(draft.Clone());
            Assert.That(notifications, Is.EqualTo(new[] { store.FilePath, store.FilePath }));
        }
        finally { HotkeyPreferencesStore.Saved -= handler; }
    }

    [Test]
    public void SerializationIsStableOrderedAndContainsLogicalKeysAndIdsRatherThanLabelsOrEnumOrdinals()
    {
        using TemporaryProfile temporary = new();
        HotkeyBindings bindings = new();
        KeyModifiers all = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta;
        bindings.Set("FileViewer", 0, new(Key.K, all));
        temporary.Store.Save(bindings);
        byte[] first = File.ReadAllBytes(temporary.Store.FilePath);
        temporary.Store.Save(bindings.Clone());
        Assert.That(File.ReadAllBytes(temporary.Store.FilePath), Is.EqualTo(first));
        Assert.That(first.Take(3), Is.Not.EqualTo(new byte[] { 0xef, 0xbb, 0xbf }), "Output is plain UTF-8 without a BOM.");
        using JsonDocument document = JsonDocument.Parse(first);
        JsonElement[] entries = [.. document.RootElement.GetProperty("Bindings").EnumerateArray()];
        Assert.That(entries.Length, Is.EqualTo(171));
        string[] actualOrder = [.. entries.Select(entry => entry.GetProperty("Context").GetString() + ":" + entry.GetProperty("Code").GetInt32())];
        string[] expectedOrder = [.. HotkeyCatalog.Contexts.SelectMany(context => HotkeyCatalog.For(context).Select(command => context + ":" + command.Code))];
        Assert.That(actualOrder, Is.EqualTo(expectedOrder));
        Assert.That(actualOrder[0], Is.EqualTo("Commit:16"));
        Assert.That(actualOrder[^1], Is.EqualTo("Stash:2"));
        Assert.That(entries.Any(entry => entry.TryGetProperty("Name", out _)), Is.False);
        Assert.That(entries.Any(entry => entry.GetProperty("Context").GetString() == "Scripts"), Is.False);
        JsonElement viewer = entries.Single(entry => entry.GetProperty("Context").GetString() == "FileViewer" && entry.GetProperty("Code").GetInt32() == 0);
        Assert.That(viewer.GetProperty("Key").GetString(), Is.EqualTo("K"));
        Assert.That(viewer.GetProperty("Modifiers").EnumerateArray().Select(value => value.GetString()), Is.EqualTo(new[] { "Control", "Shift", "Alt", "Meta" }));
    }

    [Test]
    public void ABlockedProfileParentReportsFailureWithoutPublishingOrChangingTheBlocker()
    {
        using TemporaryProfile temporary = new();
        string blocker = Path.Combine(temporary.Directory, "blocked");
        byte[] before = Encoding.UTF8.GetBytes("literal preexisting file Ω");
        File.WriteAllBytes(blocker, before);
        HotkeyPreferencesStore store = new(blocker);
        int notifications = 0;
        Action<string> handler = _ => notifications++;
        HotkeyPreferencesStore.Saved += handler;
        try
        {
            Assert.Catch<IOException>(() => store.Save(new()));
            Assert.That(notifications, Is.Zero);
            Assert.That(File.ReadAllBytes(blocker), Is.EqualTo(before));
            Assert.That(Directory.GetFiles(temporary.Directory), Is.EqualTo(new[] { blocker }));
        }
        finally { HotkeyPreferencesStore.Saved -= handler; }
    }

    [Test]
    public void ADestinationDirectoryIsAnAccessErrorAndNeverAnEmptySuccessfulProfile()
    {
        using TemporaryProfile temporary = new();
        HotkeyPreferencesStore store = temporary.Store;
        Directory.CreateDirectory(store.FilePath);
        string sentinel = Path.Combine(store.FilePath, "do-not-replace");
        byte[] before = [0x00, 0xff, 0x41];
        File.WriteAllBytes(sentinel, before);
        int notifications = 0;
        Action<string> handler = _ => notifications++;
        HotkeyPreferencesStore.Saved += handler;
        try
        {
            Exception? loadError = Assert.Catch(() => store.Load());
            Exception? saveError = Assert.Catch(() => store.Save(new()));
            Assert.That(loadError is IOException or UnauthorizedAccessException, Is.True);
            Assert.That(saveError is IOException or UnauthorizedAccessException, Is.True);
            Assert.That(notifications, Is.Zero);
            Assert.That(File.ReadAllBytes(sentinel), Is.EqualTo(before));
            Assert.That(Directory.GetFileSystemEntries(store.FilePath), Is.EqualTo(new[] { sentinel }));
        }
        finally { HotkeyPreferencesStore.Saved -= handler; }
    }

    [Test]
    public void RemovingTheProfileRestoresDefaultsOnReadWithoutRecreatingItOrPublishing()
    {
        using TemporaryProfile temporary = new();
        HotkeyBindings changed = new();
        changed.Set("Commit", 11, null);
        temporary.Store.Save(changed);
        File.Delete(temporary.Store.FilePath);
        int notifications = 0;
        Action<string> handler = _ => notifications++;
        HotkeyPreferencesStore.Saved += handler;
        try
        {
            HotkeyBindings defaults = temporary.Store.Load();
            Assert.That(defaults.Get("Commit", "StageAll")!.Key, Is.EqualTo(Key.S));
            Assert.That(File.Exists(temporary.Store.FilePath), Is.False);
            Assert.That(notifications, Is.Zero);
        }
        finally { HotkeyPreferencesStore.Saved -= handler; }
    }

    [Test]
    public void MetaAndNoneSurviveNewInstancesWithoutFallingBackToControlOrTheOriginalChord()
    {
        using TemporaryProfile temporary = new();
        HotkeyBindings draft = new();
        draft.Set("Browse", 43, null);
        draft.Set("FileViewer", 0, new(Key.F11, KeyModifiers.Meta));
        temporary.Store.Save(draft);
        HotkeyBindings loaded = new HotkeyPreferencesStore(temporary.Directory).Load();
        Assert.That(loaded.Match("Browse", new() { Key = Key.T, KeyModifiers = KeyModifiers.Control }), Is.Null);
        Assert.That(loaded.Match("FileViewer", new() { Key = Key.F, KeyModifiers = KeyModifiers.Control }), Is.Null);
        Assert.That(loaded.Match("FileViewer", new() { Key = Key.F11, KeyModifiers = KeyModifiers.Control }), Is.Null);
        Assert.That(loaded.Match("FileViewer", new() { Key = Key.F11, KeyModifiers = KeyModifiers.Meta })!.Code, Is.EqualTo(0));
        Assert.That(loaded.Get("Browse", 43), Is.Null);
    }

    private static string Describe(KeyGesture? gesture) => gesture is null ? "None" : $"{gesture.Key}|{gesture.KeyModifiers}";

    private sealed class TemporaryProfile : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-hotkeys-" + Guid.NewGuid().ToString("N"));
        internal HotkeyPreferencesStore Store => new(Directory);
        internal TemporaryProfile() => System.IO.Directory.CreateDirectory(Directory);
        internal void Write(string text) => Write(Encoding.UTF8.GetBytes(text));
        internal void Write(byte[] bytes)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Store.FilePath)!);
            File.WriteAllBytes(Store.FilePath, bytes);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
