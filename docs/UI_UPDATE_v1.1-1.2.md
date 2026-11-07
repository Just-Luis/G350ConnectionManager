# UI Update — Connect, Share Wi-Fi, and the sharing-state fixes

**Releases:** `v1.1.0` → `v1.2.0`
**Scope:** the Home page, the Network page, the sharing service, and the link probe.
**Status:** all four releases built, tagged, published, and verified on hardware.

This document records what changed, why, and what measurement supported each
change. It covers the work done after `v1.0.5` (the clean-up pass).

---

## 1. Summary

Four releases, each independently testable:

| Release | Change |
| :--- | :--- |
| **1.1.0** | Connect button and Share Wi-Fi switch added to Home; sharing logic moved into a shared coordinator |
| **1.1.1** | Sharing controls made reachable before any status read, and from either page |
| **1.1.2** | Disabled adapters and a disabled ICS service are reported correctly and can be repaired |
| **1.2.0** | The Network page's two buttons replaced by the same switch; turning sharing off no longer breaks the console link |

Net effect across the four: **+1050 / −209 lines** in 10 files, of which one is new
(`SharingCoordinator.cs`, 235 lines).

---

## 2. The user-visible changes

### 2.1 Connect (Home)

Runs a full probe and reports what it found.

The case that previously caused the most confusion is now named explicitly:

> *"The console is reachable but its services are stopped. Press [A] on the
> console, or turn on Share Wi-Fi."*

Before this, "adapter up, console reachable, port 22 closed" rendered identically
to a broken connection. The link is fine in that state; only the device can start
its own SSH server.

### 2.2 Share Wi-Fi (Home and Network)

A single slide switch, rendered identically on both pages.

Behaviour:

| Situation | Result |
| :--- | :--- |
| Toggle succeeds | Reports whether the console **reached the internet**, not merely that Windows accepted the change |
| Toggle fails | The knob **snaps back** — the switch never claims a state Windows refused |
| Status probe runs | Does not override the switch once the user has operated it |
| No status read yet | Switch is still usable; either direction reads state first |

### 2.3 One switch, two pages

Both pages subscribe to a single `SharingCoordinator` instance. Operating the
switch on either page moves the other immediately.

Verified by driving the UI and reading both switches back through UI Automation:

```
clicked Network switch : On → Off
Home switch            : Off          (same moment, no manual refresh)
```

---

## 3. Defects found and fixed

Each of these was reproduced before being changed, and re-tested afterwards.

### 3.1 The sharing buttons were inside a collapsed panel

The controls lived in `IcsDetail`, which starts `Collapsed` and only expands once a
snapshot has been rendered. Before pressing **Check** there was nothing on screen
to press at all — and a user who had enabled sharing from Home had no way to stop
it from the Network page.

### 3.2 Both buttons were disabled after a status read

```csharp
var canShare = console is not null && s.Connections.Count > 1;
IcsEnableButton.IsEnabled = !_icsBusy && canShare;
IcsDisableButton.IsEnabled = !_icsBusy && canShare;
```

The controls greyed out precisely when the user asked for status. Combined with
3.1 — one button always hidden, both possibly disabled — there were states in
which no control was reachable.

### 3.3 Each page kept a private copy of the sharing state

`HomeView` and `NetworkView` each constructed their own `SharingCoordinator`. A
toggle on Home was invisible to the Network page, which would then show
*"not checked"* and a stale control over a connection that was already shared.

### 3.4 A disabled adapter was reported as "no adapter found"

Measured on hardware:

| Adapter state | `.NET NetworkInterface` | `Get-NetAdapter` |
| :--- | :--- | :--- |
| Enabled | 1 entry | `Up` |
| **Disabled** | **0 entries** | `Disabled` |

A network adapter that Windows has administratively disabled does not appear in
`NetworkInterface.GetAllNetworkInterfaces()` at all. Enumerating through .NET
alone therefore produced:

> *"No USB Ethernet adapter found — is the console connected and in PC mode?"*

for a console that was plugged in the whole time. The message sent the user to
check the cable, the console's power and its USB mode — none of which was the
problem. The probe now asks the operating system as well and reports the actual
cause.

Detection only. Enabling a network adapter is machine-wide state, so it stays a
decision the user makes rather than something a 4-second status poll does.

### 3.5 The ICS service advice could not be followed

`IcsHealth()` said *"Start it and try again"* when `SharedAccess` was not running.
That is impossible when the service's start type is **Disabled**, and the app
offered no way out.

`--ics-repair` now restores the start type to Manual and starts the service.

A defect was found while testing this: **a disabled service can still report
`Running`**, because Windows leaves the process up and only refuses to start it
again. Checking "is it running" first made the repair answer *"already running"*
and leave the service disabled. The start type is now examined before the running
state.

### 3.6 The shared adapter was left on an unrelated address

`EnableSharing` does not always renumber the private interface into the ICS
scope. It has been observed leaving the adapter on **`192.168.0.1`** — unrelated
to anything this project uses — while reporting success.

The console only ever carries `192.168.7.2` and `192.168.137.2`, so it ends up
with no gateway while the panel shows sharing as active.

`EnsureScopeAddress()` now runs inside the already-elevated helper, so it costs no
extra elevation prompt:

1. waits briefly for Windows to renumber on its own
2. otherwise sets the scope address read from the registry, falling back to
   `ScopeAddressBackup`, then `192.168.137.1`
3. reads the address back rather than trusting `netsh`'s exit code
4. reports what it changed

### 3.7 Turning sharing off broke the console link

