# Lekha Books

<img src="src/LekhaBook.UI/wwwroot/branding/lekha_books_logo.png" alt="Lekha Books" width="360" />

The shared [brand assets and usage notes](docs/branding/README.md) cover the web and native apps.

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
| `src/LekhaBook.Data` | EF Core model shared by both databases, plus balance queries |
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

## Local database (PostgreSQL)

Do this once per machine. Choose your own password for the `lekhabook` role. It stays in your local user-secrets store and never goes in the repo.

**Easiest:** run the setup script from the repo folder. It asks for the password with hidden input; add `-CreateRole` the first time to also create the role:

```powershell
./scripts/set-dev-database.ps1 -CreateRole
```

Or do the same steps by hand:

1. Create a login role that can create databases (the tests create and drop a fresh database each run). You'll be asked for the `postgres` password:

   ```powershell
   & "C:\Program Files\PostgreSQL\18\bin\psql.exe" -h localhost -U postgres -c "CREATE ROLE lekhabook LOGIN CREATEDB PASSWORD 'your-dev-password';"
   ```

2. Store the connection string as a user secret. `LekhaBook.Api` and `LekhaBook.Data.Tests` share it:

   ```powershell
   dotnet user-secrets set "ConnectionStrings:LekhaBook" "Host=localhost;Port=5432;Database=lekhabook;Username=lekhabook;Password=your-dev-password" --project src/LekhaBook.Api
   ```

3. Check it: `dotnet test tests/LekhaBook.Data.Tests` should report the PostgreSQL test as passed, not skipped. Running the API in Development creates the `lekhabook` database and applies migrations automatically.

To run only the PostgreSQL tests against some other server (for example a throwaway one), set `LEKHABOOK_TEST_PG` to a connection string whose user has CREATEDB. It takes priority over the user secret.

**Throwaway test server, no password needed.** This uses the installed PostgreSQL binaries and keeps its own data folder under `%LOCALAPPDATA%\LekhaBook\test-postgres`, listening on localhost:5440 only:

```powershell
./scripts/test-postgres.ps1 start
$env:LEKHABOOK_TEST_PG = 'Host=localhost;Port=5440;Database=postgres;Username=postgres'
dotnet test
./scripts/test-postgres.ps1 stop
```

Without either setting, the database tests show as **skipped** (not failed).

**The API needs a database to start.** Without `ConnectionStrings:LekhaBook` it stops with a message pointing here. To try it quickly against the throwaway test server (test data only), run `$env:ConnectionStrings__LekhaBook = 'Host=localhost;Port=5440;Database=lekhabook;Username=postgres'` in the same shell, then `dotnet run --project src/LekhaBook.Api`.

**API sign-in (development).** Register and sign in with `POST /api/auth/register` and `POST /api/auth/login` (email + password). Then send `Authorization: Bearer <accessToken>`. The full list is in the OpenAPI document at `/openapi/v1.json`.

With Docker instead, `docker compose up -d` starts PostgreSQL on port **5433** (user `lekhabook`, password `lekhabook_dev`). Use `Port=5433` in the connection string.

**Migrations** (run from the repo root after changing the model; `dotnet tool restore` installs `dotnet-ef`):

```powershell
dotnet ef migrations add <Name> --project src/LekhaBook.Data.Postgres --startup-project src/LekhaBook.Data.Postgres
dotnet ef migrations add <Name> --project src/LekhaBook.Data.Sqlite --startup-project src/LekhaBook.Data.Sqlite
```

Always add a migration to **both** projects. The app applies SQLite migrations on start.

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

**Signing in from the apps (development).** Both clients need the API running with the `http` profile on port 5050:
- **Web:** `Api:BaseUrl` (default `http://localhost:5050/`). Sign up at http://localhost:5121/signup.
- **App:** Debug builds call `http://10.0.2.2:5050/` on the Android emulator (the host PC) and `http://localhost:5050/` on Windows. Plain HTTP is allowed only to those hosts (`Platforms/Android/Resources/xml/network_security_config.xml`).
- Put any test accounts you create in `.dev/test-accounts.md`, which is gitignored.

**Android emulator:** the dev PC has an AVD named `LekhaBook_Pixel` (Pixel 7, Android 36, x86_64). Start it, then deploy:

```powershell
& "$env:LOCALAPPDATA\Android\Sdk\emulator\emulator.exe" -avd LekhaBook_Pixel
```

**Shared report links** (D24). "Share" on a party or account statement makes a public link whose address comes from `PublicSite:BaseUrl` on the API. There is no default: set it to the address where the web app is deployed, or no links can be made. To try them locally, start the API from a shell with:

```powershell
$env:PublicSite__BaseUrl = 'http://localhost:5121'
```

**iOS** is only part of the build on a Mac, or when you ask for it: `dotnet build src/LekhaBook.App -f net10.0-ios -p:IncludeIos=true` (needs a paired Mac).

`LekhaBook.Server.slnf` is a solution filter without the MAUI app, for machines or CI runners without the mobile workloads.
