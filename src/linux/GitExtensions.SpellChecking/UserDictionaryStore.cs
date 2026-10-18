#nullable enable
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux.Spelling;

/// <summary>Profile-owned user.dic; never writes installed dictionaries.</summary>
internal sealed partial class UserDictionaryStore
{
    // Only file-write exclusion is shared, never a mutable spelling engine.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal string FilePath { get; }

    internal UserDictionaryStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!Path.IsPathFullyQualified(filePath)) { throw new ArgumentException("An explicit absolute user dictionary path is required.", nameof(filePath)); }
        FilePath = Path.GetFullPath(filePath);
    }

    [GeneratedRegex(@"\A\w(?:[\w']*\w)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex SingleWord();

    internal static void ValidateWord(string word)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(word);
        if (!SingleWord().IsMatch(word)) { throw new ArgumentException("Add requires one literal spelling word.", nameof(word)); }
    }

    internal ImmutableArray<string> Load(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        FileStream stream;
        try { stream = new(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        using (stream)
        using (StreamReader reader = new(stream, Utf8, detectEncodingFromByteOrderMarks: true))
        {
            HashSet<string> words = new(StringComparer.Ordinal);
            while (reader.ReadLine() is { } line)
            {
                token.ThrowIfCancellationRequested();
                string word = line.Trim();
                if (word.Length == 0) { continue; }
                if (!SingleWord().IsMatch(word)) { throw new InvalidDataException($"Invalid word in user dictionary '{FilePath}'."); }
                words.Add(word);
            }
            return words.Order(StringComparer.Ordinal).ToImmutableArray();
        }
    }

    internal async Task<bool> AddAsync(string word, CancellationToken token)
    {
        ValidateWord(word);
        SemaphoreSlim semaphore = WriteLocks.GetOrAdd(FilePath, _ => new(1, 1));
        await semaphore.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        Exception? primaryFailure = null;
        try
        {
            string directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            // Cross-process .NET writers must also obtain this handle. Retaining
            // the empty lock file avoids an unlink/recreate split-lock race.
            using FileStream fileLock = new(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            HashSet<string> words = new(Load(token), StringComparer.Ordinal);
            if (!words.Add(word)) { return false; }
            temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            await using (StreamWriter writer = new(output, Utf8) { NewLine = "\n" })
            {
                foreach (string item in words.Order(StringComparer.Ordinal))
                { token.ThrowIfCancellationRequested(); await writer.WriteLineAsync(item.AsMemory(), token).ConfigureAwait(false); }
                await writer.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, FilePath, overwrite: true);
            temporary = null;
            return true;
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            try { if (temporary is not null) { File.Delete(temporary); } }
            catch (Exception cleanup) when (primaryFailure is not null)
            { primaryFailure.Data["UserDictionaryCleanupError"] = cleanup; }
            finally { semaphore.Release(); }
        }
    }
}
