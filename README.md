\# Simplifiarr



\*\*Store once. Seed everywhere. Waste nothing.\*\*



Simplifiarr is a storage intelligence and hardlink remediation tool for self-hosted media libraries.

Whilst building my media server stack, I kept experiencing issues with the automated hardlink creation built into the Sonar/Radar servers. If I was actively seeding a torrent file, the server wouldn't move the media over to the media library or it would copy the file instead of creating the hard link, creating doubled storage. So I built a PowerShell app that would create them manually when I wanted to move the files to the library, but this required me to be present and actively on the computer, which was annoying.



Simplifiarr is designed for users who run media stacks with tools like Jellyfin, Sonarr, Radarr, Lidarr, qBittorrent, Prowlarr, Jackett, and long-term torrent seeding workflows where hardlinks are preferred over duplicate file copies. 



\---



\## Mission Statement



Simplifiarr exists to help self-hosted media users keep seeding, protect their media libraries, and reclaim wasted storage by identifying when library files and torrent download files should be hardlinks instead of duplicate copies.



The goal is simple:



\---

Store once.

Seed everywhere.

Waste nothing.

\---



Simplifiarr is being built as a practical utility first. The priority is a working, understandable tool that solves real storage problems before adding unnecessary complexity.



\---



\## Project Philosophy



Simplifiarr follows these principles:



\- Build the simplest thing that works.

\- Prefer hardlinks over copies.

\- Preserve existing library paths.

\- Preserve torrent seeding.

\- Make storage waste visible.

\- Make remediation deliberate and reviewable.

\- Avoid automatic destructive actions without user control.

\- Keep users in control of quarantined files.

\- Add larger architecture only after the core workflow works.



\---



\## Current Status



```

Phase 1.5 Prototype Complete

```



Simplifiarr currently has a working prototype that can:



\- discover configured media and download locations

\- scan movie and TV libraries

\- identify duplicate media candidates

\- detect whether candidates are already healthy hardlinks

\- calculate potential storage waste

\- show a prioritized Potential Waste Queue

\- generate hardlink conversion plans

\- move duplicate library files to Quarantine

\- create hardlinks at the original library path

\- verify hardlink creation

\- show quarantined files

\- delete quarantined files after verification



\---



\## Current Workflow



```text

Media Library File

&#x20;       +

Torrent Download File

&#x20;       ↓

Duplicate Candidate Detection

&#x20;       ↓

Hardlink Status Check

&#x20;       ↓

Potential Waste Calculation

&#x20;       ↓

Remediation Plan

&#x20;       ↓

Move Original Library Copy To Quarantine

&#x20;       ↓

Create Hardlink At Original Library Path

&#x20;       ↓

Verify Hardlink Count

&#x20;       ↓

Review Quarantine

&#x20;       ↓

Delete Quarantined Copy To Reclaim Space

```



\---



\## Core Features



\### Dashboard



\- Media candidate count

\- Healthy hardlink count

\- Duplicate copy count

\- Potential waste calculation

\- Candidate media size

\- Torrent count

\- Storage location count

\- Storage overview table

\- Potential Waste Queue



\---



\### Storage Overview



Simplifiarr displays configured storage roots with:



\- path

\- role

\- online/offline status

\- file count

\- video count

\- total size



\---



\### Duplicate Detection



Simplifiarr currently detects duplicate media by comparing media library files against torrent download files.



Current supported categories:



\- Movies

\- TV Shows



\---



\### Hardlink Detection



Simplifiarr checks whether matching files are already hardlinked.



A healthy hardlink means:



```text

Library path and download path point to the same NTFS file record.

```



\---



\### Remediation Planning



Before remediation, Simplifiarr generates a plan that verifies:



\- library file exists

\- download file exists

\- both files are on the same volume

\- file is not already hardlinked



\---



\### Quarantine Workflow



Instead of permanently deleting the duplicate library copy immediately, Simplifiarr moves the original duplicate file into Quarantine.



This gives the user a safety buffer before final deletion.



Current Quarantine behavior:



```text

Move duplicate library file to:

C:\\ProgramData\\Simplifiarr\\Quarantine



Create hardlink at original library path.



Let the user review Quarantine before deleting.

```



\---



\## Technology Stack



\- C#

\- .NET 10

\- ASP.NET Core

\- Blazor Server

\- PowerShell

\- Windows

\- NTFS hardlinks



\---



\## Current Requirements



Simplifiarr currently assumes:



\- Windows

\- NTFS volumes

\- local or mounted media paths

\- hardlink-compatible library/download locations

\- qBittorrent running locally

\- media libraries managed outside Simplifiarr



\---



\## Current Limitations



Simplifiarr is still a prototype.



Known limitations:



\- paths are currently hardcoded in early services

\- no settings page yet

\- no persistent database yet

\- no conversion history database yet

\- no Sonarr/Radarr API integration yet

\- no batch conversion UI yet

\- no scheduled scans yet

\- no authentication yet

\- no multi-user support

\- no containerized deployment yet

\- no cross-platform hardlink abstraction yet

\- no cross-seed discovery yet



\---



\# Roadmap



This section used to contain Simplifiarr's phase-by-phase roadmap. That

content has moved to \[SIMPLIFIARR\_BIBLE.md\](SIMPLIFIARR\_BIBLE.md), which

is now the single canonical source for project phase status - it

reconciles this file's old numbering with a couple of other divergent

roadmap documents that had drifted out of sync with each other and with

the actual commit history.



If you're looking for "what phase are we in" or "what's next," read that

file instead. Update it, not this section, when phase status changes.


\# Long-Term Vision



The long-term goal of Simplifiarr is to become a storage and seeding intelligence platform for self-hosted media environments.



The finished application should help answer questions like:



```text

Where am I wasting storage?



Which files should already be hardlinked?



Which library files are safe to convert?



How much space can I reclaim?



What has already been remediated?



What is still sitting in Quarantine?



Which releases are good cross-seed candidates?



Can I seed on additional trackers without downloading another copy?



Can I promote downloads into my library without duplicating data?

```



Simplifiarr is not intended to replace:



\- Sonarr

\- Radarr

\- Lidarr

\- Prowlarr

\- Jackett

\- qBittorrent

\- Jellyfin

\- cross-seed



Instead, Simplifiarr aims to become the intelligence layer between these systems:



```text

Arr Applications

&#x20;       ↓

Media Acquisition



qBittorrent

&#x20;       ↓

Torrent Management



Jellyfin

&#x20;       ↓

Media Consumption



cross-seed

&#x20;       ↓

Tracker Expansion



Simplifiarr

&#x20;       ↓

Storage + Seeding Intelligence

```



The ultimate goal is simple:



```text

Store once.

Seed everywhere.

Waste nothing.

```



\---



\## Development



Build:



```powershell

dotnet build

```



Run:



```powershell

dotnet run --project Simplifiarr.Web

```



Default local URL:



```text

http://localhost:6481

```



\---



\## Repository Status



This repository is currently private and experimental.



Simplifiarr is under active personal development.



\---



\## License



Private project.


