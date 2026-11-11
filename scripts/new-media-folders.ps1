<#
.SYNOPSIS
    Creates the folders Simplifiarr expects on one drive: a downloads folder
    and the library folders beside it.

.DESCRIPTION
    Simplifiarr works one drive at a time, because a hardlink can only join two
    places on the same drive. Each drive needs:

        <drive>\<root>\Torrents               finished movies, TV and books
        <drive>\<root>\Torrents\Audiobooks    finished audiobooks
        <drive>\<root>\Torrents\Music         finished music
        <drive>\<root>\Torrents\Comics        finished comics
        <drive>\<root>\Torrents\SeedOnly      kept only for seeding (optional)
        <drive>\<root>\Movies
        <drive>\<root>\TV
        <drive>\<root>\Books
        <drive>\<root>\Audiobooks
        <drive>\<root>\Music
        <drive>\<root>\Comics

    Audiobooks, music and comics get their own download folders because their
    files can't be told apart by content alone (an MP3 could be either).

    Only folders that don't exist yet are created. Nothing is moved, renamed or
    deleted, so it is safe to run on a drive that already has some of these.
    At the end it prints the paths to type into Simplifiarr's Settings, and
    where to point qBittorrent.

    Run it once per drive. Admin rights are not needed unless the drive itself
    requires them.

.PARAMETER Drive
    The drive to set up, e.g. D or D:\

.PARAMETER Root
    A folder on that drive to put everything in, e.g. "Media" for D:\Media\...
    Leave it out to put the folders straight on the drive (D:\Movies, ...).

.PARAMETER Types
    Which libraries this drive holds. Defaults to all of them.
    Any of: Movies, TV, Books, Audiobooks, Music, Comics

.PARAMETER DownloadsName
    Name of the downloads folder. Defaults to "Torrents".

.PARAMETER SeedOnly
    Also create a SeedOnly folder inside the downloads folder, for torrents you
    keep only to seed and never want in a library.

.EXAMPLE
    .\scripts\new-media-folders.ps1 -Drive D

    Creates D:\Torrents (with Audiobooks, Music and Comics inside) and
    D:\Movies, D:\TV, D:\Books, D:\Audiobooks, D:\Music, D:\Comics.

.EXAMPLE
    .\scripts\new-media-folders.ps1 -Drive E -Root Media -Types Movies,TV -SeedOnly

    Creates E:\Media\Torrents, E:\Media\Torrents\SeedOnly, E:\Media\Movies and
    E:\Media\TV.

.EXAMPLE
    .\scripts\new-media-folders.ps1 -Drive D -WhatIf

    Shows what would be created without creating anything.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string]$Drive,

    [string]$Root = '',

    [ValidateSet('Movies', 'TV', 'Books', 'Audiobooks', 'Music', 'Comics')]
    [string[]]$Types = @('Movies', 'TV', 'Books', 'Audiobooks', 'Music', 'Comics'),

    [string]$DownloadsName = 'Torrents',

    [switch]$SeedOnly
)

$ErrorActionPreference = 'Stop'

# ------------------------------------------------------------ The drive

$letter = $Drive.Trim().TrimEnd('\', '/', ':')

if ($letter -notmatch '^[A-Za-z]$') {
    throw "'$Drive' isn't a drive letter. Use something like D or D:\"
}

$driveRoot = "$($letter.ToUpperInvariant()):\"

if (-not (Test-Path -LiteralPath $driveRoot)) {
    throw "Drive $driveRoot isn't there. Is it plugged in?"
}

# Hardlinks need NTFS. Warn rather than stop: the check can fail on some
# setups even when the drive is fine.
try {
    $fileSystem = (Get-Volume -DriveLetter $letter -ErrorAction Stop).FileSystem

    if ($fileSystem -and $fileSystem -ne 'NTFS') {
        Write-Warning "$driveRoot is $fileSystem, not NTFS. Hardlinks need NTFS, so Simplifiarr won't be able to link files on this drive."
    }
}
catch {
    Write-Verbose "Couldn't check the file system of ${driveRoot}: $($_.Exception.Message)"
}

foreach ($name in @($Root, $DownloadsName)) {
    if ($name -match '[<>:"|?*]') {
        throw "'$name' has a character Windows doesn't allow in folder names."
    }
}

$base = if ($Root.Trim()) { Join-Path $driveRoot $Root.Trim().Trim('\', '/') } else { $driveRoot }
$downloads = Join-Path $base $DownloadsName.Trim().Trim('\', '/')

# ---------------------------------------------------- What goes where

# Label as it appears in Settings, and the folder.
$plan = [System.Collections.Generic.List[object]]::new()

function Add-Folder($label, $path) {
    $plan.Add([pscustomobject]@{ Setting = $label; Path = $path })
}

Add-Folder 'Downloads folder' $downloads

if ($SeedOnly) {
    Add-Folder 'Seed-only folder' (Join-Path $downloads 'SeedOnly')
}

foreach ($type in $Types | Select-Object -Unique) {
    switch ($type) {
        'Movies'     { Add-Folder 'Movies' (Join-Path $base 'Movies') }
        'TV'         { Add-Folder 'TV' (Join-Path $base 'TV') }
        'Books'      { Add-Folder 'Books' (Join-Path $base 'Books') }
        'Audiobooks' {
            Add-Folder 'Audiobooks' (Join-Path $base 'Audiobooks')
            Add-Folder 'Audiobook downloads' (Join-Path $downloads 'Audiobooks')
        }
        'Music' {
            Add-Folder 'Music' (Join-Path $base 'Music')
            Add-Folder 'Music downloads' (Join-Path $downloads 'Music')
        }
        'Comics' {
            Add-Folder 'Comics' (Join-Path $base 'Comics')
            Add-Folder 'Comic downloads' (Join-Path $downloads 'Comics')
        }
    }
}

# ---------------------------------------------------------- Create them

$created = 0
$existing = 0

foreach ($entry in $plan) {
    if (Test-Path -LiteralPath $entry.Path -PathType Container) {
        $existing++
        $entry | Add-Member Status 'already there'
        continue
    }

    if (Test-Path -LiteralPath $entry.Path) {
        throw "$($entry.Path) is a file, not a folder. Rename or move it, then run this again. Nothing after this point was created."
    }

    if ($PSCmdlet.ShouldProcess($entry.Path, 'Create folder')) {
        New-Item -ItemType Directory -Path $entry.Path | Out-Null
        $created++
        $entry | Add-Member Status 'created'
    }
    else {
        $entry | Add-Member Status 'would be created'
    }
}

# ---------------------------------------------------------------- Report

Write-Host ''
Write-Host "Drive $driveRoot" -ForegroundColor Cyan
$plan | Format-Table Setting, Path, Status -AutoSize | Out-String | Write-Host

if (-not $WhatIfPreference) {
    Write-Host "$created folder(s) created, $existing already there." -ForegroundColor Green
}

$ownDownloads = @($Types | Where-Object { $_ -in 'Audiobooks', 'Music', 'Comics' })

$qbittorrent =
    if ($ownDownloads.Count -gt 0) {
        "`n     Give $(($ownDownloads -join ', ').ToLowerInvariant()) their own categories that save into" +
        "`n     the matching download folders above."
    }
    else { '' }

Write-Host @"

Next:
  1. In Simplifiarr, open Settings > Drives > Add drive.
     Set Drive to $driveRoot and copy each path above into the field
     with the same name. Leave any field not listed here blank.
  2. In qBittorrent, save finished downloads to $downloads.$qbittorrent
  3. Point Radarr, Sonarr and your media server at the library folders above.
"@
