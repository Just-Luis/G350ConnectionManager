# The Port-Move Bug — what it was, what we changed, where things stand

**Written:** 2026-11-07
**Covers:** the whole investigation, the root cause, every change made, and the exact state of both repositories.

**Short version:** the console's USB network adapter used to be bound to the
*physical USB port* instead of to the console. Moving the cable therefore
created a brand-new Windows network adapter each time, which silently discarded
the internet-sharing configuration. It is now fixed at the source, and internet
sharing survives unplugging and moving ports.

---

## 1. The symptom everyone was chasing

Reported originally as:

> Console on USB port 1 → sharing on → unplug → plug into port 2 →
> "Turn on sharing" fails with `0x80040201`.
> The same sequence **on the same port** works.

Along the way, three further symptoms appeared:

- The adapter was called `Ethernet 2` on one port, `Ethernet 4` on another, `Ethernet 5` on a third — for the **same console**.
- Ghost adapters accumulated: four `Remote NDIS…` entries in Device Manager, three of them absent.
- The app sometimes showed sharing as *on* while the console had no internet.

Three separate investigations had tried to explain this:

| Attempt | Theory | Outcome |
| :--- | :--- | :--- |
| Session A | ICS is wedged; `NetBT` stuck in `STOP_PENDING` | Wrong — after reboot `NetBT` reported RUNNING and the fault persisted |
| Session B | Missing `ScopeAddress` registry value causes `0x80040201` | Wrong — see §5; sharing works with the value absent |
| External review (`propoused solution.txt`) | The gadget reports no USB serial, so Windows keys the device on the port | **Correct** |

---

## 2. Root cause

A USB device's identity in Windows is **vendor id + product id + serial number**.

The RNDIS gadget on the console reported **no serial number at all**:

```
/sys/module/g_ether/parameters/iSerialNumber  →  (null)
```

With no serial to key on, Windows fell back to the **physical path to the
device**:

```
USB\VID_0525&PID_A4A2\5&1134133F&0&1     port 1
USB\VID_0525&PID_A4A2\5&1134133F&0&2     port 2
USB\VID_0525&PID_A4A2\5&1134133F&0&3     port 3
USB\VID_0525&PID_A4A2\5&1134133F&0&4     port 4
```

That `5&<hash>&0&<port>` tail is the giveaway. Every port meant:

- a new PnP device instance
- a new `NetCfgInstanceId` GUID
- a new adapter name (`Ethernet 2` / `4` / `5`)
- a **new, empty network configuration**

So Windows' entire notion of "the shared adapter" was destroyed by moving a
cable — and the orphaned previous instance stayed behind as a ghost.

---

## 3. The fix — G350Wired `v1.7.0`

`USB_Network_Manager.sh` now reads the console's own **factory-burned SoC
serial** and hands it to the gadget.

### Before / after

```
before   USB\VID_0525&PID_A4A2\5&1134133F&0&1   ... &0&2   ... &0&3
after    USB\VID_0525&PID_A4A2\G350-C3D9B8674F4B94F6
```

One identity. No port component. The identity follows the **console**.

### Why a derived serial, not a fixed one

A fixed value such as `G350-0001` was rejected: shipping it in every console
would make two consoles look like the *same device* to one PC, colliding on
adapter name and sharing state.

Instead each console derives its own, from hardware it already has:

```
1. /proc/device-tree/serial-number     ← factory-burned  (c3d9b8674f4b94f6 here)
2. /proc/cpuinfo Serial                ← fallback
3. /etc/machine-id                     ← last resort
```

Nothing to provision. No serial to hand out. Several consoles on one PC remain
distinct.

### What was added

| Symbol | Purpose |
| :--- | :--- |
| `g350_serial()` | Reads the factory serial through the three-source fallback chain. |
| `load_gadget()` | Loads `g_ether` with `iSerialNumber=G350-<serial>` plus a `dev_addr` derived from the same serial, so two consoles cannot present the same MAC. Falls back to a **plain load** if the module rejects the option — losing the stable identity is a nuisance, losing the USB network leaves the console unreachable. |
| 2 call sites | `start_services_cli()` and `start_services()` call `load_gadget` instead of a bare `modprobe g_ether`. |

