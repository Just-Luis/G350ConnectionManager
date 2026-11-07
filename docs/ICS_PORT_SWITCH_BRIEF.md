# ICS + USB port changes — problem brief

**For:** a second opinion / specialist review.
**Question I want answered:** *how do we make Windows Internet Connection Sharing survive the console being moved to a different USB port, without giving up ICS?*

I am not looking for a rewrite of the app. I want to know whether the ICS behaviour described below is fixable in code, fixable by configuration, or is a Windows limitation that has to be worked around at the network-configuration level.

---

## 1. The setup

A Windows 11 Pro PC shares its Wi-Fi internet with a handheld console over a USB cable.

| Side | Detail |
| :--- | :--- |
| **PC** | Windows 11 Pro, build 26200 (25H2-era). Intel AX201 Wi-Fi, 192.168.1.100, gateway 192.168.1.1. |
| **Console** | Rockchip RK3326 handheld, Debian 13 (trixie), kernel `4.4.189`, Python 3.13.5. |
| **USB link** | The console runs `g_ether` (RNDIS gadget). Windows enumerates it as *Remote NDIS based Internet Sharing Device*. Console interface `usb0`, MTU 1500, `dr_mode=peripheral`. |
| **Console addressing** | `usb0` deliberately carries **both** `192.168.7.2/24` and `192.168.137.2/24`. Default route points at whichever gateway answers. |
| **PC addressing** | The console adapter carries **both** `192.168.7.1/24` and `192.168.137.1/24`. |
| **Internet sharing** | Windows ICS (the `SharedAccess` service, `HNetCfg.HNetShare` COM API). Wi-Fi is the *public* (source) connection, the RNDIS adapter is *private* (destination). |
| **Client app** | C# / .NET 8 WPF, `asInvoker`, re-launches itself elevated per privileged operation. |

Two subnets exist because ICS always assigns `192.168.137.x` to the shared adapter, while the documented "no sharing" setup is `192.168.7.x`. The console supports both so that file transfer (SFTP on port 22) keeps working whether sharing is on or off, since the PC's address changes when sharing toggles.

**When it works, it works very well** — that is why I do not want to remove ICS:

- SFTP transfers at ~25–35 MB/s
- Latency on the USB link: 1.5 ms, 0 % loss
- The PC's own internet is unaffected (25 ms, 0 % loss)
- Console internet is fast enough for PortMaster and box-art scraping

---

## 2. The failure

### 2.1 What triggers it

The console's network adapter is **bound to the physical USB port**, not to the device. Moving the cable creates a new adapter with a new name:

```
USB port 1  ->  "Ethernet 2"
USB port 2  ->  "Ethernet 4"
USB port 3  ->  "Ethernet 5"
```

A port move therefore looks like *"one RNDIS adapter disappeared and a different one appeared"*. The device is the same, the console is the same, only the Windows-side port association changed.

Worse, the old ports' adapters are not removed. Windows keeps them as **ghost PnP devices**, so at times there are four RNDIS-ish entries in the system:

```
Status   FriendlyName                                 InstanceId
OK       Remote NDIS based Internet Sharing Device #2  USB\VID_0525&PID_A4A2\5&1134133F&0&1
Unknown  Remote NDIS based Internet Sharing Device     USB\VID_0525&PID_A4A2\5&1134133F&0&2
Unknown  Remote NDIS based Internet Sharing Device #3  USB\VID_0525&PID_A4A2\5&1134133F&0&3
Unknown  Remote NDIS Compatible Device                 USB\VID_0525&PID_A4A2\5&1134133F&0&4
```

Only one is ever present at a time, but `HNetCfg.HNetShare.EnumEveryConnection` may still surface entries for the absent ones.

### 2.2 Symptoms

1. **Unplug while sharing is active, then press "Turn off sharing":**
   `EnableSharing`/`DisableSharing` both require the connection objects. The app used to look both adapters up by name and abort if either was missing:
   `"Adapter 'Ethernet 2' was not found. Reconnect the console and try again."`
   Result: **Wi-Fi stayed shared**, and ICS kept a `PrivateConnection` reference to an adapter that no longer existed.

   *Already fixed — see §3.1.* The disable path now clears sharing on every connection without any lookup, and this case works.

2. **Reconnect on a different port and press "Turn on sharing":**
   ```
   System.Runtime.InteropServices.COMException: An event was unable to invoke any of
   the subscribers (0x80040201)
   ```
   This is the core unresolved problem.

