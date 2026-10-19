
# TrpgMaps

[简体中文](Readme.md) | **English**

A map display tool for tabletop RPGs. The DM runs it on a laptop and throws the map
onto a TV or projector; players scan a QR code and see the same map on their phones —
grid, zoom, rotation and basemap all stay in sync.

**Current version: v1.0.3 (32-bit build)**

- Source: <https://github.com/starnight-dirt/TrpgMaps>　(mirror: <https://gitee.com/starnight-dirt/TrpgMaps>)
- Download: the [**Releases**](https://github.com/starnight-dirt/TrpgMaps/releases) page
- Author: [starnight-dirt](https://github.com/starnight-dirt)

---

## 1. Requirements

| Item | Requirement |
| --- | --- |
| OS | Windows 7 SP1 or newer (8 / 8.1 / 10 / 11 all fine) |
| Architecture | 32-bit or 64-bit Windows — the program itself is 32-bit |
| Runtime | **Nothing to install.** Win7 uses its built-in .NET Framework 3.5; Win8+ automatically uses the system .NET Framework 4.x |
| Memory | 1 GB or more |
| Disk | About 10 MB (roughly 5.2 MB after install, ~5 MB download) |
| Display | 1366×768 recommended; the window can shrink to 800×480 |
| Network | A LAN that phones can reach (same Wi-Fi / router). DM and players must be on the **same** LAN |

Player side (phone / tablet):

| Item | Requirement |
| --- | --- |
| OS | iOS 12+ / Android 7+ |
| Browser | The built-in browser, Chrome, Safari — no app to install |
| Network | Same LAN as the DM |

> The program never uploads anything. Map and drawing sync happen entirely on your
> own machine; the only outbound request is "check for updates".

---

## 2. Install and start

### Option A: installer (recommended)

1. Download `TrpgMaps_v1.0.3_win32_installer.exe` and double-click it.
2. Pick the install folder (default `C:\Program Files (x86)\TrpgMaps`), then **Next**.
3. Choose the shortcuts you want (desktop / Start menu / taskbar — all on by default), then **Install**.
4. If Windows asks "allow TrpgMaps through the firewall" on first run, **say yes** —
   otherwise phones cannot connect (see section 4).
5. To uninstall: **Settings → Apps** (Windows 7: **Control Panel → Programs and Features**),
   find TrpgMaps, choose Uninstall. You may keep your maps and drawings.

### Option B: portable

1. Download `TrpgMaps_v1.0.3_win32_portable.zip`.
2. Extract to **any writable folder** (e.g. `D:\TrpgMaps`, or straight onto a USB stick).
   ⚠️ Do not use `C:\Program Files` — the portable build has no installer to grant write
   access, so uploading maps and saving drawings will fail.
3. Double-click `TrpgMaps.exe`.
4. To remove it, just delete the folder (nothing is written elsewhere, no registry entries).

Both options are functionally identical; only the installer writes uninstall info and
creates shortcuts. The portable build is fully self-contained.

---

## 3. Using it

### DM side (your computer)

1. Double-click `TrpgMaps.exe`. It opens **full-screen, borderless and always on top** —
   ready to throw onto a TV.
2. The left sidebar, top to bottom:

   | Button | What it does |
   | --- | --- |
   | Expand | Expand / collapse the sidebar (labels appear when expanded) |
   | Map | Pick a basemap, grid size, grid colour, rotation; import your own image |
   | Network | Room number, player address, QR code, room password, port, firewall, online players |
   | Draw | Paint terrain / entities / items onto the map (draggable floating panel) |
   | Audio | Background music: seek, prev/next, volume, play mode, lyrics |
   | Fullscreen | Toggle full-screen / windowed |
   | About | Author, version, check for updates |

3. Click **Network** to get a **room number**, a **player address** and a **QR code**.
   - The room number is **new every time the program starts** (8 digits) and is part of
     the address: `http://192.168.1.100:20020/<room>/player`. Each session, players just
     scan the **new** QR code.
   - Want the room to be private? Type a password under "Room password" and press
     **Save password & refresh address** — the QR code and address will carry it
     automatically, so players only type their name. Clear the box and save to remove it.
4. **Map → Import image** loads your own map. PNG / JPG / GIF / BMP / WebP are supported;
   you can also drag a file onto the window.
5. **Draw** paints terrain (grass, water, fire…), entities and items. "Clear asset" only
   removes the currently selected asset; "Clear all" wipes every layer.

### Background music

1. The app **ships with 8 soundtrack tracks** (`audio\battle`, `audio\daily`) — open the
   panel and press play. Your own music goes into the same **`audio\`** folder; subfolders
   may be **nested to any depth** and show up as a collapsible menu, exactly like the
   drawing-asset tree (each folder row has ▶ / ▼ and a track count).
2. Open **Audio** in the sidebar — **one click** on a track starts playing it.
3. The panel has: seek bar, previous / play-pause / next, volume and play mode
   (**repeat one** by default / in order / shuffle).
4. Lyrics: put a matching `.lrc` next to the file (`ending.mp3` + `ending.lrc`); the panel
   shows three rolling lines — previous / current (highlighted) / next.
5. **mp3 / wav / flac / ogg / m4a / aac / wma** are supported. m4a and friends go through
   the Windows decoder, which also works on old machines (at the cost of loading the whole
   file into memory, roughly 10 MB per minute).
6. Closing the panel does **not** stop the music.
7. Want to remix? The source repository has an **`audio-flac\`** folder with the 48 kHz
   lossless masters (8 tracks, ~144 MB). The release ships only the ~90 kbps OGG files
   (~15 MB) — the difference is inaudible at the table.

### Organising entity / item assets (subfolders)

The `entity\` and `item\` folders may contain subfolders (**any depth**); the list renders
them as a **collapsible menu**:

- folder rows have **▶ / ▼** on the left — click to expand or collapse;
- image rows keep their thumbnail and name, and images in subfolders **can be painted**
  onto the map just like root-level ones;
- when a name is too long, the list gets a horizontal scrollbar at the bottom
  (`Shift + wheel` also scrolls sideways).

Every folder gets an auto-generated `locales.toml`; fill in `foldername` to give it a
per-language name (leave it empty to use the folder name):

```toml
[zh-cn]
foldername = "战士"
[en-us]
foldername = "Fighter"
```

The name follows the **program's interface language** (change it in About).

### Changing the interface language

**Desktop:** About → **Language** → pick one → **Apply**. The UI switches immediately and
asks you to confirm: **Keep** remembers it; **Cancel**, or letting the **countdown** (it ticks
down second by second) expire, switches back.

**Phone / tablet:** the floating button at the bottom right → **Settings** → **Language**.
The choice takes effect immediately — no confirmation, no revert — and is remembered on that
device. The list follows the same rule as the desktop: the system language comes first and is
marked *Recommended*, the rest are sorted by English name.

Word lists live in `locales\zh-cn.json` / `en-us.json` next to the exe and can be edited in
Notepad (restart to apply). You can also append `?lang=en-us` to the player URL — it only
applies to devices that have not picked a language yet.

### Player side (phone / tablet)

1. Join the **same Wi-Fi / router** as the DM (not mobile data).
2. Scan the QR code on the DM screen, or type the player address
   (`http://192.168.1.100:20020/12345678/player`).
   No QR code, or a broken camera? Open `http://ip:port/join` and type the **room number +
   name** (plus the password if the room has one). It tells you whether the room is missing
   or the password is wrong.
3. The map appears and every change on the DM side syncs live. The page is read-only.
4. Joined players show up in the DM's **Network → Online players** list.
5. To switch language, use the floating button → **Settings** → **Language** → English.
6. To leave, use the **floating button** → **Leave room**. The DM and everyone else see
   "**xxx left the room**", and the identity is cleared locally (re-joining just needs the
   name again). If the DM restarts (which changes the room number), the player page notices
   and re-joins the new room by itself.

---

## 4. Can't connect?

Nine times out of ten it is the **Windows Firewall**, and the symptoms are misleading:
everything on the DM side looks fine, the address is right, the QR code scans — and the phone
just spins.

What to do:

1. Open **Network** and look at the **Firewall:** line. If it says blocked, press
   **Allow through firewall (admin)** and accept the prompt, then retry.
2. There is also a **`放行防火墙.bat`** (allow-firewall) file next to the exe — right-click →
   Run as administrator. It is in the extracted folder for the portable build and in the
   install folder for the installed build.
3. On first launch, if Windows asks "allow TrpgMaps through the firewall" — that dialog can
   hide **behind** the full-screen window. Minimise or alt-tab to find it and click Allow.

> Why: the window is full-screen and topmost, while the Windows prompt is a normal window.
> If it is dismissed, Windows records a permanent block rule and never asks again. The
> program therefore checks on startup and offers a one-click fix.

Other possibilities:

- **Phone and PC are not on the same LAN** (mobile data, or a guest Wi-Fi). The first three
  octets of both IPs should match (e.g. `192.168.1.x`).
- **AP / client isolation** is enabled on the router, so devices cannot talk to each other.
  Turn it off or use another Wi-Fi.
- **Port in use** — change the port in the Network panel (e.g. 5001) and save.
- **Campus / corporate networks** usually block device-to-device traffic; use a phone hotspot.

---

## 5. Updates

- A few seconds after startup the program checks for a new version; you can also use
  **About → Check for updates**.
- When a new version exists you get three choices:
  - **Update** — download and install (portable replaces program files; installed builds also
    fix the registry entries);
  - **Later** — skip this time, ask again next start;
  - **Don't remind me** — ignore this version; newer ones still prompt.
- **Gitee** is tried first, with **GitHub** as a fallback.
- You can always download from the Releases page manually.

---

## 6. FAQ

**The program won't start / closes immediately?**
Check `app.log` next to the exe — the reason is recorded there.

**The map is black?**
WebP has been supported since v1.0.0. For your own images, make sure the extension is one of
`.png / .jpg / .jpeg / .gif / .bmp / .webp`.

**The grid does not line up with the map?**
Cell size is computed in centimetres and has nothing to do with print size. Drag the
"cm per cell" slider in Map settings until the grid matches your map.

**Several players at once?**
Yes — any number of phones / tablets on the LAN can watch the same map.

**Are my maps uploaded anywhere?**
No. There is no upload behaviour at all; the only network request is the update check.

**Do I lose my maps when uninstalling?**
The installer asks whether to keep `maps\` and `drawing.json`. For the portable build, back up
the folder before deleting it.

---

## 7. Version history

| Version | Highlights |
| --- | --- |
| **v1.0.3** | Multi-level folders for entities / items (collapsible list, auto `locales.toml` per folder); **the audio list is a multi-level menu too**, with **8 built-in soundtrack tracks** (OGG, ~15 MB; lossless masters under `audio-flac\` in the source repo); switchable UI language (desktop in About, players via Settings → Language); **fixes**: assets could not be placed, some strings were still hardcoded Chinese, the language-dialog countdown never ticked; quit confirmation |
| v1.0.2 | Per-run room number + optional room password; "Leave room" and the "xxx left the room" toast; built-in music player (mp3 / wav / flac / ogg / m4a / aac / wma, LRC lyrics); default port 20020 with automatic fallback; `/join` entry page |
| v1.0.1 | Self-drawn installer / uninstaller; terrain assets moved to WebP (33.6 MB → 5.2 MB); fixed all-black asset thumbnails; updater no longer deletes without replacing |
| v1.0.0 | First release: custom basemaps, grid size and colour, rotation and zoom; live phone sync; terrain / entity / item layers; built-in update check |

---

Copyright © starnight-dirt. All rights reserved.
