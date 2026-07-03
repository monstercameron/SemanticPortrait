using System.IO;

namespace SemanticPortrait.Core;

/// <summary>
/// Dev-time .env loader. Walks up from the app base directory to find a `.env`
/// file (the repo root in dev) and parses simple KEY=VALUE lines.
/// Secrets stay local; .env is git-ignored.
/// </summary>
public static class EnvLoader
{
    private static readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public static string? Get(string key)
    {
        if (!_loaded) Load();
        return _values.TryGetValue(key, out var v) ? v : null;
    }

    private static void Load()
    {
        _loaded = true;
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                // Only trust a .env that sits at a repo root (dev checkout). Without this, an
                // installed build would happily read a stranger's .env found above its install dir.
                if (!Directory.Exists(Path.Combine(dir.FullName, ".git"))
                    && dir.EnumerateFiles("*.sln").FirstOrDefault() is null) continue;
                var path = Path.Combine(dir.FullName, ".env");
                if (!File.Exists(path)) continue;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = line[..eq].Trim();
                    var val = line[(eq + 1)..].Trim().Trim('"', '\'');
                    _values[k] = val;
                }
                return;
            }
        }
        catch { /* dev convenience only — ignore */ }
    }
}
