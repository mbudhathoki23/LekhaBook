using System.Globalization;

namespace LekhaBook.UI.Localization;

/// <summary>
/// The languages the app ships with. English is the default and the fallback for any missing string.
/// To add a language: add a <c>SharedResource.{code}.resx</c> file and an entry in <see cref="Supported"/>.
/// </summary>
public static class Languages
{
    public const string English = "en";
    public const string Nepali = "ne";
    public const string Bengali = "bn";
    public const string Arabic = "ar";
    public const string Chinese = "zh"; // Simplified
    public const string Japanese = "ja";

    /// <summary>Where a device keeps the chosen language (MAUI Preferences). The web host uses a cookie instead.</summary>
    public const string PreferenceKey = "lekhabook.language";

    /// <summary>Two-letter code and the language's own name, for the language picker.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Supported =
    [
        (English, "English"),
        (Nepali, "नेपाली"),
        (Bengali, "বাংলা"),
        (Arabic, "العربية"),
        (Chinese, "中文（简体）"),
        (Japanese, "日本語"),
    ];

    /// <summary>
    /// The supported two-letter code for a stored or requested value ("ne-NP" becomes "ne").
    /// Anything unknown or empty is English.
    /// </summary>
    public static string Normalize(string? code)
    {
        var language = code?.Split('-')[0].ToLowerInvariant();
        return Supported.Any(l => l.Code == language) ? language! : English;
    }

    /// <summary>Whether screens in this language read from right to left.</summary>
    public static bool IsRightToLeft(string? code) => Normalize(code) == Arabic;

    /// <summary>
    /// The culture for a language: its own texts and month names, but always Gregorian dates and "." decimals.
    /// Without this, Arabic would show Hijri dates and could expect another decimal mark in amount fields.
    /// </summary>
    public static CultureInfo CultureFor(string? code)
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo(Normalize(code)).Clone();
        culture.NumberFormat = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        if (culture.DateTimeFormat.Calendar is not GregorianCalendar)
        {
            var gregorian = culture.OptionalCalendars.OfType<GregorianCalendar>().FirstOrDefault();
            if (gregorian is null)
            {
                culture.DateTimeFormat = (DateTimeFormatInfo)CultureInfo.InvariantCulture.DateTimeFormat.Clone();
            }
            else
            {
                culture.DateTimeFormat.Calendar = gregorian;
            }
        }

        return culture;
    }

    /// <summary>
    /// Makes the language the default for every thread. Only the MAUI host calls this; the web host gets its
    /// culture per request from RequestLocalization.
    /// <para>
    /// Don't also assign <see cref="CultureInfo.CurrentCulture"/>: that pins a value to the calling context, and
    /// .NET undoes such a change when an async method returns. A language switched from an async handler would
    /// then fall back to the pinned one on later screens.
    /// </para>
    /// </summary>
    public static void Apply(string? code)
    {
        var culture = CultureFor(code);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
