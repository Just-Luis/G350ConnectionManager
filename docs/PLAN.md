# 🎮 G350 Connection Manager — Project Plan

> Windows companion app for the **Battle XP G350 / Rockchip RK3326** handhelds.
> Sets up the USB network link, Windows internet sharing, and makes screenshotting,
> recording and screen streaming one-click operations.

**Status:** Planning · **Target:** Windows 10/11 x64 · **Deliverable:** single `.exe`

---

## 1. Scope

The app replaces the manual ritual documented in `COMMUNITY_TUTORIAL.md`:

| Today (manual) | With Connection Manager |
| :--- | :--- |
| Open `ncpa.cpl`, find "Ethernet 2", set `192.168.7.1/24` by hand | Detected and configured automatically |
| Right-click Wi‑Fi → Sharing → pick adapter → OK | One toggle |
| Run `Test_Connection_G350.bat` to see if it worked | Live status in the UI |
| `python Stream_Screen.py` + `pip install paramiko pillow` | Built-in, no dependencies |
| Screenshots via a Tk window + `S` key | Dedicated capture / record / stream |
| Drag files through WinSCP / Explorer | Built-in dual-pane file browser |

### Goals
1. **Zero setup friction** — works on a clean Windows install with no Python, no WinSCP.
2. **Safe by default** — every system change is reversible and explicitly listed before it happens.
3. **Self-diagnosing** — the UI shows *why* something failed, not just that it failed.
4. **One file** — a single portable `.exe`, no installer required.

### Non-goals (v1.0)
* macOS / Linux host support.
* Anything that modifies the console's firmware or SD card beyond writing a helper to `/tmp`.
* RetroArch netplay hosting (that stays with `C2C_Netplay_Prep.sh`).

---

## 2. Language & Stack Decision

You asked for C if it fits, and it can — but for **this** app I recommend **C# / .NET 8**.
Here is the honest trade-off:

| Concern | C (Win32 + Winsock + libssh2) | **C# / .NET 8** | Python / Electron |
| :--- | :--- | :--- | :--- |
| Single-file `.exe` | ✅ native, no runtime | ✅ self-contained (~55 MB) | ❌ 30–180 MB |
| SSH/SFTP (needed for screen + files) | ❌ hand-roll libssh2, build deps | ✅ **SSH.NET** (pure managed, MIT) | ✅ paramiko |
| Raw frame streaming / video encoding | ❌ very painful | ✅ `System.Drawing`, FFmpeg pipe | ✅ |
| GUI matching the provided design | ❌ ~3–5× the code (Win32 owner-draw) | ✅ WPF/XAML maps 1:1 to the CSS tokens | ✅ |
| Windows IP config + ICS | ⚠️ COM, verbose in C | ✅ same COM, far less ceremony | ✅ |
| Maintainability for a hobby project | ❌ high | ✅ high | ✅ high |

**Decision: C# / .NET 8 + WPF**, published as a **self-contained single-file** `.exe`
(~55 MB, no .NET install required). ✅ Confirmed.

---

