namespace SemanticPortrait.Core;

// Row records returned by Db — the app's storage-level DTOs.
public record StoredMessage(long Id, string Role, string Text, string CreatedUtc, string? Detail = null);
public record StoredNote(long Id, string Text, string CreatedUtc, string UpdatedUtc);
public record SearchHit(string RefType, long RefId, string Text, string CreatedUtc, double Score);
public record GraphNode(long Id, string Category, string Label, bool Inferred, double Confidence);
public record GraphEdge(long Id, long Src, long Dst, string Type, string Label, bool Inferred, double Confidence);
public record EntryMeta(long MessageId, string EntryUtc, string Mood, double Valence, double Intensity,
    double Energy, string Topics, string People, string Summary);
public record Prediction(long Id, string CreatedUtc, string Claim, string Criterion,
    string? DueUtc, string? ResolvedUtc, string? Outcome, double? Score);
public record EventRow(long Id, string EventUtc, string Summary);
public record TodoItem(long Id, string Text, bool Done, string CreatedUtc);
public record Reminder(long Id, string DueUtc, string Text, bool Fired);
public record Notification(long Id, string CreatedUtc, string RefType, long RefId, string Title, string Body, bool Read, bool Surfaced);
/// <summary>One 1-hop graph edge as seen FROM a focus node: the relation, its direction, and the peer.</summary>
public record NeighborEdge(string Type, bool Outgoing, GraphNode Peer, bool Inferred, double Confidence);

/// <summary>
/// Local SQLite store for the eternal thread + embeddings.
/// Vector search is brute-force cosine over stored embeddings (ample for one user's
/// data; an ANN index / sqlite-vec is a later optimization). Encryption-at-rest is a
/// later milestone. All access is serialized via a lock (single shared connection,
/// hit from both the UI thread and the background analyst subagent).
/// </summary>
