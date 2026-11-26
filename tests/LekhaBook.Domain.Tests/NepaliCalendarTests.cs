using System.Globalization;
using LekhaBook.Domain;

namespace LekhaBook.Domain.Tests;

public sealed class NepaliCalendarTests
{
    /// <summary>
    /// The reference month list the calendar was built from: BS year, BS month, first and last AD day.
    /// Poush 2087 (29 days) and Jestha 2090 (32 days) differ from the original list (30 and 31), see D20.
    /// </summary>
    private const string ReferenceMonths = """
        2075,4,2018-07-17,2018-08-16
        2075,5,2018-08-17,2018-09-16
        2075,6,2018-09-17,2018-10-17
        2075,7,2018-10-18,2018-11-16
        2075,8,2018-11-17,2018-12-15
        2075,9,2018-12-16,2019-01-14
        2075,10,2019-01-15,2019-02-12
        2075,11,2019-02-13,2019-03-14
        2075,12,2019-03-15,2019-04-13
        2076,1,2019-04-14,2019-05-14
        2076,2,2019-05-15,2019-06-15
        2076,3,2019-06-16,2019-07-16
        2076,4,2019-07-17,2019-08-17
        2076,5,2019-08-18,2019-09-17
        2076,6,2019-09-18,2019-10-17
        2076,7,2019-10-18,2019-11-16
        2076,8,2019-11-17,2019-12-16
        2076,9,2019-12-17,2020-01-14
        2076,10,2020-01-15,2020-02-12
        2076,11,2020-02-13,2020-03-13
        2076,12,2020-03-14,2020-04-12
        2077,1,2020-04-13,2020-05-13
        2077,2,2020-05-14,2020-06-14
        2077,3,2020-06-15,2020-07-15
        2077,4,2020-07-16,2020-08-16
        2077,5,2020-08-17,2020-09-16
        2077,6,2020-09-17,2020-10-16
        2077,7,2020-10-17,2020-11-15
        2077,8,2020-11-16,2020-12-15
        2077,9,2020-12-16,2021-01-13
        2077,10,2021-01-14,2021-02-12
        2077,11,2021-02-13,2021-03-13
        2077,12,2021-03-14,2021-04-13
        2078,1,2021-04-14,2021-05-14
        2078,2,2021-05-15,2021-06-14
        2078,3,2021-06-15,2021-07-15
        2078,4,2021-07-16,2021-08-16
        2078,5,2021-08-17,2021-09-16
        2078,6,2021-09-17,2021-10-17
        2078,7,2021-10-18,2021-11-16
        2078,8,2021-11-17,2021-12-15
        2078,9,2021-12-16,2022-01-14
        2078,10,2022-01-15,2022-02-12
        2078,11,2022-02-13,2022-03-14
        2078,12,2022-03-15,2022-04-13
        2079,1,2022-04-14,2022-05-14
        2079,2,2022-05-15,2022-06-14
        2079,3,2022-06-15,2022-07-16
        2079,4,2022-07-17,2022-08-16
        2079,5,2022-08-17,2022-09-16
        2079,6,2022-09-17,2022-10-17
        2079,7,2022-10-18,2022-11-16
        2079,8,2022-11-17,2022-12-15
        2079,9,2022-12-16,2023-01-14
        2079,10,2023-01-15,2023-02-12
        2079,11,2023-02-13,2023-03-14
        2079,12,2023-03-15,2023-04-13
        2080,1,2023-04-14,2023-05-14
        2080,2,2023-05-15,2023-06-15
        2080,3,2023-06-16,2023-07-16
        2080,4,2023-07-17,2023-08-17
        2080,5,2023-08-18,2023-09-17
        2080,6,2023-09-18,2023-10-17
        2080,7,2023-10-18,2023-11-16
        2080,8,2023-11-17,2023-12-16
        2080,9,2023-12-17,2024-01-14
        2080,10,2024-01-15,2024-02-12
        2080,11,2024-02-13,2024-03-13
        2080,12,2024-03-14,2024-04-12
        2081,1,2024-04-13,2024-05-13
        2081,2,2024-05-14,2024-06-14
        2081,3,2024-06-15,2024-07-15
        2081,4,2024-07-16,2024-08-16
        2081,5,2024-08-17,2024-09-16
        2081,6,2024-09-17,2024-10-16
        2081,7,2024-10-17,2024-11-15
        2081,8,2024-11-16,2024-12-15
        2081,9,2024-12-16,2025-01-13
        2081,10,2025-01-14,2025-02-12
        2081,11,2025-02-13,2025-03-13
        2081,12,2025-03-14,2025-04-13
        2082,1,2025-04-14,2025-05-14
        2082,2,2025-05-15,2025-06-14
        2082,3,2025-06-15,2025-07-16
        2082,4,2025-07-17,2025-08-16
        2082,5,2025-08-17,2025-09-16
        2082,6,2025-09-17,2025-10-17
        2082,7,2025-10-18,2025-11-16
        2082,8,2025-11-17,2025-12-15
        2082,9,2025-12-16,2026-01-14
        2082,10,2026-01-15,2026-02-12
        2082,11,2026-02-13,2026-03-14
        2082,12,2026-03-15,2026-04-13
        2083,1,2026-04-14,2026-05-14
        2083,2,2026-05-15,2026-06-14
        2083,3,2026-06-15,2026-07-16
        2083,4,2026-07-17,2026-08-16
        2083,5,2026-08-17,2026-09-16
        2083,6,2026-09-17,2026-10-17
        2083,7,2026-10-18,2026-11-16
        2083,8,2026-11-17,2026-12-15
        2083,9,2026-12-16,2027-01-14
        2083,10,2027-01-15,2027-02-12
        2083,11,2027-02-13,2027-03-14
        2083,12,2027-03-15,2027-04-13
        2084,1,2027-04-14,2027-05-14
        2084,2,2027-05-15,2027-06-14
        2084,3,2027-06-15,2027-07-16
        2084,4,2027-07-17,2027-08-16
        2084,5,2027-08-17,2027-09-16
        2084,6,2027-09-17,2027-10-16
        2084,7,2027-10-17,2027-11-15
        2084,8,2027-11-16,2027-12-15
        2084,9,2027-12-16,2028-01-13
        2084,10,2028-01-14,2028-02-12
        2084,11,2028-02-13,2028-03-13
        2084,12,2028-03-14,2028-04-12
        2085,1,2028-04-13,2028-05-13
        2085,2,2028-05-14,2028-06-14
        2085,3,2028-06-15,2028-07-15
        2085,4,2028-07-16,2028-08-16
        2085,5,2028-08-17,2028-09-15
        2085,6,2028-09-16,2028-10-16
        2085,7,2028-10-17,2028-11-15
        2085,8,2028-11-16,2028-12-15
        2085,9,2028-12-16,2029-01-13
        2085,10,2029-01-14,2029-02-12
        2085,11,2029-02-13,2029-03-14
        2085,12,2029-03-15,2029-04-13
        2086,1,2029-04-14,2029-05-13
        2086,2,2029-05-14,2029-06-14
        2086,3,2029-06-15,2029-07-15
        2086,4,2029-07-16,2029-08-16
        2086,5,2029-08-17,2029-09-16
        2086,6,2029-09-17,2029-10-16
        2086,7,2029-10-17,2029-11-15
        2086,8,2029-11-16,2029-12-15
        2086,9,2029-12-16,2030-01-13
        2086,10,2030-01-14,2030-02-12
        2086,11,2030-02-13,2030-03-14
        2086,12,2030-03-15,2030-04-13
        2087,1,2030-04-14,2030-05-14
        2087,2,2030-05-15,2030-06-14
        2087,3,2030-06-15,2030-07-16
        2087,4,2030-07-17,2030-08-16
        2087,5,2030-08-17,2030-09-16
        2087,6,2030-09-17,2030-10-17
        2087,7,2030-10-18,2030-11-16
        2087,8,2030-11-17,2030-12-16
        2087,9,2030-12-17,2031-01-14
        2087,10,2031-01-15,2031-02-13
        2087,11,2031-02-14,2031-03-15
        2087,12,2031-03-16,2031-04-14
        2088,1,2031-04-15,2031-05-14
        2088,2,2031-05-15,2031-06-14
        2088,3,2031-06-15,2031-07-16
        2088,4,2031-07-17,2031-08-17
        2088,5,2031-08-18,2031-09-16
        2088,6,2031-09-17,2031-10-17
        2088,7,2031-10-18,2031-11-16
        2088,8,2031-11-17,2031-12-16
        2088,9,2031-12-17,2032-01-14
        2088,10,2032-01-15,2032-02-13
        2088,11,2032-02-14,2032-03-14
        2088,12,2032-03-15,2032-04-13
        2089,1,2032-04-14,2032-05-13
        2089,2,2032-05-14,2032-06-14
        2089,3,2032-06-15,2032-07-15
        2089,4,2032-07-16,2032-08-16
        2089,5,2032-08-17,2032-09-16
        2089,6,2032-09-17,2032-10-16
        2089,7,2032-10-17,2032-11-15
        2089,8,2032-11-16,2032-12-15
        2089,9,2032-12-16,2033-01-13
        2089,10,2033-01-14,2033-02-12
        2089,11,2033-02-13,2033-03-14
        2089,12,2033-03-15,2033-04-13
        2090,1,2033-04-14,2033-05-13
        2090,2,2033-05-14,2033-06-14
        2090,3,2033-06-15,2033-07-15
        """;

