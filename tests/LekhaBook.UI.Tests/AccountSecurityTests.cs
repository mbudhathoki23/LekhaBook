using Bunit;
using LekhaBook.Contracts;
using LekhaBook.UI;
using LekhaBook.UI.Pages.Account;
using LekhaBook.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LekhaBook.UI.Tests;

public sealed class AccountSecurityTests : TestContext
{
    private readonly Session _session = new();
    private readonly SecurityApi _security = new();

    public AccountSecurityTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLekhaBookUI();
        Services.AddSingleton<ISessionService>(_session);
        Services.AddSingleton<IDeviceUnlock, UnsupportedDeviceUnlock>();
        Services.AddSingleton<IAccountSecurity>(_security);
    }

    [Fact]
    public void Password_sign_in_shows_a_separate_code_step_and_supports_recovery_codes()
    {
        var page = RenderComponent<SignIn>();
        page.Find("input[type=email]").Input("user@example.test");
        page.Find("input[type=password]").Input("test-password");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("Verify your sign-in", page.Markup));
        Assert.Empty(page.FindAll("input[type=password]"));
        page.FindAll("button").Single(b => b.TextContent.Contains("Use a recovery code", StringComparison.Ordinal)).Click();
        page.Find("input").Input("abcde-fghij");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Equal("abcde-fghij", _session.Recovery));
        Assert.Null(_session.Authenticator);
    }

    [Fact]
    public void Authenticator_setup_generates_a_local_qr_and_hides_codes_after_acknowledgement()
    {
        var page = RenderComponent<Security>();
        page.WaitForAssertion(() => Assert.Contains("Set up an authenticator app", page.Markup));
        Assert.Empty(page.FindAll("img"));
        page.FindAll("button").Single(b => b.TextContent.Contains("Set up an authenticator app", StringComparison.Ordinal)).Click();
        page.WaitForAssertion(() => Assert.StartsWith("data:image/png;base64,", page.Find("img").GetAttribute("src")));
        page.Find("input").Input("123456");
        page.FindAll("button").Single(b => b.TextContent.Contains("Verify and continue", StringComparison.Ordinal)).Click();
        page.WaitForAssertion(() => Assert.Contains("abcde-fghij", page.Markup));
        Assert.Empty(page.FindAll("img"));
        page.FindAll("button").Single(b => b.TextContent.Contains("I have saved these codes", StringComparison.Ordinal)).Click();
        Assert.DoesNotContain("abcde-fghij", page.Markup);
        Assert.DoesNotContain("JBSWY3DPEHPK3PXP", page.Markup);
    }

    private sealed class SecurityApi : IAccountSecurity
    {
        public Task<ApiResult<TwoFactorState>> GetTwoFactorAsync() => Task.FromResult(
            new ApiResult<TwoFactorState>(new(string.Empty, 0, null, false, false), null, 200));

        public Task<ApiResult<TwoFactorState>> ConfigureTwoFactorAsync(TwoFactorChange change) => Task.FromResult(
            new ApiResult<TwoFactorState>(new(change.Enable == true ? "" : "JBSWY3DPEHPK3PXP", change.Enable == true ? 1 : 0,
                change.Enable == true ? ["abcde-fghij"] : null, change.Enable == true, false), null, 200));
    }

    private sealed class Session : ISessionService
    {
        public bool IsInitialized => true;
        public bool IsSignedIn => false;
        public bool IsOffline => false;
        public MeResponse? Me { get; } = new(Guid.CreateVersion7(), "user@example.test", "Test", []);
        public CompanySummary? CurrentCompany => null;
        public string? UserName => "Test";
        public string? CompanyName => null;
        public string? Recovery { get; private set; }
        public string? Authenticator { get; private set; }
        public event EventHandler? Changed { add { } remove { } }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<SignInOutcome> SignInAsync(string email, string password, string? returnUrl, string? twoFactorCode = null, string? recoveryCode = null)
        {
            Authenticator = twoFactorCode;
            Recovery = recoveryCode;
            return Task.FromResult(twoFactorCode is null && recoveryCode is null ? new SignInOutcome(null, RequiresTwoFactor: true) : new SignInOutcome(null));
        }
        public Task<SignInOutcome> RegisterAsync(string displayName, string email, string password, string? returnUrl) => throw new NotSupportedException();
        public Task SignOutAsync() => Task.CompletedTask;
        public Task SelectCompanyAsync(Guid companyId) => Task.CompletedTask;
        public Task RefreshAsync() => Task.CompletedTask;
        public bool Can(string permission) => false;
    }
}
