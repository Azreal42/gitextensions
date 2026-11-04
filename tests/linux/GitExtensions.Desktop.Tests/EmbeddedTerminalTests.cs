using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Porta.Pty;
using static GitExtensions.Desktop.Tests.ReferenceDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class EmbeddedTerminalTests
{
    [AvaloniaTest]
    public void KeyboardCompositionControlKeysRepeatAndBracketedPasteEmitExactBytes()
    {
        EmbeddedTerminalControl terminal = new() { Model = new AvaloniaTerminal.TerminalControlModel() };
        List<byte> input = [];
        terminal.Model.UserInput += bytes => input.AddRange(bytes);
        Window window = new() { Content = terminal, Width = 800, Height = 500 };
        try
        {
            window.Show(); terminal.Focus();
            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
            window.KeyTextInput("é日本");
            window.KeyReleaseQwerty(PhysicalKey.E, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
            window.KeyReleaseQwerty(PhysicalKey.C, RawInputModifiers.Control);
            window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
            Assert.That(input, Is.EqualTo(Encoding.UTF8.GetBytes("é日本\u0003\u007f\u007f")));
            input.Clear();
            terminal.Model.Feed("\u001b[?2004h");
            terminal.PasteText("first\r\nsecond");
            Assert.That(input, Is.EqualTo(Encoding.UTF8.GetBytes("\u001b[200~first\nsecond\u001b[201~")));
        }
        finally { window.Close(); }
    }

    [Test]
    public async Task Utf8SplitAcrossReadsAndFinalOutputArePreserved()
    {
        FakePty pty = new();
        await using EmbeddedTerminalSession session = new(pty);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        StringBuilder text = new();
        Task<int> running = session.RunAsync((chunk, _) => { text.Append(chunk); return Task.CompletedTask; }, timeout.Token);
        foreach (byte value in Encoding.UTF8.GetBytes("é λ 日本 🐈\r\n")) { pty.Output.Push([value]); }
        pty.Exit(19);
        Assert.That(await running, Is.EqualTo(19));
        Assert.That(text.ToString(), Is.EqualTo("é λ 日本 🐈\r\n"));
    }

    [AvaloniaTest]
    public async Task SwitchingTabsKeepsTheSessionAndClosingRepositoryDisposesIt()
    {
        FakePty pty = new(); int starts = 0;
        TextBox output = new() { Text = "command log" };
        await using EmbeddedTerminalPane pane = new(output, () => Task.CompletedTask, text => text)
        {
            StartSessionAsync = (_, _) => { starts++; return Task.FromResult(new EmbeddedTerminalSession(pty)); }
        };
        TabItem console = new() { Header = "Console", Content = pane }, other = new() { Header = "Other", Content = new TextBlock() };
        TabControl tabs = new() { ItemsSource = new[] { console, other } };
        Window window = new() { Content = tabs, Width = 800, Height = 500 };
        try
        {
            window.Show(); pane.SetRepository(new GitRepository(Path.GetTempPath()));
            await pane.EnsureStartedAsync();
            pty.Output.Push(Encoding.UTF8.GetBytes("\u001b[31mred\u001b[0m\r\n"));
            tabs.SelectedItem = other; Dispatcher.UIThread.RunJobs();
            tabs.SelectedItem = console; Dispatcher.UIThread.RunJobs();
            await pane.EnsureStartedAsync();
            Assert.That(starts, Is.EqualTo(1)); Assert.That(pty.Disposed, Is.False);
            pane.FocusTerminal(); window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control); window.KeyReleaseQwerty(PhysicalKey.C, RawInputModifiers.Control);
            await UntilAsync(() => pty.Input.ToArray().Contains((byte)3));
            Assert.That(pane.Terminal.Model!.Terminal.Engine.Buffer.GetLine(0)![0].Content, Is.EqualTo("r"));
            pane.SetRepository(null);
            await pane.DisposeAsync();
            Assert.That(pty.Disposed, Is.True);
            Assert.That(output.Text, Is.EqualTo("command log"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task SwitchingRepositoryDuringStartupDisposesTheLateSession()
    {
        TaskCompletionSource<EmbeddedTerminalSession> opening = new(); FakePty pty = new();
        await using EmbeddedTerminalPane pane = new(new TextBox(), () => Task.CompletedTask, text => text)
        { StartSessionAsync = (_, _) => opening.Task };
        pane.SetRepository(new GitRepository(Path.GetTempPath()));
        Task ready = pane.EnsureStartedAsync();
        pane.SetRepository(null);
        opening.SetResult(new(pty));
        await ready; await pane.DisposeAsync();
        Assert.That(pty.Disposed, Is.True); Assert.That(pane.ProcessId, Is.Null);
    }

    [Test, Platform("Linux,MacOsX")]
    public async Task RealPtyHasJobControlResizeWorkingDirectoryAndExitStatus()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ge-terminal ' λ-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            GitExecutionEnvironment environment = GitExecutionEnvironment.Capture(new(), new Dictionary<string, string?>
            { ["PATH"] = "/usr/bin:/bin", ["SHELL"] = "/bin/sh", ["HOME"] = directory, ["LC_ALL"] = "C" });
            PtyOptions options = EmbeddedTerminalSession.CreateOptions(directory, environment, 80, 24);
            options.CommandLine = ["-i"];
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            await using EmbeddedTerminalSession session = await EmbeddedTerminalSession.StartAsync(options, timeout.Token);
            StringBuilder output = new(); object gate = new();
            string Text() { lock (gate) { return output.ToString(); } }
            Task<int> running = session.RunAsync((text, _) => { lock (gate) { output.Append(text); } return Task.CompletedTask; }, timeout.Token);
            void Send(string text) => session.Send(Encoding.UTF8.GetBytes(text));
            Send("test -t 0 && test -t 1 && printf '%s%s\\n' '__TTY_' 'OK__'; test . -ef \"$HOME\" && printf '%s%s\\n' '__CWD_' 'OK__'\r");
            await UntilAsync(() => Text().Contains("__TTY_OK__") && Text().Contains("__CWD_OK__"), running, Text);
            session.Resize(113, 37);
            Send("printf '__SIZE__'; stty size\r");
            await UntilAsync(() => Text().Contains("__SIZE__37 113"), running, Text);
            Send("printf '\\303'; sleep 0.05; printf '\\251'; printf '%s%s\\n' '__UTF8_' 'DONE__'\r");
            await UntilAsync(() => Text().Contains("é__UTF8_DONE__"), running, Text);
            Send("sleep 30 && printf '%s%s\\n' '__NOT_' 'INTERRUPTED__'\r");
            await Task.Delay(200); session.Send([3]); await Task.Delay(100);
            Send("printf '%s%s\\n' '__INTERRUPTED_' 'OK__'\r");
            await UntilAsync(() => Text().Contains("__INTERRUPTED_OK__"), running, Text);
            Assert.That(Text(), Does.Not.Contain("__NOT_INTERRUPTED__"));
            Send("exit 7\r");
            Assert.That(await running.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(7), Text());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test, Platform("Linux,MacOsX")]
    public async Task DisposingAnIdleRealPtyTerminatesTheShell()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        PtyOptions options = EmbeddedTerminalSession.CreateOptions(Path.GetTempPath(),
            GitExecutionEnvironment.Capture(new(), new Dictionary<string, string?> { ["PATH"] = "/bin:/usr/bin", ["SHELL"] = "/bin/sh" }), 80, 24);
        EmbeddedTerminalSession session = await EmbeddedTerminalSession.StartAsync(options, timeout.Token);
        using Process process = Process.GetProcessById(session.ProcessId);
        await session.DisposeAsync();
        await process.WaitForExitAsync(timeout.Token);
        Assert.That(process.HasExited, Is.True);
    }

    [AvaloniaTest, Platform("Linux,MacOsX")]
    public async Task BrowserConsoleRunsARealShellAndKeepsControlKeysInsideTheTerminal()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        MainWindow window = new();
        EmbeddedTerminalPane pane = (EmbeddedTerminalPane)typeof(MainWindow).GetField("_terminalPane",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        pane.StartSessionAsync = (options, token) =>
        {
            options.App = "/bin/sh"; options.CommandLine = ["-i"];
            // Porta's POSIX environment is an overlay; an empty value explicitly unsets an inherited variable.
            foreach (string name in new[] { "ENV", "BASH_ENV", "GIT_PAGER", "PAGER", "LESS" }) { options.Environment[name] = ""; }
            options.Environment["GIT_PAGER"] = "less -+F -+X -R";
            return EmbeddedTerminalSession.StartAsync(options, token);
        };
        try
        {
            window.Show(); await window.OpenRepositoryAsync(fixture.Repository.Path);
            TabControl tabs = window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Name == "DetailsTabs");
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "ConsoleTab");
            await pane.EnsureStartedAsync();
            Assert.That(pane.ProcessId, Is.Not.Null);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            pane.FocusTerminal(); Assert.That(pane.Terminal.IsFocused, Is.True);
            window.KeyTextInput("printf '%s%s\\n' 'Terminal int' 'égré — prêt'; git status --short --branch");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            string BufferText()
            {
                var buffer = pane.Terminal.Model!.Terminal.Buffer;
                return string.Join("\n", Enumerable.Range(0, buffer.Lines.Length)
                    .Select(row => string.Concat(buffer.GetLine(row)!.Select(cell => cell.Content))));
            }
            await UntilAsync(() => BufferText().Contains("Terminal intégré — prêt") && BufferText().Contains("## main"), diagnostic: BufferText);
            int? pid = pane.ProcessId;
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "DiffTab");
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "ConsoleTab");
            await pane.EnsureStartedAsync();
            Assert.That(pane.ProcessId, Is.EqualTo(pid));
            pane.FocusTerminal(); window.KeyTextInput("sleep 30");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Task.Delay(200);
            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control); window.KeyReleaseQwerty(PhysicalKey.C, RawInputModifiers.Control);
            window.KeyTextInput("printf '%s%s\\n' 'Control-C ' 'OK'");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await UntilAsync(() => BufferText().Contains("Control-C OK"), diagnostic: BufferText);
            window.KeyTextInput("git -c core.pager='less -+F -+X -R' log --oneline");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await UntilAsync(() => pane.Terminal.Model!.Terminal.IsAlternateBufferActive, diagnostic: BufferText);
            window.KeyTextInput("q");
            await UntilAsync(() => !pane.Terminal.Model!.Terminal.IsAlternateBufferActive, diagnostic: BufferText);
            Assert.That(BufferText(), Does.Contain("Control-C OK"));
            if (Environment.GetEnvironmentVariable("GITEXTENSIONS_TERMINAL_SCREENSHOT") is { Length: > 0 } screenshot)
            {
                window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                using RenderTargetBitmap bitmap = new(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                bitmap.Render(window); bitmap.Save(screenshot);
            }
        }
        finally
        {
            window.Close();
            await pane.DisposeAsync();
            await UntilAsync(() => !window.IsVisible);
        }
    }

    private static async Task UntilAsync(Func<bool> predicate, Task? operation = null, Func<string>? diagnostic = null)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!predicate() && timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (operation?.IsFaulted == true) { await operation; }
            await Task.Delay(10);
        }
        Assert.That(predicate(), Is.True, "Terminal did not produce the expected output. " + diagnostic?.Invoke());
    }

    private sealed class FakePty : IPtyConnection
    {
        internal readonly ChunkStream Output = new();
        internal readonly MemoryStream Input = new();
        internal bool Disposed;
        private bool _exited;
        public Stream ReaderStream => Output;
        public Stream WriterStream => Input;
        public int Pid => 42;
        public int ExitCode { get; private set; }
        public event EventHandler<PtyExitedEventArgs>? ProcessExited;
        public bool WaitForExit(int milliseconds) => _exited;
        public void Resize(int cols, int rows) { }
        public void Kill() => Exit(0);
        public void Exit(int code)
        {
            ExitCode = code; _exited = true; Output.Complete();
            ProcessExited?.Invoke(this, (PtyExitedEventArgs)Activator.CreateInstance(typeof(PtyExitedEventArgs), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, [code], null)!);
        }
        public void Dispose() { Disposed = true; Output.Dispose(); }
    }

    private sealed class ChunkStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        internal void Push(byte[] value) => _chunks.Writer.TryWrite(value);
        internal void Complete() => _chunks.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) { return 0; }
            byte[] bytes = await _chunks.Reader.ReadAsync(cancellationToken);
            bytes.CopyTo(buffer); return bytes.Length;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Complete(); base.Dispose(disposing); }
    }
}
