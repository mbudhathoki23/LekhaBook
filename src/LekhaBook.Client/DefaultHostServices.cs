using LekhaBook.Contracts;
using LekhaBook.UI.Services;

namespace LekhaBook.Client;

/// <summary>Default upgrade service on hosts without Offline Local Mode (e.g. Web).</summary>
public sealed class UnsupportedOfflineUpgradeService : IOfflineUpgradeService
{
    public bool IsAvailable => false;

    public Task<OfflineLocalSummary> GetLocalSummaryAsync(CancellationToken ct = default) =>
        Task.FromResult(new OfflineLocalSummary(0, 0, 0, 0, 0));

    public Task<UpgradeResult> UpgradeWithNewAccountAsync(
        string displayName, string email, string password, Action<string>? onProgress = null, CancellationToken ct = default) =>
        Task.FromResult(new UpgradeResult(false, "Offline upgrade is only available in the installed app."));

    public Task<UpgradeResult> UpgradeWithExistingAccountAsync(
        string email, string password, string? twoFactorCode = null, string? recoveryCode = null,
        Action<string>? onProgress = null, CancellationToken ct = default) =>
        Task.FromResult(new UpgradeResult(false, "Offline upgrade is only available in the installed app."));
}

/// <summary>Default backup service on hosts without a local device database.</summary>
public sealed class UnsupportedBackupService : IBackupService
{
    public bool IsAvailable => false;

    public Task<ApiResult<string>> ExportCompanyJsonAsync(Guid companyId, CancellationToken ct = default) =>
        Task.FromResult(new ApiResult<string>(null, "Local backup is only available in the installed app.", 0));

    public Task<ApiResult<CompanySummary>> ImportCompanyJsonAsync(string json, CancellationToken ct = default) =>
        Task.FromResult(new ApiResult<CompanySummary>(null, "Local restore is only available in the installed app.", 0));
}

/// <summary>Light/dark theme preference persisted in host local settings.</summary>
public sealed class LocalThemeService(ILocalSettings settings) : IThemeService
{
    private const string ThemeKey = "ui.dark_mode";
    private bool _initialized;

    public bool IsDarkMode { get; private set; }

    public event EventHandler? Changed;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        var saved = await settings.GetAsync(ThemeKey);
        var dark = saved == "1";
        if (dark != IsDarkMode)
        {
            IsDarkMode = dark;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task SetDarkModeAsync(bool dark)
    {
        _initialized = true;
        IsDarkMode = dark;
        await settings.SetAsync(ThemeKey, dark ? "1" : "0");
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
