# Dark Ages DAT Viewer

Windows app for browsing and editing Dark Ages client archives (DAT, maps, metafiles, palettes, and related graphics).

The app ships **empty**. Point it at a client folder you already have. This repository does not include DATs, maps, area dumps, or other game data.

Author: SlashRemix · Version: 1.0 · Last updated: 10/14/2026

## Requirements

- Windows
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Build and run

```powershell
dotnet build DarkAges.Viewer.sln
dotnet run --project DarkAges.Viewer\DarkAges.Viewer.csproj
```

## Getting started

1. Click **Browse…** and choose a full client folder (DATs, maps, metadata). The viewer loads **one client at a time** so tilesets, palettes, and maps stay with that version.
2. Use the **Client** combo box to switch between folders you have added. **Reload** re-scans the current folder. **More ▾ → Add client folder…** registers another version without mixing files.
3. The left column lists **archives** (seo.dat, ia.dat, maps, metadata, and so on). The **workspace** drop-down filters that list (Mobs, Walls, Floors, Maps, Items, …).
4. Click an archive to see its **entries**. Filter the name box, or hide `.pal` / `.tbl` noise. Search across all archives from **Search all archives** (`item 12`, `floor 100`, `stc`, filenames). Press Enter.
5. The center pane previews the selected graphic, map, sound, or table. Use Play / Pause, frame step, **Gallery**, and **Actions ▾** (PNG, JSON, CSV, SQL, extract).
6. Open tools from **Editors ▾**. Most editors can **Save to DAT** for a single entry when the bytes are a drop-in for that client. Use **DAT ▾** only to unpack or rebuild an entire archive.
7. **More ▾ → About** shows author, version, and last updated date.

Favorites (★) are saved per client. Warp labels and Back apply to map preview. Change log (under **Editors ▾ → Data**) lists edits for the current client.

### Saving

- Editors write one file back into a DAT when the format matches what that client already loads.
- If a save would smash a layout the exe cannot read, the tool refuses it (for example HEA light overlays on 3.x / 7.x / Fragments).
- **Unpack DAT…** / **Repack folder to DAT…** are bulk operations, not the usual way to add one item or monster.

## Features

**Browse**

- Classic 13-character and LoD 32-character DAT names, folder archives, and Legends AoC LRAR (read-only)
- Grouped archive list, search, favorites, gallery of every entry
- Preview EPF / MPF / HPF / SPF / EFA / HEA, palettes, maps, metafiles, fonts, and sound
- Bink cinematics with FFmpeg on PATH, next to the exe, or set under **More ▾**

**Maps**

- Map editor: floors, walls, stamps, overlays, resize, randomize, second editor for warp destinations
- Warp editor, warp check, walk flags (sotp)
- 7.xx Light metafile editor (hourly types and map assignments — not HEA pixel overlays)
- Read-only server-world overlay (Hybrasyl XML or Chaos JSON) for warps / music / light type

**Graphics**

- Item, monster, floor, wall, effects, UI / icons, and portrait editors
- Character assembler (paper-doll)
- PNG converter into EPF, MPF, HPF, EFA, or SPF with a companion palette
- TILEA splicer for isometric floors and wall columns

**Data**

- Palette editor, metafile editor, music / sound browser
- Export the current list or table as JSON, CSV, or SQL
- Per-client change log and snapshots

## What is in this repo

- `DarkAges.Viewer` — WPF UI
- `DarkAges.Formats` — archive and graphic formats

Local-only pieces are gitignored and will not be on GitHub: unit tests, Unity export, map/area dumps, and the old map-editor source dump.
