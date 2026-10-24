# 📡 WiFi Analyzer & Hotspot Pro (v1.0.9)

A native Windows desktop application with standalone executable (`.exe`) for analyzing Wi-Fi channels, bandwidth, signal strength, internet connection speed, connection stabilization with active keep-alive / auto-reconnect (Ethernet & Wi-Fi), and creating/managing a custom Wi-Fi Mobile Hotspot with device disconnect and block list capabilities.

---

## 🚀 Quick Launch

You can run the app immediately by double-clicking:
- **`WifiAnalyzerHotspot.exe`** (located right in the root directory: `c:\Users\Bisrat\Documents\APPS\Wifi analizer and hotspot\WifiAnalyzerHotspot.exe`)

---

## ✨ Key Features

### 1. 📡 Wi-Fi Channel & Spectrum Analyzer
- **Real-Time Spectrum Graph**:
  - Visual parabolic bell curves showing all nearby Wi-Fi networks and channel widths.
  - Interactive Band Switcher: **2.4 GHz Band** (Channels 1 to 14) and **5 GHz Band** (Channels 36 to 165).
  - Clearly identifies channel overlap, interference, and co-channel congestion.
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

### 3. ⚡ Internet Speed Test & Live Traffic Monitor
- **Multi-metric Speed Test**:
  - **Ping & Latency** (ms) with ICMP and TCP fallback for 100% reliability.
  - **Jitter** (ms) calculated across multi-round samples.
  - **Download Speed** (Mbps) with real-time multi-chunk download stream engine and smooth gauge animation.
  - **Upload Speed** (Mbps) with streaming payload benchmark.
  - **Dynamic Button Control**: The "Start Speed Test" button automatically disables and dims while testing is active to prevent duplicate triggers.
- **Animated Speedometer**: Glowing radial gauge with dynamic needle, glowing arc, and digital readout.
- **Real-time Adapter Throughput Monitor**:
  - Continuous live upload and download bandwidth monitor updating every second directly from the active wireless network interface.

### 4. 🔥 Mobile Hotspot Creator & Manager (v1.0.9 Enhanced)
- **Windows Mobile Hotspot Integration**:
  - Uses the official modern Windows 10/11 WinRT `NetworkOperatorTetheringManager` API.
- **Custom Configuration**:
  - Set custom **Network Name (SSID)**.
  - Set custom **Password / Security Key** (WPA2-Personal, 8-63 characters).
  - **Channel & Band Selection**:
    - `Auto (Recommended - Sync with active Wi-Fi)`
    - `2.4 GHz - Channel 1 (2412 MHz - Cleanest Non-overlapping)`
    - `2.4 GHz - Channel 6 (2437 MHz - Standard Non-overlapping)`
    - `2.4 GHz - Channel 11 (2462 MHz - High Compatibility)`
    - `5 GHz - Channel 36 / 40 / 44 / 48 (High-Speed UNII-1)`
    - `5 GHz - Channel 149 (High-Speed UNII-3)`
- **Live Active Broadcast Channel Card**:
  - Displays the active channel, frequency, and band (e.g., `Channel 11 (2462 MHz) • 2.4 GHz`) synchronized with the wireless hardware.
- **Hardware-Level Connected Client Data Flow Health Check & Auto-Reset**:
  - **Multi-Vector Health Verification**: Combines in-process native link-layer `SendARP` hardware probing, ICMP Echo ping (250ms), and Wi-Fi Direct virtual adapter traffic delta.
  - **Bypasses Phone Power-Savers & Firewalls**: Unlike ICMP ping alone, `SendARP` queries the client Wi-Fi chipset directly and is never blocked by Android/iOS screen locks or firewalls.
  - If all devices connected to the hotspot are not working / getting no data for $\ge 5$ seconds, an amber alert banner triggers with an interactive 30-second cancellation countdown before auto-recovering the hotspot.
  - **Auto-Dismissal**: If client data flow resumes while the countdown is active, the countdown cancels itself automatically.
  - **Complete Suspension During Reset**: While resetting, the health check, countdown, watchdog, and all polling completely stop to guarantee a clean slate without recursion.
  - Dedicated **`[Test Watchdog]`** diagnostic button to simulate no-data states and verify countdown/reset behavior on demand.
  - Users can cancel the auto-reset or trigger it immediately on demand.
