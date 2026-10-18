using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Spelling;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CommitCompletionSpellingRaceTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [AvaloniaTest]
    public async Task RetiredEngineCompletionPublicationCannotShowAnErrorAfterTheRealMenuSelectsNone()
    {
        using GitFixture git = await GitFixture.CreateAsync();
        PreferencesStore preferences = new(git.Output("profile"));
        // Suggestions would wait for the deliberately gated engine while opening the menu.
        // The global flag leaves the real Dictionary menu available; memory exemptions are independent.
        preferences.Save(new() { Dictionary = "none", Language = "English", ProvideAutocompletion = false });
        TextBox editor = new()
        {
            Name = "Message", AcceptsReturn = true, Height = 220,
            FontFamily = new FontFamily("DejaVu Sans Mono"), FontSize = 12
        };
        SpellingEditorView view = new(editor);
        CommitSpellingController controller = new(git.Repository, view, preferences);
        Window window = new() { Width = 720, Height = 300, Content = new StackPanel { Children = { view, controller.Notice } } };
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool drainedAtClosed = false;
        window.Closed += (_, _) =>
        {
            drainedAtClosed = controller.Completion.IsCompletedSuccessfully
                && Services(controller).All(service => service.CloseCompletion.IsCompletedSuccessfully);
            closed.TrySetResult();
        };
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource rawReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SpellCheckService? retired = null;
        window.Show(); controller.Start();
        try
        {
            await Until(() => Current(controller)?.DictionaryName == "none");
            await controller.Completion.WaitAsync(Limit);
            SpellCheckService initial = Current(controller)!;
            await initial.Ready.WaitAsync(Limit);
            await AvatarUiTestDriver.Edit(window, editor, "hello wrld");
            editor.SelectionStart = 6; editor.SelectionEnd = 10;
            await initial.BeginClose().WaitAsync(Limit);

            // Keep a genuine original loader and its queued publication alive across the swap.
            // Register the engine with the real owner before any work so final closure must drain it.
            retired = new(DictionaryCatalog.FromApplicationDirectory(AppContext.BaseDirectory), "en-US", initial.UserDictionaryFile,
                (resource, _) =>
                {
                    entered.TrySetResult(); release.Wait();
                    NetSpellEngine engine = NetSpellEngine.Load(resource, CancellationToken.None);
                    rawReturned.TrySetResult(); return engine;
                });
            Services(controller).Add(retired);
            Field("_service").SetValue(controller, retired);
            await entered.Task.WaitAsync(Limit);
            controller.AddCompletionWords(["codexoldcompletionqqq"]);
            Task oldPublication = controller.Completion;
            Assert.That(oldPublication.IsCompleted, Is.False); Assert.That(rawReturned.Task.IsCompleted, Is.False);

            await OpenDictionaryMenu(window, editor, view);
            int start = editor.SelectionStart, end = editor.SelectionEnd, caret = editor.CaretIndex;
            Assert.That(start, Is.EqualTo(6)); Assert.That(end, Is.EqualTo(10));
            MenuItem dictionary = editor.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "CommitDictionary");
            MenuItem none = dictionary.Items.OfType<MenuItem>().Single(item => item.Name == "CommitDictionary_none");
            Assert.That(none.IsEnabled, Is.True); Assert.That(none.IsChecked, Is.True);
            none.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); editor.ContextMenu.Close();
            await Until(() => Current(controller) is { DictionaryName: "none" } current && !ReferenceEquals(current, initial));
            SpellCheckService active = Current(controller)!;
            await active.Ready.WaitAsync(Limit);
            Assert.That((await git.Repository.ReadEffectiveDictionaryAsync("none")).LocalDictionary, Is.EqualTo("none"),
                "The actual menu must write its real repository-local XML before replacing the service.");
            Assert.That(Field("_debounce").GetValue(controller), Is.Null,
                "The retired-operation exception must not be accidentally hidden by an unrelated canceled debounce.");
            Assert.That(controller.Notice.IsVisible, Is.False);
            Assert.That(retired.CloseCompletion.IsCompleted, Is.False);
            Assert.That(oldPublication.IsCompleted, Is.False);

            release.Set();
            await rawReturned.Task.WaitAsync(Limit);
            await oldPublication.WaitAsync(Limit);
            await retired.CloseCompletion.WaitAsync(Limit);
            await controller.Completion.WaitAsync(Limit);
            Assert.That(Current(controller), Is.SameAs(active));
            Assert.That(controller.Notice.IsVisible, Is.False,
                "A cancellation/error from a retired completion publication must never become the new None service's Notice.");
            Assert.That(controller.Notice.Text, Is.Null.Or.Empty);
            Assert.That(controller.Misspellings, Is.Empty);
            Assert.That(editor.Text, Is.EqualTo("hello wrld"));
            Assert.That(editor.SelectionStart, Is.EqualTo(start)); Assert.That(editor.SelectionEnd, Is.EqualTo(end));
            Assert.That(editor.CaretIndex, Is.EqualTo(caret));
            Assert.That(retired.Ready.IsCompleted, Is.True); Assert.That(retired.CloseCompletion.IsCompletedSuccessfully, Is.True);

            await controller.BeginClose().WaitAsync(Limit);
            Assert.That(Services(controller).All(service => service.CloseCompletion.IsCompletedSuccessfully), Is.True);
            window.Close(); await closed.Task.WaitAsync(Limit);
            Assert.That(drainedAtClosed, Is.True, "The real Closed event must see current and retired services and controller work already drained.");
        }
        finally
        {
            release.Set();
            await controller.BeginClose().WaitAsync(Limit);
            if (retired is not null) { await retired.BeginClose().WaitAsync(Limit); }
            if (window.IsVisible) { window.Close(); await closed.Task.WaitAsync(Limit); }
        }
    }

    private static FieldInfo Field(string name) => typeof(CommitSpellingController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new AssertionException("Missing existing controller field: " + name);
    private static SpellCheckService? Current(CommitSpellingController controller) => (SpellCheckService?)Field("_service").GetValue(controller);
    private static List<SpellCheckService> Services(CommitSpellingController controller) => (List<SpellCheckService>)Field("_services").GetValue(controller)!;
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < Limit) { await Task.Delay(10); }
        Assert.That(condition(), Is.True, "The real controller operation did not complete.");
    }
    private static async Task OpenDictionaryMenu(Window window, TextBox editor, SpellingEditorView view)
    {
        using (await AvatarUiTestDriver.Frame(window)) { }
        Rect target = view.GetRangeRectangles(new(7, 1)).Single();
        Control marks = view.GetVisualDescendants().OfType<Control>().Single(item => item.Name == "SpellingUnderlines");
        Point point = marks.TranslatePoint(target.Center, window)!.Value;
        Assert.That((window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(editor), Is.True);
        window.MouseMove(point); window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right);
        await Until(() => editor.ContextMenu is { IsOpen: true } menu
            && menu.Items.OfType<MenuItem>().Any(item => item.Name == "CommitDictionary" && item.IsEnabled));
    }
}
