<h1>
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo-dark.png">
    <img src="assets/logo-light.png" alt="Simplifiarr" width="320">
  </picture>
</h1>

**Simple tools for a complex system.**

A self-hosted media server is a lot of moving parts: a torrent client, Sonarr,
Radarr, Prowlarr, Seerr, Jellyfin, and several drives that slowly fill up.
Each part does its own job well, but nothing looks after how they fit together.
Simplifiarr is that missing piece. It is a Windows web app that sits beside your
apps, shows what is going on across all your drives, and fixes what falls
through the cracks:

- **Linking.** Downloads go into your libraries as **hardlinks** instead of
  copies, so a file can keep seeding and sit in your library while taking up
  the space of one.
- **Storage.** See how full each drive is and what fills it. Move titles
  between drives one at a time, with Sonarr and Radarr updated. See what has
  been watched, and by whom.
- **Clean-up.** Find duplicates, leftover junk, downloads you no longer need,
  and files your media server can't play.
- **Seeding.** Cross-seed to more trackers, rebuild torrents from your
  library, and keep each seed on the drive its library is on.
- **Presentation.** Build Jellyfin collections and choose posters.

Nothing changes on disk until you press a button, and anything removed can be
brought back.

## Why it exists

While building my media server, I kept running into problems with the hardlinks
Sonarr and Radarr are supposed to make. If a torrent was still seeding, the file
either wasn't moved into the library at all or was copied instead of linked,
which doubled the storage. I wrote a PowerShell script to make the links by
hand, but that meant being at the computer every time.

Simplifiarr started as the tool to replace that script. Once it could see every
drive, every download and every library file, the next problems were easy to
spot: drives filling up unevenly, duplicates, files that wouldn't play, titles
nobody had watched in a year. It grew into one place for all of those, with
nothing happening until I say so.

## What it does

The pages are grouped the same way they are in the app's menu.

### Link

| Page | What it's for |
|---|---|
| **Hardlinks** | Finished downloads that aren't in your library yet. Preview where each will go, then link it in. TV is grouped by show and season. |
| **Potential Waste** | Library files that are full copies of a download. Converting swaps the copy for a hardlink; the old copy goes to Quarantine first. |
| **Move Seeds** | Torrents seeding from one drive while Sonarr or Radarr copied them into a library on another. Moving the seed to the library's drive leaves one shared copy; the torrent client checks every piece before the old copy goes to the Recycle Bin, and the torrent is left paused. |
| **Cross-Seed** | Finds other torrents of files you already seed, so you can seed them on more trackers without downloading anything. Every piece is checked against your files before a match is offered. |
| **Torrent Rebuilder** | Rebuilds one torrent from files already in your library, even renamed ones, so it can seed straight away. |
| **Collections** | Group anything in your libraries, across drives, and send it to Jellyfin as a collection. Sending again only changes what changed. |
| **Posters** | Choose the poster for any film, show or collection in Jellyfin from every poster found online, or drop in your own. |

### Clean up

| Page | What it's for |
|---|---|
| **Duplicates** | The same title more than once in your libraries, usually two qualities of one film or episode. |
| **Junk** | Sample clips, checksum and tracker files, and stray images left in your libraries. |
| **Downloads** | Downloads your library already has, on any drive, that your torrent client is no longer seeding. |
| **Playback Errors** | Files your media server may not be able to open: blocked by Windows, permissions it can't read, still packed in an archive, empty, unfinished, cloud-only or badly named. Blocks, permissions and archives can be fixed; fixes never change what is inside a file. |
| **Size Reduction** | Smaller releases of films and episodes you already have, found on your indexers. Report only; nothing is downloaded. |
| **Storage** | How full each drive is, with a limit you set (for example, keep the main drive under 75%), and the titles taking the space. Suggests which titles to move where to keep every drive under its limit. Moves run one title at a time in the background: each file is copied and checked, Radarr or Sonarr is given the new folder, and only then does the old copy go to the Recycle Bin. Also shows who has watched each title in Jellyfin and who asked for it in Seerr, and lists titles worth a look: watched by whoever asked for them, or unwatched for months. Those can be deleted through Radarr or Sonarr. Titles still seeding are never moved or deleted, since that would free nothing. |

