using System.Globalization;

namespace LekhaBook.UI.Formatting;

/// <summary>
/// Amounts as Nepali users read them (D2): "Rs." and lakh grouping, e.g. Rs. 12,34,567.50.
/// Uses its own number format, so it doesn't depend on the device's culture data.
/// </summary>
public static class Money
{
    private static readonly NumberFormatInfo Lakh = new()
    {
        NumberGroupSizes = [3, 2],
        NumberGroupSeparator = ",",
        NumberDecimalSeparator = ".",
        NumberDecimalDigits = 2,
        NegativeSign = "-",
    };

    /// <summary>"Rs. 1,23,456.00"; negative amounts as "-Rs. 1,23,456.00".</summary>
    public static string Format(decimal amount) =>
        (amount < 0 ? "-Rs. " : "Rs. ") + Number(Math.Abs(amount));

    /// <summary>"1,23,456.00" without the currency.</summary>
    public static string Number(decimal amount) => amount.ToString("N2", Lakh);
}
