using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SemanticPortrait.App.Services;
using SemanticPortrait.Core;

namespace SemanticPortrait.App.Components.Pages;

// Bulk import: file picking, chunking, and driving the analyst over historical material.
public partial class Home
{
    private bool _showImport;
    private bool _importBusy;
    private List<Microsoft.Maui.Storage.FileResult> _importFiles = new();
    private int _impTotal, _impDone;
    private int _impPct => _impTotal > 0 ? Math.Min(100, (int)(_impDone * 100.0 / _impTotal)) : (_importBusy ? 3 : 0);
    private string? _impActivity, _impNote;
    private CancellationTokenSource? _impCancel;

    private static readonly HashSet<string> _writeTools = new()
        { "save_note", "refine_note", "upsert_node", "link_nodes", "log_event", "set_profile_field", "make_prediction", "import_entry" };

    private async Task PickImportFiles()
    {
        if (_locked || _configuring || !Database.IsOpen) return;   // defense-in-depth: unlocked only
        try
        {
            var types = new Microsoft.Maui.Storage.FilePickerFileType(
                new Dictionary<Microsoft.Maui.Devices.DevicePlatform, IEnumerable<string>>
                {
                    [Microsoft.Maui.Devices.DevicePlatform.WinUI] = new[]
                        { ".txt", ".md", ".markdown", ".text",
                          ".json", ".csv", ".xml" },   // Discord export / generic CSV / SMS backup
                });
            var picked = await Microsoft.Maui.Storage.FilePicker.Default.PickMultipleAsync(
                new Microsoft.Maui.Storage.PickOptions { PickerTitle = "Import notes / analysis", FileTypes = types });
            var list = picked?.Where(x => x is not null).Select(x => x!).ToList() ?? new();
            if (list.Count == 0) return;
            _importFiles = list;
            _showImport = true;
            StateHasChanged();
        }
        catch (Exception ex)
        {
            _messages.Add(new() { Role = "sys", Text = $"import: couldn't open files — {ex.Message}" });
            StateHasChanged();
        }
    }

    private void CancelImport() => _impCancel?.Cancel();

