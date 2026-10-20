using System.Collections.ObjectModel;

namespace GitExtensions.Linux;

/// <summary>Windows BuildServerWatcher projection; raw snapshots and sibling workflows remain data.</summary>
public sealed class BuildServerRevisionProjection
{
    private sealed record Observation(BuildServerRun Run, long Sequence);
    private readonly Dictionary<(string Definition, string Id, string Hashes), Observation> _observed = [];
    private long _sequence;

    public void Clear() { _observed.Clear(); _sequence = 0; }

    public IReadOnlyDictionary<string, BuildServerCommitStatus> Publish(BuildServerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        HashSet<(string Definition, string Id, string Hashes)> admitted = [];
        foreach (BuildServerRun run in snapshot.RevisionObservations)
        {
            var identity = (run.DefinitionId, run.Id, string.Join('\0', run.CommitHashes.Select(sha => sha.ToUpperInvariant()).Order(StringComparer.Ordinal)));
            admitted.Add(identity);
            if (!_observed.TryGetValue(identity, out Observation? previous) || !SameObservation(previous.Run, run))
            { _observed[identity] = new(run, ++_sequence); }
        }
        foreach (var identity in _observed.Keys.Where(identity => !admitted.Contains(identity)).ToArray()) { _observed.Remove(identity); }
        // Rebuild from this snapshot's admitted observations: a removed winner cannot
        // survive merely because a sibling still refers to the same revision.
        Dictionary<string, Observation> revisions = new(StringComparer.OrdinalIgnoreCase);
        foreach (Observation observation in _observed.Values)
        {
            foreach (string sha in observation.Run.CommitHashes)
            {
                if (!revisions.TryGetValue(sha, out Observation? current) || Later(observation, current))
                { revisions[sha] = observation; }
            }
        }
        // One immutable build supplies every visible field and every browser target.
        // Equal starts replace only on a newly observed value, not cache re-enumeration.
        return new ReadOnlyDictionary<string, BuildServerCommitStatus>(revisions.ToDictionary(pair => pair.Key,
            pair => new BuildServerCommitStatus(pair.Key, pair.Value.Run.Status, Array.AsReadOnly(new[] { pair.Value.Run })), StringComparer.OrdinalIgnoreCase));
    }

    private static bool Later(Observation next, Observation current)
    {
        int startOrder = (next.Run.StartedAt ?? DateTimeOffset.MinValue).CompareTo(current.Run.StartedAt ?? DateTimeOffset.MinValue);
        return startOrder > 0 || startOrder == 0 && next.Sequence > current.Sequence;
    }

    private static bool SameObservation(BuildServerRun previous, BuildServerRun next)
        => (previous with { CommitHashes = next.CommitHashes }) == next
            && previous.CommitHashes.SequenceEqual(next.CommitHashes, StringComparer.OrdinalIgnoreCase);
}
