using System.Globalization;

namespace LekhaBook.Domain;

/// <summary>A day in the Bikram Sambat (Nepali) calendar, e.g. 10 Mangsir 2083.</summary>
public readonly record struct NepaliDate(int Year, int Month, int Day)
{
    /// <summary>"2083/08/10".</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Year:D4}/{Month:D2}/{Day:D2}");
}

/// <summary>
/// Converts between AD and Bikram Sambat. BS month lengths follow no formula, so they come from a table
/// (Shrawan 2075 to Ashad 2090). Dates are always stored as AD; BS is only how they are shown and typed.
/// </summary>
public static class NepaliCalendar
{
    /// <summary>The first BS year in the table (from Shrawan).</summary>
    public const int FirstYear = 2075;

    /// <summary>The last BS year in the table (up to Ashad).</summary>
    public const int LastYear = 2090;

    private const int FirstMonth = 4; // Shrawan

    /// <summary>Days in each month, one line per BS year; 2075 starts at Shrawan and 2090 ends at Ashad.</summary>
    private static readonly byte[] MonthLengths =
    [
                    31, 31, 31, 30, 29, 30, 29, 30, 30, // 2075
        31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 30, // 2076
        31, 32, 31, 32, 31, 30, 30, 30, 29, 30, 29, 31, // 2077
        31, 31, 31, 32, 31, 31, 30, 29, 30, 29, 30, 30, // 2078
        31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30, // 2079
        31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 30, // 2080
        31, 32, 31, 32, 31, 30, 30, 30, 29, 30, 29, 31, // 2081
        31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30, // 2082
        31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30, // 2083
        31, 31, 32, 31, 31, 30, 30, 30, 29, 30, 30, 30, // 2084
        31, 32, 31, 32, 30, 31, 30, 30, 29, 30, 30, 30, // 2085
        30, 32, 31, 32, 31, 30, 30, 30, 29, 30, 30, 30, // 2086
        31, 31, 32, 31, 31, 31, 30, 30, 29, 30, 30, 30, // 2087
        30, 31, 32, 32, 30, 31, 30, 30, 29, 30, 30, 30, // 2088
        30, 32, 31, 32, 31, 30, 30, 30, 29, 30, 30, 30, // 2089
        30, 32, 31,                                     // 2090
    ];

    private static readonly string[] MonthNames =
    [
        "Baishakh", "Jestha", "Ashad", "Shrawan", "Bhadra", "Ashwin",
        "Kartik", "Mangsir", "Poush", "Magh", "Falgun", "Chaitra",
    ];

    /// <summary>1 Shrawan 2075.</summary>
    public static DateOnly FirstAdDate { get; } = new(2018, 7, 17);

    /// <summary>The last day of Ashad 2090.</summary>
    public static DateOnly LastAdDate { get; } = FirstAdDate.AddDays(MonthLengths.Sum(length => length) - 1);

    /// <summary>The month's name in English; <paramref name="month"/> is 1 (Baishakh) to 12 (Chaitra).</summary>
    public static string MonthName(int month) => MonthNames[month - 1];

    /// <summary>Days in a BS month, or 0 when the table doesn't have that month.</summary>
    public static int DaysInMonth(int year, int month)
    {
        var index = IndexOf(year, month);
        return index < 0 ? 0 : MonthLengths[index];
    }

    /// <summary>False when <paramref name="date"/> is outside the table.</summary>
    public static bool TryFromAd(DateOnly date, out NepaliDate result)
    {
        result = default;
        var days = date.DayNumber - FirstAdDate.DayNumber;
        if (days < 0)
        {
            return false;
        }

        for (var index = 0; index < MonthLengths.Length; index++)
        {
            if (days < MonthLengths[index])
            {
                var month = FirstMonth - 1 + index; // months since 1 Baishakh of the first year
                result = new NepaliDate(FirstYear + month / 12, month % 12 + 1, days + 1);
                return true;
            }

            days -= MonthLengths[index];
        }

        return false;
    }

    /// <summary>False when <paramref name="date"/> is not a day the table has.</summary>
    public static bool TryToAd(NepaliDate date, out DateOnly result)
    {
        result = default;
        var index = IndexOf(date.Year, date.Month);
        if (index < 0 || date.Day < 1 || date.Day > MonthLengths[index])
        {
            return false;
        }

        result = FirstAdDate.AddDays(MonthLengths.Take(index).Sum(length => length) + date.Day - 1);
        return true;
    }

    private static int IndexOf(int year, int month)
    {
        if (month is < 1 or > 12 || year is < FirstYear or > LastYear)
        {
            return -1;
        }

        var index = (year - FirstYear) * 12 + month - FirstMonth;
        return index >= 0 && index < MonthLengths.Length ? index : -1;
    }
}

/// <summary>Writes and reads a stored (AD) date in the calendar a company uses (<see cref="CalendarMode"/>).</summary>
public static class CalendarDates
{
    private const string AdFormat = "yyyy-MM-dd";

    /// <summary>
    /// BS companies see "2083/08/10", AD companies "2026-11-26". A date outside the BS table is shown as AD,
    /// and the different separator tells the two apart.
    /// </summary>
    public static string Format(DateOnly date, CalendarMode mode) =>
        mode == CalendarMode.BS && NepaliCalendar.TryFromAd(date, out var bs)
            ? bs.ToString()
            : date.ToString(AdFormat, CultureInfo.InvariantCulture);

    /// <summary>Reads year, month and day typed in <paramref name="mode"/>'s calendar and returns the AD date.</summary>
    public static bool TryParse(string? text, CalendarMode mode, out DateOnly date)
    {
        date = default;
        var parts = text?.Trim().Split('/', '-', '.') ?? [];
        if (parts.Length != 3 || parts[0].Length != 4
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var day))
        {
            return false;
        }

        if (mode == CalendarMode.BS)
        {
            return NepaliCalendar.TryToAd(new NepaliDate(year, month, day), out date);
        }

        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }
}
