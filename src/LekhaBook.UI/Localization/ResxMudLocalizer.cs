using Microsoft.Extensions.Localization;
using MudBlazor;

namespace LekhaBook.UI.Localization;

/// <summary>
/// Gives MudBlazor's built-in text (pager, date picker and so on) the app's translations. A key the app
/// doesn't translate comes back as "not found", so MudBlazor falls back to its own English text.
/// </summary>
public sealed class ResxMudLocalizer(IStringLocalizer<SharedResource> localizer) : MudLocalizer
{
    public override LocalizedString this[string key] => localizer[key];

    public override LocalizedString this[string key, params object[] arguments] => localizer[key, arguments];
}
