namespace SemanticPortrait.Core;

public record TraceEntry(string Utc, string Source, string Kind, string Name, string Detail);

/// <summary>
/// In-memory developer trace of what the agents do: tool calls (main vs. clean analyst),
/// reflections, replies, and errors. Inspectable from the dev drawer. Thread-safe (the
/// background analyst writes to it too).
/// </summary>
public sealed class TraceLog
{
    private readonly object _gate = new();
    private readonly List<TraceEntry> _entries = new();
    private const int Max = 500;

    public void Add(string source, string kind, string name, string detail = "")
    {
        lock (_gate)
        {
            _entries.Add(new TraceEntry(DateTime.UtcNow.ToString("HH:mm:ss"), source, kind, name, detail));
            if (_entries.Count > Max) _entries.RemoveRange(0, _entries.Count - Max);
        }
    }

    /// <summary>Newest-first snapshot.</summary>
    public List<TraceEntry> Snapshot()
    {
        lock (_gate) { var c = new List<TraceEntry>(_entries); c.Reverse(); return c; }
    }

    public void Clear() { lock (_gate) _entries.Clear(); }
}