3. **The UI can lie about the state.** After a port move, the panel showed:
   ```
   Internet sharing is on (WiFi -> ?)       <- public exists, private is gone
   Share to     -                           <- no private connection
   ▲ source  up  WiFi                       <- sharing type 0 (public)
      up      Ethernet 4                    <- present but NOT shared
   ```
   ICS believed it was sharing, but nothing was actually shared to the console. The console reported `gateway 192.168.137.1` yet had no internet, because the adapter held the wrong address (`192.168.0.1` in one observed instance).

---

## 3. What I have already established (measured, not assumed)

### 3.1 Fixed so far

| Change | Effect |
| :--- | :--- |
| Disable path clears **all** sharing via `EnumEveryConnection`, no adapter lookup | Unplug → "Turn off sharing" now works. Verified on hardware. |
| `DisableSharing()` result is re-read and reported honestly (was `Ok = true` unconditionally) | A failed disable no longer claims success. |
| Target adapter resolved by **GUID first**, then name | Survives the `Ethernet 2 → 4 → 5` rename. |
| `ConsoleFor(mac)` prefers the adapter whose MAC matches the live one | Avoids picking a ghost RNDIS entry. |
| `IcsHealth()` pre-flight before enabling | Checks `SharedAccess` running and `ScopeAddress` present; refuses with a real sentence instead of a bare COM code. |
| `--ics-diag` elevated read-only dump | Prints services, the `SharedAccess\Parameters` registry values, and the live connection list with sharing type/status/address/GUID. |

### 3.2 The `ScopeAddress` discovery — important

`HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters` was found with **`ScopeAddress` deleted** (only `ScopeAddressBackup = 192.168.137.1` remained). This was almost certainly caused by an earlier repair routine in our own app that deleted the value and never wrote it back.

With `ScopeAddress` missing, **every** `EnableSharing` call failed with `0x80040201` — private *and* public, on the console adapter, the Realtek NIC and the Hyper-V switch alike.

After restoring `ScopeAddress = 192.168.137.1` and restarting `SharedAccess`:

```
private on the console adapter : FAIL 0x80040201
public on WiFi                 : OK
```

**So the missing value was a real cause of `0x80040201` for the public side, but the private side still failed.**

Current state: `ScopeAddress = 192.168.137.1`, `ScopeAddressBackup = 192.168.137.1`, `SharedAccess` RUNNING, `NetBT` RUNNING, `ipnat` driver RUNNING, `BFE` RUNNING.

### 3.3 Things ruled out

| Hypothesis | How it was ruled out |
| :--- | :--- |
| Broken Wi-Fi / uplink | PC → 1.1.1.1 measured 0 % loss, ~25 ms while ICS was misbehaving |
| Broken USB link | Console → gateway 1.5 ms, 0 % loss, 20/20 packets |
| Broken adapter or driver | `EnableSharing(0)` on the same Wi-Fi adapter succeeds |
| Adapter address conflict | Reproduced with the adapter on `192.168.137.1` only, and on `192.168.7.1` only |
| NetBT wedge | The earlier session did show `NetBT` in `STOP_PENDING`; after a reboot it reports **RUNNING** and the failure persists |
| Group policy restrictions | No ICS policy key present; `NC_PersonalFirewallConfig = 0` only |
| Third-party NDIS filter | Adapter bindings show only first-party components (`ms_tcpip`, `ms_server`, `ms_l2bridge`, …) |
| Firewall / WFP blocking forwarding | Turning all three firewall profiles **off** did not fix it |
| Stale `PublicConnection` / `PrivateConnection` registry values | Both are currently **`<MISSING>`**, yet `0x80040201` persists |
| Killing and restarting `SharedAccess` | Done repeatedly, including the "kill the hosting process, delete recorded connections, restart" sequence. Does not help. |

### 3.4 Current blocker

As of now the machine has **no sharing configured** and the console adapter (`Ethernet 4`) holds both `192.168.7.1/24` and `192.168.137.1/24`.

The last conclusive private-side test was:

```
EnableSharing(1) on "vEthernet (Default Switch)"  -> FAIL 0x80040201
EnableSharing(1) on "Ethernet 4" (console)        -> FAIL 0x80040201
EnableSharing(0) on "WiFi"                        -> OK
```

Private sharing fails on an adapter that has nothing to do with the console, which points at something global.