ICS asserts its scope address on the private interface and **removes it when
sharing stops**, leaving the adapter with no IPv4 configuration at all:

```
after disable (before this fix)
  addr : 169.254.29.110/16      <- bare link-local, no real address
  ping : no reply
```

The console was still plugged in and still configured for the direct subnet, so
every feature needing the link — SSH, SFTP, screen preview, recording, the
console tab — stopped working the moment sharing was turned off, even though
nothing about the console had changed.

`RestoreDirectAddress()` returns the adapter to `192.168.7.1` after a successful
disable, but **only when it has no usable address of its own**, so a
configuration the user chose is never overwritten.

### 3.8 A clipped card title

`Internet sharing` rendered as `Internet sharir` in the Home page cards: a 17px
bold title in a 5-column grid could not fit its column. Card titles now wrap.

Pre-existing, found from a screenshot during this work.

---

## 4. Architecture

The sharing sequence is now defined once:

```
read state
  -> resolve the console by GUID (not name: a name belongs to the USB port)
  -> pick an internet source (driver description, not adapter name)
  -> enable or disable
  -> ensure the adapter holds the scope address   (on enable)
  -> return the adapter to the direct address     (on disable)
  -> re-apply the console's networking
  -> verify the console actually reached the internet
```

This lives in `Services/SharingCoordinator.cs` and is shared by both pages.
`NetworkView` lost its private copy of the flow — **157 lines** removed in the
first commit of this series — so a future fix applies to both pages at once.
Across the four releases `NetworkView` went from 660 to 606 lines while gaining
the shared-state subscription.

`SharingCoordinator` is an application-level singleton (`App.Sharing`). It holds
the last snapshot and raises `Changed`, which both pages subscribe to.

---

## 5. Verification

Every change below was exercised against the real console and the real Windows
machine, not reasoned about.

| Test | Result |
| :--- | :--- |
| Clean build from empty `obj`/`bin` | 0 warnings, 0 errors |
| App launches | window handle present, title `G350 Companion`, responding |
| Both switches render | confirmed by screenshot on both pages |
| Cross-page sync | Network switch `On → Off`; Home switch read `Off` via UI Automation |
| Disabled adapter detection | `.NET` 0 entries / `netsh` `Disabled` → correct message; adapter restored |
| `--ics-repair` on disabled service | `Disabled → Manual`, service running |
| `--ics-repair` on healthy service | no-op, exit 0 |
| Enable via the app | `ipnat` Stopped → Running, adapter `169.254.x → 192.168.137.1`, forwarding on, console replies on `.137.2` |
| Disable via the app | adapter `192.168.137.1 → 192.168.7.1`, forwarding off, console replies on `.7.2` |

The full round trip, one process invocation per step:

```
after enable  : 192.168.137.1  forwarding on   console replies on .137.2
after disable : 192.168.7.1    forwarding off  console replies on .7.2
```

The console stays reachable in **both** states.

---

## 6. Deliberately not done

### 6.1 Restoring `ScopeAddress` / `ScopedAddress` registry values

Sharing works with those values absent — measured with `ScopeAddress` missing
before and after a fully successful enable. `EnsureScopeAddress()` already places
the *adapter* on the right address, which is the thing that actually matters.

Writing ICS internals is what damaged this class of machine originally, and the
app no longer contains any registry deletion path.

### 6.2 Enabling a disabled adapter automatically

Detection is implemented; the fix is left to the user. Enabling a network adapter
is a machine-wide change and should be a conscious decision, not a side effect of
a status probe.

### 6.3 Rewriting published history

Commits `v1.0.3`–`v1.0.5` were already withdrawn before this work; nothing here
rewrites history further.

---

## 7. Known limitations

- **`ipnat` starts only after a successful enable.** It remains `Stopped` while
  sharing is off, which is correct, but it means the adapter address and the NAT
  driver must both be checked when diagnosing.
- **`EnsureScopeAddress` waits up to ~3 s** for Windows to renumber before
  overriding. A machine that renumbers slowly will see the app set the address
  rather than Windows doing it. Both end up correct.
- **`RestoreDirectAddress` assumes `192.168.7.1`.** That matches the console's
  own script. A machine configured for a different direct subnet will need that
  value changed.
- **No automated UI test.** The switch sync was verified by driving the real
  window through UI Automation; there is no regression test in CI for it.

---

## 8. Release history

```
v1.2.0  e3ad16c  Replace the sharing buttons with one switch shared by both pages
v1.1.2  5b08582  Detect a disabled console adapter, and repair a disabled ICS service
v1.1.1  dc13b97  Make sharing controllable from either page, before any status read
v1.1.0  49b9e17  Add Connect and Share Wi-Fi controls to the Home page
```

---

## 9. Files changed

| File | Change |
| :--- | :--- |
| `Services/SharingCoordinator.cs` | **New.** The sharing sequence and the shared state |
| `Services/IcsService.cs` | `EnsureScopeAddress`, `RestoreDirectAddress`, `IsServiceDisabled`, `RunRepair`, `--ics-repair` |
| `Services/LinkProbe.cs` | Disabled-adapter detection |
| `Views/HomeView.xaml(.cs)` | Connect button, Share Wi-Fi switch, shared-state subscription |
| `Views/NetworkView.xaml(.cs)` | Two buttons replaced by one switch; the duplicated sharing flow removed in the first commit of the series |
| `Themes/Controls.xaml` | `ToggleSwitchStyle` |
| `App.xaml.cs` | `App.Sharing` singleton, `--ics-repair` entry point |
