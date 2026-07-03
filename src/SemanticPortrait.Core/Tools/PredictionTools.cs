using System.Text;
using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>
/// Calibration tools for the clean analyst:
///  - make_prediction: log a falsifiable prediction with an observable resolution criterion.
///  - list_open_predictions: see what's awaiting resolution.
///  - resolve_prediction: when reality arrives, mark outcome + an accuracy score (0..1).
/// </summary>
public sealed class PredictionTools
{
    private readonly Db _db;
    private readonly NotificationService? _notify;
    public PredictionTools(Db db, NotificationService? notify = null) { _db = db; _notify = notify; }

    private static readonly HashSet<string> _names = new()
        { "make_prediction", "list_open_predictions", "resolve_prediction" };
    public bool Handles(string name) => _names.Contains(name);

    public IReadOnlyList<object> Specs => new object[]
    {
        new
        {
            type = "function",
            name = "make_prediction",
            description = "When the user forecasts a situation, log a FALSIFIABLE prediction with an " +
                          "OBSERVABLE resolution criterion fixed now (e.g. 'she replies by Saturday', " +
                          "not 'it goes well'). Use sparingly — only for real, checkable forecasts.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    claim = new { type = "string", description = "The prediction." },
                    criterion = new { type = "string", description = "Observable condition that settles it true/false." },
                    due = new { type = "string", description = "Optional ISO date/time it should resolve by." },
                },
                required = new[] { "claim", "criterion" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "list_open_predictions",
            description = "List predictions awaiting resolution (id, claim, criterion).",
            parameters = new { type = "object", properties = new { }, additionalProperties = false },
        },
        new
        {
            type = "function",
            name = "resolve_prediction",
            description = "When the user reports what actually happened, resolve a prediction: record " +
                          "the outcome and an accuracy score from 0 (wrong) to 1 (right). Only resolve " +
                          "on real evidence, not assumption.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "integer" },
                    outcome = new { type = "string", description = "What actually happened." },
                    score = new { type = "number", description = "0..1 accuracy of the original prediction." },
                },
                required = new[] { "id", "outcome", "score" },
                additionalProperties = false,
            },
        },
    };

    public async Task<string> ExecuteAsync(string name, string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var r = doc.RootElement;
            switch (name)
            {
                case "make_prediction":
                {
                    var claim = Str(r, "claim"); var crit = Str(r, "criterion");
                    if (string.IsNullOrWhiteSpace(claim) || string.IsNullOrWhiteSpace(crit))
                        return "error: 'claim' and 'criterion' required.";
                    var due = Str(r, "due");
                    var id = _db.AddPrediction(claim, crit, due);
                    // Pre-schedule the due-time OS toast (like reminders) so it fires even if the
                    // app is closed; the body is privacy-classified first.
                    if (_notify is not null && due is not null &&
                        DateTimeOffset.TryParse(due, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dueAt))
                        await _notify.SchedulePredictionAsync(id, claim!, dueAt);
                    return $"prediction #{id} logged";
                }
                case "list_open_predictions":
                {
                    var open = _db.GetPredictions().Where(p => p.ResolvedUtc is null).ToList();
                    if (open.Count == 0) return "(no open predictions)";
                    var sb = new StringBuilder();
                    foreach (var p in open) sb.AppendLine($"- #{p.Id}: {p.Claim}  [resolves: {p.Criterion}]");
                    return sb.ToString().TrimEnd();
                }
                case "resolve_prediction":
                {
                    if (!r.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id))
                        return "error: 'id' required.";
                    var outcome = Str(r, "outcome");
                    if (string.IsNullOrWhiteSpace(outcome)) return "error: 'outcome' required.";
                    var score = r.TryGetProperty("score", out var s) && s.TryGetDouble(out var sv) ? sv : -1;
                    if (score < 0) return "error: 'score' (0..1) required.";
                    if (!_db.ResolvePrediction(id, outcome!, score))
                        return $"error: prediction #{id} not found or already resolved.";
                    _notify?.CancelPrediction(id);   // resolved before due → no stale toast
                    return $"prediction #{id} resolved (score {Math.Clamp(score, 0, 1):0.00})";
                }
                default: return $"error: unknown tool '{name}'";
            }
        }
        catch (Exception ex) { return $"error: {ex.Message}"; }
    }

    private static string? Str(JsonElement r, string k) =>
        r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
