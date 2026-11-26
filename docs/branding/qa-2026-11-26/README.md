# Native branding verification — 2026-11-26

Source: `main`, commit `2c0d8f6acd3b7fab10f6613874d144d8618feb6e`. The working tree was clean before this verification. Both native targets were built from the current source, with zero warnings and errors. The Android install target also succeeded.

## Results

| Platform / check | Result | Runtime evidence |
|---|---|---|
| Android launcher icon and name | PASS | The Pixel launcher displays the indigo ledger and teal check within its white circular icon, with the label “Lekha Books”. No clipping or .NET template artwork is visible. |
| Android native cold-start splash | PASS | A recorded cold launch shows the full-color mark centered on white, followed by the normal fade into the app. |
| Android WebView startup branding | PASS | The native splash is followed by the smaller mark and “Lekha Books” loading wordmark. |
| Android sign-in logo | PASS | The sign-in card displays the full-color mark and a readable “Lekha Books” wordmark. |
| Windows process launch | PASS | The newly built unpackaged executable started, exposed a window titled “Lekha Books”, and remained responsive. |
| Windows sign-in logo | PASS | After the desktop was unlocked, the sign-in card visibly displayed the indigo ledger, teal check and readable “Lekha Books” wordmark. |
| Windows authenticated header logo | PASS | The running Windows app visibly displayed the full-color logo on its white tile beside “Lekha Books” in the indigo app bar. A second instance restored the existing session and displayed the same header. The saved crop excludes company and customer information. |
| Windows live shell/taskbar icon | BLOCKED | A live shell icon capture has not been obtained. Desktop access was intermittent, and a desktop capture attempt returned Windows access-denied error 5. Compiled icon files are not treated as a runtime pass. |
| Windows startup splash/loading sequence | BLOCKED | An isolated instance was launched, but the desktop recording captured the other active window rather than the new instance's startup, and other input interrupted the restart. A brief pause in interaction is needed to capture this transient sequence. |

## Environment and method

- Android AVD: `LekhaBook_Pixel`, Android 16 / API 36, x86_64, `emulator-5554`, 1080 × 2400 capture.
- App: `com.lekhabook.app`, version 0.1, version code 1. The existing install was updated without uninstalling or clearing its data.
- Windows: `net10.0-windows10.0.19041.0`, `win-x64`, `WindowsPackageType=None`.
- Both targets used Debug builds. The Windows executable was launched from the current build output.
- Android was launched from its visible launcher icon, then force-stopped and launched again to record an explicit `LaunchState: COLD`. The cold activity launch reported `TotalTime: 1188` ms; the recording shows the complete transition into the sign-in page.
- The reviewed recent Android logcat entries contained no matching fatal exception or fatal-signal lines. This is a limited startup check, not a general stability claim.
- Verification submitted no sign-in and changed no account, business or transaction data. When the Windows checks resumed, a session was already signed in, which allowed the authenticated header logo to be observed.
- No application code or artwork was changed during verification. The Windows app and Android emulator were left running.

## Captured evidence

The screenshots and recording are kept on the machine that ran the check and are not part of the repository. The Android images are actual emulator captures or unretouched frames extracted from the startup recording. The contact sheet only scales and arranges frames for comparison. The Windows header image is a crop of a live native-window capture to exclude company and customer information.

- Launcher icon and product name: `android-launcher.png`
- Native splash at approximately 0.9 s: `android-native-splash.png`
- WebView loading logo at approximately 2.1 s: `android-webview-loading.png`
- Rendered sign-in screen: `android-running.png`
- Startup contact sheet, four frames per second: `android-startup-sequence.png`
- Original 12-second cold-start recording: `android-cold-start.mp4`
- Windows authenticated header logo: `windows-header-logo.png`

## Remaining Windows checks

The sign-in and authenticated header logo checks are complete. With the desktop unlocked and a brief pause in other interaction, inspect the live shell/taskbar icon and restart the isolated instance to capture startup. Do not substitute compiled assets, a manifest, build success or an unsuccessful desktop recording for these remaining visual checks. A second Windows app instance was left open because it was receiving other interaction; no active window was forcibly closed.
