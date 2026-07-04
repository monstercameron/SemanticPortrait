using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

namespace SemanticPortrait.App.Services;

/// <summary>
/// Prepares a picked image for the encrypted journal: decode, downscale to a sane max edge, and
/// re-encode as JPEG. A journal doesn't need camera originals, and one downscaled copy keeps the
/// SQLCipher DB from ballooning while staying crisp on screen. Windows-only (System.Drawing).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ImageIntake
{
    private const int FullEdge = 1600;     // display copy (lightbox / export)
    private const int ThumbEdge = 420;     // inline thread copy (tiny base64, fast paint)

    /// <summary>Two JPEG copies from one picked image: a small inline thumb and a display-sized
    /// full. Returns null if the stream isn't a usable image.</summary>
    public static (string Mime, byte[] Full, byte[] Thumb)? Prepare(Stream input)
    {
        try
        {
            using var src = System.Drawing.Image.FromStream(input);
            return ("image/jpeg", Encode(src, FullEdge, 82L), Encode(src, ThumbEdge, 74L));
        }
        catch { return null; }   // not an image / decode failure → caller skips it
    }

    private static byte[] Encode(System.Drawing.Image src, int maxEdge, long quality)
    {
        var scale = Math.Min(1.0, (double)maxEdge / Math.Max(src.Width, src.Height));
        int w = Math.Max(1, (int)Math.Round(src.Width * scale));
        int h = Math.Max(1, (int)Math.Round(src.Height * scale));
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(src, 0, 0, w, h);
        }
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var enc = new EncoderParameters(1);
        enc.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        using var ms = new MemoryStream();
        bmp.Save(ms, jpeg, enc);
        return ms.ToArray();
    }
}
