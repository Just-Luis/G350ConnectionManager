# 🧪 Battery Test — Stage 3 (Adapter IP Configuration)

> **Resolved during first hardware run.** Case 2.1 originally failed with
> *"Could not apply — the elevation prompt may have been declined"* even when
> the prompt was accepted. Three separate defects were behind it, all fixed in
> `v0.3.1`:
>
> 1. **Read-back raced Windows DAD.** After a static address is set, Windows runs
>    Duplicate Address Detection. During that window `netsh show config` has no
>    `IP Address:` line at all, so an immediate read looked like failure. The
>    write had actually succeeded.
> 2. **Elevated output was discarded.** Elevation needs `UseShellExecute=true`,
>    which cannot redirect stdio, so the real netsh message was thrown away and
>    every failure was reported as a declined prompt. The elevated call now runs
>    through a generated `.bat` that captures output and exit code.
> 3. **Quoted adapter names were re-parsed.** `ShellExecute` re-tokenised
>    `name="Ethernet 2"`; running through `.bat` preserves the name verbatim.
>
> Case 2.1 and 3.1 below now pass on real hardware.

> Manual test checklist for the G350 Companion network-configuration features.
> Every case lists the **setup**, the **steps**, and the **expected result**.
>
> Run these in order — later cases assume earlier ones passed. Tick each box as you go.

**App version:** `v0.3.0` (Stage 3)
**Where results go:** note anything that fails in the *Findings* section at the bottom.

---

## 0. Pre-flight

| | Check |
| :--- | :--- |
| X | `G350ConnectionManager.exe` launches and shows the sidebar |
| X | Sidebar pill starts as **Not connected · No console detected** |
| X | No UAC prompt appears just from launching the app |

---

## 1. Basics — connect / disconnect

### 1.1 Cold start, nothing connected

**Setup:** no console, no cable.

| Step | Expected |
| :--- | :--- |
| X Launch app | Home shows **Not connected** |
| X Open **Network & sharing** | Card 1 says **Console not connected** / *Plug in the USB cable…* |
| X Look at the buttons | **Apply** disabled · **Restore previous** disabled · **Test connection** enabled |
| X Click **Test connection** | Message: *No USB adapter found…* |

---

### 1.2 Connect → check

**Setup:** app running, console unplugged.
**Steps:** plug in the cable → start services on the console (**[A]** on `USB_Network_Manager.sh`).

| Step | Expected |
| :--- | :--- |
| X Within ~4 s | Sidebar pill → **Connected · 192.168.137.2** (or `192.168.7.2`) |
| X Home | Banner green, all 5 diagnostic stages green |
| X Network page | Card 1 shows the RNDIS adapter + its current address |
| X Buttons | **Apply** now enabled |
| X No UAC prompt | Connecting must never prompt for elevation |

> **Watch for:** the Home banner updating **without** leaving the page. This was a
> bug in Stage 2 (stale banner after navigation) — if the banner goes stale here,
> report it.

---

### 1.3 Disconnect → check

**Setup:** connected and healthy (1.2 passed).
**Steps:** unplug the cable.

| Step | Expected |
| :--- | :--- |
| X Within ~4 s | Sidebar pill → **Not connected** |
| X Home banner | Turns grey/red, **no stale "Connected" text** |
| X Home steps | Step 1 → *Waiting for console*, Step 2 → *Off* |
| X Diagnostics | Stage 1 red: *No USB Ethernet adapter found* |
| X Network page | Card 1 → **Console not connected**, Apply disabled |
| X App remains responsive | No freeze, no crash |

---

### 1.4 Reconnect after disconnect

**Setup:** finished 1.3.
**Steps:** plug the cable back in.

| Step | Expected |
| :--- | :--- |
| X Within ~4 s | Pill returns to **Connected** |
| X All stages green again | |
| X No leftover state | Activity log unchanged (nothing new was written) |

---

## 2. Applying a static IP

