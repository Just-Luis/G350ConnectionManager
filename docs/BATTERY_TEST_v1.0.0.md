# 🧪 Battery Test — Stages 5–9 (v1.0.0)

Covers everything added after the Stage 3 pass: screen preview, recording,
internet-sharing interaction, file transfer, the console, and the folder/theme
polish. Stages 1–4 already have their own documents.

**Time:** ~40 minutes. **Risk:** one section (5) can delete files — read it first.

Run with the console plugged in and, where noted, Wi-Fi sharing **on**.

---

## 0. Before you start

| Step | Expected |
| :--- | :--- |
| ☐ Console connected, services running | Sidebar shows **Connected** with an address |
| ☐ Note the current address | `192.168.7.2` (no sharing) or `192.168.137.2` (sharing on) |
| ☐ Close any other copy of the app | Only **one** `G350ConnectionManager.exe` in Task Manager |

> Two instances make every later test lie: a screenshot or status panel can show
> the *other* window. This actually happened during development and cost an hour.

---

## 1. Home overview

| Step | Expected |
| :--- | :--- |
| ☐ Open **Home** | Five cards: Network, Internet sharing, Screen, Files, Console |
| ☐ Each card shows an **icon**, not a number | Globe, broadcast, monitor, folder, terminal |
| ☐ Sidebar top-left shows the **G350 phone mark** | Not a coloured "G" |
| ☐ Switch to the light theme, return to Home | Mark stays visible (outline flips black) |

### 1.1 Card icons navigate

| Card icon | Should open |
| :--- | :--- |
| ☐ Network | Network & sharing |
| ☐ Internet sharing | Network & sharing |
| ☐ Screen | Screen |
| ☐ Files | Files |
| ☐ Console | Console |

> The sidebar highlight should follow. If the page changes but the highlight does
> not, that is a bug, not a drawing glitch.

---

## 2. Screen preview

| Step | Expected |
| :--- | :--- |
| ☐ **Screen** → **Start preview** | Live console image within ~3 s |
| ☐ Status line | `W×H · fps · qNN · frames`, rate plausible |
| ☐ Move around the console menu | Preview follows |

### 2.1 The four controls

| Step | Expected |
| :--- | :--- |
| ☐ Drag **Frame rate** while running | Measured fps changes **without restarting** |
| ☐ Set **Resolution** to 320×240 | Preview resizes mid-stream |
| ☐ Drag **JPEG quality** | No crash; status shows the new `q` value |
| ☐ Set **Delay** to 200 ms | Frame rate drops |
| ☐ Restart the app | All four settings persist |

### 2.2 Capture in EmulationStation and in a game

| Step | Expected |
| :--- | :--- |
| ☐ On the console press **[B]** to exit to EmulationStation | ES appears |
| ☐ Start the preview | ES theme visible, **not black** |
| ☐ Launch a game, keep previewing | Game visible, console still playable |
| ☐ Exit the game | Preview recovers |

> If a preview is black, check the console's screen hasn't gone to its
> screensaver. A sleeping console is indistinguishable from a capture bug.

---

## 3. Screenshots

| Step | Expected |
| :--- | :--- |
| ☐ **Take screenshot** | `Saved g350-<date>.png` |
| ☐ Open the file | Correct content, **opaque**, right colours |
| ☐ Screenshots list | New row with time, name, dimensions |
| ☐ Take one with the preview stopped | Still works |

---

## 4. Recording

| Step | Expected |
| :--- | :--- |
| ☐ Click **Record** | Button becomes **Stop recording**, status shows `● REC n frames` |
| ☐ Wait ~10 s, click **Stop recording** | Status shows the saved filename |
| ☐ Play the file | H.264 MP4, correct content, sensible length |
| ☐ Record while the preview is running | Both work |

### 4.1 Frame-rate integrity

This is the one that silently corrupts a recording.

| Step | Expected |
| :--- | :--- |
| ☐ Set **Delay** to 250 ms, record 10 s, play it back | Motion plays at roughly **real speed** |
| ☐ Compare against a zero-delay recording | Neither is sped up or slowed down |

