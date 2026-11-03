using System.Text.RegularExpressions;

namespace GitExtensions.Linux;

public static partial class GitRebaseEditor
{
    private const string TargetedCommitEditorArgument = "--target-rebase-commit";

    internal static string TargetedCommitEditorCommand(string executable, string target, GitRebaseAction action)
    {
        ValidateExecutable(executable);
        ValidateTargetedAction(target, action);
        // Command fragments are fixed words, a quoted executable, or a validated hash.
        // Git supplies its editor filename separately, with its own shell quoting.
        return QuoteExecutable(executable) + " " + TargetedCommitEditorArgument + " " + action.ToString().ToLowerInvariant() + " " + target;
    }

    private static bool TryHandleTargetedCommit(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.FirstOrDefault() != TargetedCommitEditorArgument) { return false; }
        try
        {
            if (args.Length != 4 || !Enum.TryParse(args[1], ignoreCase: true, out GitRebaseAction action))
            { throw new ArgumentException("Git must supply an action, selected commit and absolute todo filename."); }
            GitRebaseEditorRequest request = ReadNativeEditorRequest(["--edit-rebase-todo", args[3]]);
            WriteNativeEditorRequest(request, RewriteTargetedCommitTodo(request.Text, args[2], action));
        }
        catch (Exception exception)
        { Console.Error.WriteLine("Unable to prepare the selected commit: " + exception.Message); exitCode = 1; }
        return true;
    }

    /// <summary>Changes one selected instruction in Git's actual todo while retaining its merge graph and comments.</summary>
    public static string RewriteTargetedCommitTodo(string text, string target, GitRebaseAction action)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateTargetedAction(target, action);
        string[] lines = text.Split('\n');
        int changed = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            Match instruction = Regex.Match(line, @"^(?<indent>[ \t]*)(?<verb>pick|p|merge|m)(?<spacing>[ \t]+)(?:(?<flag>-C|-c)[ \t]+)?(?<hash>[0-9a-fA-F]{4,64})(?=[ \t\r]|$)", RegexOptions.CultureInvariant);
            if (!instruction.Success || !target.StartsWith(instruction.Groups["hash"].Value, StringComparison.OrdinalIgnoreCase)) { continue; }
            string verb = instruction.Groups["verb"].Value;
            bool merge = verb is "merge" or "m";
            if (merge != instruction.Groups["flag"].Success) { continue; }
            changed++;
            if (merge)
            {
                if (action == GitRebaseAction.Reword)
                {
                    Group flag = instruction.Groups["flag"];
                    lines[index] = line[..flag.Index] + "-c" + line[(flag.Index + flag.Length)..];
                }
                else
                {
                    string newline = line.EndsWith('\r') ? "\r\n" : "\n";
                    lines[index] = line.TrimEnd('\r') + newline + instruction.Groups["indent"].Value + "break" + (line.EndsWith('\r') ? "\r" : "");
                }
            }
            else
            {
                Group command = instruction.Groups["verb"];
                lines[index] = line[..command.Index] + action.ToString().ToLowerInvariant() + line[(command.Index + command.Length)..];
            }
        }
        if (changed != 1)
        { throw new InvalidOperationException("The selected commit must occur exactly once in Git's rebase instructions. No instructions were saved."); }
        return string.Join('\n', lines);
    }

    private static void ValidateTargetedAction(string target, GitRebaseAction action)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Length is not (40 or 64) || !target.All(char.IsAsciiHexDigit))
        { throw new ArgumentException("The selected commit must be a full object ID.", nameof(target)); }
        if (action is not (GitRebaseAction.Edit or GitRebaseAction.Reword))
        { throw new ArgumentOutOfRangeException(nameof(action), "Only Edit and Reword target an existing commit."); }
    }
}
