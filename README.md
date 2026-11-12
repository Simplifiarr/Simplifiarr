<h1>
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo-dark.png">
    <img src="assets/logo-light.png" alt="Simplifiarr" width="320">
  </picture>
</h1>

**Store once. Seed everywhere. Waste nothing.**

Simplifiarr is a Windows web app for self-hosted media servers. It gets your
torrent downloads into your libraries as **hardlinks** instead of copies, so a
file can keep seeding and sit in your library while taking up the space of one.
It also finds the space you're already wasting and helps you get it back
safely.

## Why it exists

While building my media server, I kept running into problems with the hardlinks
Sonarr and Radarr are supposed to make. If a torrent was still seeding, the file
either wasn't moved into the library at all or was copied instead of linked,
which doubled the storage. I wrote a PowerShell script to make the links by
hand, but that meant being at the computer every time. Simplifiarr is the tool I
wanted instead: one place that shows where storage is being wasted and fixes it,
with nothing happening until I say so.

## What it does

The pages are grouped the same way they are in the app's menu.

### Link

| Page | What it's for |
|---|---|
| **Hardlinks** | Finished downloads that aren't in your library yet. Preview where each will go, then link it in. TV is grouped by show and season. |
| **Potential Waste** | Library files that are full copies of a download. Converting swaps the copy for a hardlink; the old copy goes to Quarantine first. |
| **Move Seeds** | Torrents seeding from one drive while Sonarr or Radarr copied them into a library on another. Moving the seed to the library's drive leaves one shared copy; qBittorrent checks every piece before the old copy goes to the Recycle Bin, and the torrent is left paused. |
| **Cross-Seed** | Finds other torrents of files you already seed, so you can seed them on more trackers without downloading anything. Every piece is checked against your files before a match is offered. |
| **Torrent Rebuilder** | Rebuilds one torrent from files already in your library, even renamed ones, so it can seed straight away. |
| **Collections** | Group anything in your libraries, across drives, and send it to Jellyfin as a collection. Sending again only changes what changed. |
| **Posters** | Choose the poster for any film, show or collection in Jellyfin from every poster found online, or drop in your own. |

### Clean up

| Page | What it's for |
|---|---|
| **Duplicates** | The same title more than once in your libraries, usually two qualities of one film or episode. |
| **Junk** | Sample clips, checksum and tracker files, and stray images left in your libraries. |
| **Downloads** | Downloads your library already has, on any drive, that qBittorrent is no longer seeding. |
| **Playback Errors** | Files your media server may not be able to open: blocked by Windows, permissions it can't read, still packed in an archive, empty, unfinished, cloud-only or badly named. Blocks, permissions and archives can be fixed; fixes never change what is inside a file. |
| **Size Reduction** | Smaller releases of films and episodes you already have, found on your indexers. Report only; nothing is downloaded. |

### Safety net

| Page | What it's for |
|---|---|
| **Quarantine** | Everything Simplifiarr took out of a library, waiting for you to restore it or delete it. |
| **History** | Every change Simplifiarr has made on disk. |
| **Ignored** | Things you've told Simplifiarr to stop suggesting. |

There is one **Scan** button at the top of every page. It reads every drive and
refreshes every list at once, runs on the server, and keeps going if you close
the browser.

## How it keeps your files safe

- It never copies a file from one drive to another, and never deletes a file
  outright.
- Anything taken out of a library goes to **Quarantine** first. Restoring puts
  it back; deleting sends it to the Windows Recycle Bin.
- A hardlink only counts as done once Simplifiarr has checked it really is the
  same file.
- Cross-seeds and rebuilt torrents are only offered once every piece has been
  checked, and they are added to qBittorrent **stopped**.
- Anything that changes files asks you to confirm first.
- Radarr and Sonarr are only read. In Jellyfin, Simplifiarr only changes the
  collections it made and the posters you choose.
- The only change it makes to a torrent already in qBittorrent is **Move
  Seeds**, one torrent at a time when you ask. If qBittorrent's check doesn't
  pass, the torrent is pointed back at its old files, which were never touched.

## Requirements

- **Windows** with **NTFS** drives. A hardlink only works within one drive, so
  each download folder must be on the same drive as the libraries it feeds.
