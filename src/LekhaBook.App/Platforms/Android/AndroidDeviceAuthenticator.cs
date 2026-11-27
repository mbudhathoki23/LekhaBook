using AndroidX.Biometric;
using AndroidX.Core.Content;

namespace LekhaBook.App.Services;

internal static class AndroidDeviceAuthenticator
{
    public static async Task<bool> AuthenticateAsync(string subtitle, string description, CancellationToken token)
    {
        if (Platform.CurrentActivity is not AndroidX.Fragment.App.FragmentActivity activity || token.IsCancellationRequested)
        {
            return false;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callback = new Callback(completion);
        using var prompt = new BiometricPrompt(activity, ContextCompat.GetMainExecutor(activity)!, callback);
        // Android 7–10 needs WEAK | DEVICE_CREDENTIAL; Android 11+ also supports STRONG with that fallback.
        var strength = OperatingSystem.IsAndroidVersionAtLeast(30)
            ? BiometricManager.Authenticators.BiometricStrong : BiometricManager.Authenticators.BiometricWeak;
        using var info = new BiometricPrompt.PromptInfo.Builder()
            .SetTitle("Lekha Books")!
            .SetSubtitle(subtitle)!
            .SetDescription(description)!
            .SetAllowedAuthenticators(strength | BiometricManager.Authenticators.DeviceCredential)!
            .Build()!;

        using var cancellation = token.Register(() => MainThread.BeginInvokeOnMainThread(() =>
        {
            // Success may already have completed and disposed the native prompt before this queued callback runs.
            if (completion.Task.IsCompleted) return;
            prompt.CancelAuthentication();
            completion.TrySetResult(false);
        }));
        prompt.Authenticate(info);
        return await completion.Task;
    }

    private sealed class Callback(TaskCompletionSource<bool> completion) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result) => completion.TrySetResult(true);

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence? errString) => completion.TrySetResult(false);

        // A failed scan is not a success or a cancellation: the system keeps the prompt open for retry.
    }
}
