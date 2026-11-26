using LekhaBook.Domain;
using LekhaBook.UI.Services;

namespace LekhaBook.UI.Formatting;

/// <summary>Dates as the current company reads them: Nepali miti (BS) or AD. Stored and sent dates are always AD.</summary>
public static class SessionDates
{
    /// <summary>BS unless the company chose AD (D2).</summary>
    public static CalendarMode Calendar(this ISessionService session) =>
        session.CurrentCompany?.Calendar == nameof(CalendarMode.AD) ? CalendarMode.AD : CalendarMode.BS;

    public static string FormatDate(this ISessionService session, DateOnly date) => CalendarDates.Format(date, session.Calendar());

    /// <summary>The day of a server timestamp: miti for BS companies, "26 Nov 2026" for AD ones.</summary>
    public static string FormatDay(this ISessionService session, DateTime utc)
    {
        var local = utc.ToLocalTime();
        return session.Calendar() == CalendarMode.BS
            ? CalendarDates.Format(DateOnly.FromDateTime(local), CalendarMode.BS)
            : local.ToString("d MMM yyyy");
    }
}
