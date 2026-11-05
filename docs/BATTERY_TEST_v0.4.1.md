# 🧪 Battery Test — Stage 4b (Internet Sharing toggle)

**This stage changes things.** It rewrites adapter addresses, enables ICS, and
modifies the console's default route. Read §0 first.

| What changes | How to undo |
| :--- | :--- |
| PC adapter `192.168.7.1` → `192.168.137.1` | **Turn off sharing** |
| Console gains `192.168.137.2` | (harmless, coexists with `.7.2`) |
| Console default route → `192.168.137.1` | Reversed on Turn off |
| ICS config (WiFi → Ethernet 2) | **Turn off sharing** |

### 0. If something goes wrong

The app always offers **Turn off sharing** when sharing is on, and that is the
undo. If the app cannot reach the console, undo manually — on the console
(TUI, or SSH as `ark`/`ark`):

```bash
echo ark | sudo -S -p '' ip route replace default via 192.168.7.1 dev usb0
```

and on the PC, right-click the RNDIS adapter → Properties → IPv4 →
**Obtain an IP address automatically**, or use Step 1 **Apply** in the app.

---

## 1. Pre-flight

| Step | Expected |
| :--- | :--- |
| ☐ Console plugged in, services running | Sidebar **Connected**, console at `192.168.7.2` |
| ☐ Wi-Fi on the PC has internet | `ping 1.1.1.1` succeeds from the PC |
| ☐ Open **Network & sharing**, click **Check**, approve UAC | Panel fills; dot **grey**; *"off (service idle)"* |
| ☐ Note the Step 1 card | Reads `192.168.7.1/24` |
| ☐ **Turn on sharing** is the only action shown | **Turn off** is hidden |

---

## 2. Turn sharing ON

| Step | Expected |
| :--- | :--- |
| ☐ Click **Turn on sharing**, approve UAC | Button disables; hint says *"Waiting for Windows to apply it…"* |
| ☐ Wait ~5–15 s | No extra UAC prompts — **one** approval covers the whole operation |
| ☐ Status line | *"Internet sharing is on (WiFi → Ethernet 2)"*, **green** dot |
| ☐ Share from / Share to | `WiFi · Intel(R) Wi-Fi 6 AX201` / `Ethernet 2 · Remote NDIS…` |
| ☐ Connection list | `WiFi` `▲ source`, `Ethernet 2` `▼ dest` |
| ☐ Actions | **Turn off sharing** now shown, **Turn on** hidden |
| ☐ Step 1 card | Now reads `192.168.137.1/24` — **not** `.7.1` |
| ☐ Amber warning | Explains the console is on `.137.x` while sharing is on |

> **If the panel still says "off" after ~20 s**, the helper timed out. It will
> say so explicitly rather than showing a stale panel. Press **Refresh**.

### 2.1 The console actually gets internet

This is the part ICS alone does **not** do.

| Step | Expected |
| :--- | :--- |
| ☐ Route box appears | *"Console is online via 192.168.137.1."*, **green** dot |
| ☐ On the console, `ip route show default` | `default via 192.168.137.1 dev usb0` |
| ☐ From the console | `ping -c1 1.1.1.1` **succeeds** |
| ☐ From the console | `curl -s -o /dev/null -w '%{http_code}' https://api.github.com` → `200` |
| ☐ PortMaster on the console | Can download |

> Without the route repair the console keeps `default via 192.168.7.1`, a
> gateway that no longer exists. SSH keeps working, so the console looks alive
> while having no internet. That was the hardest failure to spot.

### 2.2 Reachability during sharing

| Step | Expected |
| :--- | :--- |
| ☐ `ping 192.168.137.2` from the PC | Replies |
| ☐ `ping 192.168.7.2` from the PC | **Fails** (expected — the PC left that subnet) |
| ☐ Sidebar | Still **Connected**, shows `192.168.137.2` |

---

## 3. Turn sharing OFF

| Step | Expected |
| :--- | :--- |
| ☐ Click **Turn off sharing**, approve UAC | One prompt only |
| ☐ Status line | *"Internet sharing is off"*, **grey** dot |
| ☐ Step 1 card | Back to `192.168.7.1/24` |
| ☐ Route box | *"Console is online via 192.168.7.1"* — or *"cannot reach the internet"*, which is correct now |
| ☐ On the console, `ip route show default` | `default via 192.168.7.1 dev usb0` |
| ☐ `ping 192.168.7.2` from PC | Replies again |
| ☐ Actions | **Turn on sharing** shown, **Turn off** hidden |

> A **fixed delay was not enough** here: the panel used to announce "turned off"
> while still showing sharing as on. The elevated helper now waits for Windows
> to confirm the state before returning.

---

## 4. Edge cases

| # | Case | Expected |
| :--- | :--- | :--- |
| 4.1 | ☐ Decline the UAC prompt on **Turn on** | *"prompt was declined. Nothing was changed."* — state unchanged, buttons usable |
| 4.2 | ☐ Unplug the console, then **Turn on** | *"Adapter 'Ethernet 2' was not found. Reconnect the console…"* |
| 4.3 | ☐ Turn on, then **unplug** the console | No crash; sidebar goes **Not connected** |
| 4.4 | ☐ Turn on twice in a row (via Refresh between) | Second attempt does not double-configure |
| 4.5 | ☐ Turn on, then quit the app, relaunch | Sharing still reported **on** (state is Windows', not the app's) |
| 4.6 | ☐ Turn on with Wi-Fi disconnected | *"No internet connection was found to share."* |
| 4.7 | ☐ Click **Fix route** manually | Re-checks and reports honestly |

---

## 5. Regressions (Stages 1–4a must still work)

| Step | Expected |
| :--- | :--- |
| ☐ Home / Screen / Files / Settings | Render, no exceptions |
| ☐ Sidebar collapse/expand | Icons stay aligned |
| ☐ Step 1 **Apply** / **Restore** | Still work |
| ☐ Step 2 **Check** with sharing off | Still reads correctly |
| ☐ Navigate away and back | Step 2 resets to *"not checked"* |
| ☐ Close the app | No orphan `G350ConnectionManager` processes |

> **Check Task Manager** after closing — the elevated helper exits on its own,
> but confirm nothing is left behind.

---

## 6. Findings

| # | Check | What happened | Expected | Severity |
| :--- | :--- | :--- | :--- | :--- |
| | | | | |
