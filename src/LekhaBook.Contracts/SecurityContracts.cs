namespace LekhaBook.Contracts;

/// <summary>Matches ASP.NET Core Identity's authenticator management endpoint.</summary>
public sealed record TwoFactorChange(
    bool? Enable = null,
    string? TwoFactorCode = null,
    bool ResetRecoveryCodes = false,
    bool ResetSharedKey = false);

public sealed record TwoFactorState(
    string SharedKey,
    int RecoveryCodesLeft,
    string[]? RecoveryCodes,
    bool IsTwoFactorEnabled,
    bool IsMachineRemembered);
