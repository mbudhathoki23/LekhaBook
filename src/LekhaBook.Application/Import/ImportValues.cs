using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LekhaBook.Domain;

namespace LekhaBook.Application.Import;

/// <summary>An amount as written: its size, and which way a minus sign, brackets or Dr/Cr pointed.</summary>
/// <param name="Value">Always zero or more.</param>
/// <param name="Negative">Written with a minus sign or in brackets.</param>
/// <param name="DrCr">'D', 'C' or null.</param>
public readonly record struct ImportAmount(decimal Value, bool Negative, char? DrCr);

/// <summary>Reads the words, amounts and dates found in other apps' exports.</summary>
public static partial class ImportValues
{
    /// <summary>
    /// Lower case with only letters, digits and vowel marks, so "Cheque No." and "cheque_no" match.
    /// Devanagari digits become 0-9.
    /// </summary>
    public static string Normalize(string? text)
    {
        var result = new StringBuilder();
        foreach (var c in AsciiDigits(text ?? string.Empty))
        {
            if (char.IsLetterOrDigit(c)
                || char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                result.Append(char.ToLowerInvariant(c));
            }
        }

        return result.ToString();
    }

    public static string AsciiDigits(string text) =>
        text.Any(c => c > 127 && char.IsDigit(c))
            ? string.Concat(text.Select(c => c > 127 && char.IsDigit(c) ? (char)('0' + (int)char.GetNumericValue(c)) : c))
            : text;

