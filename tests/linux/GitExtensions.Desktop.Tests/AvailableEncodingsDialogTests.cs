using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AvailableEncodingsDialogTests
{
    [Test]
    public void CatalogueIncludesMultibyteCodePagesAndOriginalPlatformDefaultsWithoutWeakeningCodecs()
    {
        AvailableEncodingsCatalog catalog = new(culture: CultureInfo.GetCultureInfo("en-US"));
        string[] expected = new[] { Encoding.Default, Encoding.ASCII, Encoding.Unicode, new UTF8Encoding(false),
            Encoding.GetEncoding(CultureInfo.GetCultureInfo("en-US").TextInfo.OEMCodePage), Encoding.GetEncoding(0) }
            .Select(encoding => encoding.WebName).Distinct(StringComparer.Ordinal).ToArray();
        Assert.That(catalog.Defaults, Is.EqualTo(expected));
        Assert.That(catalog.All.Select(choice => choice.Name), Is.Unique);
        Assert.That(catalog.Resolve("shift_jis"), Is.Not.Null);
        Assert.That(catalog.Resolve("gb18030"), Is.Not.Null);
        foreach (string rare in new[] { "gb18030", "hz-gb-2312", "iso-2022-jp", "euc-kr", "x-iscii-de" })
        { Assert.That(catalog.Resolve(rare)?.Name, Is.EqualTo(GitFileEncodingCodec.GetEncoding(rare).WebName), rare); }
        GitFileEncodingChoice euc = catalog.Resolve("EUC-JP")!;
        Assert.That(euc.Name, Is.EqualTo(GitFileEncodingCodec.GetEncoding("EUC-JP").WebName));
        Assert.That(euc.DisplayName, Is.EqualTo(GitFileEncodingCodec.GetEncoding(euc.Name).EncodingName));
        Assert.That(catalog.Choices(["EUC-JP", "euc-jp"], "EUC-JP"), Is.EqualTo(new[] { euc }));
        Assert.That(catalog.Resolve("utf-7"), Is.Null);
        Assert.That(catalog.All.Select(choice => GitFileEncodingCodec.GetEncoding(choice.Name).CodePage), Does.Not.Contain(65000));
        Assert.That(catalog.All.Count, Is.GreaterThan(GitFileEncodingCodec.AvailableEncodings.Count));
        Assert.That(GitFileEncodingCodec.GetEncoding("utf-8").GetPreamble(), Is.Empty);
        Assert.That(GitFileEncodingCodec.GetEncoding("shift_jis").DecoderFallback, Is.InstanceOf<DecoderExceptionFallback>());
        Assert.That(GitFileEncodingCodec.SupportsPatchText(GitFileEncodingCodec.GetEncoding("utf-16")), Is.False);
        foreach (string name in new[] { "us-ascii", "utf-8", "utf-16", "utf-16BE", Encoding.Default.WebName })
        { Assert.That(catalog.IsProtected(name), Is.True, name); }
        Assert.That(catalog.IsProtected("utf-32"), Is.False);
        Assert.That(catalog.IsProtected("shift_jis"), Is.False);
    }

    [Test]
    public void InjectedCatalogueDoesNotAcquireProviderExtrasOrForeignChoices()
    {
        AvailableEncodingsCatalog catalog = new([Encoding.UTF8, Encoding.ASCII], CultureInfo.GetCultureInfo("en-US"));
        Assert.That(catalog.All.Select(choice => choice.Name), Is.EquivalentTo(new[] { "utf-8", "us-ascii" }));
        Assert.That(catalog.Defaults, Is.EqualTo(new[] { "utf-8", "us-ascii" }));
        Assert.That(catalog.Resolve("gb18030"), Is.Null); Assert.That(catalog.Resolve("shift_jis"), Is.Null);
    }

    [Test]
    public void MissingProfileAndUntouchedOkDoNotCreateFilesOrPublishAndExplicitProfilesStayIndependent()
    {
        using Profile first = new(); using Profile second = new();
        List<string> notifications = []; Action<string> saved = notifications.Add;
        AvailableEncodingsPreferencesStore.Saved += saved;
        try
        {
            AvailableEncodingsPreferencesLoadResult initial = first.Store.Load();
            Assert.That(initial.Existed, Is.False); Assert.That(initial.Error, Is.Null);
            Assert.That(initial.Names, Is.EqualTo(first.Store.Catalog.Defaults));
            Assert.That(first.Store.SaveChanges(initial, initial.Names).Names, Is.EqualTo(initial.Names));
            Assert.That(Directory.Exists(first.Root), Is.False); Assert.That(notifications, Is.Empty);
            first.Store.SaveChanges(initial, ["shift_jis"]);
            Assert.That(first.Store.Load().Names, Is.EqualTo(new[] { "shift_jis" }), "An existing subset is not silently expanded with protected defaults.");
            Assert.That(second.Store.Load().Names, Is.EqualTo(second.Store.Catalog.Defaults));
            Assert.That(() => second.Store.SaveChanges(first.Store.Load(), ["windows-1252"]), Throws.ArgumentException);
            Assert.That(Directory.Exists(second.Root), Is.False);
            Assert.That(notifications, Is.EqualTo(new[] { first.Store.FilePath }));
        }
        finally { AvailableEncodingsPreferencesStore.Saved -= saved; }
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("\t\r\n")]
    [TestCase("\u00a0")]
    public void EmptyOrWhitespacePreferredListReadsWindowsDefaultsWithoutRepairingTheProfile(string value)
    {
        using Profile profile = new();
        profile.Write(JsonSerializer.Serialize(new { Version = 1, AvailableEncodings = value, Future = new { raw = new[] { 1, 2 } } }));
        byte[] before = File.ReadAllBytes(profile.Store.FilePath);
        List<string> notifications = []; Action<string> saved = notifications.Add;
        AvailableEncodingsPreferencesStore.Saved += saved;
        try
        {
            AvailableEncodingsPreferencesLoadResult initial = profile.Store.Load();
            Assert.That(initial.Existed, Is.True); Assert.That(initial.Error, Is.Null);
            Assert.That(initial.Names, Is.EqualTo(profile.Store.Catalog.Defaults));
            Assert.That(initial.OriginalBytes.ToArray(), Is.EqualTo(before));
            AvailableEncodingsPreferencesLoadResult untouched = profile.Store.SaveChanges(initial, initial.Names);
            Assert.That(untouched.Names, Is.EqualTo(profile.Store.Catalog.Defaults)); Assert.That(untouched.Error, Is.Null);
            Assert.That(untouched.OriginalBytes.ToArray(), Is.EqualTo(before));
            Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(before)); Assert.That(notifications, Is.Empty);
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(profile.Store.FilePath)!), Is.EqualTo(new[] { profile.Store.FilePath }));
        }
        finally { AvailableEncodingsPreferencesStore.Saved -= saved; }
    }

    [Test]
    public void WindowsNamesAliasesDefaultAndIncludedOrderLoadWithoutRewritingOrNotifying()
    {
        using Profile profile = new();
        profile.Write("""{"Version":1,"AvailableEncodings":"ISO-8859-1;Default;utf-8;UTF-16;shift_jis;iso-8859-1","Future":{"raw":[1,2]}}""");
        byte[] before = File.ReadAllBytes(profile.Store.FilePath);
        List<string> notifications = []; Action<string> saved = notifications.Add;
        AvailableEncodingsPreferencesStore.Saved += saved;
        try
        {
            AvailableEncodingsPreferencesLoadResult initial = profile.Store.Load();
            Assert.That(initial.Error, Is.Null);
            Assert.That(initial.Names, Is.EqualTo(new[] { "iso-8859-1", Encoding.Default.WebName, "utf-8", "utf-16", "shift_jis" }.Distinct()));
            profile.Store.SaveChanges(initial, initial.Names);
            Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(before)); Assert.That(notifications, Is.Empty);
            AvailableEncodingsPreferencesLoadResult savedResult = profile.Store.SaveChanges(initial, initial.Names.Append("windows-1252"));
            Assert.That(savedResult.Names.Last(), Is.EqualTo("windows-1252"));
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(profile.Store.FilePath));
            Assert.That(document.RootElement.GetProperty("Future").GetProperty("raw").EnumerateArray().Select(item => item.GetInt32()), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(document.RootElement.GetProperty("AvailableEncodings").GetString(), Does.Contain("Default"));
            Assert.That(notifications, Is.EqualTo(new[] { profile.Store.FilePath }));
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(profile.Store.FilePath)!, "*.tmp"), Is.Empty);
        }
        finally { AvailableEncodingsPreferencesStore.Saved -= saved; }
    }

    [TestCase("{broken")]
    [TestCase("[]")]
    [TestCase("null")]
    [TestCase("{}")]
    [TestCase("{\"Version\":\"1\",\"AvailableEncodings\":\"utf-8\"}")]
    [TestCase("{\"Version\":2,\"AvailableEncodings\":\"utf-8\"}")]
    [TestCase("{\"Version\":1,\"AvailableEncodings\":[]}")]
    [TestCase("{\"Version\":1,\"AvailableEncodings\":\"utf-7\"}")]
    [TestCase("{\"Version\":1,\"AvailableEncodings\":\"unknown-codec\"}")]
    [TestCase("{\"Version\":1,\"AvailableEncodings\":\"utf-8;;shift_jis\"}")]
    [TestCase("{\"Version\":1,\"AvailableEncodings\":\"utf-8\",\"AvailableEncodings\":\"shift_jis\"}")]
    public void MalformedOrUnsupportedProfilesAreObservableAndCannotBeOverwritten(string contents)
    {
        using Profile profile = new(); profile.Write(contents); byte[] before = File.ReadAllBytes(profile.Store.FilePath);
        AvailableEncodingsPreferencesLoadResult result = profile.Store.Load();
        Assert.That(result.Error, Is.Not.Null); Assert.That(result.Existed, Is.True);
        Assert.That(result.Names, Is.EqualTo(profile.Store.Catalog.Defaults)); Assert.That(result.OriginalBytes.ToArray(), Is.EqualTo(before));
        Assert.That(() => profile.Store.SaveChanges(result, result.Names), Throws.InstanceOf<InvalidDataException>());
        Assert.That(() => profile.Store.SaveChanges(result, ["shift_jis"]), Throws.InstanceOf<InvalidDataException>());
        Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(before));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(profile.Store.FilePath)!), Is.EqualTo(new[] { profile.Store.FilePath }));
    }

    [Test]
    public void InvalidUtf8AndStaleRawUnknownPropertiesRemainExactAndDoNotPublish()
    {
        using Profile profile = new(); Directory.CreateDirectory(Path.GetDirectoryName(profile.Store.FilePath)!);
        byte[] invalid = [0x7b, 0x22, 0xff, 0x22, 0x7d]; File.WriteAllBytes(profile.Store.FilePath, invalid);
        AvailableEncodingsPreferencesLoadResult bad = profile.Store.Load();
        Assert.That(bad.Error, Is.InstanceOf<DecoderFallbackException>());
        Assert.That(() => profile.Store.SaveChanges(bad, ["utf-8"]), Throws.InstanceOf<InvalidDataException>());
        Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(invalid));
        profile.Write("""{"Version":1,"AvailableEncodings":"utf-8","Future":1}""");
        AvailableEncodingsPreferencesLoadResult baseline = profile.Store.Load();
        profile.Write("""{"Version":1,"AvailableEncodings":"utf-8","Future":2}"""); byte[] concurrent = File.ReadAllBytes(profile.Store.FilePath);
        Assert.That(() => profile.Store.SaveChanges(baseline, ["utf-8", "shift_jis"]), Throws.InstanceOf<IOException>());
        Assert.That(() => profile.Store.SaveChanges(baseline, baseline.Names), Throws.InstanceOf<IOException>());
        Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(concurrent));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(profile.Store.FilePath)!, "*.tmp"), Is.Empty);
    }

    [Test]
    public void StoreResolvesXdgPerInstanceAndDoesNotCachePreferencesAcrossProfiles()
    {
        using Profile first = new(); using Profile second = new(); string? original = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", first.Root); AvailableEncodingsPreferencesStore one = new();
            one.SaveChanges(one.Load(), ["shift_jis"]);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", second.Root); AvailableEncodingsPreferencesStore two = new();
            Assert.That(one.FilePath, Is.EqualTo(first.Store.FilePath)); Assert.That(two.FilePath, Is.EqualTo(second.Store.FilePath));
            Assert.That(one.Load().Names, Is.EqualTo(new[] { "shift_jis" })); Assert.That(two.Load().Existed, Is.False);
            Assert.That(Directory.Exists(second.Root), Is.False);
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", original); }
    }

    [Test]
    public void RealIncompatibleFilesystemEntryAndInvalidDraftCannotPublish()
    {
        using Profile profile = new(); AvailableEncodingsPreferencesLoadResult baseline = profile.Store.Load();
        Assert.That(() => profile.Store.SaveChanges(baseline, ["utf-7"]), Throws.ArgumentException);
        Assert.That(Directory.Exists(profile.Root), Is.False);
        Directory.CreateDirectory(profile.Store.FilePath);
        Assert.That(profile.Store.Load().Error, Is.Not.Null);
        Assert.That(() => profile.Store.SaveChanges(baseline, ["shift_jis"]), Throws.InstanceOf<InvalidDataException>());
        Assert.That(Directory.Exists(profile.Store.FilePath), Is.True);
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(profile.Store.FilePath)!), Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task OriginalFrenchChooserUsesTwoListsProtectedBaseRowsClassicThemeAndCancelDiscardsDraft(bool dark)
    {
        using UiScope scope = new(); Window owner = scope.Owner(dark);
        TranslationCatalog french = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo("fr-FR"));
        AvailableEncodingsDialog dialog = new(scope.Profile.Store, translations: french);
        Task<bool> completion = dialog.ShowDialog<bool>(owner); Pump(dialog);
        Assert.That(dialog.Owner, Is.SameAs(owner)); Assert.That(dialog.Title, Is.EqualTo("Configurer les encodages disponibles"));
        Assert.That(Field<TextBlock>(dialog, "lSelectedEncodings").Text, Is.EqualTo("Sélectionné :"));
        Assert.That(Field<TextBlock>(dialog, "lAvaolableEncodings").Text, Is.EqualTo("Disponible :"));
        Assert.That(Field<Button>(dialog, "ButtonCancel").Content, Is.EqualTo("Annuler"));
        Assert.That(Field<Button>(dialog, "ButtonOk").FontSize, Is.EqualTo(DesktopFonts.For(DesktopFontRole.Application).SizeInDips));
        Assert.That(((ISolidColorBrush)dialog.Background!).Color, Is.EqualTo(Color.Parse(ClassicStyles.OriginalColors(dark)["PanelBackground"])));
        ListBox included = Field<ListBox>(dialog, "ListIncludedEncodings"), available = Field<ListBox>(dialog, "ListAvailableEncodings");
        foreach (string name in new[] { "us-ascii", "utf-16", "utf-8" })
        { Select(included, name); Assert.That(Field<Button>(dialog, "ToRight").IsEnabled, Is.False, name); }
        Assert.That(available.Items.OfType<GitFileEncodingChoice>().Any(choice => choice.Name == "utf-7"), Is.False);
        Select(available, "shift_jis"); Click(dialog, "ToLeft");
        Assert.That(Names(included).Last(), Is.EqualTo("shift_jis")); Assert.That(Names(available), Does.Not.Contain("shift_jis"));
        Assert.That(Field<Button>(dialog, "ToRight").IsEnabled, Is.True); Click(dialog, "ToRight");
        Assert.That(Names(included), Does.Not.Contain("shift_jis")); Assert.That(Names(available), Does.Contain("shift_jis"));
        Select(available, "utf-16BE"); Click(dialog, "ToLeft"); Assert.That(Field<Button>(dialog, "ToRight").IsEnabled, Is.False);
        Field<Button>(dialog, "ToRight").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.That(Names(included), Does.Contain("utf-16BE"), "The protected row cannot be removed even by an unsolicited click event.");
        Click(dialog, "ButtonCancel"); Assert.That(await completion, Is.False);
        Assert.That(File.Exists(scope.Profile.Store.FilePath), Is.False); Assert.That(owner.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public async Task OnlyAcceptedChooserPublishesAndUntouchedOkNeverNotifiesAgain()
    {
        using UiScope scope = new(); Window owner = scope.Owner();
        List<string> notifications = []; Action<string> saved = notifications.Add; AvailableEncodingsPreferencesStore.Saved += saved;
        try
        {
            AvailableEncodingsDialog cancelled = new(scope.Profile.Store); Task<bool> cancelledTask = cancelled.ShowDialog<bool>(owner); Pump(cancelled);
            Select(Field<ListBox>(cancelled, "ListAvailableEncodings"), "shift_jis"); Click(cancelled, "ToLeft"); Click(cancelled, "ButtonCancel");
            Assert.That(await cancelledTask, Is.False); Assert.That(notifications, Is.Empty); Assert.That(File.Exists(scope.Profile.Store.FilePath), Is.False);
            AvailableEncodingsDialog accepted = new(scope.Profile.Store); Task<bool> acceptedTask = accepted.ShowDialog<bool>(owner); Pump(accepted);
            Select(Field<ListBox>(accepted, "ListAvailableEncodings"), "shift_jis"); Click(accepted, "ToLeft"); Click(accepted, "ButtonOk");
            Assert.That(await acceptedTask, Is.True); Assert.That(accepted.Result.Names.Last(), Is.EqualTo("shift_jis"));
            Assert.That(scope.Profile.Store.Load().Names, Is.EqualTo(accepted.Result.Names)); Assert.That(notifications, Is.EqualTo(new[] { scope.Profile.Store.FilePath }));
            byte[] bytes = File.ReadAllBytes(scope.Profile.Store.FilePath);
            AvailableEncodingsDialog untouched = new(scope.Profile.Store); Task<bool> untouchedTask = untouched.ShowDialog<bool>(owner); Pump(untouched); Click(untouched, "ButtonOk");
            Assert.That(await untouchedTask, Is.True); Assert.That(File.ReadAllBytes(scope.Profile.Store.FilePath), Is.EqualTo(bytes)); Assert.That(notifications, Has.Count.EqualTo(1));
        }
        finally { AvailableEncodingsPreferencesStore.Saved -= saved; }
    }

    [AvaloniaTest]
    [TestCase("")]
    [TestCase(" \t\r\n")]
    public async Task EmptyPreferredListChooserUsesDefaultsAndOnlyAnExplicitChangedOkPublishes(string value)
    {
        using UiScope scope = new(); Window owner = scope.Owner();
        scope.Profile.Write(JsonSerializer.Serialize(new { Version = 1, AvailableEncodings = value, Future = new { raw = new[] { 1, 2 } } }));
        byte[] before = File.ReadAllBytes(scope.Profile.Store.FilePath);
        List<string> notifications = []; Action<string> saved = notifications.Add;
        AvailableEncodingsPreferencesStore.Saved += saved;
        try
        {
            AvailableEncodingsDialog cancelled = new(scope.Profile.Store); Task<bool> cancelledTask = cancelled.ShowDialog<bool>(owner); Pump(cancelled);
            ListBox included = Field<ListBox>(cancelled, "ListIncludedEncodings");
            Assert.That(Names(included), Is.EqualTo(scope.Profile.Store.Catalog.Defaults)); Assert.That(cancelled.LastError, Is.Null);
            Assert.That(Field<TextBlock>(cancelled, "AvailableEncodingsError").IsVisible, Is.False);
            Assert.That(Field<Button>(cancelled, "ButtonOk").IsEnabled, Is.True);
            foreach (string name in scope.Profile.Store.Catalog.Defaults.Where(scope.Profile.Store.Catalog.IsProtected))
            { Select(included, name); Assert.That(Field<Button>(cancelled, "ToRight").IsEnabled, Is.False, name); }
            Select(Field<ListBox>(cancelled, "ListAvailableEncodings"), "shift_jis"); Click(cancelled, "ToLeft");
            Assert.That(Names(included).Last(), Is.EqualTo("shift_jis")); Click(cancelled, "ButtonCancel");
            Assert.That(await cancelledTask, Is.False); Assert.That(File.ReadAllBytes(scope.Profile.Store.FilePath), Is.EqualTo(before));
            Assert.That(notifications, Is.Empty); Assert.That(owner.OwnedWindows, Is.Empty);

            AvailableEncodingsDialog untouched = new(scope.Profile.Store); Task<bool> untouchedTask = untouched.ShowDialog<bool>(owner); Pump(untouched);
            Click(untouched, "ButtonOk"); Assert.That(await untouchedTask, Is.True);
            Assert.That(untouched.Result.Names, Is.EqualTo(scope.Profile.Store.Catalog.Defaults));
            Assert.That(File.ReadAllBytes(scope.Profile.Store.FilePath), Is.EqualTo(before)); Assert.That(notifications, Is.Empty);

            AvailableEncodingsDialog accepted = new(scope.Profile.Store); Task<bool> acceptedTask = accepted.ShowDialog<bool>(owner); Pump(accepted);
            Select(Field<ListBox>(accepted, "ListAvailableEncodings"), "shift_jis"); Click(accepted, "ToLeft"); Click(accepted, "ButtonOk");
            Assert.That(await acceptedTask, Is.True);
            Assert.That(accepted.Result.Names, Is.EqualTo(scope.Profile.Store.Catalog.Defaults.Append("shift_jis")));
            Assert.That(scope.Profile.Store.Load().Names, Is.EqualTo(accepted.Result.Names));
            Assert.That(notifications, Is.EqualTo(new[] { scope.Profile.Store.FilePath }));
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(scope.Profile.Store.FilePath));
            Assert.That(document.RootElement.GetProperty("Future").GetProperty("raw").EnumerateArray().Select(item => item.GetInt32()), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(scope.Profile.Store.FilePath)!, "*.tmp"), Is.Empty);
            Assert.That(owner.OwnedWindows, Is.Empty);
        }
        finally { AvailableEncodingsPreferencesStore.Saved -= saved; }
    }

    [AvaloniaTest]
    public async Task MalformedAndStaleErrorsStayVisibleAndCancelNeverReplacesTheirBytes()
    {
        using UiScope scope = new(); Window owner = scope.Owner(); scope.Profile.Write("{damaged");
        byte[] damaged = File.ReadAllBytes(scope.Profile.Store.FilePath);
        AvailableEncodingsDialog bad = new(scope.Profile.Store); Task<bool> badTask = bad.ShowDialog<bool>(owner); Pump(bad);
        Assert.That(bad.LastError, Does.Contain("Unable to read")); Assert.That(Field<TextBlock>(bad, "AvailableEncodingsError").IsVisible, Is.True);
        Assert.That(Field<Button>(bad, "ButtonOk").IsEnabled, Is.False); Click(bad, "ButtonCancel"); Assert.That(await badTask, Is.False);
        Assert.That(File.ReadAllBytes(scope.Profile.Store.FilePath), Is.EqualTo(damaged));
        File.Delete(scope.Profile.Store.FilePath);
        AvailableEncodingsDialog stale = new(scope.Profile.Store); Task<bool> staleTask = stale.ShowDialog<bool>(owner); Pump(stale);
        Select(Field<ListBox>(stale, "ListAvailableEncodings"), "shift_jis"); Click(stale, "ToLeft");
        scope.Profile.Write("""{"Version":1,"AvailableEncodings":"windows-1252","Concurrent":true}"""); byte[] concurrent = File.ReadAllBytes(scope.Profile.Store.FilePath);
        Click(stale, "ButtonOk"); Assert.That(stale.IsVisible, Is.True); Assert.That(stale.LastError, Does.Contain("changed after"));
        Click(stale, "ButtonCancel"); Assert.That(await staleTask, Is.False); Assert.That(File.ReadAllBytes(scope.Profile.Store.FilePath), Is.EqualTo(concurrent));
    }

    [AvaloniaTest]
    public async Task ClosingOwnerCancelsChooserWithoutPublishingAndReleasesOwnership()
    {
        using UiScope scope = new(); Window owner = scope.Owner(); AvailableEncodingsDialog dialog = new(scope.Profile.Store);
        Task<bool> completion = dialog.ShowDialog<bool>(owner); Pump(dialog);
        Select(Field<ListBox>(dialog, "ListAvailableEncodings"), "shift_jis"); Click(dialog, "ToLeft"); owner.Close(); Pump(owner);
        Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
        Assert.That(dialog.IsVisible, Is.False); Assert.That(File.Exists(scope.Profile.Store.FilePath), Is.False); Assert.That(owner.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public void SharedViewerMenusRefreshOnlyTheirProfileAndPreserveCurrentEncodingAfterRemoval()
    {
        using UiScope scope = new(); using Profile other = new();
        scope.Profile.Store.SaveChanges(scope.Profile.Store.Load(), ["utf-8", "shift_jis", "windows-1252"]);
        using DiffView view = new(new TextBox(), new DiffViewerPreferencesStore(scope.Profile.Root)); view.FileEncodingName = "shift_jis";
        using DiffViewerReadingMenu menu = new(view, scope.Profile.Store);
        ComboBox first = menu.CreateEncodingSelector(), second = menu.CreateEncodingSelector();
        Window owner = scope.Owner(); owner.Content = new StackPanel { Children = { first, second, view } }; Pump(owner);
        List<string?> changes = []; view.FileEncodingChanged += (_, _) => changes.Add(view.FileEncodingName);
        IReadOnlyList<GitFileEncodingChoice> strictChoices = GitFileEncodingCodec.AvailableEncodings;
        scope.Profile.Store.SaveChanges(scope.Profile.Store.Load(), ["utf-8", "windows-1252"]); Pump(owner);
        Assert.That(first.Items.OfType<DiffViewerEncodingChoice>().Select(choice => choice.Name), Is.EqualTo(new string?[] { null, "utf-8", "windows-1252", "shift_jis" }));
        Assert.That(((DiffViewerEncodingChoice)first.SelectedItem!).Name, Is.EqualTo("shift_jis")); Assert.That(second.SelectedItem, Is.EqualTo(first.SelectedItem));
        Assert.That(view.FileEncodingName, Is.EqualTo("shift_jis")); Assert.That(view.DefaultFileEncodingName, Is.EqualTo("utf-8")); Assert.That(changes, Is.Empty);
        object? previous = first.ItemsSource; other.Store.SaveChanges(other.Store.Load(), ["gb18030"]); Pump(owner);
        Assert.That(first.ItemsSource, Is.SameAs(previous)); Assert.That(GitFileEncodingCodec.AvailableEncodings, Is.SameAs(strictChoices));
        scope.Profile.Write("{bad external change"); first.IsDropDownOpen = true; Pump(owner);
        Assert.That(menu.EncodingPreferencesError, Is.Not.Null); Assert.That(ToolTip.GetTip(first)?.ToString(), Does.Contain("Unable to read"));
        Assert.That(((DiffViewerEncodingChoice)first.SelectedItem!).Name, Is.EqualTo("shift_jis")); Assert.That(changes, Is.Empty); first.IsDropDownOpen = false;
        owner.Content = null;
    }

    private static IEnumerable<string> Names(ListBox list) => list.Items.OfType<GitFileEncodingChoice>().Select(choice => choice.Name);
    private static void Select(ListBox list, string name) => list.SelectedItem = list.Items.OfType<GitFileEncodingChoice>().Single(choice => choice.Name == name);
    private static T Field<T>(TopLevel root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Window window, string name) => PushProcessTestDriver.Pointer(Field<Button>(window, name));
    private static void Pump(TopLevel root) { root.UpdateLayout(); Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); }

    private sealed class Profile : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-encodings-" + Guid.NewGuid().ToString("N"));
        internal AvailableEncodingsPreferencesStore Store { get; }
        internal Profile() => Store = new(Root, new(culture: CultureInfo.GetCultureInfo("en-US")));
        internal void Write(string value) { Directory.CreateDirectory(Path.GetDirectoryName(Store.FilePath)!); File.WriteAllText(Store.FilePath, value); }
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, true); } }
    }
    private sealed class UiScope : IDisposable
    {
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly List<Window> _owners = [];
        internal Profile Profile { get; } = new();
        internal UiScope() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Profile.Root);
        internal Window Owner(bool dark = false)
        {
            Window owner = new() { Width = 760, Height = 500, Content = new TextBlock { Text = "Owner" }, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            _owners.Add(owner); owner.Show(); Pump(owner); return owner;
        }
        public void Dispose()
        {
            foreach (Window owner in _owners)
            { foreach (Window child in owner.OwnedWindows.ToArray()) { child.Close(); } owner.Close(); }
            Dispatcher.UIThread.RunJobs(); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg); Profile.Dispose();
        }
    }
}
