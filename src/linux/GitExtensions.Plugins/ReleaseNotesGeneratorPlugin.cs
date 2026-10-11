using System.Net;
using System.Text;
using GitExtensions.Plugins.ReleaseNotesGenerator;

namespace GitExtensions.Linux.Plugins;

/// <summary>Reuses the original parser and its fixed Git log format, with structured native exports.</summary>
public sealed class ReleaseNotesGeneratorPlugin : INativePlugin
{
    public PluginDescriptor Descriptor { get; } = new(new Guid("49E7F2D6-AD79-489E-80A4-5CD212AE6DF3"),
        "Release Notes Generator", "Generate release notes for an exclusive-from, inclusive-to revision range, most recent first.",
        "IconReleaseNotesGenerator",
        [new("fromRevision", "From revision (excluded)", PluginParameterKind.Text, Required: true),
         new("toRevision", "To revision", PluginParameterKind.Text, "HEAD", Required: true),
         new("format", "Export format", PluginParameterKind.Choice, "text", Choices: ["text", "tsv", "html"])]);

    public async Task<PluginResult> ExecuteAsync(PluginContext context, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        string from = PluginValues.Text(Descriptor, values, "fromRevision"), to = PluginValues.Text(Descriptor, values, "toRevision");
        string format = PluginValues.Text(Descriptor, values, "format");
        string fromHash = await context.Repository.ResolveRevisionAsync(from, cancellationToken).ConfigureAwait(false);
        string toHash = await context.Repository.ResolveRevisionAsync(to, cancellationToken).ConfigureAwait(false);
        string output = (await context.Repository.RunAsync(["log", "--no-show-signature", "--no-decorate", "--pretty=format:%h@%s%b", "--abbrev-commit", fromHash + ".." + toHash, "--"], cancellationToken).ConfigureAwait(false)).StandardOutput;
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
