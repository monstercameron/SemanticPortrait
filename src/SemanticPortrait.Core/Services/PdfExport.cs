using System.Text.RegularExpressions;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace SemanticPortrait.Core;

/// <summary>
/// Renders the journal (user's own entries only — no assistant/tool/sys chatter) as a printable
/// "book": a title page, then one section per local day with a date header and each entry as a
/// paragraph. This is a keepsake artifact, distinct from <see cref="ExportService"/>'s
/// machine-readable exports.
///
/// Built on PDFsharp/MigraDoc (pure managed, MIT-licensed) rather than QuestPDF: QuestPDF ships a
/// native Skia binary that has no win-arm64 build (confirmed against this Snapdragon X2 box — its
/// static ctor throws "Your runtime is currently not supported by QuestPDF" for win-arm64), which
/// is this app's only target platform. PDFsharp's Core build has no native dependency at all, so
/// it runs the same on x64 and arm64. Font glyphs are resolved via
/// <see cref="GlobalFontSettings.UseWindowsFontsUnderWindows"/>, PDFsharp's built-in resolver that
/// reads standard Windows font files (e.g. Times New Roman) directly — no bundled font asset needed.
///
/// Photos aren't embedded in v1 (see <see cref="StripPhotoTag"/>) — TODO: embed attachment
/// thumbnails/images inline once a photo layout is designed.
/// </summary>
public sealed class PdfExport
{
    static PdfExport()
    {
        // Core (non-GDI) PDFsharp build ships with no font source of its own; this flag turns on
        // its built-in resolver for standard Windows fonts (Arial, Times New Roman, ...) — the
        // whole app is Windows-only, so this is always available.
        GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    private const string SerifFont = "Times New Roman";

    // Matches the "[shared N photo(s)]" tag Home.Chat.cs appends to a persisted entry — on its
    // own line (or as the whole text, for a photo-only entry). Stripped so the book reads clean;
    // the photos themselves aren't in the PDF yet.
    private static readonly Regex PhotoTagLine = new(@"^\[shared \d+ photos?\]$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static string StripPhotoTag(string text) => PhotoTagLine.Replace(text, "").Trim();

    private static bool InRange(string createdUtc, DateTime? fromLocal, DateTime? toLocal, out DateTime local)
    {
        local = default;
        if (!DateTime.TryParse(createdUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var utc)) return false;
        local = utc.ToLocalTime();
        if (fromLocal is not null && local.Date < fromLocal.Value.Date) return false;
        if (toLocal is not null && local.Date > toLocal.Value.Date) return false;
        return true;
    }

    /// <summary>
    /// Builds a PDF "book" of the user's own journal entries (role=="user"; assistant/tool/sys
    /// excluded — this is the person's book, not the AI's side of the conversation). Entries are
    /// filtered to the LOCAL date range [fromLocal, toLocal] (inclusive, by day) when given, and
    /// grouped by local day in chronological order. Returns raw PDF bytes; never throws on an
    /// empty result set (renders a "No entries in this range." title page instead).
    /// </summary>
    public byte[] BuildJournalPdf(IReadOnlyList<StoredMessage> messages, string? ownerName,
        DateTime? fromLocal = null, DateTime? toLocal = null)
    {
        var byDay = new List<(DateTime Day, string Time, string Text)>();
        foreach (var m in messages)
        {
            if (m.Role != "user") continue;
            if (!InRange(m.CreatedUtc, fromLocal, toLocal, out var local)) continue;
            var text = StripPhotoTag(m.Text);
            if (text.Length == 0) continue;
            byDay.Add((local.Date, local.ToString("h:mm tt"), text));
        }
        byDay.Sort((a, b) => a.Day.CompareTo(b.Day));

        var title = string.IsNullOrWhiteSpace(ownerName) ? "Journal" : $"{ownerName}'s Journal";
        var rangeLabel = (fromLocal, toLocal) switch
        {
            (null, null) => "All entries",
            (not null, null) => $"From {fromLocal:MMMM d, yyyy}",
            (null, not null) => $"Through {toLocal:MMMM d, yyyy}",
            _ => $"{fromLocal:MMMM d, yyyy} - {toLocal:MMMM d, yyyy}",
        };
        var generatedLabel = $"Generated {DateTime.Now:dddd, MMMM d, yyyy h:mm tt}";

        var document = new Document();
        document.Info.Title = title;

        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromCentimeter(2.2);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2.2);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(2.2);
        section.PageSetup.RightMargin = Unit.FromCentimeter(2.2);

        // Footer: page X / N, centered.
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.Format.Font.Name = SerifFont;
        footer.Format.Font.Size = 9;
        footer.AddPageField();
        footer.AddText(" / ");
        footer.AddNumPagesField();

        // Title block.
        var titlePara = section.AddParagraph(title);
        titlePara.Format.Font.Name = SerifFont;
        titlePara.Format.Font.Size = 28;
        titlePara.Format.Font.Bold = true;
        titlePara.Format.SpaceAfter = Unit.FromPoint(4);

        var rangePara = section.AddParagraph(rangeLabel);
        rangePara.Format.Font.Name = SerifFont;
        rangePara.Format.Font.Size = 12;
        rangePara.Format.Font.Color = Colors.DimGray;
        rangePara.Format.SpaceAfter = Unit.FromPoint(2);

        var genPara = section.AddParagraph(generatedLabel);
        genPara.Format.Font.Name = SerifFont;
        genPara.Format.Font.Size = 9;
        genPara.Format.Font.Color = Colors.Gray;
        genPara.Format.SpaceAfter = Unit.FromPoint(18);

        if (byDay.Count == 0)
        {
            var empty = section.AddParagraph("No entries in this range.");
            empty.Format.Font.Name = SerifFont;
            empty.Format.Font.Size = 14;
            empty.Format.Font.Italic = true;
            empty.Format.SpaceBefore = Unit.FromPoint(24);
        }
        else
        {
            string? currentDay = null;
            foreach (var (day, time, text) in byDay)
            {
                var dayLabel = day.ToString("dddd, MMMM d, yyyy");
                if (dayLabel != currentDay)
                {
                    currentDay = dayLabel;
                    var dayHeader = section.AddParagraph(dayLabel);
                    dayHeader.Format.Font.Name = SerifFont;
                    dayHeader.Format.Font.Size = 16;
                    dayHeader.Format.Font.Bold = true;
                    dayHeader.Format.SpaceBefore = Unit.FromPoint(14);
                    dayHeader.Format.SpaceAfter = Unit.FromPoint(4);
                }

                var timePara = section.AddParagraph(time);
                timePara.Format.Font.Name = SerifFont;
                timePara.Format.Font.Size = 8;
                timePara.Format.Font.Color = Colors.Gray;
                timePara.Format.SpaceBefore = Unit.FromPoint(6);
                timePara.Format.SpaceAfter = Unit.FromPoint(1);

                var entryPara = section.AddParagraph(text);
                entryPara.Format.Font.Name = SerifFont;
                entryPara.Format.Font.Size = 11;
                entryPara.Format.LineSpacing = Unit.FromPoint(15);
                entryPara.Format.LineSpacingRule = LineSpacingRule.Exactly;
            }
        }

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();

        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms);
        return ms.ToArray();
    }
}
