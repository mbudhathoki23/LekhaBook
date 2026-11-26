using System.Globalization;
using LekhaBook.Client;
using LekhaBook.UI.Localization;
using LekhaBook.UI.Services;

namespace LekhaBook.App.Services;

/// <summary>
/// The app's language on the device. The choice is saved in the same settings store as the rest of the
/// device's preferences, applied to the current thread at once, and announced so the layout re-creates its screens.
/// The saved language is applied at startup in <see cref="App"/>, before any screen is built.
/// </summary>
public sealed class DeviceLanguageService(ILocalSettings settings) : ILanguageService
{
    public string CurrentCode => Languages.Normalize(CultureInfo.CurrentUICulture.Name);

    public event EventHandler? Changed;

    public async Task<bool> SetAsync(string code)
    {
        var normalized = Languages.Normalize(code);
        await settings.SetAsync(Languages.PreferenceKey, normalized);
        Languages.Apply(normalized);
        Changed?.Invoke(this, EventArgs.Empty);
        return false; // nothing to reload: the layout re-creates its content
    }
}