- **Robust Consolidated Hotspot Reset**:
  - Eliminates multi-process race conditions by running a unified, resilient reset flow:
    - Waits for clean virtual adapter teardown (`OperationalState == Off`).
    - Cleanly resets and restarts `SharedAccess` (ICS / DHCP) service and locks the `192.168.137.1` subnet.
    - Flushes stale ARP cache.
    - Reconfigures SSID, password, and band.
    - Starts tethering with multi-attempt retry logic and operational status verification.
- **Rock-Solid Connected Device Scanning (Before & After Reset)**:
  - **Direct IPv4 Extraction**: Inspects WinRT `HostNames` collection to resolve IPv4 addresses directly from the Tethering Manager.
  - **Fast Hardware ARP Subnet Sweep**: Parallel `SendARP` subnet probe discovers connecting/reconnecting devices in $< 50\text{ ms}$, ensuring devices are never lost after a reset.
  - **Smooth In-Place Reconciliation**: Client list updates without clearing rows, preventing UI flicker and selection loss.
  - **Post-Reset Grace Period**: 25-second grace window after reset to allow reconnecting clients to establish DHCP leases without triggering false health check alarms.
- **Improved DHCP Settings & Conflict Scope (192.168.137.1)**:
  - Automates Windows Internet Connection Sharing (ICS / `SharedAccess`) scope management.
  - Automatically verifies service status and flushes stale subnet ARP caches to eliminate "Obtaining IP address..." hangs on phones.
  - Dedicated "Repair DHCP" one-click diagnostic button.
- **Real-Time Connected Client Discovery & Management**:
  - Displays connected device **Host Name**, **IP Address**, and **MAC Address**.
  - **Live Per-Client Connection Speed & Latency**: Shows real-time negotiated link speed (Mbps) and live round-trip latency for every active client.
  - **Device Disconnect**: Disconnect any specific device on-demand.
  - **Ironclad Multi-Layer Device Blocking (Blacklist)**: Blocks unauthorized devices across Windows Defender Firewall (Inbound, Outbound, ICS DNS port 53, DHCP port 67), route blackhole loop, and normalized MAC address filtering.
  - **Unblock Controls**: Easily restore network access with one-click unblocking.

### 5. 🛡️ Connection Stabilizer (Active Keep-Alive & Auto-Reconnect)
- **Ethernet & Wi-Fi Active Reconnect**:
  - Automatic link loss detection with user-configurable drop threshold.
  - Multi-tier recovery strategies supporting both **Ethernet Cable Connections** and **Wi-Fi**:
    - **Adapter Reset**: Disables and re-enables the active NIC via native Windows netsh.
    - **DHCP IP Renew & DNS Flush**: Fixes IP lease expirations, conflicts, and stale DNS.
    - **Fast Re-negotiation**: Resets ARP cache and initiates fast link recovery.
- **Active Keep-Alive Pulse**:
  - Continuous ping pulse (ICMP/TCP/HTTP) to prevent network adapter power throttling or ISP idle disconnects.
  - Real-time heartbeat stream with latency metrics and packet loss tracking.

### 6. ℹ️ Network Details & Diagnostics
- Local IPv4 Address, Default Gateway, and MAC address.
- Active Wi-Fi adapter model and description.
- Transmit and Receive link rates (Mbps) in real time.

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
│   │   └── SpeedGauge.xaml / .cs    # Animated speed dial gauge
│   ├── Converters/
│   │   └── Converters.cs            # WPF value converters
│   ├── Models/
│   │   ├── WifiNetwork.cs           # Wi-Fi network model
│   │   ├── ChannelRating.cs         # Channel congestion model
│   │   ├── SpeedTestMetrics.cs      # Speed test & traffic models
│   │   └── HotspotConfig.cs         # Hotspot state & client models
│   ├── Services/
│   │   ├── WifiScannerService.cs    # Native WLAN & netsh scan engine
│   │   ├── ChannelAnalysisService.cs# Channel interference rating algorithm
│   │   ├── SpeedTestService.cs      # Ping, Jitter, Download, Upload engine
│   │   ├── NetworkMonitorService.cs # Live bandwidth monitor
│   │   └── HotspotService.cs        # Windows Mobile Hotspot manager
│   └── ViewModels/
│       ├── MainViewModel.cs         # Core MVVM coordinator
│       └── RelayCommand.cs          # ICommand implementation
```

---

## 💻 Technical Requirements

- Windows 10 (Version 1903+) or Windows 11
- .NET 9.0 Desktop Runtime (installed on system)
