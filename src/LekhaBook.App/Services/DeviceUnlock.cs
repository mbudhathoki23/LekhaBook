using LekhaBook.UI.Localization;
using LekhaBook.UI.Services;
using Microsoft.Extensions.Localization;

namespace LekhaBook.App.Services;

/// <summary>Opt-in device verification before restoring the saved account. No phone credential enters the app.</summary>
public sealed class DeviceUnlock(IStringLocalizer<SharedResource> text) : IDeviceUnlock
{
    private const string EnabledUserKey = "lekhabook.device-unlock.user";
    private readonly SemaphoreSlim _promptLock = new(1, 1);

    public async Task<DeviceUnlockState> GetStateAsync()
    {
        if (!Supported)
        {
            return new(false, false, false);
        }

        try
        {
            var enabled = !string.IsNullOrEmpty(await SecureStorage.Default.GetAsync(EnabledUserKey));
            return new(true, Available, enabled);
        }
        catch (Exception)
        {
            // Fail closed: an unreadable opt-in flag must never silently expose a saved session.
            return new(true, false, true);
        }
    }

    public async Task<bool> IsEnabledForAsync(Guid userId)
    {
        if (!Supported) return false;
        try
        {
            return Guid.TryParse(await SecureStorage.Default.GetAsync(EnabledUserKey), out var enabledUser)
                && enabledUser == userId;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<DeviceUnlockResult> EnableAsync(Guid userId)
    {
        if (userId == Guid.Empty) return new(false, text["Security.DeviceUnavailable"]);
        var result = await UnlockAsync();
        if (!result.Succeeded) return result;
        try
        {
            await SecureStorage.Default.SetAsync(EnabledUserKey, userId.ToString());
            return new(true);
        }
        catch (Exception)
        {
            return new(false, text["Security.DeviceStorageError"]);
        }
    }

    public async Task<DeviceUnlockResult> DisableAsync()
    {
        var proof = await UnlockAsync();
        if (!proof.Succeeded) return proof;
        await ForgetAsync();
        return new(true);
    }

    public Task ForgetAsync()
    {
        SecureStorage.Default.Remove(EnabledUserKey);
        return Task.CompletedTask;
    }

    public async Task<DeviceUnlockResult> UnlockAsync()
    {
        await _promptLock.WaitAsync();
        try
        {
            if (!Supported || !Available)
            {
                return new(false, text["Security.DeviceUnavailable"]);
            }
#if ANDROID
            if (OperatingSystem.IsAndroidVersionAtLeast(24))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var verified = await MainThread.InvokeOnMainThreadAsync(() =>
                    AndroidDeviceAuthenticator.AuthenticateAsync(text["Security.DevicePrompt"],
                        text["Security.DeviceHelp"], timeout.Token));
                return verified
                    ? new(true) : new(false, text["Security.DeviceCancelled"]);
            }
#endif
            return new(false, text["Security.DeviceUnavailable"]);
        }
        catch (Exception)
        {
            // Never surface native exception text or stack traces in the sign-in screen.
            return new(false, text["Security.DeviceCancelled"]);
        }
        finally
        {
            _promptLock.Release();
        }
    }

    private static bool Supported => OperatingSystem.IsAndroidVersionAtLeast(24);

    private static bool Available
    {
        get
        {
#if ANDROID
            if (OperatingSystem.IsAndroidVersionAtLeast(24))
            {
                return Android.App.Application.Context.GetSystemService(Android.Content.Context.KeyguardService)
                    is Android.App.KeyguardManager { IsDeviceSecure: true };
            }
#endif
            return false;
        }
    }
}
