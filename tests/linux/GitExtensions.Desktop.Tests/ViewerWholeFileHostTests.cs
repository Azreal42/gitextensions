using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class ViewerWholeFileHostTests
{
    private const string LiteralPath = ":whole[1]* λ.txt";
    private const string ChangedCode = "edited high λ 🧪";

    [AvaloniaTest]
    public async Task MainChangesFocusedControlETogglesWholeFileAndKeepsSelectionThroughOriginalMenuAndIcon()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        MainWindow main = fixture.Track(new MainWindow { Width = 1440, Height = 900 });
        main.Show();
        await main.OpenRepositoryAsync(fixture.Repository.Path);
        await Until(() => !main.IsBusy);
        TabControl tabs = Find<TabControl>(main, "DetailsTabs");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "ChangesTab");
        main.UpdateLayout();
        FileStatusTestHelpers.Select(Find<TreeView>(main, "WorkingFiles"), LiteralPath);
        DiffView view = Find<DiffView>(main, "WorktreeDiff");
        await Until(() => !main.IsBusy && view.CanStagePatch && view.Text.Contains(ChangedCode, StringComparison.Ordinal));
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
        AssertPartial(view);
        SelectCode(view);
        string identity = view.DocumentIdentity!;
        MenuItem menu = EntireMenu(view, "WorktreeDiffEntireFileMenu");
        Button button = Find<Button>(main, "WorktreeDiffEntireFile");
        AssertOriginalChrome(main, menu, button);
        await fixture.RecordReadOnlyStateAsync();

        GitDiffDocument previous = view.Document!;
        Focus(view);
        Press(main, PhysicalKey.E);
        await Until(() => !main.IsBusy && !ReferenceEquals(view.Document, previous) && IsEntire(view));
        Assert.That(menu.IsChecked, Is.True);
        AssertReadingPosition(view, identity);
        Assert.That(view.IsKeyboardFocusWithin, Is.True, "The Changes viewer must consume CtrlE before the history filter command.");

        previous = view.Document!;
        Focus(view);
        Press(main, PhysicalKey.E);
        await Until(() => !main.IsBusy && !ReferenceEquals(view.Document, previous) && !IsEntire(view));
        Assert.That(menu.IsChecked, Is.False);
        AssertPartial(view);
        AssertReadingPosition(view, identity);
        await fixture.AssertReadOnlyAsync();

        previous = view.Document!;
        menu.IsChecked = true;
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => !main.IsBusy && !ReferenceEquals(view.Document, previous) && IsEntire(view));
        AssertReadingPosition(view, identity);
        previous = view.Document!;
        Click(button);
        await Until(() => !main.IsBusy && !ReferenceEquals(view.Document, previous) && !IsEntire(view));
        Assert.That(menu.IsChecked, Is.False);
        AssertPartial(view);
        AssertReadingPosition(view, identity);
        await fixture.AssertReadOnlyAsync();
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
    }

    [AvaloniaTest]
    public async Task CommitFocusedControlETogglesWholeFileButDoesNotCaptureTheMessageEditorsShortcut()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        CommitDialog dialog = fixture.Track(new CommitDialog(fixture.Repository));
        dialog.Show();
        await dialog.InitializeAsync();
        FileStatusTestHelpers.Select(Find<TreeView>(dialog, "Unstaged"), LiteralPath);
        DiffView view = Find<DiffView>(dialog, "SelectedDiff");
        await Until(() => !dialog.IsBusy && view.CanStagePatch && view.Text.Contains(ChangedCode, StringComparison.Ordinal));
        Assert.That(dialog.LastError, Is.Null, dialog.LastError);
        AssertPartial(view);
        SelectCode(view);
        string identity = view.DocumentIdentity!;
        MenuItem menu = EntireMenu(view, "CommitDiffEntireFileMenu");
        Button button = Find<Button>(dialog, "CommitDiffEntireFile");
        AssertOriginalChrome(dialog, menu, button);
        await fixture.RecordReadOnlyStateAsync();

        GitDiffDocument previous = view.Document!;
        Focus(view);
        Press(dialog, PhysicalKey.E);
        await Until(() => !dialog.IsBusy && !ReferenceEquals(view.Document, previous) && IsEntire(view));
        Assert.That(menu.IsChecked, Is.True);
        AssertReadingPosition(view, identity);
        previous = view.Document!;
        Focus(view);
        Press(dialog, PhysicalKey.E);
        await Until(() => !dialog.IsBusy && !ReferenceEquals(view.Document, previous) && !IsEntire(view));
        Assert.That(menu.IsChecked, Is.False);
        AssertPartial(view);
        AssertReadingPosition(view, identity);

        TextBox message = Find<TextBox>(dialog, "Message");
        message.Text = "Message keeps its own command scope";
        message.Focus();
        Assert.That(message.IsKeyboardFocusWithin, Is.True);
        previous = view.Document!;
        Press(dialog, PhysicalKey.E);
        await Task.Delay(75);
        Assert.That(message.IsKeyboardFocusWithin, Is.True);
        Assert.That(message.Text, Is.EqualTo("Message keeps its own command scope"));
        Assert.That(view.Document, Is.SameAs(previous), "CtrlE outside the diff must not queue a whole-file reload.");
        Assert.That(menu.IsChecked, Is.False);
        Assert.That(dialog.IsBusy, Is.False);
        AssertPartial(view);
        AssertReadingPosition(view, identity);
        await fixture.AssertReadOnlyAsync();

        previous = view.Document!;
        menu.IsChecked = true;
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => !dialog.IsBusy && !ReferenceEquals(view.Document, previous) && IsEntire(view));
        AssertReadingPosition(view, identity);
        previous = view.Document!;
        Click(button);
        await Until(() => !dialog.IsBusy && !ReferenceEquals(view.Document, previous) && !IsEntire(view));
        Assert.That(menu.IsChecked, Is.False);
        AssertPartial(view);
        AssertReadingPosition(view, identity);
        await fixture.AssertReadOnlyAsync();
        Assert.That(dialog.LastError, Is.Null, dialog.LastError);
        Assert.That(dialog.HasRepositoryChanges, Is.False);
        Assert.That(dialog.HasCommitted, Is.False);
    }

    private static void AssertOriginalChrome(Window owner, MenuItem menu, Button button)
    {
        RevealToolbar(button);
        owner.UpdateLayout();
        Assert.That(menu.Header, Is.InstanceOf<Avalonia.Controls.Primitives.AccessText>());
        Assert.That(((Avalonia.Controls.Primitives.AccessText)menu.Header!).Text, Is.EqualTo("Show _entire file"));
        Assert.That(menu.ToggleType, Is.EqualTo(MenuItemToggleType.CheckBox));
        Assert.That(menu.InputGesture?.Key, Is.EqualTo(Key.E));
        Assert.That(menu.InputGesture?.KeyModifiers, Is.EqualTo(KeyModifiers.Control));
        Assert.That(button.IsEnabled && button.IsEffectivelyVisible, Is.True);
        Assert.That(button.Bounds.Width, Is.EqualTo(24));
        Assert.That(button.Bounds.Height, Is.EqualTo(24));
        Assert.That(ToolTip.GetTip(button), Is.EqualTo("Show entire file (Ctrl+E)"));
        Image buttonIcon = button.GetVisualDescendants().OfType<Image>().Single();
        AssertOriginalIcon(buttonIcon);
        Assert.That(menu.Icon, Is.InstanceOf<Image>());
        AssertOriginalIcon((Image)menu.Icon!);
    }

    private static void AssertOriginalIcon(Image image)
    {
        Assert.That(image.Width, Is.EqualTo(16));
        Assert.That(image.Height, Is.EqualTo(16));
        Assert.That(image.Source, Is.InstanceOf<Bitmap>());
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/ShowEntireFile.png"));
        using Bitmap expected = new(stream);
        using MemoryStream expectedPixels = new();
        using MemoryStream actualPixels = new();
        expected.Save(expectedPixels);
        ((Bitmap)image.Source!).Save(actualPixels);
        Assert.That(actualPixels.ToArray(), Is.EqualTo(expectedPixels.ToArray()), "Both commands must retain the original ShowEntireFile PNG.");
    }

    private static MenuItem EntireMenu(DiffView view, string name)
    {
        Assert.That(view.ContextMenu, Is.Not.Null);
        return view.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == name);
    }
    private static void SelectCode(DiffView view)
    {
        int start = view.Text.IndexOf(ChangedCode, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        view.Select(start, ChangedCode.Length);
        Assert.That(view.CurrentFileLine, Is.EqualTo(180));
    }
    private static void AssertReadingPosition(DiffView view, string identity)
    {
        Assert.That(view.DocumentIdentity, Is.EqualTo(identity));
        Assert.That(view.CurrentFileLine, Is.EqualTo(180));
        Assert.That(view.Text.Substring(view.SelectionStart, view.SelectionLength), Is.EqualTo(ChangedCode));
        Assert.That(view.Document, Is.Not.Null);
        Assert.That(view.Document!.IsLosslessPatch, Is.True);
        TopLevel.GetTopLevel(view)?.UpdateLayout();
        var textView = view.Editor.TextArea.TextView;
        textView.EnsureVisualLines();
        int row = view.Editor.Document.GetLineByOffset(view.SelectionStart).LineNumber;
        var visualLine = textView.GetVisualLine(row);
        Assert.That(visualLine, Is.Not.Null, "The selected file line must actually be rendered in the viewport after a context reload.");
        double top = visualLine!.VisualTop - textView.VerticalOffset;
        Assert.That(top, Is.GreaterThanOrEqualTo(-0.5), "The retained selection must not sit above the visible viewport.");
        Assert.That(top + visualLine.Height, Is.LessThanOrEqualTo(textView.Bounds.Height + 0.5), "The retained file line must remain visible, not merely keep its caret offset below the viewport.");
    }
    private static bool IsEntire(DiffView view) => view.Text.Contains(" line 001\n", StringComparison.Ordinal) && view.Editor.Document.LineCount > 240;
    private static void AssertPartial(DiffView view)
    {
        Assert.That(view.Editor.Document.LineCount, Is.LessThan(30));
        Assert.That(view.MaxFileLine, Is.GreaterThan(180));
        Assert.That(view.Text, Does.Not.Contain(" line 001\n"));
    }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Focus(DiffView view)
    { view.Editor.TextArea.Focus(); Assert.That(view.IsKeyboardFocusWithin, Is.True); }
    private static void Press(Window owner, PhysicalKey key)
    { owner.KeyPressQwerty(key, RawInputModifiers.Control); owner.KeyReleaseQwerty(key, RawInputModifiers.Control); }
    private static void Click(Button button)
    { RevealToolbar(button); Assert.That(button.IsEnabled && button.IsEffectivelyVisible, Is.True); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static void RevealToolbar(Button button)
    {
        if (button.GetVisualAncestors().OfType<DiffViewerToolbar>().FirstOrDefault() is not { } toolbar) { return; }
        DiffView view = toolbar.GetVisualAncestors().OfType<DiffView>().Single();
        Window owner = (Window)TopLevel.GetTopLevel(view)!;
        owner.MouseMove(view.Editor.TranslatePoint(new Point(80, 80), owner)!.Value);
        Dispatcher.UIThread.RunJobs(); owner.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.That(toolbar.IsEffectivelyVisible, Is.True, "Original viewer commands become visible by moving over its text.");
    }
    private static async Task Until(Func<bool> predicate)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20)); while (!predicate()) { await Task.Delay(20, timeout.Token); } }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        private readonly List<Window> _windows = [];
        private readonly string _temporary = Path.Combine(Path.GetTempPath(), "gitextensions-viewer-whole-file-" + Guid.NewGuid().ToString("N"));
        private byte[] _original = [];
        private byte[] _changed = [];
        private byte[] _indexFile = [];
        private byte[] _configFile = [];
        public GitRepository Repository { get; private set; } = null!;
        public T Track<T>(T window) where T : Window { _windows.Add(window); return window; }

        public static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(fixture._temporary, "configuration"));
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
                fixture.Repository = await GitRepository.InitAsync(Path.Combine(fixture._temporary, "repository"), new GitInitOptions("main"));
                await fixture.Repository.SetConfigAsync("user.name", "Whole File Tester");
                await fixture.Repository.SetConfigAsync("user.email", "whole-file@example.invalid");
                await fixture.Repository.SetConfigAsync("commit.gpgsign", "false");
                await fixture.Repository.SetConfigAsync("core.autocrlf", "false");
                await fixture.Repository.SetConfigAsync("core.hooksPath", Path.Combine(fixture._temporary, "no-hooks"));
                string original = string.Concat(Enumerable.Range(1, 240).Select(line => $"line {line:000}\n"));
                fixture._original = Encoding.UTF8.GetBytes(original);
                fixture._changed = Encoding.UTF8.GetBytes(original.Replace("line 180\n", ChangedCode + "\n", StringComparison.Ordinal));
                await File.WriteAllBytesAsync(Path.Combine(fixture.Repository.Path, LiteralPath), fixture._original);
                await fixture.Repository.StageAllAsync();
                await fixture.Repository.CommitAsync("Initial whole-file fixture");
                await File.WriteAllBytesAsync(Path.Combine(fixture.Repository.Path, LiteralPath), fixture._changed);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async Task RecordReadOnlyStateAsync()
        {
            _indexFile = await File.ReadAllBytesAsync(Path.Combine(Repository.Path, ".git", "index"));
            _configFile = await File.ReadAllBytesAsync(Path.Combine(Repository.Path, ".git", "config"));
        }
        public async Task AssertReadOnlyAsync()
        {
            GitBinaryResult index = await new GitRunner().RunBinaryAsync(Repository.Path, ["show", ":" + LiteralPath]);
            Assert.That(index.Success, Is.True, index.StandardError);
            Assert.That(index.StandardOutput, Is.EqualTo(_original));
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(Repository.Path, LiteralPath)), Is.EqualTo(_changed));
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(Repository.Path, ".git", "index")), Is.EqualTo(_indexFile));
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(Repository.Path, ".git", "config")), Is.EqualTo(_configFile));
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (Window window in _windows.AsEnumerable().Reverse())
                {
                    foreach (Window child in window.OwnedWindows.ToArray()) { child.Close(); }
                    window.Close();
                }
                await Until(() => _windows.All(window => !window.IsVisible));
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg);
                CultureInfo.CurrentUICulture = _culture;
                if (Directory.Exists(_temporary)) { Directory.Delete(_temporary, recursive: true); }
            }
        }
    }
}
