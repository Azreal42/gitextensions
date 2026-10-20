using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace GitExtensions.Linux.Desktop;

/// <summary>One file-selection protocol, without Browse, first-run prompts, MRU, avatars or journal preference writers.</summary>
internal sealed class SearchFileApplication : Application
{
    // Windows leaves Environment.ExitCode at -1 when RunSearchFileCommand does not succeed.
    internal const int FailureExitCode = -1;
    private static DesktopStartupRequest? _request;
    private SearchFileSession? _session;

    public override void Initialize()
    {
        DesktopFonts.Initialize();
        Styles.Add(new FluentTheme()); Styles.Add(ClassicStyles.Create());
        RequestedThemeVariant = AskpassApplication.ReadPreferredTheme();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _session = CreateSession(_request ?? throw new InvalidOperationException("Missing searchfile request."));
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = _session.Dialog;
        }
        base.OnFrameworkInitializationCompleted();
    }

    internal static SearchFileSession CreateSession(DesktopStartupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Arguments.Mode != DesktopStartupMode.SearchFile) { throw new ArgumentException("Not a searchfile request.", nameof(request)); }
        return new(request.Repository ?? throw new InvalidOperationException("Searchfile repository discovery must complete before creating its window."));
    }

    internal static int Run(DesktopStartupArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Mode != DesktopStartupMode.SearchFile) { throw new ArgumentException("Not a searchfile request.", nameof(arguments)); }
        TextWriter output = Console.Out;
        // Framework output and preparation diagnostics must never enter the selected-path stdout protocol.
        Console.SetOut(TextWriter.Null);
        try
        {
            GitEnvironmentPreferencesLoadResult environment = new GitEnvironmentPreferencesStore().Load();
            if (environment.Error is not null) { throw environment.Error; }
            GitEnvironment.Configure(GitEnvironmentSettingsSession.Options(environment.Preferences));
            DesktopLanguage.ApplyPreferredCulture();
            _request = DesktopStartupDispatcher.PrepareAsync(arguments, Environment.CurrentDirectory).GetAwaiter().GetResult();
            AppBuilder builder = AppBuilder.Configure(() => new SearchFileApplication()).UsePlatformDetect().WithInterFont();
            int code = builder.StartWithClassicDesktopLifetime([]);
            SearchFileSession session = (builder.Instance as SearchFileApplication)?._session
                ?? throw new InvalidOperationException("Searchfile window was not created.");
            // A pending reader defers Closing, keeping the UI loop alive until its continuation and drain finish.
            session.LoadTask.GetAwaiter().GetResult();
            return code == 0 ? Complete(session, output, Console.Error) : FailureExitCode;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Unable to search repository files.");
            return FailureExitCode;
        }
        finally { _request = null; Console.SetOut(output); }
    }

    internal static int Complete(SearchFileSession session, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(output); ArgumentNullException.ThrowIfNull(error);
        if (!session.LoadTask.IsCompleted || session.Dialog.IsVisible)
        { throw new InvalidOperationException("The searchfile window and reader have not completed."); }
        if (session.LastException is not null) { error.WriteLine("Unable to search repository files."); return FailureExitCode; }
        if (session.Dialog.SelectedEntry is not { } selected) { return FailureExitCode; }
        // Windows emits the literal absolute name plus exactly its normal newline: no quoting or file opening.
        output.WriteLine(Path.Combine(session.Repository.Path, selected.Path)); output.Flush();
        return 0;
    }
}
