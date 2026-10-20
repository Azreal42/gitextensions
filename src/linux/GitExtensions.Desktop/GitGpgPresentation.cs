using System.Text;
using GitCommands.Git.Gpg;

namespace GitExtensions.Linux.Desktop;

public static class GitGpgPresentation
{
    public static string Format(GitGpgVerification verification, Func<string, string> translate)
    {
        ArgumentNullException.ThrowIfNull(verification); ArgumentNullException.ThrowIfNull(translate);
        if (verification.Information.TagStatus == TagStatus.NoTag) { return verification.CommitDisplayText; }
        StringBuilder text = new(verification.CommitDisplayText.TrimEnd('\r', '\n'));
        text.Append("\n\n").Append(translate("Tag signature")).Append(": ").Append(translate(Status(verification.Information.TagStatus)));
        foreach (GitAnnotatedTagVerification tag in verification.Tags)
        {
            text.Append("\n\n").Append(tag.Name).Append(" — ").Append(translate(Status(tag.Status))).Append('\n');
            text.Append(tag.VerificationMessage);
        }
        return text.ToString();
    }

    private static string Status(TagStatus status) => status switch
    {
        TagStatus.OneGood => "Valid tag signature", TagStatus.OneBad => "Tag signature error",
        TagStatus.Many => "Multiple tags", TagStatus.NoPubKey => "Public key unavailable",
        TagStatus.TagNotSigned => "Tag is not signed", _ => "No tag"
    };
}