### 2.1 Automatic mode → Apply

**Setup:** console connected, Wi‑Fi sharing **off** (so the console is on `192.168.7.x`).

| Step | Expected |
| :--- | :--- |
| X Open Network page | Mode = **Automatic**, fields pre-filled `192.168.7.1 / 255.255.255.0` |
| X Before/After box | Shows current address → `192.168.7.1/24` |
| X Click **Apply** | Windows UAC prompt appears (once) |
| ☐ Approve | Hint: *Applied 192.168.7.1/24.* |
| ☐ Card 1 refresh | Now shows `192.168.7.1/24` |
| ☐ **Restore previous** | Becomes **enabled** (a backup now exists) |
| ☐ Activity log | Gains `before: …` and `after : …` lines |
| ☐ `ncpa.cpl` cross-check | The RNDIS adapter shows `192.168.7.1` |

> **Note:** if Wi‑Fi sharing is **on**, Windows ICS owns the adapter and forces
> `192.168.137.1`. Apply may be overwritten by ICS — that conflict is surfaced in
> Stage 4. For this test keep sharing **off**.

---

### 2.2 Manual mode → Apply a custom IP

| Step | Expected |
| :--- | :--- |
| ☐ Switch to **Manual** | Fields become editable, hint mentions keeping the same subnet |
| ☐ Enter `192.168.50.1` / `255.255.255.0`, blank gateway | Diff box updates to `192.168.50.1/24` |
| ☐ Click **Apply**, approve UAC | Hint: *Applied 192.168.50.1/24.* |
| ☐ Card 1 | Shows `192.168.50.1/24` |
| ☐ Console is now unreachable | Sidebar pill **Not connected** is expected — the console still expects `.7.x` |

---

### 2.3 Apply rejects bad input (no UAC, no writes)

| Input | Expected |
| :--- | :--- |
| ☐ IP `abc` | *IP address is not a valid IPv4 address.* — **no UAC prompt** |
| ☐ IP `999.1.1.1` | Same rejection |
| ☐ Mask `255.0.255.0` | *Subnet mask is not a valid contiguous mask.* |
| ☐ Gateway `abc` | *Gateway is not a valid IPv4 address.* |
| ☐ Activity log | **Unchanged** — nothing was written |

---

### 2.4 Decline the UAC prompt

| Step | Expected |
| :--- | :--- |
| ☐ Click **Apply** then **No** on the UAC dialog | Hint: *Could not apply — the elevation prompt may have been declined.* |
| ☐ Card 1 shows the **previous** address | Nothing changed |
| ☐ Activity log | Records the failed attempt |
| ☐ App still usable | Buttons re-enabled |

---

## 3. Restore

### 3.1 Restore after Apply

**Setup:** finished 2.1 (backup exists).

| Step | Expected |
| :--- | :--- |
| ☐ Click **Restore previous**, approve UAC | Hint: *Restored to …* describing the original config |
| ☐ Card 1 | Returns to the pre-Apply address (often an `169.254.x.x` or DHCP state) |
| ☐ **Restore previous** | Now **disabled** (backup consumed) |
| ☐ Activity log | Gains a `restoring:` line |

---

### 3.2 Restore with no backup

**Setup:** fresh start, connected, never pressed Apply.

| Step | Expected |
| :--- | :--- |
| ☐ **Restore previous** | Disabled — cannot be clicked |
| ☐ (via SSH/automation) call restore | *No backup on record for this adapter — nothing to restore.* |

---

### 3.3 Restore while disconnected

**Setup:** Apply an IP, then **unplug** the console.

| Step | Expected |
| :--- | :--- |
| ☐ Network page | Apply disabled, Restore disabled |
| ☐ Plug back in | Restore re-enables (backup survived the unplug) |
| ☐ Click **Restore** | Works, restores the original config |

