namespace SemanticPortrait.Core.Constellation;

/// <summary>
/// Runtime mirror of schema/ontology.json — edge-type valence and per-category stroke color, the two
/// bits the constellation needs at render time. Kept in code (not parsed from the JSON) so it's
/// compile-checked and testable; if the JSON grows, sync here. Unknown keys degrade gracefully.
/// </summary>
public static class Ontology
{
    /// <summary>+1 positive, −1 negative, 0 neutral/unknown.</summary>
    public static int EdgeValence(string? type) => type switch
    {
        "lifts" or "dissolves" or "disproves" or "the-cure" or "the-real-spec"
            or "fire-returning" or "its-opposite" or "lifting" => +1,
        "steals-the-fuel" or "blocks-being-seen" or "weaponizes" or "intercepts" or "manufactures"
            or "inflates" or "walls-out" or "drains" or "burnout-engine" or "gated-by"
            or "poisoned-by" or "prototype-of" or "armor-from" or "amplifies" => -1,
        _ => 0,   // evidence-for / causes / expresses-as / unknown
    };

    /// <summary>Domain stroke color (hex) per node category. Falls back to a neutral grey.
    /// Keys cover BOTH vocabularies: the analyst prompt's live categories ("distortion", "mind")
    /// and the original ontology.json spellings ("distortion-machines", "mental-health") — the
    /// drift between them was rendering every distortion node fallback-grey.</summary>
    public static string CategoryStroke(string? category) => category switch
    {
        "core" => "#ff7a00",
        "keystone" => "#ffcc00",
        "distortion" or "distortion-machines" => "#ff2e6e",
        "fire" => "#ff7a00",
        "self" => "#19d3ff",
        "heart" => "#ff36b0",
        "wound" => "#7c6bff",
        "mind" or "mental-health" => "#00ffd0",
        "body" => "#28e070",
        "joy" => "#ffd23f",
        "connection" => "#2e9bff",
        "work" => "#b14bff",
        "money" => "#00e88f",
        "inferred" => "#cbb6ff",
        _ => "#9a9aa3",
    };
}
