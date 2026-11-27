using QRCoder;

namespace LekhaBook.UI.Formatting;

public static class AuthenticatorSetup
{
    public static string UriFor(string email, string sharedKey) =>
        $"otpauth://totp/{Uri.EscapeDataString("Lekha Books")}:{Uri.EscapeDataString(email)}"
        + $"?secret={Uri.EscapeDataString(sharedKey)}&issuer={Uri.EscapeDataString("Lekha Books")}&algorithm=SHA1&digits=6&period=30";

    /// <summary>Generated locally; no account secret or QR payload is sent to an external QR service.</summary>
    public static string QrImage(string email, string sharedKey)
    {
        using var data = QRCodeGenerator.GenerateQrCode(UriFor(email, sharedKey), QRCodeGenerator.ECCLevel.Q);
        using var renderer = new PngByteQRCode(data);
        return "data:image/png;base64," + Convert.ToBase64String(renderer.GetGraphic(4));
    }

    public static string NormalizeCode(string value) => value.Replace(" ", string.Empty, StringComparison.Ordinal)
        .Replace("-", string.Empty, StringComparison.Ordinal);
}
