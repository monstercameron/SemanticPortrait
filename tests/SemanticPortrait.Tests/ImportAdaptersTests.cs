using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

/// <summary>
/// Source adapters (design §13a): each known export format normalizes into dated-entry text;
/// unknown content passes through byte-identical so plain journals are never mangled.
/// All fixture data is fictional.
/// </summary>
public class ImportAdaptersTests
{
    [Fact]
    public void Plain_text_passes_through_untouched()
    {
        const string diary = "2025-01-05\n\nWent bouldering with Mia. Felt strong.\n";
        Assert.Equal(diary, ImportAdapters.Normalize("journal.txt", diary));
        Assert.Equal(diary, ImportAdapters.Normalize("journal.md", diary));
    }

    [Fact]
    public void Discord_json_export_becomes_dated_lines()
    {
        const string json = """
        {
          "guild": { "name": "climbing crew" },
          "channel": { "name": "general" },
          "messages": [
            { "timestamp": "2025-03-14T18:02:00+00:00", "author": { "name": "sam" }, "content": "anyone up for the gym tonight" },
            { "timestamp": "2025-03-14T18:05:00+00:00", "author": { "name": "priya", "nickname": "pri" }, "content": "in! 7pm?" },
            { "timestamp": "2025-03-14T18:06:00+00:00", "author": { "name": "sam" }, "content": "" }
          ]
        }
        """;
        var res = ImportAdapters.Normalize("chat.json", json);
        Assert.Contains("#general", res);
        Assert.Contains("sam: anyone up for the gym tonight", res);
        Assert.Contains("pri: in! 7pm?", res);                     // nickname preferred
        Assert.Contains("[2025-03-14 18:02] sam", res);
        Assert.Equal(2, res.Split('\n').Count(l => l.StartsWith('['))); // empty message dropped
    }

    [Fact]
    public void WhatsApp_export_normalizes_and_drops_media_markers()
    {
        const string wa = """
        [3/14/25, 6:02:11 PM] Sam: heading to the gym
        [3/14/25, 6:03:40 PM] Priya: <Media omitted>
        [3/14/25, 6:04:02 PM] Priya: save me a rope
        which one though
        """;
        var res = ImportAdapters.Normalize("_chat.txt", wa);
        Assert.Contains("Sam: heading to the gym", res);
        Assert.Contains("Priya: save me a rope", res);
        Assert.DoesNotContain("Media omitted", res);
        Assert.Contains("    which one though", res);              // continuation line indented
        Assert.Contains("[2025-03-14 18:02]", res);                // date normalized
    }

    [Fact]
    public void Sms_backup_xml_becomes_directional_lines()
    {
        const string xml = """
        <?xml version="1.0"?>
        <smses count="2">
          <sms address="+15550100" contact_name="Mia" date="1741975320000" type="1" body="running late" />
          <sms address="+15550100" contact_name="Mia" date="1741975440000" type="2" body="no rush" />
        </smses>
        """;
        var res = ImportAdapters.Normalize("sms-backup.xml", xml);
        Assert.Contains("Mia: running late", res);
        Assert.Contains("me → Mia: no rush", res);
    }

    [Fact]
    public void Csv_with_recognizable_columns_normalizes()
    {
        const string csv = "date,author,text\n2025-03-14 18:02,sam,\"felt strong, sent the v5\"\n2025-03-15,,rest day\n";
        var res = ImportAdapters.Normalize("log.csv", csv);
        Assert.Contains("[2025-03-14 18:02] sam: felt strong, sent the v5", res);
        Assert.Contains("[2025-03-15 00:00] rest day", res);
    }

    [Fact]
    public void Unrecognizable_csv_passes_through()
    {
        const string csv = "a,b,c\n1,2,3\n";
        Assert.Equal(csv, ImportAdapters.Normalize("data.csv", csv));
    }

    [Fact]
    public void Csv_parser_handles_quotes_and_embedded_newlines()
    {
        var rows = ImportAdapters.ParseCsv("h1,h2\n\"a,b\",\"line1\nline2\"\n\"say \"\"hi\"\"\",x\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal("a,b", rows[1][0]);
        Assert.Equal("line1\nline2", rows[1][1]);
        Assert.Equal("say \"hi\"", rows[2][0]);
    }

    [Fact]
    public void Malformed_export_degrades_to_passthrough_not_a_crash()
    {
        const string bad = "{ \"messages\": [ this is not json";
        Assert.Equal(bad, ImportAdapters.Normalize("chat.json", bad));
        const string badXml = "<sms unclosed";
        Assert.Equal(badXml, ImportAdapters.Normalize("sms.xml", badXml));
    }

    [Fact]
    public void DayOne_export_becomes_dated_blocks_and_skips_empty_entries()
    {
        const string json = """
        {
          "metadata": { "version": "1.0" },
          "entries": [
            { "creationDate": "2025-03-14T18:02:33Z", "modifiedDate": "2025-03-14T18:10:00Z", "text": "Bouldered with Mia.\n\nFelt strong on the v5.", "starred": false },
            { "creationDate": "2025-03-15T09:00:00Z", "modifiedDate": "2025-03-15T09:00:00Z", "text": "   ", "starred": false },
            { "creationDate": "2025-03-16T07:30:00Z", "modifiedDate": "2025-03-16T07:30:00Z", "text": "Rest day.", "starred": true }
          ]
        }
        """;
        var res = ImportAdapters.Normalize("Journal.json", json);
        var stamp1 = DateTimeOffset.Parse("2025-03-14T18:02:33Z").ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var stamp2 = DateTimeOffset.Parse("2025-03-16T07:30:00Z").ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        Assert.Contains($"## [{stamp1}]", res);
        Assert.Contains("Bouldered with Mia.\n\nFelt strong on the v5.", res);   // multi-paragraph preserved
        Assert.Contains($"## [{stamp2}]", res);
        Assert.Contains("Rest day.", res);
        Assert.Equal(2, res.Split("## [").Length - 1);                          // blank-text entry skipped
    }

    [Fact]
    public void Keep_note_with_title_and_list_normalizes()
    {
        const string json = """
        {
          "title": "Gym bag",
          "textContent": "Pack before Friday",
          "userEditedTimestampUsec": 1741975320000000,
          "createdTimestampUsec": 1741975200000000,
          "isTrashed": false,
          "listContent": [
            { "text": "chalk bag", "isChecked": true },
            { "text": "climbing shoes", "isChecked": false }
          ]
        }
        """;
        var res = ImportAdapters.Normalize("Gym bag.json", json);
        var stamp = DateTimeOffset.FromUnixTimeMilliseconds(1741975200000000L / 1000).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        Assert.Contains($"## [{stamp}]", res);
        Assert.Contains("Gym bag", res);
        Assert.Contains("Pack before Friday", res);
        Assert.Contains("[x] chalk bag", res);
        Assert.Contains("[ ] climbing shoes", res);
    }

    [Fact]
    public void Trashed_keep_note_produces_nothing()
    {
        const string json = """
        {
          "title": "old idea",
          "textContent": "scrap this",
          "userEditedTimestampUsec": 1741975320000000,
          "isTrashed": true
        }
        """;
        Assert.Equal("", ImportAdapters.Normalize("old idea.json", json));
    }

    [Fact]
    public void Malformed_DayOne_ish_json_degrades_to_passthrough()
    {
        const string bad = "{ \"entries\": [ { \"creationDate\": \"2025-03-14T18:02:00Z\", this is not valid json";
        Assert.Equal(bad, ImportAdapters.Normalize("journal.json", bad));
    }
}