> The encoder is told one frame rate for the whole file. If frames were written
> without padding, the video would play too fast. Loss should appear as stutter,
> never as speed-up.

---

## 5. Files ⚠️

**This section can delete data.** Create a throwaway folder first.

| Step | Expected |
| :--- | :--- |
| ☐ **Files** → console pane lists `/roms` | Folders then files, sizes shown |
| ☐ Double-click a folder | Opens it; **Up** returns |
| ☐ Create a folder called `zz_test` | Appears in the list |
| ☐ Upload a file into it | Appears with a plausible size |
| ☐ Download that file back | Byte-identical to the original |

### 5.1 Deletion confirmation

| Step | Expected |
| :--- | :--- |
| ☐ Select `zz_test`, click **Delete** | Dialog: *"Are you sure you want to delete this folder and everything inside it?"*, name shown, *"You can't recover it."*, **No** selected by default |
| ☐ Click **No** | Folder still on the console |
| ☐ Click **Delete** → **Yes** | Folder gone from the list **and** from the console |
| ☐ Click **Delete** with nothing selected | Says *"Select a file or folder on the console first."* |

### 5.2 Symlink and permission safety

| Step | Expected |
| :--- | :--- |
| ☐ Try deleting something under `/roms/tools` you know is fine to lose | Works |
| ☐ Try a write into `/` or `/etc` | Fails with a clear permission message, no crash |

---

## 6. Console

| Step | Expected |
| :--- | :--- |
| ☐ **Console** → click `ip addr` | Output appears within ~2 s, `exit 0` at the end |
| ☐ Type `echo hello` and press **Enter** | `hello` printed |
| ☐ Press **Up** arrow | Previous command returns to the input |
| ☐ Run `ls /nope` | Error text shown, does not crash |
| ☐ Run `sleep 60`, click **Stop** | Stops promptly, `SHOULD_NOT_APPEAR` never printed |
| ☐ On the console: `ps aux \| grep sleep` | **No** stray process |
| ☐ **Clear** | Output empties |
| ☐ **Copy output** | Clipboard has the log |
| ☐ **Save log** | A `.txt` file is written |

---

## 7. Internet-sharing interaction

Do this with Wi-Fi internet available on the PC.

| Step | Expected |
| :--- | :--- |
| ☐ **Network & sharing** → **Check** | Panel fills; sharing state shown |
| ☐ **Turn on sharing** | One UAC prompt; adapter moves to `192.168.137.1` |
| ☐ Console status line | *"Console updated (gateway 192.168.137.1), the console has internet."* |
| ☐ On the console | `ping 1.1.1.1` succeeds |
| ☐ **Turn off sharing** | Console returns to `via 192.168.7.1` |
| ☐ Buttons afterwards | Both re-enable **without** pressing Refresh |
| ☐ Home card | *Sharing on — PC online* / *Off*, never "no internet" while it works |

> If the console update says *"did not recognise the command"*, the device is
> running `USB_Network_Manager.sh` older than v1.6.0.

---

## 8. Settings

| Step | Expected |
| :--- | :--- |
| ☐ **Screenshots** folder icon button | Opens a folder picker; the path updates |
| ☐ **Recordings** folder icon button | Opens a picker, **independently** |
| ☐ **Use the same folder for both** | Both rows become the chosen path |
| ☐ Take a screenshot, then record a clip | Both land in the configured folders |
| ☐ Switch theme, then restart | Theme persists |
| ☐ Collapse the sidebar, restart | Stays collapsed |

---

## 9. Clean-install check

The one thing that cannot be verified from a development machine.

| Step | Expected |
| :--- | :--- |
| ☐ Copy the EXE to a Windows machine **without .NET installed** | Launches normally |
| ☐ Confirm no runtime prompt appears | It is self-contained |
| ☐ Check the EXE icon in Explorer | G350 mark |

---

## 10. Findings

| # | Section | What happened | Expected | Severity |
| :--- | :--- | :--- | :--- | :--- |
| | | | | |