**Caveat I want the reviewer to weigh:** the only time I tested the public/private *pair* together, `ScopeAddress` had just been deleted. It is possible private is fine in a healthy state and I have not yet tested it after a clean reboot with `ScopeAddress` intact. That test is cheap and should happen before deeper work.

---

## 4. Constraints on any solution

1. **The console side is hard to change.** `usb0` must keep answering on `192.168.137.2` (for ICS) and `192.168.7.2` (direct mode). The console already adds both. Changing its addressing scheme means changing a shell script on the device.
2. **The app must stay `asInvoker`** and elevate one operation at a time. It may not install a service, driver, or permanent daemon.
3. **No registry surgery as a fix.** Deleting `ScopeAddress` / `PublicConnection` / `PrivateConnection` is what damaged this machine in the first place. Any proposal that mutates ICS internals needs a compelling reason and a restore path.
4. **The console is frequently unplugged without warning.** Cable pulls are the normal case, not the exception. A design that assumes a clean "stop sharing, then unplug" sequence is not acceptable.
5. **Only one RNDIS adapter is present at a time**, but ghost adapters persist and can appear in `EnumEveryConnection`.

---

## 5. The specific questions I need answered

1. **Is `0x80040201` on `EnableSharing(1)` (private) after a port change a known Windows ICS limitation?** Is there any documented or reliable way to make ICS accept a *newly appearing* adapter as the private destination after the previous one disappeared without a reboot?

2. **Is `EnumEveryConnection` supposed to include ghost/absent adapters?** If so, is there a supported filter for "currently present" (e.g. via `NetConnectionProps.Status`, `INetConnection::GetProperties`, or the GUID against `Get-NetAdapter`)?

3. **Does `EnableSharing` require the private adapter to be in a specific state first?** Candidates I have not been able to confirm: no default gateway configured, no static address (DHCP not available on this link — there is no DHCP server), IPv6 disabled, `NetBIOS over TCP/IP` enabled, "Client for Microsoft Networks" bound, or the connection profile category (Public/Private/Domain).

4. **Is resolving the target by GUID sufficient**, or does ICS internally key on something derived from the port (device instance path, `NetCfgInstanceId`), such that a new port is always a new identity as far as ICS is concerned?

5. **Would a reboot-free repair exist?** Ideas I would like evaluated:
   - `Restart-NetAdapter` / disable–enable the RNDIS adapter before enabling sharing (does that make Windows re-register it with ICS?)
   - Restarting `SharedAccess` **before** the adapters change (e.g. when the app notices the console disappearing)
   - Using `hnetcfg`'s `INetSharingManager::get_INetSharingConfigurationForINetConnection` on a *freshly created* manager instance per call
   - `INetSharingPublicConnectionCollection` / `INetSharingPrivateConnectionCollection` instead of per-connection `EnableSharing` (is that the API the Network Connections dialog actually uses?)
   - Whether `EnableSharing(1)` should be called on the *private* side while the public side is **still enabled**, vs. after clearing everything (current code clears all first, then private, then public)

6. **If ICS genuinely cannot survive a port change without a reboot**, is the intended Microsoft-supported pattern to run ICS on top of a **stable virtual adapter** — e.g. a Hyper-V internal switch, a `Microsoft KM-TEST Loopback Adapter`, or a TAP/Wintun interface — with the RNDIS adapter bridged or NATed to it? In that design, ICS would only ever see the stable adapter, and the port churn would happen below it. I would like this evaluated specifically for:
   - whether bridging an RNDIS adapter is reliable (it drops carrier on unplug)
   - whether ICS tolerates the *public* side changing instead
   - whether a Hyper-V internal switch + ICS is a supported combination

---

## 6. What I am *not* asking for

- A recommendation to replace ICS with `New-NetNat`. I have already prototyped that and it works, but it measured around 11 Mbit/s and I would rather keep ICS's throughput. It is my fallback, not my goal.
- A rewrite of the app. The ICS surface is ~550 lines in one service class and can be changed freely *if* the underlying behaviour can be made reliable.

---

## 7. How to interrogate the machine

The app has a read-only diagnostic built in:

```
G350ConnectionManager.exe --ics-diag %TEMP%\ics.txt
```

Run unelevated it reports services, registry and reachability; elevated it also dumps the live `HNetCfg.HNetShare` connection list. It changes nothing.

Reproducing the fault in three steps:

1. Console plugged in, sharing on (Wi-Fi → console adapter), console has internet.
2. Unplug the cable. (Do **not** press "Turn off sharing".)
3. Plug the cable into a **different USB port**. Press "Turn on sharing".
   → `0x80040201`.

