using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class PdfExportTests
{
    private readonly PdfExport _pdf = new();

    private static StoredMessage Msg(long id, string role, string text, DateTime utc, string? detail = null) =>
        new(id, role, text, utc.ToString("o"), detail);

    [Fact]
    public void Full_journal_is_a_valid_nonempty_pdf()
    {
        var messages = new List<StoredMessage>
        {
            Msg(1, "user", "First entry of the day.", new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc)),
            Msg(2, "assistant", "Some analyst reply that should not appear.", new DateTime(2026, 6, 1, 9, 1, 0, DateTimeKind.Utc)),
            Msg(3, "tool", "internal tool call noise", new DateTime(2026, 6, 1, 9, 2, 0, DateTimeKind.Utc)),
            Msg(4, "user", "A second entry, a different day.", new DateTime(2026, 6, 3, 20, 0, 0, DateTimeKind.Utc)),
        };

        var bytes = _pdf.BuildJournalPdf(messages, "Cam");

        Assert.True(bytes.Length > 0);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public void Bytes_change_when_a_user_entry_is_added()
    {
        var baseline = new List<StoredMessage>
        {
            Msg(1, "user", "Only entry.", new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc)),
        };
        var withMore = new List<StoredMessage>(baseline)
        {
            Msg(2, "user", "An additional entry that adds real content to the book.", new DateTime(2026, 6, 2, 9, 0, 0, DateTimeKind.Utc)),
        };

        var bytesBaseline = _pdf.BuildJournalPdf(baseline, "Cam");
        var bytesWithMore = _pdf.BuildJournalPdf(withMore, "Cam");

        Assert.NotEqual(bytesBaseline.Length, bytesWithMore.Length);
    }

    [Fact]
    public void Assistant_and_tool_messages_are_excluded()
    {
        var withUser = new List<StoredMessage>
        {
            Msg(1, "user", "Visible entry.", new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc)),
            Msg(2, "assistant", "Hidden analyst reply.", new DateTime(2026, 6, 1, 9, 1, 0, DateTimeKind.Utc)),
        };
        var withoutAssistant = new List<StoredMessage>
        {
            Msg(1, "user", "Visible entry.", new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc)),
        };

        var a = _pdf.BuildJournalPdf(withUser, "Cam");
        var b = _pdf.BuildJournalPdf(withoutAssistant, "Cam");

        // Same visible user content in both -> same-sized output regardless of the assistant/tool
        // noise present (byte-length rather than exact-byte equality, since PDF generation may
        // stamp a fixed-width timestamp that can vary run-to-run without changing size).
        Assert.Equal(a.Length, b.Length);
    }

    [Fact]
    public void All_assistant_set_yields_a_valid_empty_state_pdf_without_throwing()
    {
        var messages = new List<StoredMessage>
        {
            Msg(1, "assistant", "Just the AI talking to itself.", new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc)),
            Msg(2, "tool", "tool output", new DateTime(2026, 6, 1, 9, 1, 0, DateTimeKind.Utc)),
            Msg(3, "sys", "system note", new DateTime(2026, 6, 1, 9, 2, 0, DateTimeKind.Utc)),
        };

        var bytes = _pdf.BuildJournalPdf(messages, "Cam");

        Assert.True(bytes.Length > 0);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public void Empty_message_list_does_not_throw_and_yields_valid_pdf()
    {
        var bytes = _pdf.BuildJournalPdf(new List<StoredMessage>(), null);

        Assert.True(bytes.Length > 0);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public void Date_range_filters_entries()
    {
        var messages = new List<StoredMessage>
        {
            Msg(1, "user", "Day one entry.", new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc)),
            Msg(2, "user", "Day two entry.", new DateTime(2026, 6, 2, 9, 0, 0, DateTimeKind.Utc)),
            Msg(3, "user", "Day three entry, well outside range.", new DateTime(2026, 6, 10, 9, 0, 0, DateTimeKind.Utc)),
        };

        var full = _pdf.BuildJournalPdf(messages, "Cam");
        var oneDay = _pdf.BuildJournalPdf(messages, "Cam",
            fromLocal: new DateTime(2026, 6, 1), toLocal: new DateTime(2026, 6, 1));

        Assert.NotEqual(full.Length, oneDay.Length);
        Assert.True(oneDay.Length > 0);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(oneDay, 0, 4));
    }

    [Fact]
    public void Leading_photo_tag_is_stripped_and_photo_only_entry_is_dropped()
    {
        var withPhotoCaption = new List<StoredMessage>
        {
            Msg(1, "user", "Look at this!\n[shared 2 photos]", new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc)),
        };
        var withoutTag = new List<StoredMessage>
        {
            Msg(1, "user", "Look at this!", new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc)),
        };
        var photoOnly = new List<StoredMessage>
        {
            Msg(1, "user", "[shared 1 photo]", new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc)),
        };

        var a = _pdf.BuildJournalPdf(withPhotoCaption, "Cam");
        var b = _pdf.BuildJournalPdf(withoutTag, "Cam");
        Assert.Equal(b.Length, a.Length);

        // A photo-only entry (nothing left after stripping the tag) contributes no content —
        // same output as an entirely empty message list.
        var emptyResult = _pdf.BuildJournalPdf(new List<StoredMessage>(), "Cam");
        var photoOnlyResult = _pdf.BuildJournalPdf(photoOnly, "Cam");
        Assert.Equal(emptyResult.Length, photoOnlyResult.Length);
    }
}
