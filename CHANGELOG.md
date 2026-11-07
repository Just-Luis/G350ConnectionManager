# Changelog

All notable changes to G350 Companion are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Console account** settings for the SSH and SFTP username and password. Blank
  fields keep the ArkOS defaults, so existing installations need no changes.
- Unit tests covering prefix and mask conversion and credential resolution, run
  by CI before publishing.

### Changed
- The console account is defined once, in `Models/ConsoleCredentials.cs`, instead
  of four service classes each declaring their own copy.
- `.gitignore` re-includes the README screenshots individually rather than
  through a `docs/screenshots/*.png` wildcard, so an image added to that folder is
  not published by default.

## [1.2.0] - 2026-11-07

### Changed
- The Network page's two sharing buttons are replaced by the same switch the
  Home page uses. Both pages now present one shared state, so operating either
  switch updates the other.
- The sharing switch is always enabled. It no longer requires a detected console
  before it can be operated.

### Fixed
- Turning sharing off no longer breaks the console link. ICS removes its address
  from the private interface when sharing stops; the adapter is returned to the
  direct address so SSH, SFTP, screen preview and recording keep working.

## [1.1.2] - 2026-11-07

### Added
- `--ics-repair` restores the ICS service start type to Manual and starts the
  service, for machines where it had been disabled.

### Fixed
- An administratively disabled network adapter is now reported as disabled
  instead of "no USB Ethernet adapter found". Such an adapter is invisible to
  .NET's `NetworkInterface` enumerator.
- The ICS health check no longer advises starting a service whose start type
  prevents it from being started. A disabled service that still reports
  `Running` is detected by examining the start type before the running state.

### Changed
- After enabling sharing, the shared adapter is placed on the ICS scope address
  when Windows does not renumber it on its own.

## [1.1.1] - 2026-11-07

### Fixed
- Sharing controls are reachable before any status read, and from either page.
  They were previously nested in a collapsed panel and disabled after a status
  read, leaving states with no reachable control.
- Both pages now subscribe to a single sharing state instead of keeping private
  copies, so a toggle on one page is visible on the other.

## [1.1.0] - 2026-11-07

### Added
- **Connect** on the Home page: runs a full link probe and names the failing
  step, including the case where the console is reachable but its SSH service is
  stopped.
- **Share Wi-Fi** on the Home page: a toggle that runs the verified sharing
  sequence and reports whether the console actually reached the internet.

### Changed
- The sharing sequence moved into `SharingCoordinator`, shared by both pages.
  `NetworkView` lost 157 lines of duplicated logic.

### Fixed
- The "Internet sharing" card title no longer clips mid-word.

## [1.0.5] - 2026-11-06

### Removed
- Never-read members across `LinkSnapshot`, `IcsConnection`, `IcsSnapshot`,
  `IcsService`, `AdapterEnumerator`, `SftpService`, `LinkMonitor` and
  `ScreenCaptureService`.
- `NetworkView.MaybeRefreshIcsForNewAdapterAsync`, which re-read sharing when the
  console reappeared under a new adapter name. The rename it worked around no
  longer occurs, and the routine raised an unrequested elevation prompt on a 4 s
  timer.

### Fixed
- `MacOf()` read registry values that are empty on every adapter and never
  matched the adapter it was asked about.
- `ConsoleFor()` compared a NetGUID against a MAC address, so the live-adapter
  preference never matched.
- `FindConsoleAdapter` preferred a disconnected ghost over a live adapter
  because its fallback clauses were inverted.

### Changed
- Build hygiene: `IDE0005`, `IDE0052` and `IDE0035` are escalated to warnings and
  doc-comment XML is validated. A clean build reports zero warnings.

## [1.0.4] - 2026-11-06

### Fixed
- Internet sharing no longer fails when the ICS `ScopeAddress` registry value is
  absent. Only a stopped `SharedAccess` service blocks sharing.
- The `0x80040201` explanation lists a wrong-subnet adapter first, which is the
  condition the evidence implicates.

### Changed
- `--ics-diag` notes that a missing `ScopeAddress` is normal before sharing has
  ever been enabled.

## [1.0.3] - 2026-11-06

### Fixed
- Internet sharing no longer fails on adapters whose USB port changed. The
  console adapter is resolved by GUID before name, and a ghost adapter from a
  previous port is not mistaken for the live console.
- Turning sharing off works with the console unplugged. It previously required
  both adapters to be present.
- Clearing sharing is verified by re-reading every connection instead of
  reporting success unconditionally.
- The release pipeline produces a binary again. `*.png` was excluded by
  `.gitignore` with an exception only for `docs/screenshots`, which also excluded
  the brand marks declared as `<Resource>` in the project file, so every clone
  failed with `error BG1002`.

### Added
- `--ics-diag`: an elevated, read-only dump of service states, the
  `SharedAccess\Parameters` values and every connection with sharing type,
  status, address and GUID.