    /// <summary>
    /// Reads "1,00,000.00", "Rs. 500/-", "(250)", "-250", "1200 Dr". Commas must group digits the usual way, so a
    /// decimal comma ("12,5") is refused rather than read as 125. False for text that isn't an amount.
    /// </summary>
    public static bool TryAmount(string? text, out ImportAmount amount)
    {
        amount = default;
        var value = AsciiDigits(text ?? string.Empty).Replace('−', '-').Trim();
        char? drCr = null;
        if (DrCrSuffix().Match(value) is { Success: true } suffix)
        {
            drCr = char.ToUpperInvariant(suffix.Groups[1].Value[0]);
            value = value[..suffix.Index].Trim();
        }

        value = Currency().Replace(value, string.Empty).Replace("/-", string.Empty);
        value = string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

        var negative = false;
        if (value is ['(', .., ')'])
        {
            negative = true;
            value = value[1..^1];
        }

        if (value.StartsWith('-') || value.EndsWith('-'))
        {
            negative = true;
            value = value.Trim('-');
        }
        else if (value.StartsWith('+'))
        {
            value = value[1..];
        }

        if (!Grouped().IsMatch(value)
            || !decimal.TryParse(value.Replace(",", string.Empty), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        amount = new ImportAmount(number, negative && number != 0, drCr);
        return true;
    }

    /// <summary>True for an empty cell or a dash, which exports use for "nothing".</summary>
    public static bool IsBlank(string? text) => string.IsNullOrWhiteSpace(text) || text.Trim() is "-" or "–" or "—";

    /// <summary>
    /// Works out whether dates with the year last are day or month first: a first part over 12 can only be a day,
    /// a second part over 12 only means month first. Day first when the dates don't say.
    /// </summary>
    public static ImportDateOrder DetectOrder(IEnumerable<string> dates)
    {
        var monthFirst = false;
        foreach (var text in dates)
        {
            if (YearLast().Match(AsciiDigits(text.Trim())) is not { Success: true } match)
            {
                continue;
            }

            if (Int(match, 1) > 12)
            {
                return ImportDateOrder.DayFirst;
            }

            monthFirst |= Int(match, 2) > 12;
        }

        return monthFirst ? ImportDateOrder.MonthFirst : ImportDateOrder.DayFirst;
    }

    /// <summary>
    /// Reads a date in AD or Bikram Sambat and returns the AD date: "2083-08-10", "10/08/2083", "26 Nov 2026",
    /// "10 Mangsir 2083", with or without a time after it. False when it isn't a date, or is a BS date outside the
    /// calendar table.
    /// </summary>
    public static bool TryDate(string? text, ImportCalendar calendar, ImportDateOrder order, out DateOnly date)
    {
        date = default;
        var value = AsciiDigits(text ?? string.Empty).Trim();
        int year, month, day;

        if (YearFirst().Match(value) is { Success: true } first)
        {
            (year, month, day) = (Int(first, 1), Int(first, 2), Int(first, 3));
        }
        else if (YearLast().Match(value) is { Success: true } last)
        {
            year = Int(last, 3);
            (day, month) = order == ImportDateOrder.MonthFirst ? (Int(last, 2), Int(last, 1)) : (Int(last, 1), Int(last, 2));
        }
        else if (DayName().Match(value) is { Success: true } named)
        {
            return TryNamed(Int(named, 1), named.Groups[2].Value, named.Groups[3].Value, calendar, out date);
        }
        else if (NameDay().Match(value) is { Success: true } reversed)
        {
            return TryNamed(Int(reversed, 2), reversed.Groups[1].Value, reversed.Groups[3].Value, calendar, out date);
        }
        else
        {
            return false;
        }

        return TryBuild(year, month, day, calendar, out date);
    }

    private static bool TryNamed(int day, string monthName, string yearText, ImportCalendar calendar, out DateOnly date)
    {
        date = default;
        var year = int.Parse(yearText, CultureInfo.InvariantCulture);
        if (yearText.Length == 2)
        {
            year += 2000;
        }

        for (var month = 1; month <= 12; month++)
        {
            if (string.Equals(NepaliCalendar.MonthName(month), monthName, StringComparison.OrdinalIgnoreCase))
            {
                return TryBuild(year, month, day, ImportCalendar.BS, out date);
            }

            // "Nov", "Sept" and "November" all start the English name.
            if (CultureInfo.InvariantCulture.DateTimeFormat.MonthNames[month - 1].StartsWith(monthName, StringComparison.OrdinalIgnoreCase))
            {
                return calendar != ImportCalendar.BS && TryBuild(year, month, day, ImportCalendar.AD, out date);
            }
        }

        return false;
    }

    private static bool TryBuild(int year, int month, int day, ImportCalendar calendar, out DateOnly date)
    {
        date = default;
        if (calendar == ImportCalendar.BS || (calendar == ImportCalendar.Auto && year >= 2070))
        {
            return NepaliCalendar.TryToAd(new NepaliDate(year, month, day), out date);
        }

        if (year is < 1900 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }

    private static int Int(Match match, int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(?<![A-Za-z])(dr|cr)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex DrCrSuffix();

    [GeneratedRegex(@"rs\.?|npr|inr|रु\.?|रू\.?|₹|\$", RegexOptions.IgnoreCase)]
    private static partial Regex Currency();

    [GeneratedRegex(@"^(?:\d+(?:\.\d*)?|\.\d+|\d{1,3}(?:,\d{2,3})*,\d{3}(?:\.\d*)?)$")]
    private static partial Regex Grouped();

    private const string Time = @"(?:[T\s,]+\d{1,2}:\d{2}.*)?$";

    [GeneratedRegex(@"^(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})" + Time)]
    private static partial Regex YearFirst();

    [GeneratedRegex(@"^(\d{1,2})[-/.](\d{1,2})[-/.](\d{4})" + Time)]
    private static partial Regex YearLast();

    [GeneratedRegex(@"^(\d{1,2})(?:st|nd|rd|th)?[\s\-/.]+([A-Za-z]{3,})[\s\-/.,]+(\d{4}|\d{2})" + Time)]
    private static partial Regex DayName();

    [GeneratedRegex(@"^([A-Za-z]{3,})[\s\-/.]+(\d{1,2})(?:st|nd|rd|th)?[\s\-/.,]+(\d{4})" + Time)]
    private static partial Regex NameDay();
}