## 3. Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                     G350ConnectionManager.exe                │
│                        (C# / .NET 8 / WPF)                   │
├───────────────┬───────────────┬──────────────┬───────────────┤
│  UI Layer     │  Core         │  Console I/O │  Windows      │
│  (XAML views) │  Services     │  (SSH.NET)   │  Integration  │
├───────────────┼───────────────┼──────────────┼───────────────┤
│ HomeView      │ LinkMonitor   │ SshSession   │ NetworkConfig │
│ NetworkView   │ ProbeCache    │ FrameReader  │ IcsManager    │
│ ScreenView    │ SettingsSvc   │ GrabberHost  │ AdapterEnum   │
│ FilesView     │ ActivityLog   │ FileTransfer │ CredentialMgr │
│ SettingsView  │ ThemeService  │ Screenshot   │ FirewallRule  │
└───────────────┴───────────────┴──────────────┴───────────────┘
```

### Key design rules (carried over from the shell scripts)
1. **Never block the UI.** All probing runs on background tasks with a cached verdict —
   the pattern we shipped in `USB_Network_Manager.sh` v1.4.0.
2. **Every mutation is reversible.** Backup before write, restore on undo/exit.
3. **One source of truth for state.** A single `LinkState` object; views observe it.
4. **No permanent console install required.** The helper is uploaded at runtime.

---

## 4. Console Payload — `/tmp` vs permanent install

**Answer to Q4: no, a permanent file is not worth it. Keep it transient in `/tmp`.**

The helper (`g350_grabber.py`, ~16 KB) is a *slave process*: it only ever runs while the PC
is connected and asking for frames. Persisting it buys nothing and costs several things.

| | **Transient `/tmp`** (recommended) | Permanent (`/roms/tools/` or `/opt`) |
| :--- | :--- | :--- |
| Survives console reboot | ❌ (harmless — re-uploaded in ~50 ms) | ✅ |
| Upload cost per session | ~16 KB over USB Ethernet (imperceptible) | none |
| Can drift from the app version | ✅ impossible — always matches | ⚠️ **yes**, stale copy = wrong protocol, silent breakage |
| Needs write access to the SD card | ❌ no | ⚠️ yes — risk of corruption on unclean shutdown |
| User-visible clutter | ❌ none | ⚠️ a stray file in `tools/` or `roms/` |
| Cleanup burden | automatic (RAM-backed, cleared on reboot) | manual; needs an uninstaller |
| Matches existing tooling | ✅ `Stream_Screen.py` already does this | ❌ new behaviour to document |

**The decisive argument is version drift.** A permanent copy is a second source of truth.
The day the grabber protocol changes, a stale on-console copy either fails confusingly or
— worse — half-works. Everything else in this project is already built around the
"upload the matching payload each session" model, including `Stream_Screen.py`.

**What I'll do instead:**
* Upload to `/tmp/g350_grabber.py` on demand (matches `Stream_Screen.py` exactly).
* Always overwrite, never assume it's current.
* `pkill -f g350_grabber.py` on Stop *and* on app exit (crash-safe via a shutdown hook).
* Optional future nicety: keep a SHA-256 of the embedded payload and skip the upload if the
  console's copy already matches — same result, marginally faster.

> If you later want offline capture without the PC (e.g. a button combo on the console),
> *that* would justify a permanent install — but it's a different feature.

---

## 5. Recording & Streaming — video format decision (Q2)

This is the one genuinely hard trade-off, so here is the full picture.

### The constraint
The console streams **raw BGRA frames** (~3.5 MB each at 1280×720). At 20 fps that is
~70 MB/s of uncompressed data over USB — so *encoding* is unavoidable, and the question is
only *where* and *with what*.

### Option A — Bundle FFmpeg (~80 MB extra)

| Pros | Cons |
| :--- | :--- |
| ✅ Works offline, forever, on any PC | ❌ EXE grows from ~55 MB to ~135 MB |
| ✅ H.264/H.265 MP4 with audio later | ❌ **Licensing**: FFmpeg is LGPL/GPL; bundling needs careful build choice + attribution, and GPL builds force the whole app to GPL |
| ✅ No first-run download, no antivirus surprise later | ❌ Longer build/CI time, larger release assets |
| ✅ Re-encode, trim, quality presets all free | ❌ You inherit responsibility for FFmpeg CVE updates |

### Option B — Detect system FFmpeg, else guided download

| Pros | Cons |
| :--- | :--- |
| ✅ Small EXE (~55 MB) | ❌ Requires network on first record — awkward when the whole app is about a *wired offline* setup |
| ✅ Reuses an FFmpeg the user already has (OBS, etc.) | ❌ Antivirus heuristics dislike a program that downloads and executes an EXE |
| ✅ You don't redistribute FFmpeg at all | ❌ Two code paths to test; "it works on my machine" reports |

### Option C — MJPEG-only in v1.0, no video encoder at all ⭐ **recommended start**

| Pros | Cons |
| :--- | :--- |
| ✅ **Zero extra bytes** — MJPEG frames are the raw BGRA we already have, JPEG-encoded per frame | ❌ No MP4/audio; large files (MJPEG-in-AVI/`.mkv` at High ≈ 150–300 MB/min) |
| ✅ Zero licensing complexity | ❌ Not ideal for uploading to YouTube without re-encode |
| ✅ Ships faster, fewer failure modes | ❌ "Record" and "Stream" would share one encoder |
| ✅ Perfect for **progressive delivery** — proves the frame pipeline first | ❌ Needs a later swap for MP4 |

### Recommendation: **C → then A, staged**

Start with **Option C** so Milestone 5 isn't blocked on encoding decisions; you get
screenshots, live preview and MJPEG streaming working and *testable* early.
Then, as a dedicated later milestone, add **Option A (bundled FFmpeg)** for proper MP4
recording — after the frame pipeline is proven, so any bug is clearly in the encoder,
not the transport.

If you'd rather not grow the EXE at all, Option C stays the permanent answer and "Record"
writes MJPEG `.avi` — perfectly watchable, just bulkier.

> **Decision needed:** confirm stage-1 MJPEG is acceptable, and whether we eventually go
> to bundled FFmpeg (Option A) or stay MJPEG-only.

---

## 6. Progressive Delivery Route (Q3)

Each stage is **independently runnable and testable**, and adds exactly one new class of
risk. Nothing later depends on a stage you haven't validated.

### Stage 1 — Shell & Design System  🧱 *no system access*
**Build:** repo, .NET project, theme dictionaries, sidebar + 5 empty views, settings
persistence, all components from the mock (buttons, inputs, segmented controls, cards).
**Test:** does it look exactly like the mock, in both themes, resized and collapsed?
**Risk:** none — zero privileged code. Pure visual review.

### Stage 2 — Read-Only Diagnostics  👁️ *read-only*  ✅ **DONE**
**Build:** adapter enumeration, staged ping + TCP/22 probe, cached non-blocking monitor,
live status pill, diagnostics panel with per-stage verdicts.
**Test:** plug/unplug the cable, start/stop services on the console — watch status update.
**Risk:** low — nothing is modified. This also becomes the permanent diagnostic tool.

### Stage 3 — Adapter IP Configuration  ⚙️ *first write*  ✅ **DONE**
**Build:** automatic/manual IP, before/after diff, Apply, Restore, per-operation elevation.
**Test:** apply `192.168.7.1`, verify in `ncpa.cpl`, restore, verify gone.
**Risk:** medium — first mutation. Reversible by design; test on a spare adapter first.

### Stage 4a — ICS Status (read-only)  👁️  ✅ **DONE**
**Build:** `IcsService` + `--ics-probe` elevated helper, status panel with
source/target, scope address, connection list, and explicit conflict warnings.
**Test:** read succeeds with elevation, degrades honestly when declined,
correctly reports "off (service idle)" vs "on (WiFi → Ethernet 2)".
**Why split:** the COM API refuses to answer *at all* un-elevated (see §6b), so
shipping a write path first would mean debugging blind.

### Stage 4b — ICS Toggle  🌐  ✅ **DONE**
**Build:** Enable/Disable via the same elevated helper, plus console route repair.
**Test:** enable sharing, confirm internet on console, disable, confirm clean.
**Risk:** high — ICS rewrites adapter IPs.

Order was determined empirically: the **private** (target) side must be enabled
before the **public** (source) side, and disabled in reverse.

**The console script already handles the address switch.** `USB_Network_Manager.sh`
adds *both* `192.168.7.2/24` and `192.168.137.2/24` to `usb0`, then probes for
`192.168.137.1` and chooses the matching default route:

```
ip addr add 192.168.7.2/24   dev usb0
ip addr add 192.168.137.2/24 dev usb0
ping -c1 192.168.137.1 && route via 192.168.137.1 || route via 192.168.7.1
```

So after the PC's address changes the only step needed is **[A] Start Services**
on the console again. An earlier build repointed the console's route over SSH;
that was removed — it duplicated logic that already exists, would drift from it,
and would be overwritten the next time services started anyway. The app now just
tells the user to restart services.

### Stage 5 — Screen Preview & Screenshots  📷
**Build:** SSH session, grabber upload, frame reader, live preview, PNG capture, teardown.
**Test:** preview runs, FPS overlay sane, screenshots correct, no orphan process after exit.
**Risk:** medium — but no filesystem writes on the PC beyond the chosen folder.

### Stage 6 — Streaming (MJPEG)  📡
**Build:** local MJPEG server, optional remote target, quality presets.
**Test:** open stream in a browser on the PC, confirm it matches the preview.
**Risk:** low — read-only, additive.

### Stage 7 — Recording  🎬
**Build:** MJPEG `.avi` writer, timer badge, save-to path.
**Test:** record 30 s, play back, verify no dropped frames.
**Risk:** low — local file writes only.

### Stage 8 — Files (SFTP)  📁
**Build:** dual pane, transfer queue, progress/cancel, new folder, delete with confirm.
**Test:** upload/download round-trip, checksum match, cancel mid-transfer.
**Risk:** medium — destructive operations, so it goes last and gets typed confirmation.

### Stage 9 — Polish & Packaging  📦
**Build:** hotkeys, auto-start options, single-file publish, CI workflow attaching the EXE.
**Test:** run on a clean Windows VM with no .NET installed.
**Risk:** low.

**Optional Stage 10 —** swap MJPEG for H.264 via bundled FFmpeg (§5, Option A).

> Each stage ships as its own tag + GitHub release, so you can roll back to any point.

---

## 6b. Hardware Probe — findings that change later stages

Captured against a live console (Debian 13 trixie, OpenSSH 10.0p2, Python 3.13.5)
while planning Stage 4. Each of these was verified, not assumed.

### Credentials & transport

| Item | Value |
| :--- | :--- |
| SSH/SFTP | `ark@192.168.7.2`, password `ark`, port 22 |
| `ark` groups | `sudo`, `video`, `render`, `input`, `netdev`, `audio`, `adm` |
| sudo | passwordless pipe works: `echo ark \| sudo -S -p '' …` |
| SSH algs | `curve25519-sha256`, `rsa-sha2-512/256`, `ssh-ed25519`, `aes128/256-gcm`, `chacha20-poly1305` |

All of these are supported by SSH.NET, so **no algorithm workaround is needed**
for Stages 5–8.

### Screen capture works *without* root

`/dev/fb0` is `crw-rw---- root:video`, and `ark` is in `video`. A raw read of a
full frame succeeds as `ark` — **no sudo, no password on the wire.** The
existing `Stream_Screen.py` pipes the password into `sudo -S` on every run,
which is unnecessary; the new app can avoid that entirely.

The bundled DRM/KMS grabber (used for ES/RetroArch) also ran unprivileged and
produced 19 valid 640×480 BGRA frames in 4 s.

### Performance — measured, not estimated

| Path | Bytes | Time | Throughput |
| :--- | :--- | :--- | :--- |
| raw frame (640×480×4) | 1,228,800 | ~150 ms | ~8 MB/s |
| raw, base64 over SSH | 1,638,400 | 169 ms | 9.4 MB/s |
| `gzip -1 \| base64` | 36,100 | 86 ms | 408 KB/s |
| `gzip -6 \| base64` | 14,420 | 118 ms | 119 KB/s |

A text/TUI screen compresses to **2.2 %** of raw (1.23 MB → 36 KB).
A round trip costs ~60 ms of latency, so **8–12 fps is the realistic ceiling**
for a fetch-then-display loop, and ~20 fps if the console pushes a continuous
stream after the first frame.

**Decision for Stage 6:** pipe the grabber over SSH with `gzip`. Never send raw
BGRA; never spawn one SSH command per frame.

### Framebuffer facts

| Item | Value |
| :--- | :--- |
| Geometry | 640×480, 32 bpp, stride 2560 (== width×4, no padding) |
| Pixel order | **BGRA** |
| Modes | `U:640x480p-0` (single mode) |
| Double buffering | none — `fb0` is a single static buffer |

Because there is no page-flip, a capture taken mid-write can tear. Frames that
look identical are a normal result of a static screen, not a failed capture.

### Stage 4 (ICS) — a hard constraint found early

The `HNetCfg.HNetShare` COM API **requires elevation**:

* un-elevated, `EnumEveryConnection` returns **1 null entry** and both
  `NetConnectionProps` / `INetSharingConfigurationForINetConnection` throw
  `NullReferenceException`
* `EnumPublicConnections` / `EnumPrivateConnections` throw
  **`E_ACCESSDENIED` (0x80070005)**

So ICS cannot even be *inspected* from the `asInvoker` app. Stage 4 must move
its whole read/write path into an elevated helper, and the UI has to show
"needs elevation to read" rather than an empty picker.

Current machine state for that stage: `SharedAccess` is **Running**,
`ScopeAddress = 192.168.137.1`, and the internet source is **WiFi**
(`ifIndex 7`, gateway `192.168.1.1`). The RNDIS console adapter must be the
*private* side, which is what forces the console to `192.168.137.2` — the
`.7.x` vs `.137.x` conflict already tracked as Issue #3.

### Adapter naming is unstable

The RNDIS adapter was `Ethernet 2` → `Ethernet 4` → `Ethernet 5` → `Ethernet 2`
across replugs, within the same day. Anything that caches the name is wrong;
always enumerate and match by `InterfaceDescription` (which stays
`Remote NDIS based Internet Sharing Device`).

---

## 7. Feature Detail

### 7.1 Home — the dashboard
Mirrors the mock: three numbered steps with live status.
* **Step 1 · Network** — `IP`, `Gateway`, mode (Automatic / Manual), *Change settings*
* **Step 2 · Internet sharing** — on/off, source adapter, *Manage sharing*, latency badge
* **Step 3 · Screen** — ready / streaming state, quick actions
* Sidebar pill: `Connected · 192.168.137.2` driven by `LinkMonitor`.

### 7.2 Network & sharing

**A. Console-side checks (read-only)**
* Detect the RNDIS adapter (match on *Remote NDIS* / *USB Ethernet*).
* Probe `192.168.137.2` then `192.168.7.2` (matching the existing `.bat` tools).
* Confirm TCP/22 listening → the "services are running" signal.

**B. PC adapter configuration**
* **Automatic:** assign `192.168.7.1/24` to the RNDIS adapter (no gateway).
* **Manual:** embedded equivalent of `ncpa.cpl` — IP, mask, gateway.
* via `netsh interface ipv4` / IP Helper API; **no reboot, no adapter reset**.
* Shows a *Before / After* diff and requires explicit **Apply**.

**C. Internet sharing (ICS)**
* Enumerate shareable sources (Wi‑Fi, Ethernet) and the G350 target.
* Toggle via COM `INetSharingManager` — the same mechanism as the Sharing tab.
* **Detects and explains the classic footgun:** ICS forces the target to `192.168.137.1`,
  silently overriding a static `192.168.7.1`. Surfaced explicitly (this is Issue #3 in
  `ISSUES_AND_POLISH_CHECKLIST.md`) instead of leaving the user to discover it.

**D. Diagnostics panel**
* Physical link, local ping, SSH port, DNS resolution, latency — each reported separately.
* Replaces `Test_Connection_G350.bat` and `Test_Internet_On_G350.bat`.

### 7.3 Screen
* Reuses the `Stream_Screen.py` protocol: upload embedded `g350_grabber.py` to
  `/tmp/g350_grabber.py`, run via `sudo python3`, read raw BGRA over an SSH channel.
* **Live preview** with FPS + resolution overlay.
* **Take screenshot** → PNG (+ optional clipboard copy).
* **Record** → MJPEG `.avi` (Stage 7), optional H.264 later.
* **Start streaming** → local MJPEG server (+ optional remote target).
* Graceful teardown: `pkill -f g350_grabber.py` on stop/exit.

### 7.4 Files — SFTP dual pane
* Left: local filesystem; Right: console (default `/roms`).
* Upload / Download with progress and cancel, New folder, Delete (typed confirm).
* Credentials via **Windows Credential Manager**, never plain text on disk.

### 7.5 Settings
* **Theme:** White / Black.
* **Highlight color:** Blue / Teal / Green / Orange / Pink / Purple.
* Start with sidebar collapsed.
* Extra: default save folder, capture hotkeys, auto-start services, restore-on-exit.

---

## 8. Design System — extracted from `Battlexp G350 Companion.html`

The mock ships **9 boards**: Home/Network/Settings/Files in White, Home/Screen/Settings/Files
in Black, plus collapsed-sidebar variants.

### Layout
```
Window         1100 × 680 (design), resizable, min 940 × 600
Sidebar        230 px · collapsible to icon-only (~72 px)
Content        padding 32px 36px · gap 18px
```

### Light theme
| Token | Value |
| :--- | :--- |
| App background | `#F5F5F5` |
| Surface / sidebar | `#FFFFFF` |
| Border | `#E0E0E0` |
| Text primary | `#111111` |
| Text secondary | `#5F5F5F` |

### Dark theme
| Token | Value |
| :--- | :--- |
| App background | `#0B0B0B` |
| Surface / sidebar | `#151515` |
| Raised / input | `#2A2A2A` |
| Border | `#2A2A2A` |
| Text primary | `#F5F5F5` |
| Text secondary | `#A3A3A3` |
| Muted | `#B7B7B7` |

### Accent
Default `#2563EB`. Derived exactly as the mock does:
`soft = accent + "33"`, `onAccent = #111111` when luminance > 150, else `#FFFFFF`.

### Components
| Element | Spec |
| :--- | :--- |
| Nav button | h 46 · radius 10 · font 15 · active = `soft` bg + 600 weight |
| Primary button | h 44 · radius 10 · padding 0 22 · accent bg · 600 |
| Secondary button | h 44 · radius 10 · 1 px border · surface bg |
| Segmented control | h 38 · radius 8 · active accent, inactive transparent |
| Input | h 42 · radius 10 · 1 px border · 15 px text |
| Card | radius 14 · 1 px border · padding 14–20 |
| Status pill | 10 px dot + label, `Connected · <ip>` |
| Icons | 22 px stroke SVG, `stroke-width 1.8`, round caps |

Ports directly to a WPF `ResourceDictionary` with two theme dictionaries and an accent
brush recomputed at runtime — the mock's `{{accent}}/{{onAccent}}/{{soft}}` templating maps
1:1 to a `ThemeService`.

---

## 9. Safety Model

| Action | Reversible? | How |
| :--- | :--- | :--- |
| Set adapter static IP | ✅ | Previous config captured, **Restore** button + auto-restore on exit |
| Enable ICS | ✅ | Original sharing state recorded, toggled back off |
| Upload grabber to `/tmp` | ✅ | Removed on stop; `/tmp` clears on reboot |
| Delete remote file | ⚠️ | Requires typed confirmation |

* Runs `asInvoker` — **no UAC prompt until the user presses Apply**, then elevates only
  that operation.
* **Activity Log** panel records every change with timestamp + before/after.
* "Dry run" toggle shows what *would* happen without touching the system.

---

## 10. Repository Layout

```
G350ConnectionManager/
├── README.md
├── LICENSE                       (MIT, matching sibling repos)
├── .gitignore                    (.NET)
├── .gitattributes                (LF for scripts, CRLF for .bat)
├── docs/
│   ├── PLAN.md                   ← this file
│   ├── DESIGN.md                 (design tokens + component spec)
│   └── screenshots/              (UI progress shots)
├── src/
│   └── G350ConnectionManager/
│       ├── G350ConnectionManager.csproj
│       ├── App.xaml(.cs)
│       ├── Assets/
│       │   ├── g350_grabber.py       (embedded resource)
│       │   └── icons/
│       ├── Themes/
│       │   ├── Light.xaml
│       │   ├── Dark.xaml
│       │   └── Controls.xaml
│       ├── Views/            Home, Network, Screen, Files, Settings
│       ├── ViewModels/
│       ├── Services/
│       │   ├── LinkMonitor.cs
│       │   ├── NetworkConfig.cs
│       │   ├── IcsManager.cs
│       │   ├── SshSession.cs
│       │   ├── GrabberHost.cs
│       │   └── ThemeService.cs
│       └── Models/
└── .github/workflows/build.yml   (builds + attaches the .exe to a release)
```

---

## 11. Risks & Mitigations

| Risk | Impact | Mitigation |
| :--- | :--- | :--- |
| ICS COM API needs elevation and varies across Windows builds | High | Isolated in `IcsManager` (Stage 4), verify against `ncpa.cpl`, manual fallback always available |
| Static IP vs ICS conflict | High | Surfaced explicitly in UI; documents `192.168.7.x` vs `192.168.137.x` duality |
| Anti-virus flags unsigned single-file EXE | Medium | Documented as unsigned; Authenticode can be added later |
| Screen grabber depends on DRM internals | Medium | Reuses the field-tested `Stream_Screen.py` protocol verbatim |
| MJPEG file sizes | Low | Quality presets; optional FFmpeg upgrade path (§5) |
| Users on .NET-less machines | Low | Self-contained publish |

---

## 12. Decisions Recorded

| # | Question | Decision |
| :--- | :--- | :--- |
| 1 | Packaging | ✅ Self-contained single-file `.exe` (~55 MB) |
| 2 | Video format | ⏳ **Stage 1 = MJPEG** (Option C); bundled FFmpeg (Option A) as optional Stage 10 |
| 3 | Delivery | ✅ Nine progressive stages, each independently testable |
| 4 | Console payload | ✅ Transient `/tmp` upload — no permanent install |
| 5 | Repository | ✅ Public `Just-Luis/G350ConnectionManager`, MIT |

**Still open:** confirm MJPEG for stage 1, and whether to pursue bundled FFmpeg later.

---

## 13. Immediate Next Step

On confirmation, I'll do **Stage 1**: scaffold the repo, project, theme dictionaries and
navigation shell so you have a runnable `.exe` with the exact look from the mock —
then we iterate stage by stage.
