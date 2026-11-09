#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Updates an installed Simplifiarr service to the latest code in this folder.

.DESCRIPTION
    Run from an admin PowerShell, any folder:
        & .\scripts\update-service.ps1

    Looks up where the service is installed and which port it uses, then:
    runs the tests, stops the service, publishes the new build over the old
    one, starts it again and waits for it to answer.

    If the tests fail, nothing is changed and the old version keeps running.
    The service's settings (port, data folder) and your data are left alone.

.PARAMETER ServiceName
    Which service to update.

.PARAMETER SkipTests
    Publish without running the tests first.
#>
param(
    [string]$ServiceName = 'Simplifiarr',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"

if (-not $service) {
    throw "There is no service named '$ServiceName'. Install it first with scripts\install-service.ps1."
}

# PathName looks like:  "C:\Services\X\Simplifiarr.Web.exe" --Kestrel:Endpoints:Http:Url=http://0.0.0.0:6482 ...
if ($service.PathName -match '^\s*"([^"]+)"') {
    $exe = $Matches[1]
} else {
    $exe = ($service.PathName -split '\s+--')[0].Trim()
}

$port = 6481
if ($service.PathName -match 'Endpoints:Http:Url=\S*?:(\d+)') {
    $port = [int]$Matches[1]
}

& (Join-Path $PSScriptRoot 'install-service.ps1') `
    -ServiceName $ServiceName `
    -InstallDir (Split-Path $exe -Parent) `
    -Port $port `
    -SkipTests:$SkipTests
