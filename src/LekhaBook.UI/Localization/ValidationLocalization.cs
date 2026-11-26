using LekhaBook.Application;
using Microsoft.Extensions.Localization;

namespace LekhaBook.UI.Localization;

public static class ValidationLocalization
{
    /// <summary>
    /// The shared validation messages in the screen's language:
    /// <c>MasterValidation.Validate(request, Loc.ForValidation())</c>.
    /// </summary>
    public static ValidationText ForValidation(this IStringLocalizer<SharedResource> localizer) =>
        (key, args) => localizer[key, args];
}
