using System.Text.Json;
using LekhaBook.Contracts;
using LekhaBook.UI.Services;

namespace LekhaBook.Client;

/// <summary>
/// Sign-in state for both hosts. Offline-first: if the server can't be reached, a saved session stays
/// signed in using the cached profile; only a rejected refresh token signs the user out.
/// </summary>
public sealed class SessionService : ISessionService
{
    private const string CachedMeKey = "session.me";
    private const string LastCompanyKey = "session.company";

    private readonly LekhaBookApi _api;
    private readonly ITokenStore _tokens;
    private readonly ILocalSettings _settings;
    private readonly IEnumerable<IUserChangeListener> _listeners;
    private readonly IDeviceUnlock? _deviceUnlock;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public SessionService(LekhaBookApi api, ITokenStore tokens, ILocalSettings settings,
        IEnumerable<IUserChangeListener> listeners, IDeviceUnlock? deviceUnlock = null)
    {
        _api = api;
        _tokens = tokens;
        _settings = settings;
        _listeners = listeners;
        _deviceUnlock = deviceUnlock;
        _api.SessionExpired += OnSessionExpired;
    }

    /// <summary>The server rejected the refresh token: this is the one place that signs the user out for it.</summary>
    private async void OnSessionExpired(object? sender, EventArgs e)
    {
        try
        {
            await ClearSessionAsync(clearStoredTokens: true);
        }
        catch (Exception)
        {
            // Event handler: never let a storage/navigation failure crash the app.
        }
    }

    public bool IsInitialized { get; private set; }

    public bool IsSignedIn => Me is not null;

    public bool IsLocked { get; private set; }

    public bool IsOffline { get; private set; }

    public MeResponse? Me { get; private set; }

    public CompanySummary? CurrentCompany { get; private set; }

    public string? UserName => Me?.DisplayName ?? Me?.Email;

    public string? CompanyName => CurrentCompany?.Name;

    public event EventHandler? Changed;

