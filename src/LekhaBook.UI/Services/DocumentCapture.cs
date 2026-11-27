namespace LekhaBook.UI.Services;

/// <summary>What a host can capture from its own hardware. A browser page can't reach a camera or scanner, so it offers none.</summary>
/// <param name="NativeCamera">The host can open the device camera (phone camera, PC webcam).</param>
/// <param name="Scanner">A scanner or a printer that scans is connected and can be used.</param>
public sealed record DocumentCapabilities(bool NativeCamera, bool Scanner)
{
    public static DocumentCapabilities None { get; } = new(false, false);
}

/// <summary>A photo or scanned page, read into memory the same way a picked file is.</summary>
public sealed record CapturedDocument(string FileName, string ContentType, byte[] Content);

/// <summary>
/// Photos and scans taken on the device itself (AGENT_PLAN § 7). Hosts return only what they can really do:
/// the browser keeps its file picker and camera input, and the app adds its own camera and any connected scanner.
/// </summary>
public interface IDocumentCapture
{
    DocumentCapabilities Capabilities { get; }

    /// <summary>A photo from the device camera, or null when the user cancels.</summary>
    Task<CapturedDocument?> TakePhotoAsync(CancellationToken ct = default);

    /// <summary>One page from a connected scanner or a printer that scans, or null when the user cancels.</summary>
    Task<CapturedDocument?> ScanPageAsync(CancellationToken ct = default);
}
