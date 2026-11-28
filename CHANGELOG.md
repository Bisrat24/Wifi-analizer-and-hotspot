# Changelog

All notable changes to the **Wifi Analyzer and Hotspot** project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Planned
- Detailed packet capture telemetry and QoS deep inspection.

---

## [v1.7.5] - 2026-11-28

### Fixed
- **Hotspot Single-Radio Hardware Concurrency Conflict Resolver:** Eliminated intermittent hotspot failures where connecting devices could not connect (handshake/DHCP timeouts) or where the hotspot SSID did not appear on other devices:
  - Fixed Single-Channel Concurrency (SCC) frequency synchronization: on single-radio Wi-Fi cards (such as Intel Wi-Fi 6/6E/7, Realtek, Qualcomm), creating a 2.4 GHz SoftAP while connected to an upstream 5 GHz network (or vice-versa) forces the physical radio into Multi-Channel Concurrency (MCC) time-slicing, causing dropped beacon frames and dropped WPA/DHCP packets.
  - Fixed erroneous channel dropdown in `MainViewModel.cs` that mislabeled 5 GHz channels (e.g. Channel 36) as "2.4 GHz Band (Synchronized to Channel 36)", which misled users and internal selection into forcing 2.4 GHz SoftAP mode.
  - Implemented deterministic band resolution (`ResolveTetheringBandEnum`) in `HotspotService.cs` ensuring synchronized 5 GHz Wi-Fi connections enforce `FiveGigahertz` SoftAP mode for zero-drop performance.
  - Added proactive **Hardware Band Conflict Alert Banner** with one-click **⚡ Auto-Sync** button that aligns the hotspot band to the active uplink radio before startup.
  - Added pre-flight conflict protection in `ToggleHotspotAsync()` that automatically synchronizes the band if a single-radio conflict is detected.
- **Regulatory 5 GHz DFS Radar Channel Detection & Advisory:** Automatically detects when the upstream Wi-Fi uplink is on a DFS channel (Channels 52–144) where Windows SoftAP is legally prohibited from beaconing, alerting the user with actionable guidance.
- **Windows ICS DHCP Subnet Collision Prevention:** Added dynamic subnet collision detection (`DetectOptimalIcsSubnet()`). If the upstream gateway or any active interface occupies `192.168.137.x`, the ICS DHCP server automatically shifts to `192.168.173.1` to prevent silent DHCP assignment lockups.
- **Firewall & Routing Hardening:** Added explicit firewall allow rules for incoming UDP 67/68 (DHCP) and UDP 53 (DNS) on all network profiles, and enabled kernel IPv4 packet routing (`IPEnableRouter = 1`).
- **Truthful Live Broadcast Channel Telemetry:** `GetStatusAsync` now queries the actual virtual Wi-Fi Direct adapter via `DetectVirtualHotspotChannel()` first, accurately reflecting whether Single-Channel Concurrency (SCC) or Multi-Channel Concurrency (MCC) is active.

### Changed
- Promoted project release to `v1.7.5` across window title bars (`WiFi Analyzer & Hotspot Pro (v1.7.5)`), companion mini-window, sidebar branding badges, application manifest, assembly metadata, and self-check test suite.

---

## [v1.7.4] - 2026-11-26

### Added
- **Automated Band-Steering Policy Enforcement:** Actively guides dual-band clients to 5 GHz / 6 GHz based on live SNR thresholds (selectable presets: 50% Aggressive, 60% Balanced Recommended, 70% Conservative), with automatic dynamic fallback to 2.4 GHz when clients are distant or signal drops below 45% SNR to ensure zero dropped connections.
- **Live Jitter Diagnostics:** Real-time delay variation calculation according to RFC 3550 standard (`J = J + (|D(i-1, i)| - J) / 16.0`) measuring continuous packet round-trip time (RTT), min/avg/max latencies, packet loss percentage, and five-level quality rating (`EXCELLENT`, `VERY GOOD`, `GOOD`, `MODERATE`, `POOR`).
- **Packet RTT Distribution Histogram:** Five-tier latency distribution visualization in Tab 3 ("SPEED TEST & LIVE TRAFFIC") featuring live sample counts and percentage bars across `< 5 ms` (Ultra-Fast / LAN), `5 - 15 ms` (Optimal Wi-Fi), `15 - 30 ms` (Standard Broadband), `30 - 60 ms` (Moderate Latency), and `> 60 ms` (Elevated / Bufferbloat).
- **Client Steering & SNR Telemetry:** Surfaced live client SNR estimates and band-steering guidance status tags directly within the Hotspot connected devices table.
- **Automated Self-Check Verification:** Added regression test assertions verifying RFC 3550 delay variation calculations, histogram bin allocations, and band-steering decision thresholds.

### Changed
- Promoted project release to `v1.7.4` across the main window title bar (`WiFi Analyzer & Hotspot Pro (v1.7.4)`), companion mini-window, top sidebar branding badges, application manifest, and assembly metadata.

