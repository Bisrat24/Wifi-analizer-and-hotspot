# 📡 WiFi Analyzer & Hotspot Pro (v1.3.4)

A native Windows desktop application with standalone executable (`.exe`) for analyzing Wi-Fi channels, bandwidth, signal strength, internet connection speed, connection stabilization with active keep-alive / auto-reconnect (Ethernet & Wi-Fi), and creating/managing a custom Wi-Fi Mobile Hotspot with High OS priority on hotspot launch, Upstream Shared Connection Top Priority (over desktop browsers), Refresh Rules re-enforcement engine, continuous watchdog testing with 15s auto-reset countdown, top network link speed display (1.0 Gbps / 100 Mbps), on-demand manual reconnect/reset, complete settings persistence across restarts, per-client priority levels (1-7), granular low-bandwidth throttling (30 KB/s, 100 KB/s, 300 KB/s), instant disconnect on block, multi-layer ARP poison / WFP / NetQoS block isolation, dynamic Single-Channel Concurrency (SCC) radio detection, automated watchdog reset event logs, real-time in-process client polling (<10ms), zero phantom devices, customizable watchdog/log buffers, collapsible icon-only sidebar, and vibrant color-coded vector icons for effortless recognition.

---

## 🚀 Quick Launch

You can run the app immediately by double-clicking:
- **`WifiAnalyzerHotspot.exe`** (located right in the root directory: `c:\Users\Bisrat\Documents\APPS\Wifi analizer and hotspot\WifiAnalyzerHotspot.exe`)

---

## ✨ Key Features (v1.3.4)

### 1. 📡 Wi-Fi Channel & Spectrum Analyzer
- **Dual Visual Modes (Spectrum Curves & Live RSSI History)**:
  - **📊 Spectrum Curves**: Real-time parabolic bell curves showing all nearby Wi-Fi networks and channel widths across 2.4 GHz and 5 GHz.
  - **📈 Live RSSI History Tracker**: Live timeline canvas plotting real-time signal strength (dBm) over the last 60 seconds with threshold guidelines (-30 dBm Excellent, -50 dBm Good, -67 dBm Fair/Streaming, -80 dBm Weak, -90 dBm Dropping), jitter statistics, min/max values, and area gradient fills. Perfect for walking around to detect Wi-Fi dead zones and interference.
- **Detailed Network Inventory**:
  - **SSID** (Network Name) & **BSSID** (MAC Address).
  - **Channel & Bandwidth**: 20 MHz, 40 MHz, 80 MHz, 160 MHz.
  - **Signal Strength**: Both percentage (`0 - 100%`) and precise **RSSI** (`-30 dBm` to `-95 dBm`).
  - **PHY / Wi-Fi Standard**: Wi-Fi 6 (`802.11ax`), Wi-Fi 5 (`802.11ac`), Wi-Fi 4 (`802.11n`), etc.
  - **Security & Authentication**: WPA3-Personal, WPA2-Personal, Open, etc.
  - **Active Network Highlighting**: Currently connected network is highlighted with a gold star (★) and live link stats.
  - **Search & Band Filtering**: Instant filter by SSID, MAC, or Band (2.4 GHz / 5 GHz / All).

### 2. 📊 Channel Congestion & Rating Engine
- **Interference Scoring**: Evaluates co-channel and adjacent-channel interference across all 2.4 GHz and 5 GHz channels.
- **Star Ratings & Congestion Scores (0-100)**: Clear star ratings (`★★★★★`) and health indicators.
- **Smart Hotspot / Router Recommendation**:
  - Automatically identifies the cleanest, non-overlapping channels (e.g. Channel 1, 6, or 11 on 2.4 GHz; clean channels on 5 GHz) to give you the fastest speed with zero packet drops.

### 3. ⚡ Internet Speed Test & Live Traffic Monitor (Multi-CDN Engine)
- **Multi-CDN Resilient Engine**:
  - Automatically probes and benchmarks across a pool of tier-1 global CDNs and speed test servers (OVH Telecom, Microsoft Azure CDN, jsDelivr, Hetzner, httpbin).
  - **Pre-flight Endpoint Selection**: Automatically tests latency and selects the fastest responsive endpoints, completely bypassing regional ISP blocks or Cloudflare throttling.