### Removed
- The repair path that killed the `SharedAccess` host and deleted the
  `PublicConnection`, `PrivateConnection` and `ScopeAddress` registry values.
  Mutating ICS internals is not required for any supported operation.

## [1.0.2] - 2026-11-06

### Fixed
- The brand mark follows the selected theme.

## [1.0.1] - 2026-11-06

### Added
- Home page cards use icons that link to their tab.
- Application icon and brand marks.

## [1.0.0] - 2026-11-06

### Added
- Overview cards on the Home page.
- CI job that builds the artifact and verifies the published executable.

## [0.9.0] - 2026-11-06

### Added
- Console tab for running commands on the device over SSH.

## [0.8.0] - 2026-11-06

### Added
- Dual-pane SFTP file browser for transferring files to and from the console.

## [0.7.1] - 2026-11-06

### Changed
- Screenshot and recording folders are configured on the Settings page, one per
  output.

## [0.7.0] - 2026-11-06

### Added
- MP4 recording of the console screen.

## [0.6.0] - 2026-11-06

### Added
- Resolution, frame rate, JPEG quality and frame-delay controls for the screen
  pipeline.

## [0.5.4] - 2026-11-06

### Changed
- Console network updates use `--start-services`, the non-interactive equivalent
  of pressing **[A]** on the console.
- Address changes no longer wait on console addresses that are not in use.

## [0.5.3] - 2026-11-06

### Fixed
- Every address change now updates the console reliably.

## [0.5.2] - 2026-11-05

### Fixed
- A shared connection no longer reports "no internet" while sharing is on.

## [0.5.1] - 2026-11-05

### Fixed
- Screen capture reads frames through DRM, which requires elevation on the
  console.

## [0.5.0] - 2026-11-05

### Added
- Live screen preview and screenshots.

## [0.4.5] - 2026-11-05

### Fixed
- The sharing controls are released after a toggle without needing Refresh.

## [0.4.4] - 2026-11-05

### Changed
- The sharing panel layout was simplified, and the Automatic address mode
  follows the sharing state.

## [0.4.3] - 2026-11-05

### Added
- The console's networking restarts automatically after toggling sharing.

## [0.4.2] - 2026-11-05

### Changed
- The console handles the address switch itself. The app no longer changes the
  SSH route.

## [0.4.1] - 2026-11-05

### Added
- Enable and disable internet sharing, with console route repair.

## [0.4.0] - 2026-11-05

### Added
- Read-only Internet Connection Sharing status: adapter list and conflict
  warnings.

## [0.3.1] - 2026-11-05

### Fixed
- Apply failing on real hardware, caused by a read-back race, lost command output
  and quoting.

## [0.3.0] - 2026-11-05

### Added
- Adapter IP configuration, automatic and manual, with backup and restore.

## [0.2.0] - 2026-11-05

### Added
- Read-only diagnostics: adapter detection, ping, SSH port check and live status.

## [0.1.0] - 2026-11-05

### Added
- Initial UI shell: navigation, light and dark themes, accent colours and
  settings persistence.

[Unreleased]: https://github.com/Just-Luis/G350ConnectionManager/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/Just-Luis/G350ConnectionManager/compare/v1.1.2...v1.2.0
[1.1.2]: https://github.com/Just-Luis/G350ConnectionManager/compare/v1.1.1...v1.1.2
[1.1.1]: https://github.com/Just-Luis/G350ConnectionManager/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/Just-Luis/G350ConnectionManager/compare/v1.0.5...v1.1.0
[1.0.5]: https://github.com/Just-Luis/G350ConnectionManager/compare/v1.0.4...v1.0.5
[1.0.4]: https://github.com/Just-Luis/G350ConnectionManager/compare/v1.0.3...v1.0.4
[1.0.3]: https://github.com/Just-Luis/G350ConnectionManager/compare/v1.0.2...v1.0.3
[1.0.2]: https://github.com/Just-Luis/G350ConnectionManager/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/Just-Luis/G350ConnectionManager/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.9.0...v1.0.0
[0.9.0]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.8.0...v0.9.0
[0.8.0]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.7.1...v0.8.0
[0.7.1]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.7.0...v0.7.1
[0.7.0]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.5.4...v0.6.0
[0.5.4]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.5.3...v0.5.4
[0.5.3]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.5.2...v0.5.3
[0.5.2]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.5.1...v0.5.2
[0.5.1]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.4.5...v0.5.0
[0.4.5]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.4.4...v0.4.5
[0.4.4]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.4.3...v0.4.4
[0.4.3]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.4.2...v0.4.3
[0.4.2]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.4.1...v0.4.2
[0.4.1]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.4.0...v0.4.1
[0.4.0]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.3.1...v0.4.0
[0.3.1]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/Just-Luis/G350ConnectionManager/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Just-Luis/G350ConnectionManager/releases/tag/v0.1.0
