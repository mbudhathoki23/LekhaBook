namespace LekhaBook.Application;

/// <summary>
/// The addresses that hand a message to WhatsApp or the user's email app (decision D24). Nothing is sent by
/// LekhaBook: the user sees the message in that app and sends it.
/// </summary>
public static class ShareLinks
{
    private const string NepalCode = "977";

    /// <summary>
    /// A phone as WhatsApp wants it (country code and digits only), or null when it can't be told for certain.
    /// A bare 10-digit mobile starting with 9 is a Nepali number.
    /// </summary>
    public static string? WhatsAppNumber(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (phone.TrimStart().StartsWith('+'))
        {
            return digits.Length is >= 8 and <= 15 ? digits : null;
        }

        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            return digits.Length is >= 10 and <= 17 ? digits[2..] : null;
        }

        if (digits.Length == 10 && digits[0] == '9')
        {
            return NepalCode + digits;
        }

        return digits.Length == 13 && digits.StartsWith(NepalCode + "9", StringComparison.Ordinal) ? digits : null;
    }

    /// <summary>
    /// Opens WhatsApp with <paramref name="message"/> ready. With a usable phone it opens that chat; without one
    /// WhatsApp asks whom to forward it to.
    /// </summary>
    public static string WhatsApp(string message, string? phone = null) =>
        $"https://wa.me/{WhatsAppNumber(phone)}?text={Uri.EscapeDataString(message)}";

    /// <summary>Opens the email app with a draft. The address is left empty when there isn't a plain one.</summary>
    public static string Email(string subject, string body, string? address = null)
    {
        var to = address?.Trim() is { Length: > 0 } a && a.Contains('@') && a.IndexOfAny([' ', '?', '&', '#', ',', ';']) < 0
            ? Uri.EscapeDataString(a).Replace("%40", "@")
            : string.Empty;
        var crlf = body.ReplaceLineEndings("\r\n");
        return $"mailto:{to}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(crlf)}";
    }
}
