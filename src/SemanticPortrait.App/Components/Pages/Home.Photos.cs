using SemanticPortrait.App.Services;
using SemanticPortrait.Core;

namespace SemanticPortrait.App.Components.Pages;

// Photo attachments: pick images into a pending strip, attach them to the entry on send (stored
// downscaled + encrypted in the vault), and a click-to-enlarge lightbox in the thread.
public partial class Home
{
    // Prepared-but-not-yet-sent images (a caption the user can add rides into the entry text so
    // the clean-room analyst has words about the picture — no vision model, no image egress).
    private sealed class PendingPhoto
    {
        public required string Mime { get; init; }
        public required byte[] Full { get; init; }
        public required byte[] Thumb { get; init; }
        public required string Preview { get; init; }
        public string Caption { get; set; } = "";
    }
    private readonly List<PendingPhoto> _pendingPhotos = new();
    private string? _lightbox;   // full-size data URI shown in the overlay, or null

    private async Task PickPhotos()
    {
        if (_locked || _configuring || !Database.IsOpen) return;
        try
        {
            var picked = await Microsoft.Maui.Storage.FilePicker.Default.PickMultipleAsync(
                new Microsoft.Maui.Storage.PickOptions
                {
                    PickerTitle = "Attach photos",
                    FileTypes = Microsoft.Maui.Storage.FilePickerFileType.Images,
                });
            foreach (var f in picked ?? Enumerable.Empty<Microsoft.Maui.Storage.FileResult>())
            {
                if (f is null) continue;
                using var s = await f.OpenReadAsync();
                using var buf = new MemoryStream();
                await s.CopyToAsync(buf);
                buf.Position = 0;
                if (ImageIntake.Prepare(buf) is { } img)
                    _pendingPhotos.Add(new PendingPhoto
                    {
                        Mime = img.Mime, Full = img.Full, Thumb = img.Thumb,
                        Preview = $"data:{img.Mime};base64,{Convert.ToBase64String(img.Thumb)}",
                    });
            }
            _focusNext = true;
            StateHasChanged();
        }
        catch (Exception ex)
        {
            _messages.Add(new() { Role = "sys", Text = $"📷 couldn't attach — {ex.Message}" });
            StateHasChanged();
        }
    }

    private void RemovePendingPhoto(int index)
    {
        if (index >= 0 && index < _pendingPhotos.Count) _pendingPhotos.RemoveAt(index);
    }

    /// <summary>Write the pending photos onto a just-persisted message (encrypted) and hydrate
    /// its thumbs for immediate render.</summary>
    private void AttachPendingTo(long messageId, Msg bubble)
    {
        if (_pendingPhotos.Count == 0) return;
        foreach (var p in _pendingPhotos)
            try { Database.AddAttachment(messageId, p.Mime, p.Full, p.Thumb, string.IsNullOrWhiteSpace(p.Caption) ? null : p.Caption.Trim()); }
            catch (Exception e) { DevTrap.Report("attach", e); }
        _pendingPhotos.Clear();
        try { bubble.Photos = Database.ThumbsFor(messageId); } catch { }
    }

    /// <summary>Captions the user typed on pending photos, as a text line for the entry so the
    /// analyst (which can't see pixels) still has words about the images. "" when none.</summary>
    private string PendingCaptionText()
    {
        var caps = _pendingPhotos.Where(p => !string.IsNullOrWhiteSpace(p.Caption)).Select(p => p.Caption.Trim()).ToList();
        return caps.Count == 0 ? "" : "[photo: " + string.Join("; ", caps) + "]";
    }

    /// <summary>Open the full-size image (loaded from the encrypted DB on demand).</summary>
    private void OpenLightbox(long attachmentId)
    {
        try { _lightbox = Database.AttachmentDataUri(attachmentId); StateHasChanged(); }
        catch (Exception e) { DevTrap.Report("lightbox", e); }
    }
    private void CloseLightbox() { _lightbox = null; }
}
