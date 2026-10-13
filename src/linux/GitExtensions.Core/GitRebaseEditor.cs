using System.Text.Json;

namespace GitExtensions.Linux;

public enum GitRebaseAction { Pick, Reword, Edit, Squash, Fixup, Drop }
public sealed record GitRebaseStep(GitRebaseAction Action, string Hash, string Subject, string? Message = null);
public sealed record GitRebasePlan(string Upstream, string Head, IReadOnlyList<GitRebaseStep> Steps);

internal sealed record RebaseEditorState(string EditorExecutable, string DonePath, IReadOnlyDictionary<string, string> Messages, string EncodingName = "utf-8");

/// <summary>The desktop app calls this before opening its GUI when invoked by Git as its fixed editor.</summary>
public static partial class GitRebaseEditor
{
    internal const string DirectoryVariable = "GITEXTENSIONS_REBASE_DIRECTORY";

    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] is not ("--write-rebase-plan" or "--write-rebase-message"))
        {
            return false;
        }

        try
        {
            string directory = Environment.GetEnvironmentVariable(DirectoryVariable)
                ?? throw new InvalidOperationException("Missing rebase editor state.");
            if (args.Length != 2 || !System.IO.Path.IsPathFullyQualified(args[1]))
            {
                throw new ArgumentException("Git must supply one absolute editor filename.");
            }

            if (args[0] == "--write-rebase-plan")
            {
                if (System.IO.Path.GetFileName(args[1]) != "git-rebase-todo")
                {
                    throw new ArgumentException("Unexpected rebase todo filename.");
                }

                File.Copy(System.IO.Path.Combine(directory, "todo"), args[1], overwrite: true);
            }
            else
            {
                if (System.IO.Path.GetFileName(args[1]) is not ("COMMIT_EDITMSG" or "message"))
                {
                    throw new ArgumentException("Unexpected commit message filename.");
                }

                RebaseEditorState state = ReadState(directory);
                string[][] actions = File.ReadLines(state.DonePath).Where(line => line.Length > 0 && !line.StartsWith('#')).Select(line => line.Split(' ', 3)).ToArray();
                string[]? action = actions.LastOrDefault();
                if (action is { Length: >= 2 } && action[0] is "reword" or "r")
                {
                    KeyValuePair<string, string>[] matching = state.Messages.Where(message => message.Key.StartsWith(action[1], StringComparison.Ordinal)).ToArray();
                    if (matching.Length != 1)
                    {
                        throw new InvalidOperationException("Missing unique edited message for reword.");
                    }

                    File.WriteAllBytes(args[1], GetStrictEncoding(state.EncodingName).GetBytes(matching[0].Value));
                }
                else
                {
                    // Git may defer the squash editor until the last fixup in a combined group.
                    // Use the most recent explicitly edited squash message in that group.
                    for (int index = actions.Length - 1; index >= 0; index--)
                    {
                        string[] combined = actions[index];
                        if (combined.Length < 2 || combined[0] is not ("squash" or "s" or "fixup" or "f"))
                        {
                            break;
                        }

                        if (combined[0] is "squash" or "s")
                        {
                            KeyValuePair<string, string>[] matching = state.Messages.Where(message => message.Key.StartsWith(combined[1], StringComparison.Ordinal)).ToArray();
                            if (matching.Length == 1)
                            {
                                File.WriteAllBytes(args[1], GetStrictEncoding(state.EncodingName).GetBytes(matching[0].Value));
                                break;
                            }
                        }
                    }
                    // Without an edited squash message, preserve Git's generated combined message.
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            exitCode = 1;
        }

        return true;
    }

    internal static RebaseEditorState ReadState(string directory)
        => JsonSerializer.Deserialize<RebaseEditorState>(File.ReadAllText(System.IO.Path.Combine(directory, "state.json")))
            ?? throw new FormatException("Invalid rebase editor state.");

    internal static IReadOnlyDictionary<string, string?> EnvironmentFor(string directory, RebaseEditorState state)
    {
        ValidateExecutable(state.EditorExecutable);
        return new Dictionary<string, string?>
        {
            [DirectoryVariable] = directory,
            ["GIT_SEQUENCE_EDITOR"] = state.EditorExecutable + " --write-rebase-plan",
            ["GIT_EDITOR"] = state.EditorExecutable + " --write-rebase-message",
        };
    }

    internal static void ValidateExecutable(string path)
    {
        // Git's editor configuration accepts a command string. Only a fixed absolute executable
        // followed by our constant option is supplied; no user plan or message becomes command text.
        if (!System.IO.Path.IsPathFullyQualified(path) || path.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('/' or '.' or '_' or '-')) || !File.Exists(path))
        {
            throw new ArgumentException("The rebase editor requires an existing absolute executable path containing only letters, digits, '/', '.', '_' or '-'.", nameof(path));
        }
    }
}
