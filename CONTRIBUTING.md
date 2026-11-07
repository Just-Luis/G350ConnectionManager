# Contributing

## Commit messages

Use a typed, imperative subject no longer than 72 characters, then a body that
explains what changed and why. No first person and no narrative.

```
<type>(<scope>): <imperative summary, <=72 chars>

<body: what changed and why, no first person, no narrative>
```

| Type | Use for |
| :--- | :--- |
| `feat` | A new user-visible capability |
| `fix` | A defect repair |
| `refactor` | Internal restructuring with no behaviour change |
| `docs` | Documentation only |
| `build` | Project file, CI or packaging changes |
| `chore` | Housekeeping with no behaviour change |

`scope` is optional; use the area affected (`ics`, `screen`, `sftp`, `home`,
`files`, `console`, `settings`).

Worked example:

```
fix(ics): place the adapter on the scope address after enabling sharing

EnableSharing does not always renumber the private interface into the ICS
scope, and has been observed leaving it on an unrelated address while
reporting success. EnsureScopeAddress now runs inside the already-elevated
helper, waits briefly for Windows to renumber, then sets the scope address
read from the registry and reads it back.
```

Keep the subject in the imperative mood: "Add", not "Added" or "Adds".

## Branches and releases

- Work directly on `main`. History is linear and each commit should build.
- Tag releases as `vMAJOR.MINOR.PATCH` following [SemVer](https://semver.org/).
- Bump `<Version>`, `<AssemblyVersion>` and `<FileVersion>` in
  `src/G350ConnectionManager/G350ConnectionManager.csproj` before tagging.
- Add the release entry to `CHANGELOG.md` in the same commit that bumps the
  version.
- Pushing a `v*` tag runs `.github/workflows/build.yml`, which publishes the
  self-contained executable, verifies it, writes `SHA256.txt` and attaches it to
  the release. Do not upload a locally built binary to a release.
- Release titles follow `G350 Companion vX.Y.Z — <summary>` in sentence case.
  The release body is the changelog entry for that version, not an
  investigation report.

## Building

```powershell
# fast framework-dependent build
dotnet build src/G350ConnectionManager/G350ConnectionManager.csproj

# self-contained single file -> out/G350ConnectionManager.exe
dotnet publish src/G350ConnectionManager/G350ConnectionManager.csproj -c Release -o out

# unit tests
dotnet test tests/G350ConnectionManager.Tests/G350ConnectionManager.Tests.csproj
```

A clean build must report zero warnings. `IDE0005`, `IDE0052` and `IDE0035` are
escalated to warnings deliberately, and `GenerateDocumentationFile` is enabled so
malformed doc-comment XML fails to compile.

The test project covers pure logic only: prefix/mask conversion and credential
resolution. Nothing that needs a console or elevated rights is tested there, so
`dotnet test` runs anywhere. CI runs it before publishing.

## Manual test procedure

Run before tagging a release, with the console connected and the console-side
script running. Record pass or fail per step; do not record hardware details.

### Connection

1. Power the console, open USB Network Manager and press **[A] Start Services**.
2. Connect the USB-A to USB-C cable.
3. Open the app. The sidebar shows **Connected** with an address.
4. Press **Connect** on Home. Every diagnostic row reports a result, and the
   adapter, link, ping and SSH rows all pass.
5. Disable the console's network adapter in Windows, then press **Connect**.
   The app reports a disabled adapter, not a missing one.

### Internet sharing

1. With sharing off, turn on **Share Wi-Fi**. Elevation is requested once.
2. The adapter moves to `192.168.137.1` and the console reports internet access.
3. Open **Network & sharing**. The switch state matches the Home page.
4. Operate the switch on the Network page. The Home switch follows immediately.
5. Turn sharing off. The adapter returns to `192.168.7.1` and the console is
   still reachable at `192.168.7.2`.
6. Deny the elevation prompt. The switch snaps back and does not claim the state
   Windows refused.

### Screen

1. Open **Screen** and press **Start preview**. The live console image appears.
2. Change frame rate, resolution, JPEG quality and frame delay while running;
   the preview follows each change without restarting.
3. Press **Take screenshot**. The file appears in the configured folder and opens.
4. Press **Record**, run it for a few seconds, then stop. An MP4 is written and
   plays back.

### Files

1. Open **Files** and connect.
2. Select a folder on each side, upload a small file, then download it back.
3. Confirm both files match the originals.
4. Create a folder, then delete it. Both actions report their result.

### Console

1. Open **Console** and run a quick command from the shortcuts.
2. Output appears. Copy output and save log both work.

### Settings

1. Switch theme. The whole app updates, including the brand mark.
2. Change the highlight colour. Buttons, switches and status colours follow.
3. Enable **Collapsed on launch**, restart, and confirm the sidebar starts
   collapsed.
4. Point screenshots and recordings at different folders and confirm each output
   lands in its own.
5. Leave **Console account** blank and confirm the app connects with `ark` / `ark`.
   Enter a wrong username, reconnect, and confirm the failure is reported as a
   credential problem rather than a missing console.

### Regression checks

- Only one instance of the app runs at a time.
- Closing and reopening preserves theme, accent, sidebar state and folders.
- A clean clone publishes successfully:

```powershell
git clone <url> test && cd test
dotnet publish src/G350ConnectionManager/G350ConnectionManager.csproj -c Release -o out
```

## Architecture notes

### The sharing sequence

Internet sharing is one flow, defined once in `Services/SharingCoordinator.cs` and
shared by the Home and Network pages through an application-level singleton
(`App.Sharing`), which holds the last snapshot and raises `Changed`.

```
read state
  -> resolve the console by GUID (not by name: a name belongs to the USB port)
  -> pick an internet source (by driver description, not adapter name)
  -> enable or disable
  -> ensure the adapter holds the scope address   (on enable)
  -> return the adapter to the direct address     (on disable)
  -> re-apply the console's networking
  -> verify the console actually reached the internet
```

Do not add a second copy of this flow to a view. A view subscribes to the
coordinator and presents its state.

### Address constants

`192.168.7.1` and `192.168.137.1` are protocol constants, not configuration.
`192.168.137.1` is what Windows ICS assigns; `192.168.7.1` is the console
project's direct address. `RestoreDirectAddress()` assumes `192.168.7.1`, which
matches the console script.

### Console credentials

`Models/ConsoleCredentials.cs` is the single source for the SSH/SFTP account.
Services read `ConsoleCredentials.User` and `.Password`; they must not declare
their own constants. Values come from the Settings page, and blank fields fall
back to the ArkOS defaults. `App.OnStartup` applies them before any service is
used.

### Screenshots in the repository

`.gitignore` ignores `*.png` and re-includes the README images individually. Do
not replace that with a `docs/screenshots/*.png` wildcard: it would publish any
image dropped into that folder, including one that happens to show a local file
path in the UI.

### Elevation

Privileged operations re-launch the same binary elevated with a helper argument
(`--ics-toggle`, `--ics-repair`, `--ics-diag`) and exchange results through a
temporary file. A status probe must never raise an elevation prompt.

### Deliberate omissions

The app contains no path that deletes ICS registry values. Sharing works with
`ScopeAddress` absent, and mutating ICS internals is destructive. Detecting a
disabled adapter is implemented; enabling one is left to the user, because
enabling a network adapter is machine-wide state.

## Reporting a problem

Include the app version, the Windows version, the console model and firmware,
and the relevant output from the **Diagnostics** panel. The **Activity log** on
the Network & sharing page records every system change with its before and after
value.
