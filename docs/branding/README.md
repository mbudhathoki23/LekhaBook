# Lekha Books brand assets

<img src="../../src/LekhaBook.UI/wwwroot/branding/lekha_books_logo.png" alt="Lekha Books" width="480" />

Original ledger-and-check artwork created on 2026-11-24. The design uses the existing indigo (`#3949AB`) and teal (`#00897B`) palette. [The complete design prompts](PROMPTS.md) are retained for future revisions.

## Delivered files

All assets live in `src/LekhaBook.UI/wwwroot/branding/` and ship through the shared Razor class library to both hosts.

| File | Purpose |
|---|---|
| `lekha_books_mark.png` | Transparent 1254 × 1254 master symbol; source for native launcher icons and splash screens. |
| `lekha_books_logo.png` | Transparent 2172 × 724 horizontal logo for light backgrounds, documentation and sharing. |
| `lekha_books_mark_small.png` | Transparent 160 × 160 symbol used in the shared app header; exported by MAUI to keep the browser download small. |
| `lekha_books_icon.png` | 48 × 48 icon preview on an opaque white tile. |
| `favicon.ico` | Browser tab icon exported by the native Windows build. |
| `apple_touch_icon.png` | 256 × 256 white-backed touch icon exported by the native Windows build. |

## Integration

- `BrandLogo.razor` provides the shared home link, full-color symbol on a white tile, and readable text wordmark. The accessible name is “Lekha Books home”; the image is decorative to avoid repeating the name.
- The web host references the shared favicon and touch icon through its static-asset map.
- The native host uses the same master PNG as its MAUI icon foreground and splash image, with an opaque white background. Android adaptive foreground scaling is 0.75 to keep the symbol within launcher masks. MAUI also produces the Windows icon and the iOS resources when built on a supported Mac.
- The native WebView shows a matching logo and product name during startup. Browser tab titles and the native application/window title use “Lekha Books”.
- Technical project names, namespaces and `com.lekhabook.app` remain stable.

Use the primary artwork on white or very light backgrounds; retain its aspect ratio and transparent margins. The app header provides its own white backing for contrast on the indigo toolbar. The horizontal artwork is a PNG, not an editable vector source.

## Updating the exported browser icons

After replacing the master, build the Windows and Android app targets. Refresh the checked-in browser derivatives from MAUI's outputs, then rebuild the web app:

```powershell
dotnet build src/LekhaBook.App -f net10.0-windows10.0.19041.0
dotnet build src/LekhaBook.App -f net10.0-android
$brandWindowsOutput = 'src/LekhaBook.App/obj/Debug/net10.0-windows10.0.19041.0/win-x64/resizetizer/r'
$brandAssetFolder = 'src/LekhaBook.UI/wwwroot/branding'
Copy-Item "$brandWindowsOutput/appicon.ico" "$brandAssetFolder/favicon.ico"
Copy-Item "$brandWindowsOutput/appiconLogo.targetsize-256.png" "$brandAssetFolder/apple_touch_icon.png"
Copy-Item "$brandWindowsOutput/appiconLogo.targetsize-48.png" "$brandAssetFolder/lekha_books_icon.png"
Copy-Item 'src/LekhaBook.App/obj/Debug/net10.0-android/resizetizer/sp/drawable-mdpi/lekha_books_mark.png' "$brandAssetFolder/lekha_books_mark_small.png"
dotnet build src/LekhaBook.Web
```

The native settings follow Microsoft's [MAUI app-icon documentation](https://learn.microsoft.com/en-us/dotnet/maui/user-interface/images/app-icons?view=net-maui-10.0) and [splash-screen documentation](https://learn.microsoft.com/en-us/dotnet/maui/user-interface/images/splashscreen?view=net-maui-10.0).

## Verification

Web, Windows and Android debug builds passed with zero warnings and errors. Browser checks cover desktop and phone widths, loaded logo assets, favicon/touch-icon HTTP responses, and the accessible home link. Generated native icons and the Windows manifest were inspected.

The [2026-11-26 native runtime check](qa-2026-11-26/README.md) verified the Android emulator's launcher icon, native splash, WebView loading logo and sign-in logo. Windows sign-in and authenticated header logos were also visually verified after the desktop was unlocked. The Windows live shell/taskbar icon and startup sequence remain pending because desktop capture was intermittent and other input interrupted the isolated restart. Physical Android devices and iOS compilation have not been verified.
