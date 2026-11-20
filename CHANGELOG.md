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

## [v1.6.12] - 2026-11-20

### Fixed
- Hotspot settings are visible again in a normal left column; the overlay drawer that could not be shown was removed.

---

## [v1.6.11] - 2026-11-20

### Fixed
- Hotspot settings drawer opens by default and Show/Hide now uses direct code-behind handlers instead of XAML storyboards that did not fire.

---

## [v1.6.10] - 2026-11-20

### Fixed
- Hotspot settings drawer no longer relies on hover: a labeled **Hotspot Settings** button and the ⚙ tab open it, and a **Hide** button closes it.

---

## [v1.6.9] - 2026-11-20

### Changed
- Hotspot Start/Stop control moved to the center top of the page.
- Hotspot settings converted into a hover-activated auto-hiding side drawer with a persistent slim tab.
- Active Clients and management cards now occupy the full page width.

---

## [v1.6.8] - 2026-11-20

### Changed
- Hotspot page reorganized: core setup is a top manual Show/Hide panel; Priority, Watchdog, DHCP and low-latency settings are collapsed by default.
- Removed stale priority claims and moved long channel explanations into tooltips.

---

## [v1.6.7] - 2026-11-20

### Fixed
- `WLAN_AVAILABLE_NETWORK` now uses the exact SDK field order, so available networks display correct SSIDs instead of garbled names.
- WLAN reason codes are decoded with `WlanReasonCodeToString` in `wlan_debug.log`.
- Profile XML follows Microsoft's sample namespace and connection mode; a netsh add/delete fallback connects when native profile APIs reject it, leaving no saved profile behind.

---

## [v1.6.6] - 2026-11-20

### Added
- WLAN diagnostics log (`wlan_debug.log`) with API return codes and generated profile XML.
- One-time import of existing Windows saved Wi-Fi profiles into the app's DPAPI-encrypted store.
- WPA3SAE profiles now include `transitionMode`.

### Fixed
- Native WLAN struct widths (`WLAN_INTERFACE_INFO`, `WLAN_AVAILABLE_NETWORK`) were wrong, causing available networks to appear empty.
- Temporary-profile rejection now falls back to a per-user Windows profile that is deleted immediately after connecting, leaving no saved network behind.

---

## [v1.6.5] - 2026-11-20

### Fixed
- Temporary WLAN profile XML now uses the current `https://www.microsoft.com/networking/WLAN/profile/v1` schema namespace and `<autoSwitch>false</autoSwitch>`, preventing Windows from rejecting joins with "The network connection profile is corrupted".

---

## [v1.6.4] - 2026-11-20

### Added
- App-owned encrypted Secure Connect profile store (`secure_connect_profiles.json`) with DPAPI-protected passwords; Windows saved networks are never added, modified or deleted.
- `WlanDisconnect` before every join and native temporary-profile connections so the app does not remain attached to the previous SSID.

### Fixed
- Secure Connect network list now has a single owner and deduplicates by trimmed SSID, keeping the strongest BSSID; duplicate SSID names are gone.
- Profile saving now persists to the app store only after a verified successful join; Forget and password reveal use the app store.
- QR Reed–Solomon error-correction generator indexing and bottom-left format bit order were corrected; self-checks now cover both.

---

## [v1.6.3] - 2026-11-20

### Fixed
- QR codes are now scannable: corrected the second format-information copy, wrote the required dark module and version-7 information, and added an explicit error when the SSID/password payload exceeds capacity.
- Secure Connect connection joins now use native WLAN state for verification instead of localized netsh text, select the requested Wi-Fi adapter, report Win32 error details, and map WPA/WPA2/WPA3 algorithm values correctly.
- A secured network can no longer be joined with an empty password by creating an open profile.
- The window version label now shows the shipped version instead of v1.6.1.

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
