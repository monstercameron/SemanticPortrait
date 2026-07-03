using System.Text.RegularExpressions;

namespace SemanticPortrait.Core;

/// <summary>
/// Regex/rules masker for structured PII (email, phone, card, SSN, URL, @handle). Stable, reversible
/// tokens come from the encrypted alias map in <see cref="Db"/>. Free-form names/places are left to a
/// pluggable on-device NER recognizer (<see cref="INerRecognizer"/>) that slots in later — until then
/// this is the regex baseline. Pass-through when disabled or the DB is closed.
/// </summary>
public sealed class RegexMasker : IMasker
{
    private readonly Db _db;
    private readonly Func<bool> _enabled;
    private readonly INerRecognizer? _ner;

    public RegexMasker(Db db, Func<bool> enabled, INerRecognizer? ner = null)
    {
        _db = db; _enabled = enabled; _ner = ner;
    }

    public bool Enabled => _enabled() && _db.IsOpen;

    // Order matters: URL/EMAIL before PHONE (they contain digits), EMAIL before HANDLE (contains @).
    private static readonly (string Kind, Regex Rx)[] _patterns =
    {
        ("URL",   new Regex(@"https?://[^\s]+", RegexOptions.Compiled)),
        ("EMAIL", new Regex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled)),
        ("SSN",   new Regex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled)),
        ("CARD",  new Regex(@"\b(?:\d[ -]?){13,16}\b", RegexOptions.Compiled)),
        ("PHONE", new Regex(@"\+?\d[\d\-\s().]{6,}\d", RegexOptions.Compiled)),
        ("HANDLE",new Regex(@"(?<![A-Za-z0-9_])@[A-Za-z0-9_]{2,}", RegexOptions.Compiled)),
    };

    // Journal text is full of dates/timestamps, which the PHONE pattern happily matches
    // ("2026-06-19 10:30" — the match stops at the colon, leaving "2026-06-19 10"). They aren't
    // PII — leave them readable. The time tail is optional at every level for that reason.
    private static readonly Regex _dateLike = new(
        @"^(\d{4}[-/.]\d{1,2}[-/.]\d{1,2}([ T]\d{1,2}(:\d{2}(:\d{2}(\.\d+)?)?)?)?|\d{1,2}[-/.]\d{1,2}[-/.]\d{2,4})$",
        RegexOptions.Compiled);

    public string Mask(string text)
    {
        if (!Enabled || string.IsNullOrEmpty(text)) return text;

        // Collect (kind, value) candidates from regex + optional NER, dedupe by value.
        var found = new List<(string Kind, string Value)>();
        foreach (var (kind, rx) in _patterns)
            foreach (Match m in rx.Matches(text))
                found.Add((kind, m.Value.Trim()));
        if (_ner is not null)
            foreach (var (kind, value) in _ner.Recognize(text))
                found.Add((kind, value));

        // Longest values first so a shorter value that is a substring of a longer one isn't
        // partially replaced.
        var replacements = found
            .Where(f => f.Value.Length >= 3)
            .Where(f => f.Kind != "PHONE" || !_dateLike.IsMatch(f.Value))
            .DistinctBy(f => f.Value)
            .OrderByDescending(f => f.Value.Length)
            .ToList();

        var masked = text;
        foreach (var (kind, value) in replacements)
        {
            var token = _db.GetOrCreateAlias(kind, value);
            masked = ReplaceWhole(masked, value, token);
        }
        return masked;
    }

    public string Unmask(string text)
    {
        if (string.IsNullOrEmpty(text) || !_db.IsOpen) return text;
        var result = text;
        foreach (var (token, original) in _db.GetAliasReversals())   // longest tokens first
            result = ReplaceWhole(result, token, original);
        return result;
    }

    /// <summary>
    /// Replace only whole occurrences: "PERSON_1" must not eat the front of "PERSON_12", and a
    /// short value must not be rewritten inside a longer word. Word-boundary guards are applied
    /// only where the value's own edge is a word character (URLs etc. keep plain edges).
    /// </summary>
    private static string ReplaceWhole(string text, string value, string replacement)
    {
        static bool Wordy(char c) => char.IsLetterOrDigit(c) || c == '_';
        var pattern = Regex.Escape(value);
        if (Wordy(value[0])) pattern = @"(?<![A-Za-z0-9_])" + pattern;
        if (Wordy(value[^1])) pattern += @"(?![A-Za-z0-9_])";
        return Regex.Replace(text, pattern, replacement.Replace("$", "$$"));
    }
}

/// <summary>
/// On-device named-entity recognizer for free-form PII (people, orgs, places). Implemented later by
/// a local ONNX NER model; the masker works without it (regex baseline) until then.
/// </summary>
public interface INerRecognizer
{
    IEnumerable<(string Kind, string Value)> Recognize(string text);
}
