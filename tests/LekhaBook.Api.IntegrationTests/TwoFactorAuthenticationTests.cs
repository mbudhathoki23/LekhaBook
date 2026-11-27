using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using LekhaBook.Contracts;
using LekhaBook.Testing;

namespace LekhaBook.Api.IntegrationTests;

public sealed class TwoFactorAuthenticationTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [PostgresFact]
    public async Task Enrollment_requires_a_valid_code_and_then_password_sign_in_requires_two_factor()
    {
        var user = await api.NewUserAsync();
        var setup = await ConfigureAsync(user, new());
        Assert.False(setup.IsTwoFactorEnabled);
        Assert.NotEmpty(setup.SharedKey);

        var invalid = await user.Http.PostAsJsonAsync("/api/auth/manage/2fa", new TwoFactorChange(Enable: true, TwoFactorCode: WrongCode(setup.SharedKey)));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var enabled = await ConfigureAsync(user, new(Enable: true, TwoFactorCode: Code(setup.SharedKey)));
        Assert.True(enabled.IsTwoFactorEnabled);
        Assert.Empty(enabled.SharedKey);
        Assert.Equal(10, enabled.RecoveryCodes!.Length);

        using var anonymous = api.Factory.CreateClient(new() { HandleCookies = false });
        var passwordOnly = await anonymous.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "Passw0rd-123" });
        Assert.Equal(HttpStatusCode.Unauthorized, passwordOnly.StatusCode);
        Assert.Contains("RequiresTwoFactor", await passwordOnly.Content.ReadAsStringAsync());

        var wrong = await anonymous.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "Passw0rd-123", twoFactorCode = WrongCode(setup.SharedKey) });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var login = await anonymous.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "Passw0rd-123", twoFactorCode = Code(setup.SharedKey) });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.NotEmpty((await login.Content.ReadFromJsonAsync<ApiFixture.TokenResponse>())!.AccessToken);
        Assert.True(login.Headers.CacheControl?.NoStore);
    }

    [PostgresFact]
    public async Task Recovery_codes_are_single_use_and_regeneration_replaces_the_previous_set()
    {
        var user = await api.NewUserAsync();
        var setup = await ConfigureAsync(user, new());
        var enabled = await ConfigureAsync(user, new(Enable: true, TwoFactorCode: Code(setup.SharedKey)));
        var oldCodes = enabled.RecoveryCodes!;
        using var anonymous = api.Factory.CreateClient(new() { HandleCookies = false });
        var payload = new { email = user.Email, password = "Passw0rd-123", twoFactorRecoveryCode = oldCodes[0] };
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/auth/login", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login", payload)).StatusCode);

        var reset = await ConfigureAsync(user, new(TwoFactorCode: Code(setup.SharedKey), ResetRecoveryCodes: true));
        Assert.Equal(10, reset.RecoveryCodes!.Length);
        Assert.Empty(reset.SharedKey);
        Assert.Empty(oldCodes.Intersect(reset.RecoveryCodes));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login",
            new { email = user.Email, password = "Passw0rd-123", twoFactorRecoveryCode = oldCodes[1] })).StatusCode);
    }

    [PostgresFact]
    public async Task An_access_token_alone_cannot_read_the_enrolled_secret_disable_two_factor_or_reset_recovery()
    {
        var user = await api.NewUserAsync();
        var setup = await ConfigureAsync(user, new());
        var enabled = await ConfigureAsync(user, new(Enable: true, TwoFactorCode: Code(setup.SharedKey)));
        var state = await ConfigureAsync(user, new());
        Assert.Empty(state.SharedKey);
        Assert.Null(state.RecoveryCodes);

        foreach (var change in new[] { new TwoFactorChange(Enable: false), new TwoFactorChange(ResetSharedKey: true), new TwoFactorChange(ResetRecoveryCodes: true) })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await user.Http.PostAsJsonAsync("/api/auth/manage/2fa", change)).StatusCode);
        }

        var disabled = await ConfigureAsync(user, new(Enable: false, TwoFactorCode: enabled.RecoveryCodes![0]));
        Assert.False(disabled.IsTwoFactorEnabled);
    }

    [PostgresFact]
    public async Task Authenticator_management_rejects_anonymous_requests()
    {
        using var anonymous = api.Factory.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/auth/manage/2fa", new TwoFactorChange());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/account/security/two-factor")).StatusCode);
    }

    [PostgresFact]
    public async Task Reading_security_settings_does_not_create_a_key_or_invalidate_the_saved_refresh_session()
    {
        var user = await api.NewUserAsync();
        var response = await user.Http.GetAsync("/api/account/security/two-factor");
        response.EnsureSuccessStatusCode();
        var state = await response.Content.ReadFromJsonAsync<TwoFactorState>();
        Assert.Empty(state!.SharedKey);
        Assert.False(state.IsTwoFactorEnabled);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var anonymous = api.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = user.Token.RefreshToken })).StatusCode);
    }

    private static async Task<TwoFactorState> ConfigureAsync(ApiFixture.UserClient user, TwoFactorChange change)
    {
        var response = await user.Http.PostAsJsonAsync("/api/auth/manage/2fa", change);
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<TwoFactorState>())!;
    }

    // Independent RFC 6238 test client. Production code uses ASP.NET Core Identity's TOTP provider.
    private static string Code(string key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var c in key.ToUpperInvariant().TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)(buffer >> bits)); }
        }
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(bytes.ToArray(), counter);
        var offset = hash[^1] & 15;
        var number = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff;
        return (number % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static string WrongCode(string key) => ((int.Parse(Code(key), CultureInfo.InvariantCulture) + 1) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
}
