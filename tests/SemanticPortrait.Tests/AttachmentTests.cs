using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

/// <summary>Photo attachments round-trip: thumb + full stored encrypted alongside a message,
/// listed by message, served as data URIs, and surviving an encrypted reopen.</summary>
public class AttachmentTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_attach_{Guid.NewGuid():N}.db");
    private Db NewDb() { var d = new Db(_path); d.OpenPlaintext(); return d; }
    public void Dispose() { try { if (File.Exists(_path)) File.Delete(_path); } catch { } }

    private static byte[] Full => new byte[] { 1, 2, 3, 4, 5 };
    private static byte[] Thumb => new byte[] { 9, 8, 7 };

    [Fact]
    public void Attachment_roundtrips_thumb_and_full()
    {
        var db = NewDb();
        var msg = db.AddMessage("user", "beach day", DateTime.UtcNow.ToString("o"));
        var att = db.AddAttachment(msg, "image/jpeg", Full, Thumb, caption: "the shore");

        var thumbs = db.ThumbsFor(msg);
        var t = Assert.Single(thumbs);
        Assert.Equal(att, t.Id);
        Assert.Equal("the shore", t.Caption);
        Assert.StartsWith("data:image/jpeg;base64,", t.ThumbDataUri);
        Assert.Equal(Convert.ToBase64String(Thumb), t.ThumbDataUri.Split(',')[1]);

        // full (lightbox) URI carries the FULL bytes, not the thumb
        var fullUri = db.AttachmentDataUri(att);
        Assert.NotNull(fullUri);
        Assert.Equal(Convert.ToBase64String(Full), fullUri!.Split(',')[1]);
    }

    [Fact]
    public void Message_ids_with_attachments_is_a_single_lookup()
    {
        var db = NewDb();
        var a = db.AddMessage("user", "a", DateTime.UtcNow.ToString("o"));
        var b = db.AddMessage("user", "b", DateTime.UtcNow.ToString("o"));
        db.AddAttachment(a, "image/jpeg", Full, Thumb);

        var set = db.MessageIdsWithAttachments();
        Assert.Contains(a, set);
        Assert.DoesNotContain(b, set);
    }

    [Fact]
    public void Attachments_survive_an_encrypted_reopen()
    {
        var key = new byte[32];
        for (var i = 0; i < 32; i++) key[i] = (byte)(i + 1);
        var path = Path.Combine(Path.GetTempPath(), $"sp_attach_enc_{Guid.NewGuid():N}.db");
        try
        {
            var db = new Db(path); db.Open(key);
            var m = db.AddMessage("user", "x", DateTime.UtcNow.ToString("o"));
            var att = db.AddAttachment(m, "image/jpeg", Full, Thumb);
            db.Close();

            var db2 = new Db(path); db2.Open(key);
            Assert.Equal(Convert.ToBase64String(Full), db2.AttachmentDataUri(att)!.Split(',')[1]);
            db2.Close();
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
