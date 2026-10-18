#nullable enable
using System.Collections.Immutable;
using System.Globalization;

namespace GitExtensions.Linux.Spelling;

/// <summary>
/// One editor owner, one mutable engine. No UI callbacks or static engine cache.
/// Consumer cancellation does not abandon raw work; BeginClose drains it.
/// </summary>
public sealed class SpellCheckService : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<bool> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DictionaryCatalog _catalog;
    private readonly UserDictionaryStore _user;
    private readonly string _name;
    private readonly CultureInfo _culture;
    private readonly Func<SpellDictionary, CancellationToken, NetSpellEngine> _loader;
    private Task _tail;
    private NetSpellEngine? _engine;
    private bool _closing;

    public string DictionaryName => _name;
    public string UserDictionaryFile => _user.FilePath;
    public bool IsEnabled => _name != DictionaryCatalog.NoneName;
    public bool IsClosing { get { lock (_sync) { return _closing; } } }
    public Task Ready { get; }
    public Task CloseCompletion => _closed.Task;

    public SpellCheckService(DictionaryCatalog catalog, string? dictionaryName, string userDictionaryFile)
        : this(catalog, dictionaryName, userDictionaryFile, NetSpellEngine.Load) { }

    internal SpellCheckService(DictionaryCatalog catalog, string? dictionaryName, string userDictionaryFile,
        Func<SpellDictionary, CancellationToken, NetSpellEngine> loader)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _name = DictionaryCatalog.NormalizeName(dictionaryName);
        _user = new(userDictionaryFile);
        string relative = Path.GetRelativePath(catalog.InstallationDirectory, _user.FilePath);
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        { throw new ArgumentException("The user dictionary must be outside the read-only installed dictionary directory.", nameof(userDictionaryFile)); }
        try { _culture = CultureInfo.GetCultureInfo(_name); }
        catch (CultureNotFoundException) { _culture = CultureInfo.InvariantCulture; }
        _tail = IsEnabled ? Task.Run(Load, CancellationToken.None) : Task.CompletedTask;
        Ready = _tail;
    }

    private void Load()
    {
        CancellationToken token = _lifetime.Token;
        token.ThrowIfCancellationRequested();
        using CultureScope culture = new(_culture);
        SpellDictionary descriptor = _catalog.Resolve(_name);
        token.ThrowIfCancellationRequested();
        _engine = _loader(descriptor, token);
        token.ThrowIfCancellationRequested();
        _engine.SetUserWords(_user.Load(token));
    }

    public Task<SpellCheckResult> CheckAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Enqueue(token => IsEnabled ? Engine(token).Check(text, token) : new(_name, text, []), cancellationToken);
    }

    public Task<SpellSuggestionsResult> SuggestAsync(string word, int maximum = 5, CancellationToken cancellationToken = default)
    {
        UserDictionaryStore.ValidateWord(word);
        if (maximum < 1) { throw new ArgumentOutOfRangeException(nameof(maximum)); }
        return Enqueue(token => IsEnabled ? Engine(token).Suggest(word, maximum, token) : new(_name, word, []), cancellationToken);
    }

    /// <summary>
    /// Appends exact, case-sensitive completion words only to this editor's engine memory.
    /// The captured words remain until that engine retires; no user dictionary is read or written.
    /// A canceled waiter may return before admitted raw work has drained.
    /// </summary>
    public Task AddCompletionWordsAsync(IEnumerable<string> words, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(words);
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<string> captured = words.ToImmutableArray();
        return Enqueue(token =>
        {
            if (IsEnabled)
            {
                // Enqueue has awaited Ready. Do not use Engine(token), whose normal
                // Check/Suggest path refreshes user.dic for other editors' persistent additions.
                NetSpellEngine engine = _engine ?? throw new InvalidOperationException("Dictionary initialization did not produce an engine.");
                engine.AddCompletionWords(captured, token);
            }
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Adds to the explicit profile file, atomically. A canceled waiter may return
    /// after the commit point; retrying this operation is idempotent.
    /// </summary>
    public Task<bool> AddWordAsync(string word, CancellationToken cancellationToken = default)
    {
        UserDictionaryStore.ValidateWord(word);
        return Enqueue(token =>
        {
            if (!IsEnabled) { throw new InvalidOperationException("No dictionary is selected."); }
            _ = Engine(token);
            bool added = _user.AddAsync(word, token).GetAwaiter().GetResult();
            _engine!.SetUserWords(_user.Load(token));
            return added;
        }, cancellationToken);
    }

    private NetSpellEngine Engine(CancellationToken token)
    {
        Ready.GetAwaiter().GetResult(); // Queue ordering means this never blocks a UI thread.
        token.ThrowIfCancellationRequested();
        NetSpellEngine engine = _engine ?? throw new InvalidOperationException("Dictionary initialization did not produce an engine.");
        // Fresh immutable words keep independent windows coherent after Add.
        engine.SetUserWords(_user.Load(token));
        return engine;
    }

    private Task<T> Enqueue<T>(Func<CancellationToken, T> operation, CancellationToken consumer)
    {
        consumer.ThrowIfCancellationRequested();
        Task<T> raw;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            Task previous = _tail;
            raw = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); } catch (Exception) { /* Each job observes its own result; a failed Add does not poison later reads. */ }
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, consumer);
                CancellationToken token = linked.Token;
                token.ThrowIfCancellationRequested();
                // Load failures remain visible on every read, never become None.
                await Ready.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                using CultureScope culture = new(_culture);
                T result = operation(token);
                token.ThrowIfCancellationRequested();
                return result;
            }, CancellationToken.None);
            _tail = raw;
        }
        return raw.WaitAsync(consumer);
    }

    public Task BeginClose()
    {
        Task tail;
        lock (_sync)
        {
            if (_closing) { return _closed.Task; }
            _closing = true;
            tail = _tail;
        }
        _ = FinishCloseAsync(tail);
        return _closed.Task;
    }

    private async Task FinishCloseAsync(Task tail)
    {
        Exception? failure = null;
        try
        {
            Task cancellation = _lifetime.CancelAsync();
            try { await tail.ConfigureAwait(false); } catch (Exception) { /* Work faults belong to Ready/Check/Suggest/Add, while closure still drains. */ }
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            NetSpellEngine? engine = _engine;
            _engine = null;
            try { engine?.Dispose(); }
            catch (Exception error) { failure ??= error; }
            _lifetime.Dispose();
            if (failure is null) { _closed.TrySetResult(true); }
            else { _closed.TrySetException(failure); }
        }
    }

    public async ValueTask DisposeAsync() => await BeginClose().ConfigureAwait(false);

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;
        internal CultureScope(CultureInfo culture) { CultureInfo.CurrentCulture = culture; }
        public void Dispose() { CultureInfo.CurrentCulture = _previous; }
    }
}