---

## [v1.7.3] - 2026-11-26

### Changed
- Promoted project release to `v1.7.3` with synchronized version display across the OS window title bar, companion mini-window title, top sidebar branding badge, application manifest, and assembly metadata.
- Automated self-check test suite validated to assert `v1.7.3` version consistency.

---

## [v1.7.2] - 2026-11-26

### Added
- Window header version code: surfaced `(v1.7.2)` prominently in the OS Window Title bar across both the primary dashboard (`MainWindow`) and the companion mini-window (`SecureConnectWindow`).
- App branding version badge: updated sidebar top header and collapsed icon tooltips to explicitly display `v1.7.2` on window open.
- Exposed `AppVersionString`, `AppVersion`, and dynamic `WindowTitle` on `MainViewModel` with an automated self-check consistency assertion.

---

## [v1.7.1] - 2026-11-26

### Fixed
- Zero-latency DNS cache hit telemetry precision: clamped hits so `CacheHits <= TotalQueries` is strictly enforced under high-concurrency client bursts.
- Exported and asserted distinct DNS cache key generation between plain queries and EDNS0 queries with empty OPT records, eliminating key collision and cache cross-contamination.

### Added
- Seamless Mesh Roaming Transition Telemetry: automatically detects when a connected client roams between BSSID access points on the same mesh SSID, logging the handover with previous/new BSSID, channel, band, and signal strength to the event timeline and live Hotspot status ribbon.
- Self-Check regression test suite asserting EDNS0 vs plain query key differentiation (`:edns=0:do=0` vs `:edns=1&v=0:do=1`).

---

## [v1.7.0] - 2026-11-25

### Added
- Wi-Fi 6E & Wi-Fi 7 (6 GHz Band) Spectrum Visualization: full channel grid (channels 1–233, 5.925–7.125 GHz) on `SpectrumCanvas`, up to 320 MHz channel width curve spans, Preferred Scanning Channels (PSC: 5, 21, ..., 229 with `★`), and Analyzer tab `6 GHz (6E/7)` filter button.
- 6 GHz RF Channel Analysis & Star Ratings: dedicated 6 GHz rating grid evaluating co-channel and wide channel overlap penalties, with automatic PSC cleanest channel recommendations.
- Station-to-Station Guest Isolation for Mobile Hotspot: client-to-client firewall isolation blocking peer-to-peer traffic across the `192.168.137.2-192.168.137.254` ICS range via Windows Defender Firewall (`Hotspot_Guest_Isolation_In` / `Hotspot_Guest_Isolation_Out`), while preserving gateway (`192.168.137.1`), DNS, DHCP, and WAN access.
- UI control for Guest Isolation in Hotspot Settings overlay with persistent configuration in `AppSettingsModel` and atomic cleanup staging via `SystemRestoreService`.
- Self-check test cases covering 6 GHz channel-to-frequency mappings, PSC recognition, and Hotspot Guest Isolation state machine defaults.

---

## [v1.6.13] - 2026-11-20

### Fixed
- Secure Connect band labels: decode native PHY values instead of assuming 5 GHz from a non-empty PHY list, then override with real analyzer channel data (channel <= 14 = 2.4 GHz) when available.
- Secure Connect populates its network list instantly from the analyzer's cached scan and refreshes in the background on window open; the manual Refresh button still forces a native rescan.

### Changed
- No connection, throughput or QoS behavior changed; band/channel are display data only.

---

## [v1.6.12] - 2026-11-20

### Fixed
- Secure Connect available networks now show the connected badge and hide the Connect button for the SSID the system is already connected to; removes the misleading "Already connected" prompt on an unmarked row.

---

## [v1.6.11] - 2026-11-20

### Changed
- Hotspot control banner slimmed down: smaller fonts, padding and buttons; only state, uptime, power, gear and a compact status line remain.
- Broadcast channel/frequency and spectrum advice moved out of the banner into the ⚙ settings overlay, leaving the main column with clients, blocked devices and reset log only.

---

## [v1.6.10] - 2026-11-20

### Changed
- Hotspot tab: broadcast channel/frequency and spectrum advice moved into the centered control banner.
- Hotspot tab: Priority, Watchdog/Recovery and Low-Latency/Networking moved into the ⚙ settings overlay; the main page now shows only control and client data.
- Scrollbars app-wide restyled: slim 9 px track, rounded thumb, cyan on hover/drag.

---

## [v1.6.9] - 2026-11-20

### Changed
- Hotspot tab is now a single centered column: control banner (state, uptime, power, settings gear) on top, then clients, blocked devices and reset log, with advanced sections collapsed below.
- SSID / password / band-channel / QR / Save moved into a settings overlay opened by the banner's gear button; closes with ✕, backdrop click or the gear.
- Tables use fixed heights so the page scrolls once instead of squeezing a 360 px side column on small screens.

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