- **Real-World Multi-Stream Throughput**:
  - 4 parallel download workers continuously saturating the line for the full test duration.
  - Real sliding-window instantaneous throughput and animated radial speedometer needle.
  - Multi-stream upload benchmark delivering real binary payload to live echo/benchmark servers.
  - **No Fake Fallback Numbers**: Every displayed metric is derived from actual bytes transferred over the network interface.
- **Real-time Adapter Throughput Monitor**:
  - Continuous live upload and download bandwidth monitor updating every second directly from the active wireless network interface.

### 4. 🔥 Mobile Hotspot Creator & Manager (with High OS Priority, QoS & Admin Manifest)
- **🛡️ Full Administrator Manifest & Privilege Gating**:
  - Application includes a native `app.manifest` declaring `<requestedExecutionLevel level="requireAdministrator" />`. All privileged operations (`arp -s`, `netsh advfirewall`, `New-NetQosPolicy`, ICS virtual adapter configurations) execute with verified administrative rights rather than failing silently.
- **⚡ Deadlock-Free Asynchronous Process Pipeline**:
  - Replaced synchronous standard stream reading with asynchronous, non-blocking execution (`RunPowerShellAsync` reading `StandardOutput` and `StandardError` concurrently with `WaitForExitAsync` and `CancellationTokenSource`). Eliminates parent process hangs and "hotspot stuck in transition" issues caused by full OS pipe buffers.
- **🌐 Tri-Vector Internet Reachability Verification**:
  - `CheckInternetAvailableAsync` employs multi-vector probing: ICMP Echo Ping + Layer 4 TCP Port 443 handshake to `1.1.1.1` + HTTP 204 head probe. Completely immune to networks, hotels, or enterprise firewalls that block or deprioritize ICMP.
- **🧠 Upstream-Aware Intelligent Watchdog & Backoff**:
  - Differentiates upstream ISP outages from local SoftAP failures. Auto-reset is automatically suspended if the host PC's upstream network is offline.
  - Rate-limited consecutive resets: caps auto-resets at 3 consecutive attempts, pausing for a 90-second backoff cooldown to prevent infinite reset loops.
- **📊 100% Honest Telemetry & Accounting**:
  - Zero fabricated data usage: totals come from real byte deltas on the virtual network adapter, split evenly across online devices (Windows exposes no per-client counters).
  - Speed test strictly reports honest measurements with no artificial minimum fallbacks.
- **🔐 Encrypted Settings & Silent Elevated Startup**:
  - Hotspot and proxy passwords are DPAPI-encrypted (current-user scope) inside `settings.json`.
  - Autostart uses a highest-privileges logon scheduled task, so the app starts elevated without a UAC prompt.
  - Disabling the proxy restores the original system proxy instead of leaving a stale override.
