using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>
/// Tool definitions + execution for reading/writing the user profile,
/// exposed to the model via the OpenAI Responses API (flat function-tool format).
/// </summary>
public sealed class ProfileTools
{
    private readonly ProfileStore _store;
    public ProfileTools(ProfileStore store) => _store = store;

    private static readonly object SetSpec = new
    {
        type = "function",
        name = "set_profile_field",
        description = "Persist a durable fact about the user — e.g. their name (key=\"name\") "
                    + "or why they're using the app (key=\"purpose\"). Use when they reveal such facts.",
        parameters = new
        {
            type = "object",
            properties = new
            {
                key = new { type = "string", description = "Short field name, e.g. name, purpose, location." },
                value = new { type = "string", description = "The value to store." },
            },
            required = new[] { "key", "value" },
            additionalProperties = false,
        },
    };

    private static readonly object GetSpec = new
    {
        type = "function",
        name = "get_profile",
        description = "Retrieve everything currently known about the user (identity, purpose, etc.).",
        parameters = new { type = "object", properties = new { }, additionalProperties = false },
    };

    /// <summary>Full specs (read + write) — for the clean analyst subagent.</summary>
    public IReadOnlyList<object> Specs => new[] { SetSpec, GetSpec };

    /// <summary>Read-only specs — for the main chat agent (no writes; writes go via the subagent).</summary>
    public IReadOnlyList<object> ReadSpecs => new[] { GetSpec };

    /// <summary>Executes a tool call; returns a short result string for the model.</summary>
    public Task<string> ExecuteAsync(string name, string argumentsJson)
    {
        try
        {
            switch (name)
            {
                case "set_profile_field":
                {
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                    var root = doc.RootElement;
                    var key = root.TryGetProperty("key", out var k) ? k.GetString() : null;
                    var value = root.TryGetProperty("value", out var v) ? v.GetString() : null;
                    if (string.IsNullOrWhiteSpace(key) || value is null)
                        return Task.FromResult("error: 'key' and 'value' are required.");
                    _store.Set(key, value);
                    return Task.FromResult($"stored {key} = {value}");
                }
                case "get_profile":
                {
                    var all = _store.All();
                    if (all.Count == 0) return Task.FromResult("(nothing stored about the user yet)");
                    return Task.FromResult(JsonSerializer.Serialize(all));
                }
                default:
                    return Task.FromResult($"error: unknown tool '{name}'");
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult($"error: {ex.Message}");
        }
    }
}
