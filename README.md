# 📡 WiFi Analyzer & Hotspot Pro (v1.1.4)

A native Windows desktop application with standalone executable (`.exe`) for analyzing Wi-Fi channels, bandwidth, signal strength, internet connection speed, connection stabilization with active keep-alive / auto-reconnect (Ethernet & Wi-Fi), and creating/managing a custom Wi-Fi Mobile Hotspot with device disconnect, block list capabilities, per-client bandwidth throttling (QoS), and cumulative data usage tracking.

---

## 🚀 Quick Launch

You can run the app immediately by double-clicking:
- **`WifiAnalyzerHotspot.exe`** (located right in the root directory: `c:\Users\Bisrat\Documents\APPS\Wifi analizer and hotspot\WifiAnalyzerHotspot.exe`)

---

## ✨ Key Features (v1.1.4)

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

### 4. 🔥 Mobile Hotspot Creator & Manager (with Highest PC Priority & QoS)
- **⚡ Highest PC Priority Mode**:
  - Automatically created and run with maximum OS scheduling and hardware priority:
    1. **High CPU Priority**: Elevates the application process, `SharedAccess` (ICS NAT & DHCP router service), and `wlanext.exe` (Wi-Fi 802.11 framework) to `High` process priority class.
    2. **Win32 Execution Lock**: Invokes `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED)` to guarantee Windows never parks CPU cores, throttles Wi-Fi, or sleeps while hotspot is active.
    3. **Maximum Radio Power**: Enforces Wi-Fi adapter Transmit Power to **Level 5 (Highest)**, turns on **Throughput Booster**, and disables **Packet Coalescing** & **MIMO Power-Saving** sleep.
    4. **Zero Network Throttling**: Sets `NetworkThrottlingIndex = 0xFFFFFFFF` and `SystemResponsiveness = 0` (allocating 100% CPU priority to network routing).
    5. **Top Virtual Route Metric**: Assigns `InterfaceMetric = 5` to the Microsoft Wi-Fi Direct Virtual Adapter for prioritized routing.
- **🚦 Per-Client Bandwidth Throttling / QoS**:
  - Cap individual client speeds with one-click QoS limits (`Unlimited`, `1 Mbps`, `2 Mbps`, `5 Mbps`, `10 Mbps`, `20 Mbps`).
  - Implemented using native Windows `New-NetQosPolicy` kernel traffic shaping matching client IPv4 addresses for zero overhead.
- **📊 Cumulative Session Data Usage per Device**:
  - Accurately tracks and displays per-device cumulative download and upload data usage (`↓ MB • ↑ MB`) throughout the hotspot session.
- **Multi-Vector Client Data Flow Health Check**:
  - Multi-vector verification eliminating false "No Data" flags:
    1. **ICMP Ping**: Fast echo request.
    2. **Layer 4 TCP Probe (Port 80/443)**: For devices that drop ICMP packets by default (e.g. iOS/iPadOS, firewalled phones), listens for TCP RST/ACK confirming active IP stack communication.
    3. **Hardware ARP Link Verification**: Confirms active L2 Wi-Fi association when host internet is operational.
  - Correctly marks clients as `● Getting Data` when active and `⚠ No Data` when data is stalled or cut.
- **Continuous 1-Second Watchdog Timer**:
  - Continuously evaluates hotspot health every 1 second.
  - 30-second cancellation countdown triggers if 100% of connected devices experience zero data flow for 5 continuous seconds.
  - Countdown cancels immediately if any connected client resumes active data flow.
- **Ironclad Multi-Layer Device Blocking (Blacklist)**:
  - Blocks unauthorized devices across Windows Defender Firewall (Inbound, Outbound, ICS DNS port 53, DHCP port 67), route blackhole loop, and normalized MAC address filtering.

### 5. 🛡️ Connection Stabilizer & Reconnect Guardian
- **Ethernet & Wi-Fi Active Reconnect**:
  - Automatic link loss detection with user-configurable drop threshold.
  - Multi-tier recovery strategies supporting both **Ethernet Cable Connections** and **Wi-Fi**:
    - **Adapter Reset**: Disables and re-enables the active NIC via native Windows netsh.
    - **DHCP IP Renew & DNS Flush**: Fixes IP lease expirations, conflicts, and stale DNS.
    - **Fast Re-negotiation**: Resets ARP cache and initiates fast link recovery.
- **Active Keep-Alive Pulse**:
  - Continuous ping pulse (ICMP/TCP/HTTP) to prevent network adapter power throttling or ISP idle disconnects.
  - Real-time heartbeat stream with latency metrics and packet loss tracking.

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
