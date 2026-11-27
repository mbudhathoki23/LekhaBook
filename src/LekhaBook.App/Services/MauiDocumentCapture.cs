using LekhaBook.UI.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Platform;
using Microsoft.Maui.Media;

namespace LekhaBook.App.Services;

/// <summary>
/// The camera (MediaPicker: the phone camera, or a PC webcam) and, on Windows, a connected scanner or a printer that
/// scans (WIA). Every photo or scan is shrunk to JPEG first, so a phone photo fits the 5 MB attachment limit.
/// </summary>
public sealed class MauiDocumentCapture : IDocumentCapture
{
    private const float PhotoMaxPixels = 1600;
    private const float ScanMaxPixels = 2200;
    private const float JpegQuality = 0.8f;

    private readonly Lazy<DocumentCapabilities> _capabilities = new(() => new DocumentCapabilities(
        NativeCamera: MediaPicker.Default.IsCaptureSupported,
        Scanner: ScannerPresent()));

    public DocumentCapabilities Capabilities => _capabilities.Value;

    public async Task<CapturedDocument?> TakePhotoAsync(CancellationToken ct = default)
    {
        var permission = await Permissions.RequestAsync<Permissions.Camera>();
        if (permission != PermissionStatus.Granted)
        {
            throw new UnauthorizedAccessException("The camera permission was not granted.");
        }

        var photo = await MediaPicker.Default.CapturePhotoAsync();
        if (photo is null)
        {
            return null;
        }

        await using var source = await photo.OpenReadAsync();
        return Shrink(source, Path.ChangeExtension(photo.FileName, ".jpg"), PhotoMaxPixels);
    }

    public Task<CapturedDocument?> ScanPageAsync(CancellationToken ct = default)
    {
#if WINDOWS
        // WIA's dialog is modal and needs the UI thread, which is where Blazor event handlers run.
        var jpeg = WiaScanner.ScanPage();
        if (jpeg is null)
        {
            return Task.FromResult<CapturedDocument?>(null);
        }

        using var source = new MemoryStream(jpeg);
        return Task.FromResult<CapturedDocument?>(Shrink(source, $"scan-{DateTime.Now:yyyyMMdd-HHmmss}.jpg", ScanMaxPixels));
#else
        return Task.FromException<CapturedDocument?>(new NotSupportedException("Scanning needs the Windows app with a connected scanner."));
#endif
    }

    private static bool ScannerPresent() =>
#if WINDOWS
        WiaScanner.HasScanner();
#else
        false;
#endif

    /// <summary>Reads a photo or scan, shrinks it so its longest side is at most <paramref name="maxPixels"/>, and saves it as JPEG.</summary>
    private static CapturedDocument Shrink(Stream source, string fileName, float maxPixels)
    {
        var image = PlatformImage.FromStream(source, ImageFormat.Jpeg);
        var scaled = Math.Max(image.Width, image.Height) > maxPixels
            ? image.Downsize(maxPixels, disposeOriginal: true)
            : image;

        using var output = new MemoryStream();
        scaled.Save(output, ImageFormat.Jpeg, JpegQuality);
        return new CapturedDocument(fileName, "image/jpeg", output.ToArray());
    }
}
