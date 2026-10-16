using System.Globalization;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux.Scripts;

public enum ScriptExpansionStatus { Completed, Aborted, Cancelled, Declined }
public enum ScriptExpansionError { None, EmptyCommand, NoRevisionGrid, MissingSelectedRevision, MissingCurrentRevision, InputUnavailable }
public enum ScriptRequestKind { Confirmation, ChooseReference, ChooseRemote, UserInput, UserFiles }
public enum ScriptReplyKind { Accepted, Rejected, Cancelled }
public enum ScriptTargetKind { External, OpenUrl, Plugin, NavigateTo, PowerShell }

public sealed record ScriptRequest(ScriptRequestKind Kind, string ScriptName, string? Option = null,
    string? Label = null, string? DefaultValue = null, IReadOnlyList<string>? Candidates = null);
public sealed record ScriptReply(ScriptReplyKind Kind, string? Value = null);
/// <summary>Prepared template text, not process argv or an execution result. Shell/argument transport belongs to the future launcher.</summary>
public sealed record ScriptExpansionResult(string? Arguments, ScriptExpansionStatus Status,
    ScriptExpansionError Error = ScriptExpansionError.None, string? Option = null)
{
    public string? Command { get; init; }
    public ScriptTargetKind Target { get; init; } = ScriptTargetKind.External;
    public IReadOnlyList<ScriptRequest> Requests { get; init; } = [];
}

/// <summary>Windows substitution order and prompt semantics, without process launch or implicit shell evaluation.</summary>
public static class ScriptTemplate
{
    private static readonly Regex Quotes = new("(?<!\\\\)\"", RegexOptions.ExplicitCapture);
    private static readonly Regex UserInput = new(@"\{UserInput:(?<label>[^}=]+)(=(?<defaultValue>[^{}]*(({[^{}]+})+[^{}]*)*))?\}", RegexOptions.ExplicitCapture);
    private static readonly Regex Plugin = new(@"\{plugin.(?<name>.+)\}", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture);
    public static IReadOnlyList<string> Options { get; } = Array.AsReadOnly(new[]
    {
        "sHashes", "sTag", "sBranch", "sLocalBranch", "sRemoteBranch", "sRemoteBranchName", "sRemote", "sRemoteUrl", "sRemotePathFromUrl",
        "sHash", "sMessage", "sSubject", "sAuthor", "sCommitter", "sAuthorDate", "sCommitDate", "HEAD", "cTag", "cBranch", "cLocalBranch",
        "cRemoteBranch", "cRemoteBranchName", "cHash", "cMessage", "cSubject", "cAuthor", "cCommitter", "cAuthorDate", "cCommitDate",
        "cDefaultRemote", "cDefaultRemoteUrl", "cDefaultRemotePathFromUrl", "RepoName", "WorkingDir"
    });
    private static readonly string[] FileOptions = ["SelectedRelativePaths", "LineNumber", "ColumnNumber"];

    public static bool Contains(string arguments, string option)
    { ArgumentNullException.ThrowIfNull(arguments); ArgumentNullException.ThrowIfNull(option); return arguments.Contains(CreateOption(option, false), StringComparison.Ordinal); }
    public static bool DependsOnSelectedRevision(string option)
    { ArgumentNullException.ThrowIfNull(option); return option.StartsWith('s'); }
    public static string CreateOption(string option, bool quoted)
        => string.IsNullOrWhiteSpace(option) ? "" : quoted ? "{{" + option.Trim() + "}}" : "{" + option.Trim() + "}";

    public static string ReplaceOption(string option, string arguments, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(arguments); ArgumentNullException.ThrowIfNull(values);
        return arguments.Replace(CreateOption(option, true), string.Join(' ', values.Select(Quote)), StringComparison.Ordinal)
            .Replace(CreateOption(option, false), string.Join(' ', values), StringComparison.Ordinal);
    }

