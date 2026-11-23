<#
.SYNOPSIS
    Installs or updates Simplifiarr from its latest GitHub release.

.DESCRIPTION
    In an admin PowerShell:

        irm https://raw.githubusercontent.com/Simplifiarr/Simplifiarr/main/scripts/install.ps1 | iex

    With options:

        & ([scriptblock]::Create((irm https://raw.githubusercontent.com/Simplifiarr/Simplifiarr/main/scripts/install.ps1))) -Port 7000

    Downloads the release's installer, checks it against the release's
    SHA256SUMS.txt, and runs it without asking any questions. Run it again
    later to update: an existing install keeps its port and data folder.

.PARAMETER Port
    Web page port for a new install. Default 6481.

.PARAMETER DataPath
    Data folder for a new install. Default %ProgramData%\Simplifiarr.

.PARAMETER InstallDir
    Program folder for a new install. Default C:\Program Files\Simplifiarr.

.PARAMETER ServiceName
    Windows service name. Use a different one to run a second copy.

.PARAMETER Version
    A specific version, such as 1.0.0. Default: the latest.
#>
param(
    [int]$Port = 6481,
    [string]$DataPath = '',
    [string]$InstallDir = '',
    [string]$ServiceName = 'Simplifiarr',
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is far faster without it.

$repo = 'Simplifiarr/Simplifiarr'

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $admin) {
    Write-Host 'Simplifiarr installs a Windows service, so this needs an admin PowerShell.' -ForegroundColor Yellow
    Write-Host 'Right-click PowerShell (or Terminal), choose "Run as administrator", and run the command again.'
    return
}

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$api = if ($Version) { "https://api.github.com/repos/$repo/releases/tags/v$($Version.TrimStart('v'))" } else { "https://api.github.com/repos/$repo/releases/latest" }

Write-Host "Looking up the release on GitHub..."
$release = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'Simplifiarr-installer' }

$setup = $release.assets | Where-Object { $_.name -like 'Simplifiarr-Setup-*.exe' } | Select-Object -First 1
$sums = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' } | Select-Object -First 1

if (-not $setup -or -not $sums) {
    throw "Release $($release.tag_name) has no installer or checksum list attached."
}

$folder = Join-Path ([IO.Path]::GetTempPath()) ("simplifiarr-install-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $folder | Out-Null

try {
    $setupPath = Join-Path $folder $setup.name

    Write-Host "Downloading $($setup.name) ($([math]::Round($setup.size / 1MB)) MB)..."
    Invoke-WebRequest -Uri $setup.browser_download_url -OutFile $setupPath -UseBasicParsing -Headers @{ 'User-Agent' = 'Simplifiarr-installer' }

    $list = (Invoke-WebRequest -Uri $sums.browser_download_url -UseBasicParsing -Headers @{ 'User-Agent' = 'Simplifiarr-installer' }).Content
    if ($list -is [byte[]]) { $list = [Text.Encoding]::UTF8.GetString($list) }

    $expected = ($list -split "`n" |
        ForEach-Object { $_.Trim() -split '\s+', 2 } |
        Where-Object { $_.Count -eq 2 -and $_[1].TrimStart('*') -eq $setup.name } |
        Select-Object -First 1)[0]

    $actual = (Get-FileHash -Algorithm SHA256 -Path $setupPath).Hash

    if (-not $expected -or $actual -ne $expected.ToUpperInvariant()) {
        throw 'The download did not match the release checksum, so it was not run.'
    }

    $arguments = @('/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/SERVICE=`"$ServiceName`"", "/PORT=$Port")
    if ($DataPath) { $arguments += "/DATA=`"$DataPath`"" }
    if ($InstallDir) { $arguments += "/DIR=`"$InstallDir`"" }
    $arguments += "/LOG=`"$(Join-Path $folder 'setup.log')`""

    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    Write-Host $(if ($existing) { "Updating Simplifiarr to $($release.tag_name)..." } else { "Installing Simplifiarr $($release.tag_name)..." })

    $process = Start-Process -FilePath $setupPath -ArgumentList $arguments -Wait -PassThru

    if ($process.ExitCode -ne 0) {
        throw "The installer stopped with code $($process.ExitCode). Its log: $(Join-Path $folder 'setup.log')"
    }

    $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    $shownPort = if ($service.PathName -match 'Http:Url=\S*?:(\d+)') { $Matches[1] } else { $Port }

    Write-Host "`nSimplifiarr $($release.tag_name) is running at http://localhost:$shownPort" -ForegroundColor Green
    Remove-Item -Recurse -Force $folder -ErrorAction SilentlyContinue
}
catch {
    Write-Host "`n$($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Downloaded files are in $folder"
}
