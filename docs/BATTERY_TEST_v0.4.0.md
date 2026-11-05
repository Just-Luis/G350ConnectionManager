# 🧪 Battery Test — Stage 4a (Internet Sharing status, read-only)

Read-only. **Nothing in this stage changes any adapter, route, or service.**
If a check fails, no repair step is needed.

Stage 4a exists because the Windows ICS COM API **refuses to answer at all**
without elevation — un-elevated, `EnumEveryConnection` yields one null entry
and `EnumPublicConnections` throws `E_ACCESSDENIED`. So the app reads ICS by
re-launching **its own binary** elevated with `--ics-probe`. That is the only
elevated operation in this stage.

---

## 1. Pre-flight

| Step | Expected |
| :--- | :--- |
| ☐ Build/run `v0.4.0` | App opens normally, no UAC prompt at startup |
| ☐ Console plugged in, services running | Sidebar shows **Connected** and the console IP |
| ☐ Open **Network & sharing** | Step 1 shows the adapter; Step 2 shows *"Internet sharing — not checked"* with a **Check** button |

> The panel deliberately does **not** read automatically. A UAC prompt every
> time the page opens would be hostile, so it waits to be asked.

---

## 2. Reading ICS — approved

| Step | Expected |
| :--- | :--- |
| ☐ Click **Check** | Windows UAC prompt appears |
| ☐ Click **Yes** | Button becomes **Refresh**; dot and text update |
| ☐ Status line | *"Internet sharing is off (service idle)"* with a **grey** dot |

**Expected detail block:**

| Field | Expected on a machine that has never shared |
| :--- | :--- |
| Share from | *nothing is being shared* |
| Share to | `—` |
| ICS address | `192.168.137.1  (ICS always uses this subnet)` |
| Connection list | Every adapter, with `up`/`down` and blank share column |

> `ScopeAddress` is `192.168.137.1` even when nothing is shared — Windows
> pre-seeds it. That is why the `.137.x` subnet exists at all, and it is the
> root of the `.7.x` vs `.137.x` confusion.

---

## 3. Reading ICS — declined

| Step | Expected |
| :--- | :--- |
| ☐ Click **Refresh**, click **No** on the prompt | *"Internet sharing — could not be read"* with an **amber** dot |
| ☐ Step 1 hint | *"Administrator approval is required to read internet sharing. Nothing was changed."* |
| ☐ Detail block | Hidden (no stale data from the previous successful read) |
| ☐ Click **Refresh** again, click **Yes** | Recovers; hint clears |

> The hint clearing on recovery is a real bug that was caught during
> development: a failed read left its explanation in place, so a later
> successful read still displayed "approval is required".

---

## 4. With sharing actually ON

Set this up in Windows: **Settings → Network → Advanced network settings →
Network and Sharing Center → Change adapter settings → right-click WiFi →
Properties → Sharing → "Allow other network users to connect…" → Home
networking connection: `Ethernet 2`**.

| Step | Expected |
| :--- | :--- |
| ☐ Enable sharing to `Ethernet 2`, then **Refresh** | *"Internet sharing is on (WiFi → Ethernet 2)"* with a **green** dot |
| ☐ Share from | `WiFi · Intel(R) Wi-Fi 6 AX201 160MHz` |
| ☐ Share to | `Ethernet 2 · Remote NDIS based Internet Sharing Device` |
| ☐ Connection list | `WiFi` marked **▲ source**, `Ethernet 2` marked **▼ dest** |
| ☐ **Conflict warning** | Amber box explaining the console is on `.137.x` while sharing is on |
| ☐ Step 1 card | Adapter now reads `192.168.137.1` — **not** `192.168.7.1` |

> This is the case the warning exists for. While sharing is on, Windows owns
> the adapter address and the `.7.x` value in Step 1 is simply not in effect.

### 4.1 Sharing to the wrong adapter

| Step | Expected |
| :--- | :--- |
| ☐ Point sharing at `vEthernet (Default Switch)` instead | Refresh shows that adapter as **▼ dest** |
| ☐ Warning | *"Sharing is on, but it targets … not the console adapter"* |
| ☐ Console | Still listed, **not** shared |

### 4.2 Sharing backwards

| Step | Expected |
| :--- | :--- |
| ☐ (If you can) make the console adapter the *source* | Refresh marks it **▲ source** |
| ☐ Warning | *"The console adapter is currently the internet source… almost certainly backwards"* |

---

## 5. Theme & layout

| Step | Expected |
| :--- | :--- |
| ☐ Light theme | Panel legible, borders visible, no clipped text |
| ☐ Dark theme | Same, no white flashes |
| ☐ Narrow window | Connection list wraps rather than overflowing |
| ☐ Maximised | Panel stays within the card width, not stretched edge-to-edge |

---

## 6. Regressions (Stages 1–3 must still work)

| Step | Expected |
| :--- | :--- |
| ☐ Home / Screen / Files / Settings pages | Still render, no exceptions |
| ☐ Sidebar collapse/expand | Icons stay aligned |
| ☐ Step 1 **Apply** 192.168.7.1 | Still succeeds, console stays reachable |
| ☐ Step 1 **Restore previous** | Still works |
| ☐ **Test connection** | Reports latency |
| ☐ Navigate away and back | Step 2 resets to *"not checked"* (no stale ICS data) |
| ☐ Close the app | No orphan process; `--ics-probe` helpers have exited |

> Check Task Manager for stray `G350ConnectionManager` processes after closing.

---

## 7. Findings

| # | Check | What happened | Expected | Severity |
| :--- | :--- | :--- | :--- | :--- |
| | | | | |
