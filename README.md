# LekhaBook

Mobile-first bookkeeping for **party ledgers, bank accounts and petty cash**, with photo proof attached to entries.

- **Android, iOS and Windows app** (.NET MAUI Blazor Hybrid) that works offline on SQLite and syncs when online.
- **Web app** (Blazor, online-only in v1) with the same screens.
- **API** (ASP.NET Core) backed by PostgreSQL.

The build plan is in [docs/AGENT_PLAN.md](docs/AGENT_PLAN.md), decisions in [docs/DECISIONS.md](docs/DECISIONS.md), and the reference-app review in [docs/discovery/](docs/discovery/00-summary.md).

## Projects

| Project | What it is |
|---|---|
| `src/LekhaBook.UI` | Razor class library with **all** pages, layout and styles, shared by the app and the web |
| `src/LekhaBook.App` | .NET MAUI Blazor Hybrid host: Android, iOS, Windows |
| `src/LekhaBook.Web` | Blazor Web App host for browsers (interactive server) |
| `src/LekhaBook.Api` | ASP.NET Core API: auth, sync, attachments, reports |
| `src/LekhaBook.Domain` | Entities and business rules |
| `src/LekhaBook.Contracts` | DTOs shared by the API and clients |
| `src/LekhaBook.Application` | Use cases and validation |
| `src/LekhaBook.Data.Sqlite` | Device database (EF Core SQLite) and sync outbox |
| `src/LekhaBook.Data.Postgres` | Server database (EF Core Npgsql) and migrations |
| `tests/*` | Unit, sync-simulation and API integration tests |

The UI only talks to the device or server through interfaces in `LekhaBook.UI/Services` (`IPlatformInfo`, `IConnectivityService`, `ISyncService`, `ISessionService`, and more in later phases). Each host registers its own implementations.

## Prerequisites

- **.NET 10 SDK** (10.0.400 or later; see `global.json`)
- MAUI workloads: `dotnet workload install maui-android maui-windows` (and `maui-ios` on a Mac)
- **Android:** Android SDK + an emulator (Android Studio's Device Manager) or a USB-connected phone
- **iOS:** a Mac with Xcode and an Apple developer account. Pair it from Visual Studio, or build on the Mac
- **PostgreSQL** 16+: either a local install, or Docker Desktop with `docker compose up -d` (from phase 3)

## Open the project

### Visual Studio 2026 (v18)

1. Install the **.NET Multi-platform App UI** and **ASP.NET and web development** workloads.
2. Open `LekhaBook.sln`.
3. To run the web stack: right-click the solution → **Configure Startup Projects** → **Multiple startup projects** → start `LekhaBook.Api` and `LekhaBook.Web`.
4. To run the app: set `LekhaBook.App` as the startup project, pick **Windows Machine** or an Android emulator in the target dropdown, and press F5.

### VS Code

1. Open the folder and install the recommended extensions (C# Dev Kit, .NET MAUI).
2. **Run and Debug** → choose **API**, **Web**, or **API + Web**.
3. For the app, pick a device with the .NET MAUI status-bar item, then start debugging `LekhaBook.App`.

### Command line

```powershell
dotnet build LekhaBook.sln
dotnet test LekhaBook.sln
```

```powershell
dotnet run --project src/LekhaBook.Api --launch-profile http
```

```powershell
dotnet run --project src/LekhaBook.Web --launch-profile http
```

```powershell
dotnet build src/LekhaBook.App -t:Run -f net10.0-windows10.0.19041.0
```

```powershell
dotnet build src/LekhaBook.App -t:Run -f net10.0-android
```

| Host | URL |
|---|---|
| API | http://localhost:5050 (health: `/health`, OpenAPI: `/openapi/v1.json`) |
| Web | http://localhost:5121 |

**Android emulator:** the dev PC has an AVD named `LekhaBook_Pixel` (Pixel 7, Android 36, x86_64). Start it, then deploy:

```powershell
& "$env:LOCALAPPDATA\Android\Sdk\emulator\emulator.exe" -avd LekhaBook_Pixel
```

**iOS** is only part of the build on a Mac, or when you ask for it: `dotnet build src/LekhaBook.App -f net10.0-ios -p:IncludeIos=true` (needs a paired Mac).

`LekhaBook.Server.slnf` is a solution filter without the MAUI app, for machines or CI runners without the mobile workloads.
