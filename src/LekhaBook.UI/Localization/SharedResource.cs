namespace LekhaBook.UI.Localization;

/// <summary>
/// Marker type for the shared strings: SharedResource.resx (English, the default) and
/// SharedResource.ne.resx (Nepali). Razor components in LekhaBook.UI get <c>IStringLocalizer&lt;SharedResource&gt;</c>
/// injected as <c>Loc</c> (see _Imports.razor), so they can write <c>@Loc["Key"]</c>.
/// </summary>
public sealed class SharedResource
{
}