    private static readonly (int Year, int Month, DateOnly Start, DateOnly End)[] Months = ReferenceMonths
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split(','))
        .Select(p => (
            int.Parse(p[0], CultureInfo.InvariantCulture),
            int.Parse(p[1], CultureInfo.InvariantCulture),
            DateOnly.ParseExact(p[2], "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateOnly.ParseExact(p[3], "yyyy-MM-dd", CultureInfo.InvariantCulture)))
        .ToArray();

    [Fact]
    public void Reference_months_run_day_after_day_with_29_to_32_days()
    {
        Assert.Equal(15 * 12, Months.Length); // 2075 Shrawan to 2090 Ashad

        for (var i = 0; i < Months.Length; i++)
        {
            Assert.InRange(Months[i].End.DayNumber - Months[i].Start.DayNumber + 1, 29, 32);
            if (i > 0)
            {
                Assert.Equal(Months[i - 1].End.AddDays(1), Months[i].Start);
            }
        }
    }

    [Fact]
    public void Every_day_converts_to_the_reference_date_and_back()
    {
        foreach (var (year, month, start, end) in Months)
        {
            Assert.Equal(end.DayNumber - start.DayNumber + 1, NepaliCalendar.DaysInMonth(year, month));

            for (var ad = start; ad <= end; ad = ad.AddDays(1))
            {
                var expected = new NepaliDate(year, month, ad.DayNumber - start.DayNumber + 1);

                Assert.True(NepaliCalendar.TryFromAd(ad, out var bs));
                Assert.Equal(expected, bs);
                Assert.True(NepaliCalendar.TryToAd(bs, out var back));
                Assert.Equal(ad, back);
            }
        }

        Assert.Equal(Months[0].Start, NepaliCalendar.FirstAdDate);
        Assert.Equal(Months[^1].End, NepaliCalendar.LastAdDate);
    }

    [Theory]
    [InlineData("2024-04-13", "2081/01/01")]
    [InlineData("2025-04-14", "2082/01/01")]
    [InlineData("2026-11-26", "2083/08/10")]
    public void Formats_as_bs_or_ad(string ad, string bs)
    {
        var date = DateOnly.ParseExact(ad, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        Assert.Equal(bs, CalendarDates.Format(date, CalendarMode.BS));
        Assert.Equal(ad, CalendarDates.Format(date, CalendarMode.AD));
    }

    [Fact]
    public void A_date_outside_the_table_is_shown_as_ad()
    {
        var before = NepaliCalendar.FirstAdDate.AddDays(-1);
        var after = NepaliCalendar.LastAdDate.AddDays(1);

        Assert.False(NepaliCalendar.TryFromAd(before, out _));
        Assert.False(NepaliCalendar.TryFromAd(after, out _));
        Assert.Equal("2018-07-16", CalendarDates.Format(before, CalendarMode.BS));
        Assert.Equal("2033-07-16", CalendarDates.Format(after, CalendarMode.BS));
    }

    [Theory]
    [InlineData("2083/08/10")]
    [InlineData("2083-8-10")]
    [InlineData(" 2083.08.10 ")]
    public void Reads_a_typed_bs_date(string text)
    {
        Assert.True(CalendarDates.TryParse(text, CalendarMode.BS, out var date));
        Assert.Equal(new DateOnly(2026, 11, 26), date);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2083/08")]
    [InlineData("83/08/10")]
    [InlineData("2083/13/01")]
    [InlineData("2083/00/10")]
    [InlineData("2083/08/00")]
    [InlineData("2083/08/30")] // Mangsir 2083 has 29 days
    [InlineData("2075/03/32")] // before the first month in the table
    [InlineData("2090/04/01")] // after the last month in the table
    [InlineData("2026-11-26")] // an AD date is not a BS date
    public void Refuses_a_date_that_is_not_in_the_bs_calendar(string? text)
    {
        Assert.False(CalendarDates.TryParse(text, CalendarMode.BS, out _));
    }

    [Fact]
    public void Reads_a_typed_ad_date()
    {
        Assert.True(CalendarDates.TryParse("2026/11/26", CalendarMode.AD, out var date));
        Assert.Equal(new DateOnly(2026, 11, 26), date);
        Assert.False(CalendarDates.TryParse("2026-02-30", CalendarMode.AD, out _));
    }

    [Fact]
    public void Names_the_months()
    {
        Assert.Equal("Baishakh", NepaliCalendar.MonthName(1));
        Assert.Equal("Chaitra", NepaliCalendar.MonthName(12));
    }
}