    // Prompt labels and defaults are literal text, unlike generic option names.
    private static string ReplaceUserInput(string option, string arguments, string value)
        => arguments.Replace("{{" + option + "}}", Quote(value), StringComparison.Ordinal)
            .Replace("{" + option + "}", value, StringComparison.Ordinal);

    private static string Quote(string value)
    {
        string quoted = '"' + Quotes.Replace(value, "\\\"");
        if (quoted.EndsWith('\\')) { quoted += '\\'; }
        return quoted + '"';
    }

    public static Task<ScriptExpansionResult> ExpandAsync(string? arguments, ScriptContext context,
        Func<ScriptRequest, CancellationToken, Task<ScriptReply>>? requestHandler = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new Expansion(context.Snapshot(), "", requestHandler, cancellationToken).ParseAsync(arguments);
    }

    public static Task<ScriptExpansionResult> PrepareAsync(ScriptDefinition script, ScriptContext context,
        Func<ScriptRequest, CancellationToken, Task<ScriptReply>>? requestHandler = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script); ArgumentNullException.ThrowIfNull(context);
        return new Expansion(context.Snapshot(), script.GetDisplayName(), requestHandler, cancellationToken).PrepareAsync(script);
    }

    private sealed class Expansion(ScriptContext context, string scriptName,
        Func<ScriptRequest, CancellationToken, Task<ScriptReply>>? handler, CancellationToken token)
    {
        private readonly List<ScriptRequest> _requests = [];
        private bool _currentLoaded, _selectedLoaded;
        private string _currentRemote = "";

        private ScriptExpansionResult Result(string? arguments, ScriptExpansionStatus status = ScriptExpansionStatus.Completed,
            ScriptExpansionError error = ScriptExpansionError.None, string? option = null)
            => new(arguments, status, error, option) { Requests = Array.AsReadOnly(_requests.ToArray()) };

        private async Task<ScriptReply?> RequestAsync(ScriptRequest request)
        {
            token.ThrowIfCancellationRequested(); _requests.Add(request);
            if (handler is null) { return null; }
            ScriptReply reply = await handler(request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (reply is null || !Enum.IsDefined(reply.Kind)) { throw new InvalidOperationException("The script request returned an invalid reply."); }
            return reply;
        }

        internal async Task<ScriptExpansionResult> PrepareAsync(ScriptDefinition script)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(script.Command)) { return Result(null, ScriptExpansionStatus.Aborted, ScriptExpansionError.EmptyCommand); }
            if (!context.HasRevisionGrid && !string.IsNullOrEmpty(script.Arguments))
            {
                string? selectedOption = Options.FirstOrDefault(option => DependsOnSelectedRevision(option) && Contains(script.Arguments, option));
                if (selectedOption is not null) { return Result(null, ScriptExpansionStatus.Aborted, ScriptExpansionError.NoRevisionGrid, selectedOption); }
            }
            if (script.AskConfirmation)
            {
                ScriptReply? answer = await RequestAsync(new(ScriptRequestKind.Confirmation, scriptName)).ConfigureAwait(false);
                if (answer is null) { return Result(null, ScriptExpansionStatus.Aborted, ScriptExpansionError.InputUnavailable); }
                if (answer.Kind != ScriptReplyKind.Accepted)
                { return Result(null, answer.Kind == ScriptReplyKind.Cancelled ? ScriptExpansionStatus.Cancelled : ScriptExpansionStatus.Declined); }
            }
            ScriptExpansionResult inputs = await ParseInputsAsync(script.Arguments).ConfigureAwait(false);
            if (inputs.Status != ScriptExpansionStatus.Completed) { return inputs; }
            ScriptExpansionResult parsed = await ParseAsync(inputs.Arguments).ConfigureAwait(false);
            if (parsed.Status != ScriptExpansionStatus.Completed) { return parsed; }
            string command = script.Command;
            bool Matches(string value) => context.Culture.CompareInfo.Compare(command, value, CompareOptions.IgnoreCase) == 0;
            bool openUrl = Matches("{openurl}");
            if (Matches("git") || Matches("{git}")) { command = context.GitExecutable; }
            else if (Matches("gitextensions") || Matches("{gitextensions}") || Matches("gitex") || Matches("{gitex}")) { command = context.ApplicationExecutable; }
            else if (openUrl) { command = context.OpenUrlExecutable; }
            else if (Plugin.Match(command) is { Success: true } plugin) { command = "plugin:" + plugin.Groups["name"].Value.ToLower(context.Culture); }
            command = command.Replace("{WorkingDir}", context.WorkingDir, StringComparison.Ordinal);
            ScriptTargetKind target = script.IsPowerShell ? ScriptTargetKind.PowerShell
                : command.StartsWith("plugin:", StringComparison.Ordinal) ? ScriptTargetKind.Plugin
                : command.StartsWith("navigateTo:", StringComparison.Ordinal) ? ScriptTargetKind.NavigateTo
                : openUrl && script.RunInBackground ? ScriptTargetKind.OpenUrl : ScriptTargetKind.External;
            if (target == ScriptTargetKind.NavigateTo && !context.HasRevisionGrid)
            { return Result(null, ScriptExpansionStatus.Aborted, ScriptExpansionError.NoRevisionGrid); }
            if (target == ScriptTargetKind.Plugin) { command = command.Replace("plugin:", "", StringComparison.Ordinal); }
            if (target == ScriptTargetKind.NavigateTo) { command = command.Replace("navigateTo:", "", StringComparison.Ordinal); }
            return parsed with { Command = command, Target = target };
        }

        private async Task<ScriptExpansionResult> ParseInputsAsync(string? arguments)
        {
            if (arguments is null) { return Result(null); }
            Match match;
            while ((match = UserInput.Match(arguments)).Success)
            {
                token.ThrowIfCancellationRequested();
                ScriptExpansionResult defaultValue = await ParseAsync(match.Groups["defaultValue"].Value).ConfigureAwait(false);
                if (defaultValue.Status != ScriptExpansionStatus.Completed) { return defaultValue; }
                string label = match.Groups["label"].Value;
                ScriptReply? reply = await RequestAsync(new(ScriptRequestKind.UserInput, scriptName, "UserInput:" + label, label, defaultValue.Arguments)).ConfigureAwait(false);
                if (reply is null) { return Result(null, ScriptExpansionStatus.Aborted, ScriptExpansionError.InputUnavailable); }
                if (reply.Kind != ScriptReplyKind.Accepted) { return Result(null, ScriptExpansionStatus.Cancelled); }
                arguments = ReplaceUserInput("UserInput:" + label, arguments, reply.Value ?? "");
                arguments = ReplaceUserInput(match.Value[1..^1], arguments, reply.Value ?? "");
            }
            foreach (string option in new[] { "UserInput", "UserFiles" })
            {
                if (!Contains(arguments, option)) { continue; }
                ScriptReply? reply = await RequestAsync(new(option == "UserInput" ? ScriptRequestKind.UserInput : ScriptRequestKind.UserFiles,
                    scriptName, option, DefaultValue: option == "UserInput" ? "" : null)).ConfigureAwait(false);
                if (reply is null) { return Result(null, ScriptExpansionStatus.Aborted, ScriptExpansionError.InputUnavailable, option); }
                if (reply.Kind != ScriptReplyKind.Accepted) { return Result(null, ScriptExpansionStatus.Cancelled); }
                arguments = ReplaceOption(option, arguments, [reply.Value ?? ""]);
            }
            return Result(arguments);
        }

        internal async Task<ScriptExpansionResult> ParseAsync(string? arguments)
        {
            token.ThrowIfCancellationRequested();
            _currentLoaded = _selectedLoaded = false; _currentRemote = "";
            if (string.IsNullOrWhiteSpace(arguments)) { return Result(arguments); }
            IEnumerable<string> providerOptions = context.ProviderOptions ?? FileOptions.Concat(context.AdditionalOptions.Select(value => value.Name));
            foreach (string option in Options.Concat(providerOptions).Distinct(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (!Contains(arguments, option)) { continue; }
                if (!_currentLoaded && (option.StartsWith('c') || option == "HEAD"))
                {
                    if (context.CurrentRevision is null) { return Result(null, ScriptExpansionStatus.Aborted, ScriptExpansionError.MissingCurrentRevision, option); }
                    ScriptReference[] local = context.CurrentReferences.Where(reference => reference.Kind == ScriptReferenceKind.LocalBranch).ToArray();
                    if (local.Length == 1) { _currentRemote = context.BranchRemotes.GetValueOrDefault(local[0].Name) ?? ""; }
                    else
                    {
                        _currentRemote = context.CurrentRemote;
                        if (_currentRemote.Length == 0)
                        {
                            string? branch = await SelectAsync("cDefaultRemote", local.Select(reference => reference.Name).ToArray(), false).ConfigureAwait(false);
                            if (branch is null) { return Result(null, ScriptExpansionStatus.Aborted, ScriptExpansionError.InputUnavailable, option); }
                            _currentRemote = context.BranchRemotes.GetValueOrDefault(branch) ?? "";
                        }
                    }
                    _currentLoaded = true;
                }
                else if (!_selectedLoaded && context.HasRevisionGrid && DependsOnSelectedRevision(option))
                {
                    if (context.SelectedRevision is null) { return Result(null, ScriptExpansionStatus.Aborted, ScriptExpansionError.MissingSelectedRevision, option); }
                    _selectedLoaded = true;
                }
                if (!context.HasRevisionGrid && Options.Contains(option) && DependsOnSelectedRevision(option))
                { return Result(null, ScriptExpansionStatus.Aborted, ScriptExpansionError.NoRevisionGrid, option); }
                ScriptRevision? revision = option.StartsWith('s') ? context.SelectedRevision : context.CurrentRevision;
                if ((option is "sAuthor" or "cAuthor") && revision?.Author is null
                    || (option is "sCommitter" or "cCommitter") && revision?.Committer is null)
                { continue; } // ParseScriptArguments retains a placeholder when a nullable scalar is absent.
                string[]? values = await ValuesAsync(option).ConfigureAwait(false);
                if (values is null) { return Result(null, ScriptExpansionStatus.Aborted, ScriptExpansionError.InputUnavailable, option); }
                arguments = ReplaceOption(option, arguments, values);
            }
            return Result(arguments);
        }

        private async Task<string?> SelectAsync(string option, string[] values, bool remote)
        {
            if (values.Length == 0) { return ""; }
            if (values.Length == 1) { return values[0]; }
            ScriptReply? reply = await RequestAsync(new(remote ? ScriptRequestKind.ChooseRemote : ScriptRequestKind.ChooseReference,
                scriptName, option, Candidates: Array.AsReadOnly(values))).ConfigureAwait(false);
            if (reply is null) { return null; }
            if (reply.Kind != ScriptReplyKind.Accepted) { return ""; } // Linux uses an empty choice; the Windows script caller ignores DialogResult.
            string value = reply.Value ?? "";
            if (value.Length > 0 && !values.Contains(value, StringComparer.Ordinal)) { throw new ArgumentException("The selected value is not a candidate.", nameof(reply)); }
            return value;
        }

        private async Task<string[]?> ValuesAsync(string option)
        {
            if (!Options.Contains(option) && context.AdditionalOptions.FirstOrDefault(item => item.Name == option) is { } supplied)
            { return supplied.Values.ToArray(); }
            bool selected = option.StartsWith('s');
            ScriptRevision? revision = selected ? context.SelectedRevision : context.CurrentRevision;
            IReadOnlyList<ScriptReference> references = selected ? context.SelectedReferences : context.CurrentReferences;
            string? value;
            switch (option)
            {
                case "sHashes": value = string.Join(' ', context.SelectedRevisions.Select(commit => commit.Hash)); break;
                case "HEAD": value = context.CurrentBranch.Length == 0 ? context.CurrentRevision!.Hash : context.CurrentBranch; break;
                case "sHash": case "cHash": value = revision!.Hash; break;
                case "sMessage": case "cMessage": value = revision!.Body?.Replace("\n", "\\n", StringComparison.Ordinal) ?? revision.Subject; break;
                case "sSubject": case "cSubject": value = revision!.Subject; break;
                case "sAuthor": case "cAuthor": value = revision!.Author; break;
                case "sCommitter": case "cCommitter": value = revision!.Committer; break;
                case "sAuthorDate": case "cAuthorDate": value = revision!.AuthorDate.ToString(context.Culture); break;
                case "sCommitDate": case "cCommitDate": value = revision!.CommitDate.ToString(context.Culture); break;
                case "sTag": case "cTag": value = await SelectReferences(ScriptReferenceKind.Tag); break;
                case "sLocalBranch": case "cLocalBranch": value = await SelectReferences(ScriptReferenceKind.LocalBranch); break;
                case "sRemoteBranch": case "cRemoteBranch": value = await SelectReferences(ScriptReferenceKind.RemoteBranch); break;
                case "sBranch": case "cBranch": value = await SelectAsync(option, references.Where(reference => reference.Kind != ScriptReferenceKind.Tag).Select(reference => reference.Name).ToArray(), false); break;
                case "sRemoteBranchName": case "cRemoteBranchName":
                    value = await SelectReferences(ScriptReferenceKind.RemoteBranch);
                    if (value is not null) { int slash = value.IndexOf('/'); if (slash >= 0) { value = value[(slash + 1)..]; } }
                    break;
                case "sRemote": case "sRemoteUrl": case "sRemotePathFromUrl":
                    value = await SelectAsync(option, references.Where(reference => reference.Kind == ScriptReferenceKind.RemoteBranch).Select(reference => reference.Remote).Distinct(StringComparer.Ordinal).ToArray(), true);
                    if (value is { Length: > 0 } && option != "sRemote") { value = context.RemoteUrls.GetValueOrDefault(value) ?? ""; if (option == "sRemotePathFromUrl") { value = RemotePath(value); } }
                    break;
                case "cDefaultRemote": value = _currentRemote; break;
                case "cDefaultRemoteUrl": value = _currentRemote.Length == 0 ? "" : context.RemoteUrls.GetValueOrDefault(_currentRemote) ?? ""; break;
                case "cDefaultRemotePathFromUrl": value = _currentRemote.Length == 0 ? "" : RemotePath(context.RemoteUrls.GetValueOrDefault(_currentRemote) ?? ""); break;
                case "RepoName": value = context.RepoName; break;
                case "WorkingDir": value = context.WorkingDir; break;
                case "SelectedRelativePaths":
                    return (context.SelectedFolder is string folder ? new[] { folder } : context.SelectedRelativePaths).Select(EscapePath).ToArray();
                case "LineNumber": return context.LineNumber is int line ? [line.ToString(context.Culture)] : [];
                case "ColumnNumber": return context.ColumnNumber is int column ? [column.ToString(context.Culture)] : [];
                default: return context.AdditionalOptions.FirstOrDefault(item => item.Name == option)?.Values.ToArray() ?? [];
            }
            return value is null ? null : [value];
            Task<string?> SelectReferences(ScriptReferenceKind kind) => SelectAsync(option, references.Where(reference => reference.Kind == kind).Select(reference => reference.Name).ToArray(), false);
        }

        private string EscapePath(string path) => context.ArgumentPlatform == ScriptArgumentPlatform.Windows
            ? path.Replace("\"", "\"\"", StringComparison.Ordinal)
            : path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

        private static string RemotePath(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && !Uri.TryCreate("ssh://" + url.Replace(":", "/", StringComparison.Ordinal), UriKind.Absolute, out uri)) { return ""; }
            string path = uri.LocalPath; int dot = path.LastIndexOf('.'); return dot >= 0 ? path[..dot] : path;
        }
    }
}
