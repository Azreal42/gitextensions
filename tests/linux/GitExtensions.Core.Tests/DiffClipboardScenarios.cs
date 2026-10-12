using GitCommands.Settings;
using GitExtensions.Linux;

internal static class DiffClipboardScenarios
{
    private const string Patch = "diff --git a/a b/a\nindex 111..222 100644\n--- a/a\n+++ b/a\n@@ -20,3 +20,3 @@\n keep\n-old λ\n+new 🦊\n tail\n\\ No newline at end of file\n";
    private const string Header = "diff --git a/a b/a\nindex 111..222 100644\n--- a/a\n+++ b/a\n@@ -20,3 +20,3 @@\n";

    public static Task PatchVerbatimAsync()
    {
        string patch = Patch.Replace("\n", "\r\n", StringComparison.Ordinal);
        Check(GitDiffClipboard.GetText(patch, GitDiffCopyOperation.Patch, autoCrlf: AutoCRLFType.@true) == patch,
            "Copy patch preserves the complete display, including CRLF, headers and no-newline markers, regardless of autocrlf.");
        int start = patch.IndexOf("new 🦊", StringComparison.Ordinal);
        GitDiffTextSelection selection = new(start, "new 🦊".Length);
        Check(GitDiffClipboard.GetText(patch, GitDiffCopyOperation.Patch, selection) == "new 🦊",
            "A selected patch fragment stays exact at UTF16 offsets, including surrogate pairs and missing diff prefixes.");
        Check(GitDiffClipboard.GetText(patch, GitDiffCopyOperation.Patch, new(patch.Length, 0)) == patch,
            "An empty selection at the document end copies the whole patch rather than an empty string.");
        Check(GitDiffClipboard.GetText(patch, GitDiffCopyOperation.Patch, new(2, 9)) == patch.Substring(2, 9),
            "Copy patch does not insert a protective prefix when selection starts mid-line.");
        return Task.CompletedTask;
    }

    public static Task WholeVersionsAsync()
    {
        Check(GitDiffClipboard.GetText(Patch, GitDiffCopyOperation.NewVersion) == Header + " keep\n+new 🦊\n tail\n\\ No newline at end of file\n",
            "Whole-document new copy excludes deletions but preserves headers, hunk markers and remaining prefixes when the header is included.");
        Check(GitDiffClipboard.GetText(Patch, GitDiffCopyOperation.OldVersion) == Header + " keep\n-old λ\n tail\n\\ No newline at end of file\n",
            "Whole-document old copy excludes additions without claiming to reconstruct a complete source file.");
        Check(GitDiffClipboard.GetText(Patch, GitDiffCopyOperation.NewVersion, new(Patch.Length, 0))
            == GitDiffClipboard.GetText(Patch, GitDiffCopyOperation.NewVersion), "Empty version selection uses original position0 header semantics.");
        return Task.CompletedTask;
    }

    public static Task HunkSelectionAsync()
    {
        int start = Patch.IndexOf(" keep", StringComparison.Ordinal);
        GitDiffTextSelection selection = new(start, Patch.Length - start);
        Check(GitDiffClipboard.GetText(Patch, GitDiffCopyOperation.NewVersion, selection) == "keep\nnew 🦊\ntail\n\\ No newline at end of file\n",
            "A body-only new copy removes exactly one patch prefix and retains the no-newline marker verbatim.");
        Check(GitDiffClipboard.GetText(Patch, GitDiffCopyOperation.OldVersion, selection) == "keep\nold λ\ntail\n\\ No newline at end of file\n",
            "A body-only old copy retains only displayed context and removals, with the original trailing newline.");
        int added = Patch.IndexOf("+new 🦊", StringComparison.Ordinal);
        Check(GitDiffClipboard.GetText(Patch, GitDiffCopyOperation.NewVersion, new(added, "+new 🦊".Length)) == "new 🦊",
            "Selecting one added line returns only its visible content, without a synthesized newline or omitted file context.");
        return Task.CompletedTask;
    }

