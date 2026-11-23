<#
.SYNOPSIS
    One-time local PostgreSQL setup for Lekha Books development.

.DESCRIPTION
    1. Optionally creates the `lekhabook` login role (psql asks for the `postgres` password).
    2. Asks for the `lekhabook` password (hidden) and saves the connection string as a user secret
       shared by LekhaBook.Api and LekhaBook.Data.Tests. Nothing is written to the repository.

.EXAMPLE
    ./scripts/set-dev-database.ps1
    ./scripts/set-dev-database.ps1 -CreateRole
    ./scripts/set-dev-database.ps1 -Port 5433      # Docker compose database
#>
param(
    [switch] $CreateRole,
    [string] $DbHost = 'localhost',
    [int] $Port = 5432,
    [string] $Database = 'lekhabook',
    [string] $Username = 'lekhabook',
    [string] $Psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
)

$ErrorActionPreference = 'Stop'
$apiProject = Join-Path $PSScriptRoot '..\src\LekhaBook.Api\LekhaBook.Api.csproj' | Resolve-Path

$secure = Read-Host "Password for the '$Username' role" -AsSecureString
$password = [System.Net.NetworkCredential]::new('', $secure).Password
if ([string]::IsNullOrWhiteSpace($password)) { throw 'Password is empty.' }
if ($password.Contains("'") -or $password.Contains(';')) { throw "Please choose a password without ' or ; characters." }

if ($CreateRole) {
    Write-Host "Creating role '$Username' (psql will ask for the postgres password)..."
    & $Psql -h $DbHost -p $Port -U postgres -v ON_ERROR_STOP=1 -c "CREATE ROLE $Username LOGIN CREATEDB PASSWORD '$password';"
    if ($LASTEXITCODE -ne 0) { throw "psql failed (exit $LASTEXITCODE). If the role already exists, run again without -CreateRole." }
}

$connectionString = "Host=$DbHost;Port=$Port;Database=$Database;Username=$Username;Password=$password"
dotnet user-secrets set 'ConnectionStrings:LekhaBook' $connectionString --project $apiProject | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet user-secrets failed (exit $LASTEXITCODE)." }

$keys = dotnet user-secrets list --project $apiProject | ForEach-Object { ($_ -split ' = ')[0] }
if ($keys -contains 'ConnectionStrings:LekhaBook') {
    Write-Host 'Saved ConnectionStrings:LekhaBook to the lekhabook-dev user secrets.' -ForegroundColor Green
} else {
    throw 'The secret was not saved.'
}