The same sequence without step 2's port change, i.e. reconnecting to the **same** port, works.

---

## 8. Machine facts for reference

```
Windows 11 Pro 26200

Adapters (while console on port 2):
  Ethernet 4                Up            Remote NDIS based Internet Sharing Device #2
  WiFi                      Up            Intel(R) Wi-Fi 6 AX201 160MHz
  vEthernet (Default Switch) Up           Hyper-V Virtual Ethernet Adapter
  Ethernet                  Disconnected  Realtek PCIe GbE

Addresses on the console adapter:  192.168.137.1/24 (Manual), 192.168.7.1/24 (Manual)

HKLM\...\Services\SharedAccess\Parameters
  ScopeAddress         = 192.168.137.1
  ScopeAddressBackup   = 192.168.137.1
  PublicConnection     = <MISSING>
  PrivateConnection    = <MISSING>
  ServiceDll           = C:\WINDOWS\System32\ipnathlp.dll
  ServiceDllUnloadOnStop = 1

Services: SharedAccess RUNNING (Start=Manual), NetBT RUNNING, ipnat RUNNING, BFE RUNNING
ipnathlp.dll loaded in the svchost hosting SharedAccess
No ICS group policy restrictions; firewall profiles Domain/Private/Public all enabled
```

Console side:

```
Debian 13 (trixie), kernel 4.4.189, python 3.13.5
usb0: MTU 1500, inet 192.168.7.2/24 + 192.168.137.2/24
default via 192.168.7.1 dev usb0
modules: g_ether, usb_f_rndis, usb_f_eem, u_ether, libcomposite
/proc/device-tree/usb@ff300000/dr_mode = "peripheral"
```

---

## 9. ⚠️ RESOLVED — superseded by G350Wired v1.7.0

**Do not act on sections 1–7 above.** The failure this brief asks about was root-caused and fixed. Kept for the measurement record only.

### The actual cause

The RNDIS gadget reported **no USB serial number**, so Windows keyed the device on the physical path to it:

```
USB\VID_0525&PID_A4A2\5&1134133F&0&1   port 1
USB\VID_0525&PID_A4A2\5&1134133F&0&2   port 2
USB\VID_0525&PID_A4A2\5&1134133F&0&3   port 3
```

Every port was a **distinct device** with its own name (`Ethernet 2 / 4 / 5`), GUID and empty network configuration. Moving the cable discarded the sharing setup and left a ghost behind.

**Fix** (G350Wired `v1.7.0`): the console's factory-burned SoC serial is passed to the gadget.

```
USB\VID_0525&PID_A4A2\G350-C3D9B8674F4B94F6
```

Verified on hardware — unplug, move port, replug: same adapter, same address, sharing resumes immediately, no elevation prompt.

### Corrections to this brief's claims

| Claim in §3 | Reality |
| :--- | :--- |
| Missing `ScopeAddress` causes `0x80040201` on every adapter | **Wrong.** With `ScopeAddress` absent and the adapter correctly addressed, both `EnableSharing(1)` and `EnableSharing(0)` succeed — ICS writes the scope itself on first enable. The original observation was taken while the adapter sat on the wrong subnet, so addressing was blamed on the registry value. |
| Private-side `EnableSharing(1)` is globally broken on this machine | **Wrong.** It was broken *because* the adapter was on the wrong subnet with forwarding disabled. On a correct configuration it succeeds. |
| The NetBT wedge explains the failures | **Wrong.** After a reboot `NetBT` reports RUNNING and the fault persisted. |
| Ghost adapters holding `192.168.137.1` causes the conflict | **Unsupported.** Sharing enabled successfully *with* all three ghosts present. |

### Footnote — other gadget loaders

`USB_Network_Manager.sh` is the only script that passes the serial. These still load the module without one:

- `G350Wired/.usb_network/Start_Wired_Services.sh`
- `G350Wired/.usb_network/c2c_setup.sh`
- `G350Wired/.usb_network/C2C_Connection_Test.sh`
- `C2Cconnection NetPlay/.C2C/c2c_setup.sh`
- `C2Cconnection NetPlay/C2C_Netplay_Prep.sh`

Harmless in practice — `g_ether` is normally already loaded at boot, and the first load wins — but if one of these ever runs first, the console reverts to port-keyed identity and the bug returns. Low priority; worth aligning if those paths are used regularly.
