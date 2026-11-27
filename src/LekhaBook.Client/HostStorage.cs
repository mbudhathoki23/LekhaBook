using LekhaBook.Contracts;

namespace LekhaBook.Client;

/// <summary>Tokens from /api/auth/login or /refresh.</summary>
public sealed record ApiTokens(string? AccessToken, string RefreshToken, DateTime AccessTokenExpiresAtUtc);

/// <summary>
/// Where a host keeps the signed-in user's tokens. MAUI: SecureStorage. Web: an HttpOnly cookie,
/// set through a short redirect (so saving and clearing may reload the page).
/// </summary>
public interface ITokenStore
{
    Task<ApiTokens?> LoadAsync();

    /// <summary>
    /// Called after sign-in. Returns true if the host itself navigated away (the web host reloads the page
    /// to set its cookie, then goes to <paramref name="returnUrl"/>), so the caller must not navigate.
    /// </summary>
    Task<bool> SaveAsync(ApiTokens tokens, string? returnUrl);

    /// <summary>Called after a token refresh, with the new tokens. May keep them only in memory.</summary>
    Task UpdateAsync(ApiTokens tokens);

    /// <summary>Called on sign-out, or when the server rejects the refresh token.</summary>
    Task ClearAsync();
}

/// <summary>Small, non-secret settings (last company, cached profile for offline start).</summary>
public interface ILocalSettings
{
    Task<string?> GetAsync(string key);

    Task SetAsync(string key, string? value);
}

/// <summary>Told when the signed-in user changes, e.g. to open that user's device database.</summary>
public interface IUserChangeListener
{
    Task OnUserChangedAsync(Guid? userId);
}

/// <summary>Plugs standalone Offline Local Mode (no online account required) into SessionService on native hosts.</summary>
public interface ILocalSessionProvider
{
    Guid LocalUserId { get; }

    Task<IReadOnlyList<CompanySummary>> ListCompaniesAsync();

    Task<CompanySummary> CreateCompanyAsync(CreateCompanyRequest request);
}
