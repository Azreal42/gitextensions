using System.Globalization;
using System.Net;
using System.Text;
using GitExtensions.Plugins.ReleaseNotesGenerator;

namespace GitExtensions.Linux.Plugins;

/// <summary>Reuses the original parser and an editable Git log argument template, with structured native exports.</summary>
public sealed class ReleaseNotesGeneratorPlugin : INativePlugin
{
    public const string DefaultGitLogArguments = "--pretty=\"format:%h@%s%b\" --abbrev-commit {0}..{1}";
    public PluginDescriptor Descriptor { get; } = new(new Guid("49E7F2D6-AD79-489E-80A4-5CD212AE6DF3"),
        "Release Notes Generator", "Generate release notes for an exclusive-from, inclusive-to revision range, most recent first.",
        "IconReleaseNotesGenerator",
        [new("fromRevision", "From revision (excluded)", PluginParameterKind.Text, Required: true),
         new("toRevision", "To revision", PluginParameterKind.Text, "HEAD", Required: true),
         new("arguments", "git log arguments:", PluginParameterKind.Text, DefaultGitLogArguments, Required: true,
             Hint: "{0} = from revision; {1} = to revision."),
         new("format", "Export format", PluginParameterKind.Choice, "text", Choices: ["text", "tsv", "html"])]);

    public async Task<PluginResult> ExecuteAsync(PluginContext context, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        string from = PluginValues.Text(Descriptor, values, "fromRevision"), to = PluginValues.Text(Descriptor, values, "toRevision");
        string format = PluginValues.Text(Descriptor, values, "format");
        string template = PluginValues.Text(Descriptor, values, "arguments");
        string fromHash = await context.Repository.ResolveRevisionAsync(from, cancellationToken).ConfigureAwait(false);
        string toHash = await context.Repository.ResolveRevisionAsync(to, cancellationToken).ConfigureAwait(false);
        string expanded;
        try { expanded = string.Format(CultureInfo.CurrentCulture, template, fromHash, toHash); }
        catch (FormatException error)
        { throw new FormatException("Invalid Git log arguments format. Use {0} for from revision and {1} for to revision, and double literal braces. " + error.Message, error); }
        List<string> arguments;
        try { arguments = [.. GourceProcess.ParseArguments(expanded)]; }
        catch (ArgumentException error)
        { throw new ArgumentException("Invalid Git log arguments quoting. " + error.Message, nameof(values), error); }
        int boundary = arguments.FindIndex(argument => argument is "--" or "--end-of-options");
        if (boundary < 0) { boundary = arguments.Count; }
        if (arguments.Take(boundary).Any(argument => argument == "--output" || argument.StartsWith("--output=", StringComparison.Ordinal)))
        { throw new ArgumentException("Git log arguments cannot use --output: release notes must remain in the read-only result and its exports.", nameof(values)); }
        // Keep the portable reader's output contract even when the user adds log options.
        // Insert before a revision/path boundary, after user options; paths stay literal.
        arguments.InsertRange(boundary, ["--no-show-signature", "--no-decorate"]);
        arguments.Insert(0, "log");
        if (!arguments.Contains("--", StringComparer.Ordinal)) { arguments.Add("--"); }
        string output = (await context.Repository.RunAsync(arguments, cancellationToken).ConfigureAwait(false)).StandardOutput;
        LogLine[] notes = new GitLogLineParser().Parse(output.Split('\n')).ToArray();
        string header = $"Commit log from '{from}' to '{to}' (most recent first):";
        StringBuilder text = new();
        if (format == "html")
        {
            text.Append("<p>").Append(WebUtility.HtmlEncode(header)).Append("</p><table>\n");
            foreach (LogLine note in notes)
                text.Append("<tr><td>").Append(WebUtility.HtmlEncode(note.Commit)).Append("</td><td>")
                    .Append(string.Join("<br/>", note.MessageLines.Select(WebUtility.HtmlEncode))).Append("</td></tr>\n");
            text.Append("</table>");
        }
        else
        {
            text.Append(header).Append('\n');
            foreach (LogLine note in notes)
                text.Append(note.Commit).Append(format == "tsv" ? '\t' : ' ')
                    .Append(string.Join(format == "tsv" ? "\n\t" : "\n        ", note.MessageLines.Where(line => !string.IsNullOrWhiteSpace(line)))).Append('\n');
        }
        return new(text.ToString(), new(["Commit", "Message"], notes.Select(note =>
            new PluginRow(note.Commit, [note.Commit, string.Join('\n', note.MessageLines)])).ToArray()));
    }
}