### Bug found while fixing it

An SSH session gets `PATH=/usr/local/bin:/usr/bin:/bin:/usr/games` — **no
`/sbin`** — and `sudo` on this image sets no `secure_path`. Every
`sudo modprobe g_ether` in the script had therefore **always** failed when
invoked remotely, silently, because the error went to `2>/dev/null`. It only
ever worked from the console's own TUI.

This went unnoticed because `g_ether` is normally already loaded at boot, so the
`if ! lsmod | grep -q g_ether` guard rarely ran.

All `modprobe` calls now use `/sbin/modprobe` by absolute path.

### Verified on hardware

> unplug → move to a different USB port → replug
>
> **Keeps its name. Keeps its IP. Internet sharing resumes immediately. No elevation prompt.**

---

## 4. The four claims in `ICS_PORT_SWITCH_BRIEF.md` that measurement contradicted

That brief asked a second party for help. It has been closed (`§9`) and its
claims corrected in place.

| Claim | Reality |
| :--- | :--- |
| A missing `ScopeAddress` makes **every** `EnableSharing` fail with `0x80040201` | **Wrong.** With `ScopeAddress` absent and the adapter correctly addressed, private and public both succeed. ICS writes the scope itself. |
| Private-side `EnableSharing(1)` is globally broken on this machine | **Wrong.** It failed *because* the adapter sat on the wrong subnet with forwarding disabled. |
| The `NetBT` wedge explains the failures | **Wrong.** After reboot `NetBT` = RUNNING, fault persisted. |
| Ghost adapters holding `192.168.137.1` cause the conflict | **Unsupported.** Sharing enabled successfully *with* all three ghosts present. |

---

## 5. The app bug — G350ConnectionManager `v1.0.4`

`IcsHealth()` refused to attempt sharing whenever `ScopeAddress` was missing:

```csharp
if (string.IsNullOrEmpty(ReadScopeAddress()))
    return "ICS has no scope address configured, so Windows rejects every "
         + "sharing change with 0x80040201...";
```

It returned **before `HNetCfg` COM was ever called**, so the user was shown a
diagnosis for an error the app had never received. On any machine in that state,
sharing was permanently unusable.

### Measured

```
ScopeAddress      = <MISSING>          (before)
SharedAccess      = Running
EnableSharing(1)  = OK                 private, Ethernet 6
EnableSharing(0)  = OK                 public, WiFi
forwarding        = Ethernet 6 Enabled, WiFi Enabled
console           = 192.168.7.2 REPLY
ScopeAddress      = <still MISSING>    (after)
```

### How the wrong conclusion was reached

The original observation was taken while the adapter held **`192.164.7.1`** — a
typo for `192.168.7.1`. `192.164.x.x` is not even RFC-1918. The adapter was on a
subnet the console was not, with forwarding disabled. Sharing failed for *that*
reason and the missing registry value took the blame.

### Changes

| Location | Change |
| :--- | :--- |
| `IcsHealth()` | Gates **only** on `SharedAccess` running. `ScopeAddress` check removed, with a comment recording why it must not return. |
| `ExplainIcs()` | `0x80040201` now lists a **wrong-subnet adapter** first. A missing `ScopeAddress` is no longer listed as a cause. |
| `--ics-diag` | Still reports the value, but states plainly that a missing one is normal before sharing has ever been enabled. |

---

## 6. Repository state

Both repositories are clean, pushed, tagged and released.