- **.NET 10 SDK** to build and run it.
- **ffprobe** (part of [FFmpeg](https://ffmpeg.org/)) for reading video
  resolution. Simplifiarr looks in the usual install locations, or you can set
  its path in Settings.
- **[7-Zip](https://www.7-zip.org/)** (optional) for unpacking packed releases
  on the Playback Errors page.
- To fix file permissions on Playback Errors, run Simplifiarr as a Windows
  service (see below); it then has the rights to change them.

### Optional connections

Simplifiarr works on its own. Each of these adds more:

| App | Adds |
|---|---|
| qBittorrent | What is still seeding; adding cross-seeds and rebuilt torrents; moving seeds |
| Radarr / Sonarr | Exact titles, years, genres and episode numbers |
| Prowlarr | Searching your indexers for Cross-Seed, Torrent Rebuilder and Size Reduction |
| Jellyfin | Collections and Posters |
| fanart.tv | More poster choices (needs your own free API key) |

## Getting started

### Try it

```powershell
dotnet run --project Simplifiarr.Web
```

Then open <http://localhost:6481>.

### Install it as a Windows service

From an **administrator** PowerShell in the repository folder:

```powershell
.\scripts\install-service.ps1
```

This runs the tests (and stops if any fail), publishes the app to
`C:\Services\Simplifiarr`, creates a service that starts with Windows, and
waits for it to answer on port 6481. To update after pulling new code:

```powershell
.\scripts\update-service.ps1
```

Your data folder is never touched by either script. Both take `-ServiceName`,
and the installer also takes `-Port`, `-InstallDir` and `-DataPath`, so a
second copy can run side by side with its own data.

### Set up your folders

Each drive needs a downloads folder and its library folders side by side, so
files can be hardlinked between them. If you're starting fresh, this script
creates them for one drive and tells you what to put in Settings:

```powershell
.\scripts\new-media-folders.ps1 -Drive D
```

That gives:

```text
D:\Torrents               finished movies, TV and books
D:\Torrents\Audiobooks    finished audiobooks
D:\Torrents\Music         finished music
D:\Torrents\Comics        finished comics
D:\Movies   D:\TV   D:\Books   D:\Audiobooks   D:\Music   D:\Comics
```

Add `-Root Media` to put everything under `D:\Media`, `-Types Movies,TV` to
create only some libraries, `-SeedOnly` for a folder of torrents you only keep
for seeding, and `-WhatIf` to see what it would do first. It only creates
folders that are missing and never moves or deletes anything. Run it once per
drive.

Audiobooks, music and comics need their own download folders because their
files can't be told apart by content alone.

### First steps

1. Open **Settings**. Add one entry per drive, with its downloads folder and
   whichever library folders it has (Movies, TV, Books, Audiobooks, Music,
   Comics).
2. Under **Connections**, add any of the apps above and press **Test**.
3. Press **Scan**.
4. Look at **Home**. It shows what the scan found and links to anything that
   needs you.

The **Help** page in the app explains every page.

## Where it keeps its data

Settings, history, caches and logs go in `%ProgramData%\Simplifiarr`, or the
folder given with `-DataPath`. Quarantine and the cross-seed folder can be moved
in Settings.

## Development

```powershell
dotnet build Simplifiarr.slnx
dotnet test Simplifiarr.slnx
```

| Project | Contents |
|---|---|
| `Simplifiarr.Core` | Scanning, matching, hardlinks, Quarantine, and the qBittorrent, Arr, Prowlarr and Jellyfin clients |
| `Simplifiarr.Web` | The Blazor Server app (.NET 10) |
| `Simplifiarr.Tests` | MSTest tests. They work in temporary folders and talk to stand-in servers, never real ones. |

There are no third-party NuGet packages in the app itself.

## Support

Simplifiarr is free. If it saves you space, you can buy me a coffee on
[Ko-fi](https://ko-fi.com/simplifiarr).

## Logo

The logo is a rounded purple square (`#7c3aed`) beside the name in bold. The
files are in [`assets`](assets): `mark.svg` and large PNGs of the square on its
own, and `logo-light.png` / `logo-dark.png` with the name for light and dark
backgrounds.

## License

Simplifiarr is free software, released under the
[GNU General Public License v3.0](LICENSE). You can use, study, change and
share it. If you share a changed version, it must stay under the same license
with its source code available.
