# Changelog

All notable changes to the **Wifi Analyzer and Hotspot** project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Planned
- Automated band-steering policy enforcement for multi-band APs.
- Detailed packet capture telemetry and QoS jitter diagnostics.

---

## [v1.6.2] - 2026-11-19

### Added
- Dependency-free ISO/IEC 18004 Wi-Fi QR Code generator in Hotspot view with live preview, PNG export, and one-click clipboard copying.
- Wi-Fi site survey export in Analyzer tab supporting both CSV (RFC 4180 compliant) and indented JSON output formats.
- Cleartext security key reveal and one-click clipboard copy for saved Wi-Fi profiles in Secure Connect.
- Real-time client transfer rate computation and dedicated "LIVE RATE" throughput column in the Connected Devices table.
- Self-check test assertions for Wi-Fi QR escaping, matrix dimensions, and finder pattern verification.

---

## [v1.6.1] - 2026-11-19

### Fixed
- Removed hard-coded `IsSelected` values from DoH provider, captive-portal and proxy ComboBoxes so the UI always reflects the persisted setting.
- Added keyboard-focus highlighting to buttons, text boxes and password boxes in the main and Secure Connect windows.
- Button tooltips are now exposed as accessibility names for icon-only controls.

---

## [v1.6.0] - 2026-11-19

### Added
- Native WLAN API discovery for Secure Connect networks and saved profiles, with verified connection results.
- Reversible per-adapter Wi-Fi power-saving mode for Secure Connect, restored on OFF, exit, or startup after a crash.
- Adaptive keep-alive scheduler with jitter, exponential backoff, and a no-network skip path.
- Dependency-free `--selfcheck` startup flag covering speed parsing, backoff, DNS TTL extraction and PowerShell escaping.

### Changed
- Hotspot lifecycle operations share one semaphore; reconfiguring an active hotspot now restarts and verifies it.
- Keep-alive and reconnect probes are fully asynchronous through the shared ProcessRunner.
- Reconnect re-resolves the active adapter and verifies IP, gateway, DNS and internet reachability before reporting success.

### Fixed
- Operation statuses for block, unblock, pause, resume, QoS and priority now reflect actual OS results.
- Dual-speed input `100,20` no longer collapses into `10020` and applies a ~10 Gbps throttle.
- Comma-thousands input (`1,000/20`) still parses correctly.

---

## [v1.5.1] - 2026-11-18

### Fixed
- Removed `RealTime` process priority from child commands and the ICS service; network adapter resets and DHCP renews now always restore the adapter/lease when a watchdog operation is cancelled.
- Timed-out PowerShell processes are killed and reaped instead of continuing to change OS state after the app exits.
- Global power scheme/TCP settings are no longer changed by Secure Connect; ICS registry values, route metrics and NIC priority/VLAN settings are snapshotted and restored on stop, exit, or the next startup, even after a crash.
- QoS Packet Scheduler binding is scoped to the hotspot and upstream adapters only; restored DNS reply validation, TTL handling, and client/unblock cleanup after restarts.
- Removed the hardcoded hotspot password; passwords are masked in the UI, excluded from clipboard copies, and DPAPI never falls back to plaintext storage.
- Hardened netsh/PowerShell argument construction and IP/MAC validation; watchdog ticks are now serialized and the reset backoff applies to every trigger.

---

## [v1.5.0] - 2026-11-17

### Added
- **Secure Connect AnyConnect Wi-Fi Manager**: Integrated Wi-Fi manager with full-power operational mode, fast reconnect capability, and compact mini-window view.

---

## [v1.4.9] - 2026-11-17

### Added
- Native DWM dark title bar styling across Windows desktop builds.
- Active keep-alive settings persistence with a 1-second default heartbeat interval.
- Responsive layout adaptations for varied display resolutions.

---

## [v1.4.8] - 2026-11-16

### Added
- EDNS0 explicit keying and QoS re-entrant packet coalescing.
- Real-time Access Point (AP) credentials and band verification pipeline.

### Fixed
- Stabilized process shutdown and resource deallocation routines.

---

## [v1.4.7] - 2026-11-15

### Added
- DNS question splicing with 0x20 bit-case preservation.
- Dual-speed frame parser alternation with priority coalescing.

---

## [v1.4.6] - 2026-11-14

### Changed
- Enforced precedence 240 uplink clamp and keystroke QoS serialization.
- Bounded AP synchronization loop to decrease idle CPU usage and accelerate startup.
