using LekhaBook.Data.Postgres;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;

namespace LekhaBook.Api.Security;

/// <summary>Protect authenticator management in addition to Identity's ordinary bearer authorization.</summary>
public sealed class IdentitySecurityFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        var change = context.Arguments.OfType<TwoFactorRequest>().SingleOrDefault();
        if (change is null)
        {
            return await next(context);
        }

        var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<AppUser>>();
        var user = await users.GetUserAsync(context.HttpContext.User);
        if (user is null) return await next(context);

        var enabled = await users.GetTwoFactorEnabledAsync(user);
        var destructive = change.Enable == false || change.ResetRecoveryCodes || change.ResetSharedKey;
        if (enabled && destructive)
        {
            if (await users.IsLockedOutAsync(user))
            {
                return Results.Problem("Too many verification attempts. Please try again later.", statusCode: 429);
            }

            var proof = (change.TwoFactorCode ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal);
            var verified = proof.Length > 0 && await users.VerifyTwoFactorTokenAsync(
                user, users.Options.Tokens.AuthenticatorTokenProvider, proof.Replace("-", string.Empty, StringComparison.Ordinal));
            if (!verified && proof.Length > 0)
            {
                verified = (await users.RedeemTwoFactorRecoveryCodeAsync(user, proof)).Succeeded;
            }

            if (!verified)
            {
                await users.AccessFailedAsync(user);
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["TwoFactorCode"] = ["Enter a valid authenticator code or an unused recovery code."],
                });
            }

            await users.ResetAccessFailedCountAsync(user);
        }

        var result = await next(context);
        var response = result;
        while (response is INestedHttpResult nested) response = nested.Result;
        // A stolen access token must not be enough to retrieve an already enrolled TOTP secret.
        if (response is IValueHttpResult<TwoFactorResponse> { Value: { IsTwoFactorEnabled: true } state })
        {
            return TypedResults.Ok(new TwoFactorResponse
            {
                SharedKey = string.Empty,
                RecoveryCodes = state.RecoveryCodes,
                RecoveryCodesLeft = state.RecoveryCodesLeft,
                IsTwoFactorEnabled = true,
                IsMachineRemembered = state.IsMachineRemembered,
            });
        }
        return result;
    }
}