| Repo | HEAD | Tag | Release | CI |
| :--- | :--- | :--- | :--- | :--- |
| **G350Wired** | `9bcfa97` | `v1.7.0` | [published](https://github.com/Just-Luis/BatleXP-G350_USBNetworkManager/releases/tag/v1.7.0) | no CI in repo |
| **G350ConnectionManager** | `4c38fe1` | `v1.0.4` | [published](https://github.com/Just-Luis/G350ConnectionManager/releases/tag/v1.0.4) | ✅ success, 65.8 MB EXE attached |

### History rewritten earlier in this work

The releases `v1.0.3`, `v1.0.4` and `v1.0.5` of **G350ConnectionManager** were
withdrawn. They rested on the `NetBT` diagnosis and repaired ICS by deleting
registry values — the very thing that damaged the machine. They were deleted,
their commits orphaned, and `v1.0.3` was reissued from the new tree.

A CI fault was fixed at the same time: `.gitignore` excluded `*.png` with an
exception only for `docs/screenshots`, which also excluded `brand.png` and
`brand_light.png`. Both are `<Resource>` in the project file, so a clean clone
failed with `error BG1002: File 'brand.png' cannot be found`. **Every tagged
build from v1.0.1 through v1.0.5 had failed**, so no release had a binary
attached since v1.0.0.

A backup bundle of the pre-rewrite refs is at `../_backups/G350CM-all-refs-*.bundle`.

---

## 7. Current machine state

**PC** — Windows 11 Pro, build 26200

```
USB identity : USB\VID_0525&PID_A4A2\G350-C3D9B8674F4B94F6
Adapter      : Ethernet 6    Up    MAC 92-C3-34-BB-D5-D6    ifIndex 33
Console IP   : 192.168.7.1/24
ScopeAddress : <MISSING>     harmless — sharing works without it
Forwarding   : Ethernet 6 Enabled, WiFi Enabled
NetNat       : none          (G350-USB-NAT removed)
Ghosts       : 4 RNDIS entries absent, 1 present
```

**Console** — Debian 13 (trixie), kernel 4.4.189

```
hostname     : g350
serial       : c3d9b8674f4b94f6
usb0         : 192.168.7.2/24 + 192.168.137.2/24
script       : /opt/system/Tools/USB_Network_Manager.sh
               /roms/tools/USB_Network_Manager.sh          (md5 identical)
backups      : *.bak-20261106-213742
```

### Note on the adapter name

`Ethernet 6` is a **one-time** artifact: it is the name Windows assigned when it
first met the serial-keyed device. It will never change again — that is the
point of the fix. The name is cosmetic; nothing in either project keys on it.

---

## 8. Things that are still open

Ranked, with honest severity.

### 8.1 Other gadget loaders omit the serial — **low priority**

Only `USB_Network_Manager.sh` passes `iSerialNumber`. These still load the module
bare:

- `G350Wired/.usb_network/Start_Wired_Services.sh`
- `G350Wired/.usb_network/c2c_setup.sh`
- `G350Wired/.usb_network/C2C_Connection_Test.sh`
- `C2Cconnection NetPlay/.C2C/c2c_setup.sh`
- `C2Cconnection NetPlay/C2C_Netplay_Prep.sh`

Harmless in practice: `g_ether` is normally already loaded at boot and the first
load wins. **If one of these ever runs first, the port-keyed identity returns.**
Documented, not fixed, by request.

### 8.2 Ghost adapters not cleaned up — **cosmetic**

Four absent RNDIS entries remain from the old port-keyed days. They are now
permanently inert — the serial-keyed identity never reuses them. Removing them
is `pnputil /remove-device <InstanceId>`, run elevated, and would tidy Device
Manager. Not required for anything to work.

### 8.3 No regression test for the port move — **worth doing**

The fix was verified by hand. There is no automated check that the instance id
stays stable across ports. A short PowerShell script comparing
`InstanceId` before and after a move would turn that into a repeatable test.

### 8.4 `ScopeAddress` left missing — **deliberately not fixed**

The app no longer cares, and ICS does not need it. Writing a value back would be
registry surgery on the component that was previously damaged, for no functional
gain. Leave it.

---

## 9. For anyone picking this up

- **The identity is the whole story.** Anything that keys on the adapter *name*
  is fragile by design; key on the PnP instance id or the GUID.
- **`0x80040201` is not a diagnosis.** It is a generic COM failure. Check the
  adapter is correctly addressed *first*, before suspecting ICS.
- **Measure before concluding.** Three sessions produced three confident wrong
  answers because a test ran against a broken baseline.
- **Don't mutate ICS registry internals.** Deleting `ScopeAddress` and friends
  is what damaged this machine; the values were never the problem.
