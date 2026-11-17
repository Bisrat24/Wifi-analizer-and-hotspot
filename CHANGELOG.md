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
