using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Scripts;

namespace GitExtensions.Linux.Desktop;

/// <summary>Native reference identities captured alongside the pure script facts, never read again during preparation.</summary>
internal sealed record ScriptReferenceMetadata(IReadOnlyList<GitReference> Selected, IReadOnlyList<GitReference> Current)
{
    internal ScriptReferenceMetadata Snapshot() => new(Array.AsReadOnly(Selected.ToArray()), Array.AsReadOnly(Current.ToArray()));
}

internal sealed record ScriptPreparationResponse(ScriptRequest Request, ScriptReply Reply, GitReference? SelectedReference = null);
internal sealed record ScriptPreparationResult(ScriptExpansionResult Expansion, IReadOnlyList<ScriptPreparationResponse> Responses);

/// <summary>Prepares template text through the existing modal inputs. It does not launch or register scripts.</summary>
internal sealed class ScriptPreparationDispatcher
{
    private readonly Window _owner;
    private readonly ScriptContext _context;
    private readonly ScriptReferenceMetadata _references;
    private readonly TranslationCatalog? _translations;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>>? _browseFiles;
    private readonly PixelPoint? _selectorPosition;
    private readonly Func<Window, PixelPoint>? _selectorPlacement;
    private int _preparing;

    internal ScriptPreparationDispatcher(Window owner, ScriptContext context, ScriptReferenceMetadata references,
        TranslationCatalog? translations = null, Func<string, CancellationToken, Task<IReadOnlyList<string>>>? browseFiles = null,
        PixelPoint? selectorPosition = null, Func<Window, PixelPoint>? selectorPlacement = null)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(references);
        _owner = owner; _context = context.Snapshot(); _references = references.Snapshot();
        _translations = translations; _browseFiles = browseFiles; _selectorPosition = selectorPosition; _selectorPlacement = selectorPlacement;
    }

    internal async Task<ScriptPreparationResult> PrepareAsync(ScriptDefinition script, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (Interlocked.Exchange(ref _preparing, 1) != 0) { throw new InvalidOperationException("Script preparation is already active."); }
        Session? session = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            session = await Dispatcher.UIThread.InvokeAsync(() => new Session(this, cancellationToken));
            ScriptExpansionResult result = await ScriptTemplate.PrepareAsync(script, _context,
                session.RequestAsync, session.Token).ConfigureAwait(false);
            return new(result, Array.AsReadOnly(session.Responses.ToArray()));
        }
        finally
        {
            try { if (session is not null) { await Dispatcher.UIThread.InvokeAsync(session.Dispose); } }
            finally { Interlocked.Exchange(ref _preparing, 0); }
        }
    }

    private IReadOnlyList<GitReference> ReferenceChoices(ScriptRequest request)
    {
        bool selected = request.Option?.StartsWith('s') == true;
        IReadOnlyList<ScriptReference> facts = selected ? _context.SelectedReferences : _context.CurrentReferences;
        IReadOnlyList<GitReference> metadata = selected ? _references.Selected : _references.Current;
        ScriptReferenceKind[] kinds = request.Option switch
        {
            "sTag" or "cTag" => [ScriptReferenceKind.Tag],
            "sLocalBranch" or "cLocalBranch" or "cDefaultRemote" => [ScriptReferenceKind.LocalBranch],
            "sRemoteBranch" or "cRemoteBranch" or "sRemoteBranchName" or "cRemoteBranchName" => [ScriptReferenceKind.RemoteBranch],
            "sBranch" or "cBranch" => [ScriptReferenceKind.LocalBranch, ScriptReferenceKind.RemoteBranch],
            _ => throw new InvalidOperationException("Unsupported script reference request.")
        };
        List<GitReference> choices = [];
        foreach (ScriptReference fact in facts.Where(fact => kinds.Contains(fact.Kind)))
        {
            if (request.Candidates?.Contains(fact.Name, StringComparer.Ordinal) != true) { continue; }
            GitReference[] matches = metadata.Where(reference => reference.ShortName == fact.Name && (fact.Kind switch
            {
                ScriptReferenceKind.LocalBranch => reference.IsBranch,
                ScriptReferenceKind.RemoteBranch => reference.IsRemote,
                ScriptReferenceKind.Tag => reference.IsTag,
                _ => false
            })).ToArray();
            if (matches.Length != 1) { throw new InvalidOperationException("Captured script reference metadata is missing or ambiguous: " + fact.Name); }
            choices.Add(matches[0]);
        }
        if (request.Candidates is null || !choices.Select(reference => reference.ShortName).Order(StringComparer.Ordinal)
            .SequenceEqual(request.Candidates.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        { throw new InvalidOperationException("Script reference candidates do not match the captured context."); }
        return choices;
    }

    private sealed class Session : IDisposable
    {
        private readonly ScriptPreparationDispatcher _host;
        private readonly TranslationCatalog _translations;
        private readonly CancellationTokenSource _lifetime;
        private readonly CancellationTokenRegistration _closeOnCancellation;
        private Window? _active;
        internal CancellationToken Token { get; }
        internal List<ScriptPreparationResponse> Responses { get; } = [];

        internal Session(ScriptPreparationDispatcher host, CancellationToken token)
        {
            Dispatcher.UIThread.VerifyAccess(); _host = host; _translations = host._translations ?? TranslationCatalog.LoadDefault();
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token); Token = _lifetime.Token;
            _host._owner.Closing += OwnerClosing;
            _closeOnCancellation = Token.Register(() => Dispatcher.UIThread.Post(() => _active?.Close()));
            if (!_host._owner.IsVisible) { _lifetime.Cancel(); }
        }

        private void OwnerClosing(object? sender, WindowClosingEventArgs args) => _lifetime.Cancel();

        internal async Task<ScriptReply> RequestAsync(ScriptRequest request, CancellationToken token)
        {
            // ScriptTemplate deliberately uses ConfigureAwait(false) between requests.
            return await Dispatcher.UIThread.InvokeAsync(() => ShowAsync(request, token)).ConfigureAwait(false);
        }

        private async Task<ScriptReply> ShowAsync(ScriptRequest request, CancellationToken token)
        {
            Dispatcher.UIThread.VerifyAccess(); token.ThrowIfCancellationRequested();
            if (!_host._owner.IsVisible) { _lifetime.Cancel(); token.ThrowIfCancellationRequested(); }
            if (request.Kind == ScriptRequestKind.Confirmation)
            {
                string title = _translations.Translate("TranslatedStrings", "_scriptText", "Text", "Script");
                string message = _translations.Translate("TranslatedStrings", "_scriptConfirmExecuteText", "Text", "Do you want to execute script") + ": '" + request.ScriptName + "'?";
                bool? choice = await Dialogs.ConfirmChoiceAsync(_host._owner, title, message, "Yes", "No", true, token, _translations,
                    _host._owner.ActualThemeVariant);
                ScriptReply answer = new(choice is true ? ScriptReplyKind.Accepted : choice is false ? ScriptReplyKind.Rejected : ScriptReplyKind.Cancelled);
                Responses.Add(new(request, answer)); return answer;
            }
            Window dialog;
            Func<ScriptReply> reply;
            Func<GitReference?> selectedReference = () => null;
            switch (request.Kind)
            {
                case ScriptRequestKind.UserInput:
                    string caption = string.Format(_host._context.Culture,
                        _translations.Translate("TranslatedStrings", "_scriptUserInputCaption", "Text", "User input for script '{0}'"), request.ScriptName);
                    SimplePromptDialog prompt = new(caption, request.Label, request.DefaultValue, _translations);
                    dialog = prompt; reply = () => prompt.Reply;
                    break;
                case ScriptRequestKind.UserFiles:
                    FilePromptDialog files = new(_translations, _host._browseFiles);
                    dialog = files; reply = () => files.Reply;
                    break;
                case ScriptRequestKind.ChooseRemote:
                    QuickStringSelectorDialog strings = new(request.Candidates ?? [], _translations);
                    dialog = strings; reply = () => strings.Reply;
                    break;
                case ScriptRequestKind.ChooseReference:
                    QuickGitRefSelectorDialog references = new(QuickGitRefAction.Select, _host.ReferenceChoices(request), _translations);
                    dialog = references;
                    reply = () => references.WasAccepted
                        ? new(ScriptReplyKind.Accepted, references.SelectedReference?.ShortName ?? "") : new(ScriptReplyKind.Cancelled);
                    selectedReference = () => references.WasAccepted ? references.SelectedReference : null;
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(request));
            }
            dialog.RequestedThemeVariant = _host._owner.ActualThemeVariant;
            _active = dialog;
            try
            {
                token.ThrowIfCancellationRequested();
                if (dialog is QuickStringSelectorDialog or QuickGitRefSelectorDialog)
                {
                    bool Current() => !token.IsCancellationRequested && !_lifetime.IsCancellationRequested
                        && _host._owner.IsVisible && ReferenceEquals(_active, dialog);
                    if (!Current()) { throw new OperationCanceledException(token); }
                    PixelPoint initial = dialog.Position = _host._selectorPosition ?? _host._owner.PointToScreen(new Point(12, 32));
                    if (_host._selectorPlacement is { } placement)
                    {
                        dialog.Opened += (_, _) =>
                        {
                            // Apply the actual mapped size once. Never chase a later user move or resize.
                            if (Current() && dialog.IsVisible && dialog.Position == initial) { dialog.Position = placement(dialog); }
                        };
                    }
                }
                token.ThrowIfCancellationRequested(); await dialog.ShowDialog(_host._owner);
            }
            finally
            {
                dialog.Close();
                // Drain the owned picker (or an injected provider) before exposing
                // prepared text, including when the prompt or owner closes.
                if (dialog is FilePromptDialog files) { await files.BrowseCompletion; }
                if (ReferenceEquals(_active, dialog)) { _active = null; }
            }
            token.ThrowIfCancellationRequested();
            ScriptReply response = reply(); Responses.Add(new(request, response, selectedReference()));
            return response;
        }

        public void Dispose()
        {
            Dispatcher.UIThread.VerifyAccess();
            _host._owner.Closing -= OwnerClosing; _closeOnCancellation.Dispose(); _lifetime.Dispose();
        }
    }
}
