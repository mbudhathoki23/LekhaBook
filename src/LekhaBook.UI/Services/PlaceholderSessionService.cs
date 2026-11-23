namespace LekhaBook.UI.Services;

/// <summary>
/// Stand-in session until sign-in is built (phase 4). Both hosts use it for now.
/// </summary>
public sealed class PlaceholderSessionService : ISessionService
{
    public string? UserName => null;

    public string? CompanyName => "Demo Company";
}
