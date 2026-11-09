#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs Simplifiarr as a Windows service, or updates one that is already installed.

.DESCRIPTION
    Run from an admin PowerShell, any folder:
        & .\scripts\install-service.ps1

    - Runs the tests first. If any fail, nothing is touched.
    - Stops the service (if it's running).
    - Publishes the latest code to the install folder.
    - First run only: creates the service (starts with Windows, restarts
      itself if it crashes). An existing service's settings are left alone.
    - Starts the service and waits for it to answer on its port.

    Your data folder is never touched.

.PARAMETER ServiceName
    Windows service name. Use a different name to run a second copy side by side.

.PARAMETER InstallDir
    Where the program files go. Defaults to C:\Services\<ServiceName>.

.PARAMETER Port
    Port the web page is served on.

.PARAMETER DataPath
    Where this copy keeps settings, history, caches and logs. Leave blank for
    the default (%ProgramData%\Simplifiarr). A second copy MUST get its own
    folder, or the two will overwrite each other's files.

.PARAMETER SkipTests
    Publish without running the tests first.
#>
param(
    [string]$ServiceName = 'Simplifiarr',
    [string]$InstallDir = '',
    [int]$Port = 6481,
    [string]$DataPath = '',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

if (-not $InstallDir) { $InstallDir = "C:\Services\$ServiceName" }

$repoRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repoRoot 'Simplifiarr.Web\Simplifiarr.Web.csproj'
$tests = Join-Path $repoRoot 'Simplifiarr.Tests\Simplifiarr.Tests.csproj'
$exe = Join-Path $InstallDir 'Simplifiarr.Web.exe'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

function Get-ThisInstallProcess {
    # Match on the full path, not just the process name: another copy of
    # Simplifiarr installed in a different folder must be left alone.
    Get-Process -Name 'Simplifiarr.Web' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ($_.Path -ieq $exe) }
}

function Stop-ThisInstallProcess {
    # The service can report "Stopped" while the process is still alive (for
    # example while Windows Error Reporting handles a crash), keeping the
    # install folder locked. Make sure it is really gone.
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and (Get-ThisInstallProcess)) {
        Start-Sleep -Milliseconds 500
    }
    $left = Get-ThisInstallProcess
    if ($left) {
        Write-Host "  Process still running - ending it." -ForegroundColor Yellow
        $left | Stop-Process -Force -ErrorAction SilentlyContinue
        $left | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    }
}

if (-not $SkipTests) {
    Step "Running tests"
    dotnet test $tests --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed - nothing was changed and the service is still running the old version."
    }
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($existing -and $existing.Status -ne 'Stopped') {
    Step "Stopping the $ServiceName service"
    try {
        Stop-Service -Name $ServiceName -ErrorAction Stop
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(45))
    } catch {
        Write-Host "  Service did not stop cleanly ($($_.Exception.Message)) - forcing it." -ForegroundColor Yellow
    }
}
Stop-ThisInstallProcess

Step "Publishing to $InstallDir"
$published = $false
foreach ($attempt in 1..3) {
    dotnet publish $project -c Release -o $InstallDir --nologo --verbosity quiet
    if ($LASTEXITCODE -eq 0) { $published = $true; break }
    Write-Host "  Publish attempt $attempt failed - retrying in 5s (files may still be locked)." -ForegroundColor Yellow
    Stop-ThisInstallProcess
    Start-Sleep -Seconds 5
}
if (-not $published) {
    throw "Publish failed after 3 attempts. (The old version, if any, is stopped; start it with: Start-Service $ServiceName)"
}

if (-not $existing) {
    Step "Creating the $ServiceName service"

    # The port and data folder are passed on the service's own command line,
    # so they belong to this install and survive every later update.
    $binaryPath = "`"$exe`" --Kestrel:Endpoints:Http:Url=http://0.0.0.0:$Port"
    if ($DataPath) {
        $binaryPath += " --Simplifiarr:DataPath=`"$DataPath`""
    }

    New-Service -Name $ServiceName `
        -BinaryPathName $binaryPath `
        -DisplayName $ServiceName `
        -Description "Simplifiarr media library tools (http://localhost:$Port)" `
        -StartupType Automatic | Out-Null
}

# If it ever crashes, restart it after 1 minute (forget past failures after a day).
# Applied on every run so an existing service gets it too.
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

Step "Starting the $ServiceName service"
try {
    Start-Service -Name $ServiceName -ErrorAction Stop
} catch {
    Write-Host "  First start failed ($($_.Exception.Message)) - trying once more." -ForegroundColor Yellow
    Start-Sleep -Seconds 5
    Start-Service -Name $ServiceName
}

# Wait for it to answer - the first start after a publish can be slow
# (antivirus scans the new files), so give it up to five minutes.
#
# Check that the port accepts connections rather than loading the home page:
# the home page can take more than a couple of seconds to render.
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
    Write-Host "`n$ServiceName is running at http://localhost:$Port (answered after $([int]$waited.Elapsed.TotalSeconds)s)" -ForegroundColor Green
} else {
    Write-Host "`nThe service started but isn't answering on port $Port yet." -ForegroundColor Yellow
    Write-Host "Check: Get-Service $ServiceName, and the Logs folder inside the data folder."
}