    public async Task InitializeAsync()
    {
        if (IsInitialized)
        {
            return;
        }

        await _initLock.WaitAsync();
        try
        {
            if (IsInitialized)
            {
                return;
            }

            if (_deviceUnlock is not null && (await _deviceUnlock.GetStateAsync()).Enabled)
            {
                // No token, cached profile, company or local database is opened before device proof.
                IsLocked = true;
                if ((await _deviceUnlock.UnlockAsync()).Succeeded)
                {
                    await RestoreSavedSessionAsync();
                }
            }
            else
            {
                await RestoreSavedSessionAsync();
            }

            IsInitialized = true;
        }
        finally
        {
            _initLock.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<SignInOutcome> SignInAsync(string email, string password, string? returnUrl,
        string? twoFactorCode = null, string? recoveryCode = null)
    {
        var login = await _api.LoginAsync(email.Trim(), password, twoFactorCode, recoveryCode);
        if (!login.Ok)
        {
            return new(login.Error, RequiresTwoFactor: login.RequiresTwoFactor);
        }

        return await CompleteSignInAsync(login.Value!, returnUrl, "Signed in, but your profile couldn't be loaded. Please try again.");
    }

    public async Task<SignInOutcome> UnlockAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            if (IsSignedIn)
            {
                return new(null);
            }

            if (!IsLocked || _deviceUnlock is null)
            {
                return new("Please sign in with your account password.");
            }

            var proof = await _deviceUnlock.UnlockAsync();
            if (!proof.Succeeded)
            {
                return new(proof.Error ?? "Device unlock was cancelled. Please try again or use your account password.");
            }

            await RestoreSavedSessionAsync();
            Changed?.Invoke(this, EventArgs.Empty);
            return IsSignedIn ? new(null) : new("Your saved session could not be restored. Please sign in with your account password.");
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task RestoreSavedSessionAsync()
    {
        if (await _tokens.LoadAsync() is not { } saved)
        {
            IsLocked = false;
            if (_deviceUnlock is not null)
            {
                await _deviceUnlock.ForgetAsync();
            }
            return;
        }

        _api.SetTokens(saved);
        await LoadProfileAsync(allowCached: true);
        if (IsSignedIn)
        {
            IsLocked = false;
        }
        else
        {
            _api.SetTokens(null);
        }
    }

    public async Task<SignInOutcome> RegisterAsync(string displayName, string email, string password, string? returnUrl)
    {
        var register = await _api.RegisterAsync(email.Trim(), password);
        if (!register.Ok)
        {
            return new(register.Error);
        }

        var login = await _api.LoginAsync(email.Trim(), password);
        if (!login.Ok)
        {
            return new(login.Error);
        }

        await _api.SendAsync<bool>(HttpMethod.Put, "/api/me", new UpdateMeRequest(displayName.Trim()));
        return await CompleteSignInAsync(login.Value!, returnUrl, "Account created, but your profile couldn't be loaded. Please sign in.");
    }

    private async Task<SignInOutcome> CompleteSignInAsync(ApiTokens tokens, string? returnUrl, string profileError)
    {
        if (!await LoadProfileAsync(allowCached: false))
        {
            return new(profileError);
        }

        IsLocked = false;
        if (_deviceUnlock is not null && !await _deviceUnlock.IsEnabledForAsync(Me!.UserId))
        {
            // A different account must explicitly opt in on this device after its own sign-in.
            await _deviceUnlock.ForgetAsync();
        }

        var navigated = await _tokens.SaveAsync(tokens, returnUrl);
        Changed?.Invoke(this, EventArgs.Empty);
        return new(null, navigated);
    }

    public Task SignOutAsync() => ClearSessionAsync(clearStoredTokens: true);

    public async Task SelectCompanyAsync(Guid companyId)
    {
        CurrentCompany = Me?.Companies.FirstOrDefault(c => c.CompanyId == companyId) ?? CurrentCompany;
        await _settings.SetAsync(LastCompanyKey, CurrentCompany?.CompanyId.ToString());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAsync()
    {
        if (IsSignedIn)
        {
            await LoadProfileAsync(allowCached: true);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool Can(string permission) =>
        CurrentCompany?.Permissions.Contains(permission, StringComparer.Ordinal) == true;

    /// <summary>Loads /api/me. Returns false if there is no profile (signed out).</summary>
    private async Task<bool> LoadProfileAsync(bool allowCached)
    {
        var previousUser = Me?.UserId;
        var me = await _api.GetAsync<MeResponse>("/api/me");

        if (me.Ok)
        {
            Me = me.Value;
            IsOffline = false;
            await _settings.SetAsync(CachedMeKey, JsonSerializer.Serialize(Me, LekhaBookApi.Json));
        }
        else if (me.IsNetworkError && allowCached && await _settings.GetAsync(CachedMeKey) is { } json)
        {
            Me = JsonSerializer.Deserialize<MeResponse>(json, LekhaBookApi.Json);
            IsOffline = true;
        }
        else
        {
            // A rejected refresh already signed out via OnSessionExpired (tokens are gone then);
            // only clear here for a 401 that happened with tokens still in place.
            if (me.StatusCode == 401 && _api.HasTokens)
            {
                await ClearSessionAsync(clearStoredTokens: true);
            }

            return false;
        }

        // Keep the last company if the user still belongs to it; otherwise fall back to the first.
        var lastId = Guid.TryParse(await _settings.GetAsync(LastCompanyKey), out var id) ? id : (Guid?)null;
        var wanted = CurrentCompany?.CompanyId ?? lastId;
        CurrentCompany = Me!.Companies.FirstOrDefault(c => c.CompanyId == wanted) ?? Me.Companies.FirstOrDefault();

        if (previousUser != Me.UserId)
        {
            foreach (var listener in _listeners)
            {
                await listener.OnUserChangedAsync(Me.UserId);
            }
        }

        return true;
    }

    private async Task ClearSessionAsync(bool clearStoredTokens)
    {
        var wasSignedIn = Me is not null;
        Me = null;
        IsLocked = false;
        CurrentCompany = null;
        IsOffline = false;
        _api.SetTokens(null);
        await _settings.SetAsync(CachedMeKey, null);
        await _settings.SetAsync(LastCompanyKey, null);

        if (wasSignedIn)
        {
            foreach (var listener in _listeners)
            {
                await listener.OnUserChangedAsync(null);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);

        if (clearStoredTokens)
        {
            if (_deviceUnlock is not null)
            {
                await _deviceUnlock.ForgetAsync();
            }
            await _tokens.ClearAsync();
        }
    }
}
