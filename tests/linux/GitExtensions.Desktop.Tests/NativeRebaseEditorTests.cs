using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.RebaseDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class NativeRebaseEditorTests
{
    [AvaloniaTest] public Task GitGeneratedTodoKeepsEveryCommandAndCommentLight() => KeepRawTodo(false);
    [AvaloniaTest] public Task GitGeneratedTodoKeepsEveryCommandAndCommentDark() => KeepRawTodo(true);
    private async Task KeepRawTodo(bool dark)
    {
        using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true); await f.StartConflict();
        string todo = "# Git-generated structure, comments and arguments stay raw\r\nlabel onto\r\nreset onto\r\npick " + f.Topic + " Topic\r\n"
            + "exec printf '%s' '$HOME; $(literal)'\r\nfixup -C " + f.Topic + " message\r\nfixup -c " + f.Topic + " editable\r\n"
            + "merge -C " + f.Topic + " preserved-label # merged subject\r\nupdate-ref refs/heads/other\r\n# café λ\r\n";
        string path = f.File(".git/rebase-merge/git-rebase-todo"); System.IO.File.WriteAllBytes(path, new UTF8Encoding(false, true).GetBytes(todo));
        string before = await f.Witness(); GitRebaseEditorRequest request = f.Request(GitRebaseEditorKind.Todo);
        NativeRebaseEditorWindow editor = f.ShowEditor(request, dark);
        Assert.That(Field<TextBox>(editor, "NativeRebaseEditorText").Text, Is.EqualTo(todo));
        Assert.That(editor.ExitCode, Is.EqualTo(1)); Capture(editor, "native-rebase-raw-todo-" + (dark ? "dark" : "light") + ".png");
        Click(Field<Button>(editor, "SaveNativeRebaseEditor")); Assert.That(editor.IsVisible, Is.False); Assert.That(editor.Saved, Is.True); Assert.That(editor.ExitCode, Is.Zero);
        Assert.That(System.IO.File.ReadAllBytes(path), Is.EqualTo(new UTF8Encoding(false, true).GetBytes(todo)));
        Assert.That(await f.Witness(), Is.EqualTo(before), "An unchanged raw todo save must preserve all native commands, HEAD/index and worktree bytes.");
    }

    [AvaloniaTest]
    public async Task CancelLeavesTodoAndNativePendingStateUntouchedAndReportsEditorFailure()
    {
        using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true); await f.StartConflict(); string before = await f.Witness();
        GitRebaseEditorRequest request = f.Request(GitRebaseEditorKind.Todo); NativeRebaseEditorWindow editor = f.ShowEditor(request);
        Field<TextBox>(editor, "NativeRebaseEditorText").Text = "drop " + f.Topic + " unaccepted edit\n";
        Click(Field<Button>(editor, "CancelNativeRebaseEditor")); Assert.That(editor.IsVisible, Is.False); Assert.That(editor.Saved, Is.False); Assert.That(editor.ExitCode, Is.EqualTo(1));
        Assert.That(await f.Witness(), Is.EqualTo(before)); Assert.That((await f.Repository.GetRebaseStateAsync()).IsActive, Is.True);
    }

    [AvaloniaTest]
    public async Task NativeMessageUsesStrictCp1252AndPreservesCommentsAndSourceBytes()
    {
        using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true); await f.Git("config", "i18n.commitencoding", "windows-1252"); await f.StartConflict();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); Encoding codec = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        string text = "café €\r\n\r\n# Git comment stays intact\r\n"; string path = f.File(".git/COMMIT_EDITMSG"); System.IO.File.WriteAllBytes(path, codec.GetBytes(text));
        GitRebaseEditorRequest request = f.Request(GitRebaseEditorKind.Message); Assert.That(request.EncodingName, Is.EqualTo("windows-1252"));
        NativeRebaseEditorWindow editor = f.ShowEditor(request); Assert.That(Field<TextBox>(editor, "NativeRebaseEditorText").Text, Is.EqualTo(text));
        string head = await f.Text("rev-parse", "HEAD"), index = await f.Text("ls-files", "--stage");
        Click(Field<Button>(editor, "SaveNativeRebaseEditor")); Assert.That(editor.ExitCode, Is.Zero); Assert.That(System.IO.File.ReadAllBytes(path), Is.EqualTo(codec.GetBytes(text)));
        Assert.That(await f.Text("rev-parse", "HEAD"), Is.EqualTo(head)); Assert.That(await f.Text("ls-files", "--stage"), Is.EqualTo(index));
    }

    [AvaloniaTest]
    public async Task UnrepresentableTextAndConcurrentPreimageChangesLeaveEditorOpenWithoutWriting()
    {
        using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true); await f.Git("config", "i18n.commitencoding", "windows-1252"); await f.StartConflict();
        string path = f.File(".git/COMMIT_EDITMSG"); byte[] original = Encoding.ASCII.GetBytes("original message\n# comment\n"); System.IO.File.WriteAllBytes(path, original);
        GitRebaseEditorRequest request = f.Request(GitRebaseEditorKind.Message); NativeRebaseEditorWindow editor = f.ShowEditor(request);
        Field<TextBox>(editor, "NativeRebaseEditorText").Text = "cannot encode λ"; Click(Field<Button>(editor, "SaveNativeRebaseEditor"));
        Assert.That(editor.IsVisible, Is.True); Assert.That(editor.ExitCode, Is.EqualTo(1)); Assert.That(Field<TextBlock>(editor, "NativeRebaseEditorError").IsVisible, Is.True);
        Assert.That(System.IO.File.ReadAllBytes(path), Is.EqualTo(original));
        byte[] changed = Encoding.ASCII.GetBytes("concurrent Git message\n"); System.IO.File.WriteAllBytes(path, changed);
        Field<TextBox>(editor, "NativeRebaseEditorText").Text = "valid replacement"; Click(Field<Button>(editor, "SaveNativeRebaseEditor"));
        Assert.That(Field<TextBlock>(editor, "NativeRebaseEditorError").Text, Does.Contain("changed")); Assert.That(editor.IsVisible, Is.True); Assert.That(editor.Saved, Is.False);
        Assert.That(System.IO.File.ReadAllBytes(path), Is.EqualTo(changed)); Click(Field<Button>(editor, "CancelNativeRebaseEditor"));
    }

    [AvaloniaTest]
    public async Task ClosingEditorAndInvalidNativeInvocationReturnFailureWithoutConsumingRecovery()
    {
        using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true); await f.StartConflict(); string before = await f.Witness();
        NativeRebaseEditorWindow editor = f.ShowEditor(f.Request(GitRebaseEditorKind.Todo)); editor.Close(); Assert.That(editor.ExitCode, Is.EqualTo(1));
        Assert.That(NativeRebaseEditorApplication.Run(["--edit-rebase-todo"]), Is.EqualTo(1));
        Assert.That(await f.Witness(), Is.EqualTo(before));
    }
}