- **🔒 Input Validation & Injection Hardening**:
  - Strict escaping of PowerShell string literals (`$`, `"`, `` ` ``) in SSID and password parameters.
  - Strict regex/IP address parsing for all MAC and IP address inputs across firewall, ARP, and QoS commands.
  - Captive portal URLs strictly validated for `http://` and `https://` schemes before system browser dispatch.
- **🌐 Upstream Connection Top Priority (Prioritized Over Host PC Browsers)**:
  - Enforces kernel-level NetQoS priority for hotspot transit traffic (`192.168.137.0/24`) with **Precedence 128**, **802.1p Tag 7** (Network Control / Voice), and **DSCP 46** (Expedited Forwarding).
  - Local desktop web browsers (`chrome.exe`, `msedge.exe`, `firefox.exe`, `brave.exe`, `opera.exe`, `vivaldi.exe`, `tor.exe`) are constrained to **Precedence 64**, **802.1p Tag 1** (Background), and **DSCP 8** (Scavenger).
  - By using Precedence 128 for baseline transit, individual connected client priorities (**Precedence 130 - 240**), QoS bandwidth limits (**Precedence 245**), and device blocks (**Precedence 255**) take full hierarchical precedence over the general transit queue while desktop browsers remain strictly deprioritized.
- **✅ Enforced, Verified & Self-Cleaning NetQoS**:
  - **QoS Packet Scheduler check**: rate limiting and 802.1p tagging require the `ms_pacer` binding. The hotspot's own virtual adapter is bound automatically; a disabled upstream adapter is reported with the exact remediation command instead of being silently reset.
  - **Dynamic Driver-Aware NIC Priority & VLAN Activation**: dynamically discovers and matches supported `ValidDisplayValues` on physical network adapters, safely committing once per session only upon verified success (retries if the adapter was down) to prevent unexpected adapter resets while ensuring physical NIC drivers preserve 802.1p priority tags.
  - **Consolidated Gigabit Ethernet Link Speed Preservation (Anti-10Mbps Downshift)**: powered by a unified `EthernetSpeedOptimizer` engine shared seamlessly across Active Reconnect and Hotspot Priority. Features a genuine opt-in toggle (defaults to disabled/off with one-time profile migration from v1.3.1 and atomic settings persistence); universal reversible driver defaults ("Restore Defaults" button enabled only when opt-in is off, auto-restore on opt-out, and fallback manufacturer restoration for v1.3.1/v1.3.2 modifications); distinct `ETH_SKIPPED` handling with 5-minute TTL caching preventing unverified dock/USB adapters from thrashing recoveries; driver read-back verification with 3 asynchronous retries (with no trailing sleep) across both audit and restore routines; strict 802.3 wired-only media filtering preventing Wi-Fi radio touches; SemaphoreSlim concurrency serialization gating both Hotspot Priority and Active Reconnect paths; failure-safe session caching with 30-minute TTL for both per-adapter and full audits; per-adapter transition-based 10 Mbps cache invalidation eliminating multi-adapter crosstalk; and self-contained sleep/resume invalidation (`PowerModes.Resume`). Systematically disarms driver power-saving throttles (`WOL & Shutdown Link Speed: 10 Mbps First`, `Green Ethernet`, `Energy-Efficient Ethernet`, `Advanced EEE`, `Power Saving Mode`, `Gigabit Lite`, and `Auto Disable Gigabit`) and restores `Speed & Duplex` to Auto-Negotiation only when explicitly opted in by the user.
  - **Verified policy creation & default-level verification**: every per-client QoS limit and priority is read back with `Get-NetQosPolicy`; default priority removals are verified before cache commit. The UI reports the verified throttle rate, 802.1p, DSCP, and precedence, or the real error message.
  - **Automatic Policy Activation upon DHCP Lease Acquisition**: when a connecting device receives its IP from DHCP, its saved QoS bandwidth limit and priority levels are automatically applied without requiring manual rule refreshes.
  - **Automatic Policy Restoration on Unblock**: unblocking a previously blocked device immediately restores its configured priority and QoS limits once an IP is known, without waiting for reconnect or manual refresh.
  - **Per-MAC Concurrency Serialization (`SemaphoreSlim`)**: eliminates race conditions and "policy already exists" conflicts between background DHCP auto-enforcement and manual user adjustments, with automatic semaphore pruning.
  - **In-Memory Policy State Cache with Full Invalidation Integrity**: verified policies are cached by MAC + IP. Invalidation traps are eliminated: cache entries are proactively cleared on block, unblock, disconnect, and IP reassignment (with empty/hyphen IP filtering) so the UI never reports active status for deleted kernel policies.
  - **Default Priority 5 Lightweight Bypass**: clients at standard Priority 5 rely on baseline Hotspot Transit QoS (Precedence 128) without creating redundant individual NetQoS policies, cutting connect-time PowerShell spawns on HDD by 50%.
  - **Stale policy eviction**: if DHCP hands an address previously used by another device to a new one, the old per-IP QoS/priority policy is removed automatically.
  - **Full cleanup**: stopping the hotspot and exiting the app remove every `Hotspot_*` NetQoS policy, so stale per-IP rules never survive a session.
- **⚡ HDD & Low-Spec Storage Performance Overhaul**:
  - **No Cold-Start Thrashing**: Hotspot transit rules and process prioritization are engaged on hotspot activation rather than loading heavy PowerShell modules on application cold boot. Saved blocked clients are enforced in a non-blocking background task during startup.
  - **Adaptive Wi-Fi Scanner Cadence**: Wi-Fi network scanning runs every 5 seconds on the "Analyzer" tab, and scales back to 25 seconds when using Hotspot or Stabilizer tabs to prevent constant `netsh.exe` disk seeking.
  - **ExitCode-Verified Scheduled Task Deduplication**: `schtasks.exe` is only invoked when startup preferences or executable path genuinely change and cache is only committed when execution succeeds with `ExitCode == 0`.
  - **Optimized 2-Second Client Polling**: Cuts background ARP and tethering telemetry queries in half while maintaining real-time responsiveness.
- **🛡️ "Refresh Rules" Quick Enforcement Button**:
  - Located directly beside **Refresh Clients** in the Connected Client Devices card.
  - Re-applies and re-asserts all active QoS rate limits, client priority tags (1-7), static ARP blackholes, firewall blocks, and upstream transit prioritization in a single click.
- **⚡ High Process Priority & Safe Power Hygiene**:
  - Process elevates safely to `High` priority (avoiding `RealTime` to prevent starving OS input/audio/DWM).
  - `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED)` executed directly on the persistent UI thread and cleanly released on application exit.
  - Zero invasive global registry modifications (protecting system responsiveness and multimedia scheduling).
  - Full Windows shutdown/reboot compatibility: window close interceptor respects `SessionEnding` to allow clean OS shutdown.
- **🛑 Rock-Solid Device Disconnect & Permanent Block**:
  - **Immediate Connection Cut & L2/L3 Drop**: Clicking **Block** immediately severs client transit, installs persistent static ARP poison, enforces kernel NetQoS freeze, and seals Windows Defender Firewall drop rules without conflicting unblock timers.
  - **Multi-Layer Kernel Isolation**:
    - **Layer 2 Static ARP Blackhole (`00-00-00-00-00-00`)**: Forces the host NIC to forward frames destined for the blocked IP into a null sink, killing communication immediately at the link layer.
    - **Kernel NetQoS 8 bps Clamp (`Precedence 255`)**: Hard-freezes bandwidth at 1 byte/sec on both Source and Destination IP addresses.
    - **Windows Defender Firewall (Inbound & Outbound)**: Drops all transit and local packets, including UDP/TCP 53 (DNS) and UDP 67 (DHCP) to block DNS lookup and DHCP lease renewals.
    - **Robust MAC Address Normalization**: Client MAC lookup handles all variations (hyphenated `f4-34-...`, colon-delimited `F4:34:...`, and clean hexadecimal `F434...`), ensuring IP resolution never misses (`client.IpAddress != '-'`) and rules are reliably applied.
    - **Continuous Watchdog Auto-Seal**: If a blocked device attempts to reconnect or is assigned a different IP, the watchdog detects the MAC and immediately seals the new IP.
    - **Persistent Blacklist**: Saved to `%APPDATA%\WifiAnalyzerHotspot\settings.json` across app and PC restarts.
- **📻 Dynamic Single-Channel Concurrency (SCC) Hardware Detection**:
  - The Hardware Radio Architecture panel dynamically detects your real network connection:
    - **When connected to Wi-Fi**: Shows your actual network name (SSID) and operating channel, explaining how Windows SCC synchronizes SoftAP to prevent disconnections.
    - **When on Ethernet / Cable / Disconnected**: Explains that SCC is inactive and all 2.4 GHz and 5 GHz channels are unlocked for independent broadcasting.
- **📜 Automatic Reset & Recovery Event Log**:
  - Dedicated card in the Hotspot view that records every auto-recovery event with exact timestamps and trigger reasons (`Watchdog: No Client Data Flow`, `Watchdog: Hotspot Stuck in Transition`, `Manual User Trigger`, etc.).
  - Includes a "Clear Log" button and live event counter badge.
- **🚫 Zero Phantom / Ghost Devices Fix**:
  - Eliminated stale Windows ARP cache resurrection loops that previously caused disconnected devices to linger as `Device (IP)`. Only true WinRT-authenticated associated clients are displayed.
- **⚡ Real-Time In-Process Client Polling Engine (<10ms)**:
  - Compiled direct in-process Windows Runtime C# APIs (`Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager`).
  - Cuts background telemetry query time from 2500ms down to **under 10ms** with zero CPU consumption.
  - Client devices table refreshes smoothly every **1 second** with zero UI stutter, button freezing, or reentrancy lockouts.
- **⚡ Hardware SendARP-First Reachability Verification**:
  - Probes live Layer 2 radio connectivity via raw `SendARP` packets answered directly by client device Wi-Fi chips in 1-3ms (immune to phone lock-screen sleep, firewall drops, or ICMP blocks).
  - Accompanied by fast ICMP Echo and TCP fallback (port 80/443).
- **⏱️ Configurable Watchdog Check Frequency Dropdown**:
  - Selectable consecutive check thresholds: **`2s (Ultra-Fast)`**, **`3s (Aggressive)`**, **`5s (Recommended)`**, **`10s (Standard)`**, **`15s (Relaxed)`**, and **`30s (High Tolerance)`**.
- **📶 Per-Client Priority System (1 - 7, Lower = Higher Priority)**:
  - Configure traffic priority directly from the connected devices table:
    - **Priority 1 (Highest)**: Real-time network control, mapped to 802.1p tag `7` and DSCP `46` (Expedited Forwarding). Zero-latency scheduling queue for critical devices.
    - **Priority 2 (High)**: Voice and low-latency interactive audio, mapped to 802.1p `6` and DSCP `34`.
    - **Priority 3 (Elevated)**: Streaming video and gaming, mapped to 802.1p `5` and DSCP `26`.
    - **Priority 4 (Normal)**: Accelerated data, mapped to 802.1p `4` and DSCP `18`.
    - **Priority 5 (Default)**: Standard Best Effort traffic, mapped to 802.1p `3` and DSCP `0`. Default priority for all new connected devices.
    - **Priority 6 (Low)**: Deprioritized background traffic, mapped to 802.1p `2` and DSCP `10`.
    - **Priority 7 (Lowest)**: Scavenger class, mapped to 802.1p `1` and DSCP `8`. Gets the lowest packet scheduling priority.
  - **Hardware MAC Address Persistence Across Restarts**: The priority selected for each device is permanently saved by physical MAC address in `%APPDATA%\WifiAnalyzerHotspot\settings.json`. When that device reconnects or after the app or PC restarts, its custom priority is automatically retrieved and re-enforced in the Windows kernel.
- **🚦 Granular Per-Client Bandwidth Throttling / QoS (with Hardware Persistence)**:
  - Cap individual client speeds with one-click QoS limits via native Windows `New-NetQosPolicy`:
    - **Ultra-Light Low Bandwidth Caps**: **`30 KB/s`** (240 kbps), **`100 KB/s`** (800 kbps), **`300 KB/s`** (2.4 Mbps) tailored specifically for metered, capped, or low-speed networks.
    - **High-Speed Caps**: **`1 Mbps`**, **`2 Mbps`**, **`5 Mbps`**, **`10 Mbps`**, **`20 Mbps`**, or **`Unlimited`**.
  - Rate limits are automatically remembered by MAC address in `%APPDATA%\WifiAnalyzerHotspot\settings.json` and restored across restarts.
- **📊 Cumulative Session Data Usage per Device**:
  - Estimates each device's share of cumulative download and upload data (`↓ MB • ↑ MB`) from real adapter deltas throughout the hotspot session.
- **Multi-Vector Client Data Flow Health Check**:
  - Multi-vector verification (ICMP Echo + Layer 4 TCP Port 80/443 + Hardware ARP Link Verification) eliminating false "No Data" flags.

### 5. 🛡️ Connection Stabilizer & Reconnect Guardian
- **⚡ Top Active Network Link Speed Display (Wi-Fi or Ethernet)**:
  - Dynamically detects the primary connected network adapter (Gigabit / Fast Ethernet or Wi-Fi 6 / 5) and displays its negotiated hardware link speed at the top of Active Reconnect (e.g. **`1.0 Gbps (Ethernet)`**, **`100 Mbps (Ethernet)`**, or **`866 Mbps (Wi-Fi: Africa)`**).
- **🔄 On-Demand Manual Reconnect / Reset Button**:
  - `[⚡ RECONNECT / RESET NOW]` button placed directly beside Start/Stop Active Reconnect.
  - Instantly executes the configured recovery mode (Adapter Reset, DHCP Renew, Radio Cycle, or Fast Re-auth), refreshes ARP cache, renegotiates IP lease, and logs the manual recovery event.
- **Continuous Hotspot Watchdog with 15s Auto-Reset Countdown**:
  - Continuously monitors client data flow and underlying hotspot infrastructure (ICS `SharedAccess` service, Wi-Fi Direct virtual adapter IP `192.168.137.1`, and upstream internet connectivity).
  - Reduced stuck countdown from 30s down to **15s**, providing an audible/visual grace alert with one-click `[Cancel Auto-Reset]` and `[Reset Now]` options.
- **Complete Settings Persistence Across App & PC Restarts**:
  - Automatically preserves and restores all configurations in `%APPDATA%\WifiAnalyzerHotspot\settings.json`:
    - **Active Keep-Alive Settings**: Target host (`1.1.1.1`), ping interval, probe method (ICMP/TCP/HTTP), active state.
    - **Active Reconnect Settings**: Target SSID (`Africa` / `Ethernet`), drop threshold (2/3/5 drops), recovery mode, grace buffer delay, active state.
    - **Hotspot Configuration & Watchdog**: SSID, password, band/channel, watchdog interval, auto-reset watchdog toggle.
- **Configurable Log Buffers (50, 100, 300 entries)**:
  - Dropdown selectors on both **Active Keep-Alive (Heartbeat Log)** and **Active Reconnect (Watchdog Event Log)** to select log retention limits (`50`, `100`, `300`).
- **Ethernet & Wi-Fi Active Reconnect**:
  - Automatic link loss detection with user-configurable drop threshold.
  - Multi-tier recovery strategies supporting both **Ethernet Cable Connections** and **Wi-Fi** (Adapter Reset, DHCP Renew, DNS Flush, ARP Cache Reset).
- **Active Keep-Alive Pulse**:
  - Continuous ping pulse (ICMP/TCP/HTTP) to prevent network adapter power throttling or ISP idle disconnects.

### 6. 🚀 Windows Startup & Automation Settings
- **Launch on Windows Startup**: Configure registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` seamlessly without requiring admin elevation.
- **Start Minimized to System Tray**: Option to boot cleanly into the Windows notification tray without popping up on the screen.
- **Auto-Start Hotspot on Launch**: Automatically activates the Mobile Hotspot as soon as the app starts.
- **Auto-Start Stabilizer Keep-Alive**: Automatically begins active connection stabilization on startup.

### 7. ⚙️ Settings, Secure DNS, Captive Portal & Proxy
- **Top Header Quick Settings (⚙️ Gear Icon)**: Accessible directly from the main window toolbar.
- **🔒 Secure DNS (DNS-over-HTTPS / DoH)**: Toggle Secure DNS resolution On/Off with Cloudflare, Google, Quad9, AdGuard, or custom providers.
- **🌐 Active Keep-Alive HTTP HEAD Captive Portal Mode**: Sends lightweight HTTP HEAD requests to standard captive portal detection endpoints to keep public/hotel Wi-Fi connections alive without timing out.
- **🛡️ Proxy & Advanced Network Conditions**: System Proxy or Custom Manual Proxy support with probe timeout and VPN buffer delay settings.

### 8. 🎨 Vibrant Color-Coded Vector Icons & Collapsible Sidebar
- **Vibrant, High-Contrast Color Badging**:
  - Replaced monochrome font glyphs with high-definition, hand-crafted WPF vector geometry (`Path`) housed in custom 30×30 tinted badge containers:
    - 📡 **Spectrum & Channels**: Electric Cyan (`#00E5FF`) broadcast tower with radiating spectrum waves.
    - 📊 **Channel Ratings**: Vibrant Golden Amber (`#FFD600` / `#FFC400`) 3-bar signal analytics with optimal-channel sparkle.
    - ⚡ **Speed Test & Live Traffic**: High-voltage Electric Neon Green (`#00E676`) lightning bolt with inner speed streak.
    - 🔥 **Mobile Hotspot**: Fiery Orange & Yellow (`#FF3D00` / `#FF9100` / `#FFEE58`) dual-layer flame.
    - ℹ️ **Network Info**: Crisp Sky Blue (`#00B0FF` / `#40C4FF`) circular information badge.
    - 🛡️ **Connection Stabilizer**: Glowing Emerald (`#00E676`) protection shield with mint checkmark.
- **Flawless Recognition in Collapsed Mode**:
  - Each icon's vibrant color theme makes navigation instant, intuitive, and identifiable from across the room even when text labels are hidden.
- **Vector Active Wi-Fi Signal Badge**:
  - Minimized adapter status card features a vector 3-wave Wi-Fi signal beacon dynamically colorized based on actual signal strength (`#00E676` Green, `#00E5FF` Cyan, `#FFEA00` Yellow, `#FF5252` Red).
- **Instant Collapse / Expand Toggle**: Click the sidebar `[ ◀ / ▶ ]` chevron button or the top content header hamburger `[ ☰ ]` button.
- **Compact Icon-Only Mode (68px)**: Collapses the full 230px sidebar down to 68px, reclaiming 162px of horizontal screen estate for data tables, spectrum graphs, and connected clients.
- **Small Screen & High-DPI Optimization**: Lowered window minimum dimensions (`MinWidth="760"`, `MinHeight="540"`) with horizontal scroll support on device tables to guarantee flawless rendering on 1366x768, 720p, or 125%/150% scaled displays.
- **Settings Persistence**: Sidebar state (`IsSidebarCollapsed`) is automatically remembered and restored from `%APPDATA%\WifiAnalyzerHotspot\settings.json`.

---

## 🛠️ Project Structure

```text
├── WifiAnalyzerHotspot.exe          # Compiled single-file application executable
├── WifiAnalyzerHotspot/
│   ├── WifiAnalyzerHotspot.csproj   # .NET 9 WPF Project Configuration
│   ├── App.xaml / App.xaml.cs       # Application resources & converters
│   ├── MainWindow.xaml / .cs        # Main dark-themed user interface
│   ├── Controls/
│   │   ├── SpectrumCanvas.cs        # High-performance vector spectrum canvas
│   │   ├── RssiTimelineCanvas.cs    # Real-time RSSI signal strength timeline canvas
│   │   └── SpeedGauge.xaml / .cs    # Animated speed dial gauge
│   ├── Converters/
│   │   └── Converters.cs            # WPF value converters
│   ├── Models/
│   │   ├── WifiNetwork.cs           # Wi-Fi network model
│   │   ├── ChannelRating.cs         # Channel congestion model
│   │   ├── SpeedTestMetrics.cs      # Speed test & traffic models
│   │   ├── HotspotConfig.cs         # Hotspot state & client models (QoS, cumulative MB/GB)
│   │   ├── RssiHistoryPoint.cs      # Live RSSI time-series data points
│   │   └── AppSettingsModel.cs      # Settings model (Startup, Secure DNS, Captive Portal, Proxy)
│   ├── Services/
│   │   ├── WifiScannerService.cs    # Native WLAN & netsh scan engine
│   │   ├── ChannelAnalysisService.cs# Channel interference rating algorithm
│   │   ├── SpeedTestService.cs      # Ping, Jitter, Download, Upload engine
│   │   ├── NetworkMonitorService.cs # Live bandwidth monitor
│   │   ├── HotspotService.cs        # Windows Mobile Hotspot manager & NetQoS engine
│   │   ├── ConnectionStabilizerService.cs # Active keep-alive & reconnect service
│   │   └── NetworkSettingsService.cs# Settings persistence & Windows startup manager
│   └── ViewModels/
│       ├── MainViewModel.cs         # Core MVVM coordinator
│       └── RelayCommand.cs          # ICommand implementation
```

---

## 💻 Technical Requirements

- Windows 10 (Version 1903+) or Windows 11
- .NET 9.0 Desktop Runtime (installed on system)
