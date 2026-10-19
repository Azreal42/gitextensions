using Avalonia.Controls;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed record FileEditorOutcome(bool Accepted, bool HasFileChanges);

internal static class FileEditorWorkflow
{
    internal static async Task<FileEditorOutcome> OpenAsync(Window owner, GitRepository repository, string fullPath,
        int? lineNumber = null, string? encodingName = null, bool showWarning = false, CancellationToken cancellationToken = default,
        Action? fileChanged = null, TranslationCatalog? translations = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsVisible) { throw new InvalidOperationException("The file editor owner is no longer visible."); }
        string codec = encodingName ?? await repository.GetFileEncodingNameAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using FileEditorDialog dialog = new(fullPath, codec, showWarning, lineNumber: lineNumber, translations: translations)
            { RequestedThemeVariant = owner.ActualThemeVariant };
        if (fileChanged is not null) { dialog.FileChanged += fileChanged; }
        using CancellationTokenRegistration registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(dialog.RequestClose));
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); await dialog.ShowDialog(owner);
            cancellationToken.ThrowIfCancellationRequested();
            return new(dialog.CloseAccepted, dialog.HasFileChanges);
        }
        finally { dialog.Close(); }
    }

    internal static async Task<string> LocalConfigurationPathAsync(GitRepository repository, CancellationToken token = default)
    {
        GitRepositoryInfo info = repository.Info ?? await repository.GetInfoAsync(token);
        token.ThrowIfCancellationRequested(); return Path.Combine(info.CommonDirectory, "config");
    }

    internal static async Task<FileEditorOutcome> OpenLocalConfigurationAsync(Window owner, GitRepository repository,
        CancellationToken token = default, Action? fileChanged = null, TranslationCatalog? translations = null)
    {
        string path = await LocalConfigurationPathAsync(repository, token);
        // The raw editor must remain able to repair a config whose invalid syntax prevents Git codec lookup.
        string codec;
        try { codec = await repository.GetFileEncodingNameAsync(token); }
        catch (Exception error) when (error is GitCommandException or NotSupportedException) { codec = "utf-8"; }
        return await OpenAsync(owner, repository, path, encodingName: codec, showWarning: true,
            cancellationToken: token, fileChanged: fileChanged, translations: translations);
    }
}
