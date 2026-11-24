<#
.SYNOPSIS
    Start or stop a throwaway PostgreSQL server for running the database tests locally.

.DESCRIPTION
    Creates a private PostgreSQL cluster under %LOCALAPPDATA%\LekhaBook\test-postgres using the locally
    installed PostgreSQL binaries. It listens only on localhost:5440 and uses "trust" auth (no password),
    so it must only ever hold test data. Your installed PostgreSQL service is not touched.

    After "start", run the tests with:
        $env:LEKHABOOK_TEST_PG = 'Host=localhost;Port=5440;Database=postgres;Username=postgres'
        dotnet test

.EXAMPLE
    ./scripts/test-postgres.ps1 start
    ./scripts/test-postgres.ps1 stop
    ./scripts/test-postgres.ps1 stop -Delete
#>
param(
    [Parameter(Mandatory)][ValidateSet('start', 'stop')] [string] $Action,
    [int] $Port = 5440,
    [string] $PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [switch] $Delete
)

$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'LekhaBook\test-postgres'
$data = Join-Path $root 'data'
$log = Join-Path $root 'server.log'
$pgCtl = Join-Path $PgBin 'pg_ctl.exe'

if ($Action -eq 'start') {
    if (-not (Test-Path (Join-Path $data 'PG_VERSION'))) {
        New-Item -ItemType Directory -Force $root | Out-Null
        & (Join-Path $PgBin 'initdb.exe') -D $data -U postgres -A trust -E UTF8 --no-instructions | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "initdb failed ($LASTEXITCODE)." }
    }

    & $pgCtl -D $data status *> $null
    if ($LASTEXITCODE -ne 0) {
        # Start detached with output going to the log, so this script doesn't hang on the server's pipes.
        Start-Process -FilePath $pgCtl -WindowStyle Hidden -ArgumentList @(
            '-D', "`"$data`"", '-l', "`"$log`"", '-o', "`"-p $Port -c listen_addresses=localhost`"", 'start')
    }

    $ready = Join-Path $PgBin 'pg_isready.exe'
    foreach ($i in 1..30) {
        & $ready -h localhost -p $Port -q
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Test PostgreSQL is running on localhost:$Port." -ForegroundColor Green
            Write-Host "`$env:LEKHABOOK_TEST_PG = 'Host=localhost;Port=$Port;Database=postgres;Username=postgres'"
            return
        }
        Start-Sleep -Milliseconds 500
    }
    throw "PostgreSQL did not start; see $log"
}

& $pgCtl -D $data status *> $null
if ($LASTEXITCODE -eq 0) {
    & $pgCtl -D $data -m fast -w stop | Out-Null
    Write-Host 'Test PostgreSQL stopped.'
}
if ($Delete -and (Test-Path $root)) {
    Remove-Item -Recurse -Force $root
    Write-Host "Deleted $root."
}
