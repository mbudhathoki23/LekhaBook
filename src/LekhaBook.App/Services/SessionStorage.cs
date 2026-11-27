using System.Reflection;
using System.Text.Json;
using LekhaBook.Client;
using LekhaBook.Data.Sqlite;

namespace LekhaBook.App.Services;

/// <summary>Where the app finds LekhaBook.Api. Pass -p:ApiBaseUrl=https://api.yourdomain.com/ on release builds.</summary>
public static class ApiEndpoint
{
    public static Uri BaseAddress { get; } = ResolveBaseAddress();

    private static Uri ResolveBaseAddress()
    {
        var configured = typeof(ApiEndpoint).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LekhaBook.ApiBaseUrl")?.Value;
        if (!string.IsNullOrWhiteSpace(configured) && Uri.TryCreate(configured, UriKind.Absolute, out var uri))
        {
            return uri;
        }
#if DEBUG
        // Development: the API runs on this PC with the "http" profile. The Android emulator reaches the
        // host PC at 10.0.2.2 (allowed for plain HTTP by Platforms/Android/Resources/xml/network_security_config.xml).
        return DeviceInfo.Current.Platform == DevicePlatform.Android
            ? new Uri("http://10.0.2.2:5050/")
            : new Uri("http://localhost:5050/");
#else
        return new Uri("https://api.lekhabooks.com/");
#endif
    }
}

/// <summary>Tokens in the platform's secure store (Android Keystore, iOS Keychain, Windows DPAPI).</summary>
public sealed class SecureTokenStore : ITokenStore
{
    private const string Key = "lekhabook.tokens";

    public async Task<ApiTokens?> LoadAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(Key) is { } json ? JsonSerializer.Deserialize<ApiTokens>(json) : null;
        }
        catch (Exception)
        {
            // Unreadable (e.g. app reinstalled and the key was lost): treat as signed out.
            try
            {
                SecureStorage.Default.Remove(Key);
            }
            catch (Exception)
            {
                // The store itself is unavailable; nothing more to clean up.
            }

            return null;
        }
    }

    public async Task<bool> SaveAsync(ApiTokens tokens, string? returnUrl)
    {
        await SecureStorage.Default.SetAsync(Key, JsonSerializer.Serialize(tokens));
        return false; // the caller navigates
    }

    public Task UpdateAsync(ApiTokens tokens) => SecureStorage.Default.SetAsync(Key, JsonSerializer.Serialize(tokens));

    public Task ClearAsync()
    {
        SecureStorage.Default.Remove(Key);
        return Task.CompletedTask;
    }
}

public sealed class PreferencesSettings : ILocalSettings
{
    public Task<string?> GetAsync(string key) => Task.FromResult(Preferences.Default.Get<string?>(key, null));

    public Task SetAsync(string key, string? value)
    {
        if (value is null)
        {
            Preferences.Default.Remove(key);
        }
        else
        {
            Preferences.Default.Set(key, value);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Opens the signed-in user's own device database (AGENT_PLAN § 6.4).</summary>
public sealed class DeviceDatabaseOpener(UserDatabaseFactory databases) : IUserChangeListener
{
    public Task OnUserChangedAsync(Guid? userId) => Task.Run(() => databases.OpenForUser(userId));
}