> The backup lives in `%APPDATA%\G350ConnectionManager\backups\` and must survive
> a disconnect **and** an app restart.

---

## 4. Combined scenarios (the ones that bite)

### 4.1 Connect → set IP → disconnect → check

| Step | Expected |
| :--- | :--- |
| ☐ Connect, Apply `192.168.7.1` | Successful |
| ☐ Unplug | Pill → Not connected; **no crash** |
| ☐ Check `ncpa.cpl` | Adapter may linger, still holding `192.168.7.1` — this is normal Windows behaviour |
| ☐ Replug | Pill → Connected; Card 1 shows `192.168.7.1/24` |
| ☐ **Restore** still available | Backup survived |

### 4.2 Connect → set IP → restart app → check

| Step | Expected |
| :--- | :--- |
| ☐ Connect, Apply a custom IP | Successful |
| ☐ Close the app completely | |
| ☐ Reopen the app | Card 1 reflects the **applied** address |
| ☐ **Restore previous** | Enabled — backup loaded from disk |
| ☐ Restore | Returns to the original config |

### 4.3 Connect → set IP → disconnect → reconnect to a *different* PC state

| Step | Expected |
| :--- | :--- |
| ☐ Apply `192.168.7.1`, then toggle Windows ICS **on** | ICS may rewrite to `192.168.137.1` |
| ☐ App reflects reality | Card 1 shows whatever ICS actually set, not what was applied |
| ☐ Restore | Restores the **snapshot taken before Apply**, not the ICS value |

### 4.4 Rapid apply/restore cycling

| Step | Expected |
| :--- | :--- |
| ☐ Apply → Restore → Apply → Restore (4×) | Each cycle succeeds |
| ☐ Buttons never get stuck disabled | |
| ☐ Activity log | One clear entry pair per cycle |
| ☐ No orphaned snapshots | `backups\` has at most one file per adapter |

### 4.5 Adapter name changes between sessions

**Observed on hardware:** unplugging and replugging created `Ethernet 4` where
`Ethernet 2` had been.

| Step | Expected |
| :--- | :--- |
| ☐ Apply an IP on `Ethernet 2`, unplug, replug as `Ethernet 4` | App re-detects the new name automatically |
| ☐ Card 1 shows the current adapter name | Not the stale one |
| ☐ Restore | Applies to the **current** adapter |

> Backups are keyed by adapter name, so a renamed adapter keeps its own
> snapshot rather than silently restoring another adapter's settings.

### 4.6 Unplug during Apply

| Step | Expected |
| :--- | :--- |
| ☐ Click **Apply**, then unplug before approving UAC | Hind: adapter not found / graceful message |
| ☐ App does not crash | |
| ☐ Replug → state consistent | Card 1 shows the real current config |

---

## 5. Regression checks (Stage 1–2 must still work)

| | Check |
| :--- | :--- |
| ☐ | Sidebar collapse/expand — icons stay on the same axis, nothing jumps |
| ☐ | Dark theme + all six accent colours still apply |
| ☐ | Settings persist across restart (theme, accent, collapsed) |
| ☐ | Files page fills the window at 940×600 and at 1920×1080 |
| ☐ | Navigating Home ↔ Network ↔ Screen repeatedly keeps live updates working |
| ☐ | No UAC prompt unless **Apply** or **Restore** is pressed |

---

## 6. Findings

Record anything that failed, with enough detail to reproduce.

| # | Case | What happened | Expected | Severity |
| :--- | :--- | :--- | :--- | :--- |
| | | | | |

### Severity guide
* **Blocker** — data loss, wrong IP applied silently, app crash.
* **Major** — feature unusable, or UI shows something untrue.
* **Minor** — cosmetic, wording, or a rare edge case.

---

## 7. Cleanup

| | Check |
| :--- | :--- |
| ☐ | Restore the adapter to its original configuration |
| ☐ | `%APPDATA%\G350ConnectionManager\backups\` is empty when nothing is pending |
| ☐ | `ncpa.cpl` shows the adapter back on DHCP (if that is where it started) |
