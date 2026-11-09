#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or updates Simplifiarr as a Windows service.

.DESCRIPTION
    Run from an admin PowerShell, any folder:
        & C:\Projects\Simplifiarr\scripts\install-service.ps1

    - Stops the Simplifiarr service (if it's running).
    - Publishes the latest code to C:\Services\Simplifiarr.
    - First run only: creates the "Simplifiarr" service (starts with Windows,
      restarts itself if it crashes). An existing service's settings are left alone.
    - Starts the service and waits for it to answer on port 6481.
    Your data in C:\ProgramData\Simplifiarr is never touched.
    (There's no test project in this repo yet, so there's no test step.)
#>
param(
    [string]$InstallDir = 'C:\Services\Simplifiarr',
    [int]$Port = 6481
)

$ErrorActionPreference = 'Stop'
$serviceName = 'Simplifiarr'
$repoRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repoRoot 'Simplifiarr.Web\Simplifiarr.Web.csproj'
$exe = Join-Path $InstallDir 'Simplifiarr.Web.exe'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

function Stop-SimplifiarrProcess {
    # The service can report "Stopped" while the process is still alive (for
    # example while Windows Error Reporting handles a crash), keeping the
    # install folder locked. Make sure it is really gone.
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name 'Simplifiarr.Web' -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 500
    }
    $left = Get-Process -Name 'Simplifiarr.Web' -ErrorAction SilentlyContinue
    if ($left) {
        Write-Host "  Process still running - ending it." -ForegroundColor Yellow
        $left | Stop-Process -Force -ErrorAction SilentlyContinue
        $left | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    }
}

if ($existing -and $existing.Status -ne 'Stopped') {
    Step "Stopping the $serviceName service"
    try {
        Stop-Service -Name $serviceName -ErrorAction Stop
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(45))
    } catch {
        Write-Host "  Service did not stop cleanly ($($_.Exception.Message)) - forcing it." -ForegroundColor Yellow
    }
}
Stop-SimplifiarrProcess

Step "Publishing to $InstallDir"
$published = $false
foreach ($attempt in 1..3) {
    dotnet publish $project -c Release -o $InstallDir --nologo --verbosity quiet
    if ($LASTEXITCODE -eq 0) { $published = $true; break }
    Write-Host "  Publish attempt $attempt failed - retrying in 5s (files may still be locked)." -ForegroundColor Yellow
    Stop-SimplifiarrProcess
    Start-Sleep -Seconds 5
}
if (-not $published) {
    throw "Publish failed after 3 attempts. (The old version, if any, is stopped; start it with: Start-Service $serviceName)"
}

if (-not $existing) {
    Step "Creating the $serviceName service"
    New-Service -Name $serviceName `
        -BinaryPathName "`"$exe`"" `
        -DisplayName 'Simplifiarr' `
        -Description "Simplifiarr media library tools (http://localhost:$Port)" `
        -StartupType Automatic | Out-Null

}

# If it ever crashes, restart it after 1 minute (forget past failures after a day).
# Applied on every run so an existing service gets it too.
sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

Step "Starting the $serviceName service"
try {
    Start-Service -Name $serviceName -ErrorAction Stop
} catch {
    Write-Host "  First start failed ($($_.Exception.Message)) - trying once more." -ForegroundColor Yellow
    Start-Sleep -Seconds 5
    Start-Service -Name $serviceName
}

# Wait for it to answer - the first start after a publish can be slow
# (antivirus scans the new files), so give it up to five minutes.
#
# Check that the port accepts connections rather than loading the home page:
# the home page takes more than 2 seconds to render, so a page request with a
# short timeout never succeeded and the script sat here silently.
$ok = $false
$waited = [Diagnostics.Stopwatch]::StartNew()
Write-Host "Waiting for port $Port " -NoNewline
foreach ($i in 1..300) {
    Start-Sleep -Seconds 1
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $connect = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
        if ($connect.AsyncWaitHandle.WaitOne(1000) -and $client.Connected) {
            $ok = $true
        }
    } catch { }
    finally { $client.Close() }

    if ($ok) { break }
    if ($i % 5 -eq 0) { Write-Host '.' -NoNewline }
}
Write-Host ''

if ($ok) {
    Write-Host "`nSimplifiarr is running at http://localhost:$Port (answered after $([int]$waited.Elapsed.TotalSeconds)s)" -ForegroundColor Green
} else {
    Write-Host "`nThe service started but isn't answering on port $Port yet." -ForegroundColor Yellow
    Write-Host "Check: Get-Service $serviceName, and the Windows Event Viewer (Application log)."
}