### Safety net

| Page | What it's for |
|---|---|
| **Quarantine** | Everything Simplifiarr took out of a library, waiting for you to restore it or delete it. |
| **History** | Every change Simplifiarr has made on disk. |
| **Ignored** | Things you've told Simplifiarr to stop suggesting. |

There is one **Scan** button at the top of every page. It reads every drive and
refreshes every list at once, runs on the server, and keeps going if you close
the browser. To have it run by itself every few hours or once a day, use
**Settings → Scanning**. Automatic scans only find things; nothing is changed
until you press a button.

## How it keeps your files safe

- It never deletes a file outright. Anything it removes goes to the Windows
  Recycle Bin or to Quarantine.
- The only time it copies between drives is a **Storage** move you asked for.
  Each file is read back and checked against the original before the old copy
  is removed, and titles move one at a time to go easy on the drives.
- Anything taken out of a library goes to **Quarantine** first. Restoring puts
  it back; deleting sends it to the Windows Recycle Bin.
- A hardlink only counts as done once Simplifiarr has checked it really is the
  same file.
- Cross-seeds and rebuilt torrents are only offered once every piece has been
  checked, and they are added to your torrent client **stopped**.
- Anything that changes files asks you to confirm first.
- Radarr and Sonarr are only changed when you move or delete a title on
  Storage: the title's folder is updated, or the title is removed without
  touching its files. Jellyfin and Seerr are only read, apart from the
  collections Simplifiarr made, the posters you choose, and forgetting a
  request you deleted so it can be asked for again.
- The only change it makes to a torrent already in your torrent client is
  **Move Seeds**, one torrent at a time when you ask. If the client's check doesn't
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
| Torrent client (qBittorrent; others are planned) | What is still seeding; adding cross-seeds and rebuilt torrents; moving seeds. Works with or without a Web UI login |
| Radarr / Sonarr | Exact titles, years, genres and episode numbers; Hardlinks puts files in their folders, and names new folders with their folder formats; Storage keeps them up to date when titles move or are deleted |
| Prowlarr | Searching your indexers for Cross-Seed, Torrent Rebuilder and Size Reduction |
| Jellyfin | Collections, Posters, and who has watched what on Storage |
| Seerr (Jellyseerr or Overseerr) | Who asked for each title, and whether they have watched it |
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
D:\Torrents               anything without a folder of its own
D:\Torrents\Movies        finished movies
D:\Torrents\TV            finished TV
D:\Torrents\Books         finished books
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

Movies, TV and books can share the main downloads folder, but a folder each
keeps a movie from ever being mistaken for an episode. Audiobooks, music and
comics need their own download folders because their files can't be told apart
by content alone.

### First steps

1. Open **Settings**. Add one entry per drive, with its downloads folder and
   whichever library folders it has (Movies, TV, Books, Audiobooks, Music,
   Comics).
2. Under **Connections**, add any of the apps above and press **Test**.
3. Press **Scan**, or have it run by itself under **Settings → Scanning**.
4. Look at **Home**. It shows what the scan found and links to anything that
   needs you. **Storage** shows how full each drive is.

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
| `Simplifiarr.Core` | Scanning, matching, hardlinks, storage and moving, Quarantine, and the torrent client (`ITorrentClient`), Arr, Prowlarr, Seerr and Jellyfin clients |
| `Simplifiarr.Web` | The Blazor Server app (.NET 10) |
| `Simplifiarr.Tests` | MSTest tests. They work in temporary folders and talk to stand-in servers, never real ones. |

There are no third-party NuGet packages in the app itself.

## Support

Simplifiarr is free. If it saves you space or time, you can buy me a coffee on
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
