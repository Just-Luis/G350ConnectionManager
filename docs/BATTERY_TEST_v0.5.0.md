# 🧪 Battery Test — Stage 5 (Screen preview & screenshots)

Read-only with respect to the console: it uploads a small capture script to
`/tmp`, runs it for the duration of the session, and removes the process when
stopped. Nothing is installed permanently and no console files are modified.

---

## 1. Pre-flight

| Step | Expected |
| :--- | :--- |
| ☐ Console connected, services running | Sidebar **Connected** |
| ☐ Open **Screen** | Preview area shows the placeholder, **Take screenshot** and **Start preview** both enabled |
| ☐ **Save screenshots to** | Shows a real folder path (defaults to `Pictures\G350 Captures`) |
| ☐ **Preview rate** | **Medium** selected, hint reads *"smooth enough for menus and text screens"* |

---

## 2. Live preview

| Step | Expected |
| :--- | :--- |
| ☐ Click **Start preview** | Button becomes **Stop preview** within ~2 s |
| ☐ Preview area | Live console screen appears, scaled to fit, **not black** |
| ☐ Move around the console's menu | The preview follows within a fraction of a second |
| ☐ Status line | `640×480 · N fps · M frames`, fps roughly matching the selected rate |
| ☐ Click **Stop preview** | Returns to the placeholder, button reads **Start preview** |

### 2.1 Rates

| Step | Expected |
| :--- | :--- |
| ☐ Stop, choose **Low**, start | ~3 fps |
| ☐ Stop, choose **High**, start | Higher, but may not reach the target — the hint warns about this |
| ☐ Change rate *while running* | Hint says it applies after restarting |

> High will not hit 12 fps: every frame is a full 1.2 MB capture plus transfer,
> and the round trip costs ~60 ms. That is expected, not a fault.

---

## 3. Screenshots

| Step | Expected |
| :--- | :--- |
| ☐ Click **Take screenshot** | *"Saved g350-<date>.png"* within a few seconds |
| ☐ The file exists | Open the folder — the PNG is there |
| ☐ The image looks right | Same content as the preview, **opaque**, correct colours |
| ☐ **Screenshots** list | New line with time, filename and `640×480` |
| ☐ **Choose folder…** | Picking another folder saves there from then on |
| ☐ Take a screenshot with the preview stopped | Still works (it opens its own short session) |

> If a screenshot looks fully transparent in an image viewer, that is the
> alpha-channel class of bug this stage had: the console stores alpha = 0 in
> every pixel, and the app now forces opaque output.

---

## 4. Console hygiene

| Step | Expected |
| :--- | :--- |
| ☐ While previewing, on the console: `ps aux \| grep g350_grabber` | Exactly **one** process |
| ☐ Press **Stop preview**, then check again | **No** grabber process left |
| ☐ Close the app while previewing, then check | **No** grabber process left |
| ☐ Console menu still responsive during preview | Yes — capture never blocks the device |

```bash
ssh ark@192.168.7.2
ps aux | grep g350_grabber
```

---

## 5. Games / EmulationStation

The console renders through DRM rather than fbdev, and uses a different backend.

| Step | Expected |
| :--- | :--- |
| ☐ On the console press **[B] Exit to EmulationStation** | ES starts |
| ☐ Start the preview | EmulationStation's UI appears, not a black or frozen image |
| ☐ Launch a game, keep previewing | The game is visible |
| ☐ Exit the game | Preview recovers on its own |
| ☐ Console frame rate while previewing a game | Playable; capture must not stall it |

> The grabber maps the DRM buffer, copies it, and unmaps immediately, so it
> never pins memory a game needs.

---

## 6. Regressions

| Step | Expected |
| :--- | :--- |
| ☐ Home / Network & sharing / Files / Settings | Still work |
| ☐ Navigate away from Screen while previewing | Preview stops cleanly (no orphan) |
| ☐ Come back to Screen | Placeholder shown, buttons usable |
| ☐ Turn internet sharing on/off | Still works; console updates itself |
| ☐ Sidebar collapse/expand | Unaffected |

---

## 7. Findings

| # | Check | What happened | Expected | Severity |
| :--- | :--- | :--- | :--- | :--- |
| | | | | |