    public static Task MidLineSelectionAsync()
    {
        const string document = "header\n@@ -1,2 +1,2 @@\n+δ🦊+value\n-delete\n end";
        int start = document.IndexOf("+value", StringComparison.Ordinal);
        GitDiffTextSelection selection = new(start, document.Length - start);
        Check(GitDiffClipboard.GetText(document, GitDiffCopyOperation.NewVersion, selection) == "+value\nend",
            "A mid-line literal plus is protected by the artificial space instead of being stripped as a patch marker.");
        Check(GitDiffClipboard.GetText(document, GitDiffCopyOperation.OldVersion, selection) == "+value\ndelete\nend",
            "The same literal plus is not discarded by old-version filtering, while subsequent complete added lines would be.");
        int header = Patch.IndexOf("index", StringComparison.Ordinal) + 2;
        Check(GitDiffClipboard.GetText(Patch, GitDiffCopyOperation.NewVersion, new(header, 3)) == " dex",
            "The original protective-space behavior is retained even for a selection inside the file header.");
        int atStart = document.IndexOf("-delete", StringComparison.Ordinal);
        Check(GitDiffClipboard.GetText(document, GitDiffCopyOperation.NewVersion, new(atStart, "-delete".Length)) == "",
            "A selection at a real line start remains eligible for the original removal filter.");
        return Task.CompletedTask;
    }

    public static Task TriplesAndMarkerAsync()
    {
        const string document = "header\n@@ -1,4 +1,4 @@\n--- a\n+++ b\n-- code\n++ code\n++++literal\n\\ No newline at end of file\n";
        int start = document.IndexOf("--- a", StringComparison.Ordinal);
        GitDiffTextSelection selection = new(start, document.Length - start);
        Check(GitDiffClipboard.GetText(document, GitDiffCopyOperation.NewVersion, selection)
            == "-- a\n++ b\n+ code\n+++literal\n\\ No newline at end of file\n", "Triple prefixes bypass exclusion, but lose one prefix inside a selected hunk as in the original.");
        Check(GitDiffClipboard.GetText(document, GitDiffCopyOperation.OldVersion, selection)
            == "-- a\n++ b\n- code\n+++literal\n\\ No newline at end of file\n", "The historical triple exception applies to literal repeated signs as well as file headers.");
        const string combined = "header\n@@@ -1,1 -1,1 +1,1 @@@\n++added\n--removed\n  shared\n";
        int body = combined.IndexOf("++added", StringComparison.Ordinal);
        Check(GitDiffClipboard.GetText(combined, GitDiffCopyOperation.NewVersion, new(body, combined.Length - body)) == "+added\n shared\n",
            "Combined diff version copies retain the original one-character filtering semantics rather than inventing parent reconstruction.");
        return Task.CompletedTask;
    }

    public static Task LineEndingsAsync()
    {
        const string document = "header\r\n@@ -1,2 +1,2 @@\r\n+first\r\n+second\r\n";
        int start = document.IndexOf("+first", StringComparison.Ordinal);
        GitDiffTextSelection selection = new(start, document.Length - start);
        foreach (AutoCRLFType? setting in new AutoCRLFType?[] { null, AutoCRLFType.@false, AutoCRLFType.input, AutoCRLFType.@true })
        {
            string expected = setting == AutoCRLFType.@true ? "first" + Environment.NewLine + "second" + Environment.NewLine : "first\r\nsecond\r\n";
            Check(GitDiffClipboard.GetText(document, GitDiffCopyOperation.NewVersion, selection, autoCrlf: setting) == expected,
                "Version copying uses the linked original autocrlf helper, preserving CRLF except for true's native line endings.");
        }
        const string mixed = "a\r\nb\nc\rd\r\n";
        Check(GitDiffClipboard.GetText(mixed, GitDiffCopyOperation.OldVersion, isDiff: false, autoCrlf: AutoCRLFType.@true)
            == "a" + Environment.NewLine + "b\nc\rd" + Environment.NewLine, "The original CRLF branch deliberately leaves any mixed lone CR untouched.");
        Check(GitDiffClipboard.GetText("a\rb\r", GitDiffCopyOperation.NewVersion, isDiff: false, autoCrlf: AutoCRLFType.@true)
            == "a" + Environment.NewLine + "b" + Environment.NewLine, "Old-Mac text uses native newlines only when autocrlf=true.");
        Check(GitDiffClipboard.GetText("\r\n \r", GitDiffCopyOperation.NewVersion, isDiff: false, autoCrlf: AutoCRLFType.@true) == "\r\n \r",
            "Whitespace-only clipboard text is left unchanged by the original helper.");
        return Task.CompletedTask;
    }