    private async Task RunImport()
    {
        if (_locked || _configuring || !Database.IsOpen) return;   // defense-in-depth: unlocked only
        if (_importBusy || _importFiles.Count == 0) return;
        _importBusy = true; _impTotal = 0; _impDone = 0; _impActivity = null; _impNote = null;
        _impCancel = new CancellationTokenSource();
        var ct = _impCancel.Token;
        StateHasChanged();

        void OnTool(string name, string detail)
        {
            if (_writeTools.Contains(name)) _impDone++;
            _ = InvokeAsync(() => { _impActivity = FriendlyToolNote(name) + ShortArg(detail); StateHasChanged(); }).Guard("import-tool-note");
        }

        try
        {
            // Phase 0 — read + count facts (the progress denominator). Chunks already in the
            // import ledger (a previous run finished them) are skipped in BOTH phases, so a died
            // import resumes where it stopped and re-importing a file never double-adds.
            var files = new List<(string Name, List<(string Chunk, string Hash)> Pending, string About)>();
            int skipped = 0;
            int i = 0;
            foreach (var f in _importFiles.ToList())
            {
                ct.ThrowIfCancellationRequested();
                i++;
                string text;
                try { using var s = await f.OpenReadAsync(); using var rdr = new StreamReader(s); text = await rdr.ReadToEndAsync(); }
                catch (Exception ex) { await Note($"📥 {f.FileName}: read failed — {ex.Message}"); continue; }
                // Source adapters: Discord/WhatsApp/SMS/CSV exports normalize into dated-entry
                // text; anything unrecognized passes through untouched.
                text = ImportAdapters.Normalize(f.FileName, text);
                Status($"scanning {f.FileName} ({i}/{_importFiles.Count})…");
                var about = "";
                var pending = new List<(string, string)>();
                foreach (var chunk in Chunk(text, 6000))
                {
                    var hash = ChunkHash(chunk);
                    if (Database.IsChunkImported(hash)) { skipped++; continue; }
                    pending.Add((chunk, hash));
                    var (c, ab) = await Analyst.CountFactsAsync(chunk, ct);
                    _impTotal += c; if (string.IsNullOrEmpty(about)) about = ab;
                    await InvokeAsync(StateHasChanged);
                }
                files.Add((f.FileName, pending, about));
            }
            if (skipped > 0) await Note($"📥 resuming — {skipped} chunk(s) already imported, skipping them.");

            // Phase 1 — deep import with live progress; each chunk is recorded only after the
            // analyst finished it, so failures retry on the next run.
            int n = 0;
            foreach (var (name, pending, about) in files)
            {
                ct.ThrowIfCancellationRequested();
                n++;
                if (pending.Count == 0) { await Note($"📥 {name}: already fully imported."); continue; }
                _impNote = $"Reading {n}/{files.Count}: {name}{(string.IsNullOrWhiteSpace(about) ? "" : " — " + about)}";
                await InvokeAsync(StateHasChanged);
                foreach (var (chunk, hash) in pending)
                {
                    ct.ThrowIfCancellationRequested();
                    ResetIdle();   // an active import IS activity — don't idle-lock mid-run
                    try
                    {
                        await Analyst.ImportAsync(chunk, OnTool, ct);
                        Database.MarkChunkImported(hash, name);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { await Note($"📥 {name}: analysis error — {ex.Message}"); }
                }
                await Note($"📥 imported {name}{(string.IsNullOrWhiteSpace(about) ? "" : " — " + about)}");
            }
            await Note($"📥 import complete — {_impDone} facts across {files.Count} file(s).");
            await MakeItAContinuationAsync(ct);
        }
        catch (OperationCanceledException)
        {
            await Note($"📥 import stopped (kept {_impDone} so far).");
        }
        finally
        {
            await InvokeAsync(() => { LoadGraph(); _showImport = false; _importBusy = false; _importFiles = new(); StateHasChanged(); });
        }
    }

    /// <summary>
    /// The point of importing a life is that the app then CONTINUES it. Three moves:
    /// (1) the intake is superseded — the model knows them better than 20 questions would;
    /// (2) the imported backlog folds into the rolling summary in batches, so the very next
    ///     message carries their whole story in the system prompt instead of paying a giant
    ///     compaction on first send;
    /// (3) the agent opens the thread itself — grounded in the portrait, picking up the most
    ///     recent threads like a companion who has been here all along.
    /// </summary>
    private async Task MakeItAContinuationAsync(CancellationToken ct)
    {
        Intake.CompleteViaImport();

        Status("weaving your history into the running summary…");
        var folded = 0;
        try
        {
            while (!ct.IsCancellationRequested && Database.IsOpen)
            {
                ResetIdle();
                var n = await Compaction.EnsureCompactedAsync(DateTime.UtcNow, maxBatch: 60, ct);
                if (n == 0) break;
                folded += n;
                _impActivity = $"woven {folded} entries into your story so far…";
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (Exception e) { DevTrap.Report("import-compaction", e); }   // summary catches up on later sends

        await InvokeAsync(() => { _showImport = false; _importBusy = false; StateHasChanged(); });
        await FireProactive(
            "[The user just finished importing their personal history (journal/biography) — it now " +
            "lives in the thread as dated entries, and the portrait is built from it. Open the " +
            "conversation as a CONTINUATION: use recall/portrait to ground yourself, then greet them " +
            "the way someone who has read it all and been here all along would — briefly acknowledge " +
            "2-3 specific load-bearing threads of their story (their words, not generic labels), and " +
            "pick life up from the most recent thread with ONE grounded question. Match their register. " +
            "Short — this is a hello from someone who knows them, not a book report.]");
    }

    private void Status(string activity) => _ = InvokeAsync(() => { _impActivity = activity; StateHasChanged(); }).Guard("import-status");

    private static string ShortArg(string detail)
    {
        var i = detail.IndexOf("params:", StringComparison.Ordinal);
        if (i < 0) return "";
        var s = detail[(i + 7)..].Trim();
        var nl = s.IndexOf('\n'); if (nl > 0) s = s[..nl];
        s = s.Length > 56 ? s[..56] + "…" : s;
        return string.IsNullOrWhiteSpace(s) ? "" : "  " + s;
    }

    private static string ChunkHash(string chunk) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(chunk)));

    private static IEnumerable<string> Chunk(string text, int size)
    {
        text = text?.Trim() ?? "";
        if (text.Length <= size) { if (text.Length > 0) yield return text; yield break; }
        var paras = text.Split("\n\n");
        var sb = new System.Text.StringBuilder();
        foreach (var p in paras)
        {
            if (sb.Length + p.Length > size && sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            sb.Append(p).Append("\n\n");
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
