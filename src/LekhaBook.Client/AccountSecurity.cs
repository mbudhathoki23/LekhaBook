using LekhaBook.Contracts;
using LekhaBook.UI.Services;

namespace LekhaBook.Client;

public sealed class AccountSecurity(LekhaBookApi api) : IAccountSecurity
{
    public Task<ApiResult<TwoFactorState>> GetTwoFactorAsync() =>
        api.GetAsync<TwoFactorState>("/api/account/security/two-factor");

    public Task<ApiResult<TwoFactorState>> ConfigureTwoFactorAsync(TwoFactorChange change) =>
        api.SendAsync<TwoFactorState>(HttpMethod.Post, "/api/auth/manage/2fa", change);
}
