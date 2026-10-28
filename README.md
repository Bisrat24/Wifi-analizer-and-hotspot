# 📡 WiFi Analyzer & Hotspot Pro (v1.1.3)

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

### 4. 🔥 Mobile Hotspot Creator & Manager (with Highest PC Priority)
- **⚡ Highest PC Priority Mode**:
  - Automatically created and run with maximum OS scheduling and hardware priority:
    1. **High CPU Priority**: Elevates the application process, `SharedAccess` (ICS NAT & DHCP router service), and `wlanext.exe` (Wi-Fi 802.11 framework) to `High` process priority class.
    2. **Win32 Execution Lock**: Invokes `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED)` to guarantee Windows never parks CPU cores, throttles Wi-Fi, or sleeps while hotspot is active.
    3. **Maximum Radio Power**: Enforces Wi-Fi adapter Transmit Power to **Level 5 (Highest)**, turns on **Throughput Booster**, and disables **Packet Coalescing** & **MIMO Power-Saving** sleep.
    4. **Zero Network Throttling**: Sets `NetworkThrottlingIndex = 0xFFFFFFFF` and `SystemResponsiveness = 0` (allocating 100% CPU priority to network routing).
    5. **Top Virtual Route Metric**: Assigns `InterfaceMetric = 5` to the Microsoft Wi-Fi Direct Virtual Adapter for prioritized routing.
- **Windows Mobile Hotspot Integration**:
  - Uses the official modern Windows 10/11 WinRT `NetworkOperatorTetheringManager` API.
- **Guaranteed Restart Reliability**:
  - `StopHotspotAsync` cleanly waits for virtual adapter teardown (`OperationalState == Off`) and settling delay before returning.
  - `StartHotspotAsync` verifies `SharedAccess` (ICS / DHCP) service status, gracefully handles `InTransition` state, awaits WinRT operation results, and retries up to 3 times with backoff.
  - Eliminates `OperationInProgress` errors when toggling off/on or restarting.
- **Multi-Vector Client Data Flow Health Check**:
  - Multi-vector verification eliminating false "No Data" flags:
    1. **ICMP Ping**: Fast echo request.
    2. **Layer 4 TCP Probe (Port 80/443)**: For devices that drop ICMP packets by default (e.g. iOS/iPadOS, firewalled phones), listens for TCP RST/ACK confirming active IP stack communication.
    3. **Hardware ARP Link Verification**: Confirms active L2 Wi-Fi association when host internet is operational.
  - Correctly marks clients as `● Getting Data` when active and `⚠ No Data` when data is stalled or cut.
- **Continuous 1-Second Watchdog Timer**:
  - Decoupled from routine refresh cycles; continuously evaluates hotspot health every 1 second.
  - 30-second cancellation countdown triggers if 100% of connected devices experience zero data flow for 5 continuous seconds.
  - Countdown cancels immediately if any connected client resumes active data flow.
- **Authentic Client Connection Speed**:
  - Displays real-time live responsiveness (`Online • {latency}`, `Active Flow`, or `Standby`) alongside the negotiated hardware PHY link speed.

### 5. 🛡️ Connection Stabilizer & Reconnect Guardian
- **Active Keep-Alive**: Ultra-lightweight pulses prevent Wi-Fi sleep and router NAT state table drops.
- **Multi-Strategy Reconnect**: Detects dropped connections and restores links using adapter resets, DHCP release/renew, or fast Wi-Fi re-auth.

### 6. ⚙️ Settings, Secure DNS, Captive Portal & Proxy (v1.1.2 New)
- **Top Header Quick Settings (⚙️ Gear Icon)**:
  - Accessible directly at the top of the app next to Auto-Scan and Scan Now.
- **🔒 Secure DNS (DNS-over-HTTPS / DoH)**:
  - Toggle Secure DNS resolution On/Off.
  - Built-in DoH providers: **Cloudflare** (`1.1.1.1`), **Google Public DNS** (`8.8.8.8`), **Quad9** (`9.9.9.9`), **AdGuard DNS**, and Custom DoH URLs.
  - Live DoH query tester and one-click adapter DNS configuration / DHCP restore.
- **🌐 Active Keep-Alive HTTP HEAD Captive Portal Mode**:
  - Sends lightweight `HTTP HEAD` requests directly to standard captive portal detection endpoints (Microsoft Connect Test, Google Connectivity Check, Mozilla Firefox Detect Portal, Cloudflare, or Custom URL).
  - Prevents captive portal session timeouts on hotel, airport, public Wi-Fi, and corporate Cisco AnyConnect networks.
  - **Captive Portal Redirect Detection**: Detects HTTP 302/307 redirects to login pages and alerts user with one-click browser launcher.
- **🛡️ Proxy & Advanced Network Conditions**:
  - Proxy support: System Proxy or Custom Manual Proxy (Host, Port, Local Bypass, Credentials) applied globally to app requests.
  - Configurable probe timeout (ms) and VPN / Cisco AnyConnect reconnect buffer delay.
  - Automatic persistence to `%APPDATA%\WifiAnalyzerHotspot\settings.json`.
- **Custom Configuration & Broadcast Card**:
  - Custom SSID, WPA2 passphrase, and band/channel selection (`Auto`, 2.4 GHz channels 1-11, 5 GHz high-speed channels).
  - Live broadcast channel card synchronized with the wireless hardware.
- **Real-Time Connected Client Discovery & Management**:
  - Displays connected device **Host Name**, **IP Address**, and **MAC Address**.
  - **Device Disconnect**: Disconnect any specific device on-demand.
  - **Ironclad Multi-Layer Device Blocking (Blacklist)**: Blocks unauthorized devices across Windows Defender Firewall (Inbound, Outbound, ICS DNS port 53, DHCP port 67), route blackhole loop, and normalized MAC address filtering.
  - **Unblock Controls**: Easily restore network access with one-click unblocking.
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