    public static Task PlainAndValidationAsync()
    {
        const string noHunk = "--- old\n+++ new\n-removed\n+added\n context";
        Check(GitDiffClipboard.GetText(noHunk, GitDiffCopyOperation.NewVersion) == "-- old\n++ new\nadded\ncontext",
            "The original no-hunk sentinel strips a single prefix rather than guessing a file boundary.");
        Check(GitDiffClipboard.GetText(noHunk, GitDiffCopyOperation.OldVersion, isDiff: false) == noHunk,
            "Non-diff text bypasses filtering and prefix removal entirely.");
        Check(GitDiffClipboard.GetText(noHunk, GitDiffCopyOperation.NewVersion, new(1, 4), isDiff: false) == "-- o",
            "A non-diff mid-line selection stays literal without the diff's artificial space.");
        foreach (GitDiffCopyOperation operation in Enum.GetValues<GitDiffCopyOperation>())
        { Check(GitDiffClipboard.GetText("", operation) == "", "Empty documents remain empty for every original copy command."); }
        Expect(() => GitDiffClipboard.GetText(null!, GitDiffCopyOperation.Patch));
        Expect(() => GitDiffClipboard.GetText(Patch, (GitDiffCopyOperation)99));
        foreach (GitDiffTextSelection selection in new[] { new GitDiffTextSelection(-1, 0), new(0, -1), new(int.MaxValue, 0), new(1, int.MaxValue) })
        { Expect(() => GitDiffClipboard.GetText(Patch, GitDiffCopyOperation.NewVersion, selection)); }
        return Task.CompletedTask;
    }

    public static async Task RealPartialPatchAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        const string path = "literal:[1]* λ.txt";
        string original = string.Concat(Enumerable.Range(1, 60).Select(number => $"line{number}\n"));
        fixture.Write(path, original);
        await fixture.Repository.StageAllAsync();
        await fixture.Repository.CommitAsync("Tracked multiline clipboard fixture");
        fixture.Write(path, original.Replace("line30\n", "edited λ 🦊\n", StringComparison.Ordinal));
        await fixture.Repository.SetConfigAsync("diff.context", "3");
        string beforeIndex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(fixture.Repository.Path, ".git", "index"))));
        string patch = await fixture.Repository.GetDiffAsync(path);
        int hunk = patch.IndexOf("\n@@", StringComparison.Ordinal);
        int body = patch.IndexOf('\n', hunk + 1) + 1;
        GitDiffTextSelection selection = new(body, patch.Length - body);
        Check(GitDiffClipboard.GetText(patch, GitDiffCopyOperation.Patch) == patch, "A real Git patch remains exact canonical clipboard text.");
        Check(GitDiffClipboard.GetText(patch, GitDiffCopyOperation.NewVersion, selection) == "line27\nline28\nline29\nedited λ 🦊\nline31\nline32\nline33\n",
            "The real three-context patch copies only seven displayed new-version lines, not the other53 lines of the file.");
        Check(GitDiffClipboard.GetText(patch, GitDiffCopyOperation.OldVersion, selection) == "line27\nline28\nline29\nline30\nline31\nline32\nline33\n",
            "The old-version fragment comes from the real removed Git line and visible context.");
        Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(fixture.Repository.Path, ".git", "index")))) == beforeIndex
            && File.ReadAllText(fixture.File(path)) == original.Replace("line30\n", "edited λ 🦊\n", StringComparison.Ordinal),
            "Clipboard projection is read-only for the actual index and literal-path worktree file.");
    }

    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static void Expect(Action operation)
    { try { operation(); throw new InvalidOperationException("An invalid clipboard operation or UTF16 selection was accepted."); } catch (ArgumentException) { } }
}
