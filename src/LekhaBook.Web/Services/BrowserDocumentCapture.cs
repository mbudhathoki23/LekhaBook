using LekhaBook.UI.Services;

namespace LekhaBook.Web.Services;

/// <summary>
/// A browser page can't open a camera-capture or scanner dialog by itself, so the transaction form keeps the browser's
/// file picker and camera input (which opens the phone camera on mobile). Scanning needs the Windows app.
/// </summary>
public sealed class BrowserDocumentCapture : IDocumentCapture
{
    public DocumentCapabilities Capabilities => DocumentCapabilities.None;

    public Task<CapturedDocument?> TakePhotoAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("The browser uses its own camera input.");

    public Task<CapturedDocument?> ScanPageAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("Scanning needs the Windows app with a connected scanner.");
}
