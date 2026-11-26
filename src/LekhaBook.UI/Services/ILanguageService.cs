namespace LekhaBook.UI.Services;

/// <summary>
/// The language the app shows ("en" or "ne"). Each host decides where the choice is kept and how it takes effect:
/// MAUI saves it on the device and updates the screen; the web host sets a cookie and reloads the page.
/// </summary>
public interface ILanguageService
{
    /// <summary>The language in use, as a two-letter code.</summary>
    string CurrentCode { get; }

    /// <summary>Raised after the language changes on this screen. Hosts that reload the page never raise it.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Saves and applies the language. Returns true if the host navigated away (the web host reloads the page
    /// so the new culture applies), so the caller must not update the screen itself.
    /// </summary>
    Task<bool> SetAsync(string code);
}
