using LekhaBook.Contracts;

namespace LekhaBook.UI.Services;

public interface IAccountSecurity
{
    Task<ApiResult<TwoFactorState>> GetTwoFactorAsync();
    Task<ApiResult<TwoFactorState>> ConfigureTwoFactorAsync(TwoFactorChange change);
}

public sealed record DeviceUnlockState(bool Supported, bool Available, bool Enabled);

public sealed record DeviceUnlockResult(bool Succeeded, string? Error = null);

/// <summary>Native device authentication; never receives or stores the phone's PIN/password.</summary>
public interface IDeviceUnlock
{
    Task<DeviceUnlockState> GetStateAsync();
    Task<DeviceUnlockResult> UnlockAsync();
    Task<DeviceUnlockResult> EnableAsync(Guid userId);
    Task<DeviceUnlockResult> DisableAsync();
    Task<bool> IsEnabledForAsync(Guid userId);
    Task ForgetAsync();
}

public sealed class UnsupportedDeviceUnlock : IDeviceUnlock
{
    public Task<DeviceUnlockState> GetStateAsync() => Task.FromResult(new DeviceUnlockState(false, false, false));
    public Task<DeviceUnlockResult> UnlockAsync() => Task.FromResult(new DeviceUnlockResult(false));
    public Task<DeviceUnlockResult> EnableAsync(Guid userId) => Task.FromResult(new DeviceUnlockResult(false));
    public Task<DeviceUnlockResult> DisableAsync() => Task.FromResult(new DeviceUnlockResult(false));
    public Task<bool> IsEnabledForAsync(Guid userId) => Task.FromResult(false);
    public Task ForgetAsync() => Task.CompletedTask;
}
