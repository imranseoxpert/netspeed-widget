# NetSpeed Widget

A small, always-on-top download/upload speed meter for Windows 10 and 11. It sits on the taskbar next to the clock and also has a tray icon.

```
↓ 12.4 MB/s
↑ 1.08 MB/s
```

## Download

**[Download NetSpeedWidget.exe](https://github.com/imranseoxpert/netspeed-widget/releases/latest/download/NetSpeedWidget.exe)** (about 30 KB, a single file with no installer).

Put it anywhere, for example in `Documents`, and double-click it. It runs on Windows 10 and 11 with nothing else to install, because it uses .NET Framework 4.8, which Windows already includes.

The `.exe` isn't code-signed, so the first time you open it Windows may show **"Windows protected your PC"**. Click **More info**, then **Run anyway**.

Right-click the widget or the tray icon to open the menu, and turn on **Start with Windows** there.

## Build it yourself

If you'd rather compile it yourself, download this repository, then double-click **build.bat** on a Windows PC. It uses the C# compiler that comes with Windows, so you don't need to install anything. It creates **NetSpeedWidget.exe** in the same folder.

## Using it

- **Move it:** drag it with the left mouse button. **Lock position** stops it from moving.
- **Show or hide it:** left-click the tray icon.
- **Menu options:** network adapter, Bytes (KB/s, MB/s) or Bits (Kbps, Mbps), update every 0.5, 1 or 2 s, one or two lines, dark or light, text size, opacity, always on top, hide while a full-screen app is running.
- **Details and totals...** lists the adapters being counted and how much has been downloaded and uploaded since the app started.
- Settings are saved in `%APPDATA%\NetSpeedWidget\settings.ini`.

## How accurate it is

- It reads Windows' own **64-bit byte counters** for each network adapter (`GetIfTable2`). Task Manager and Performance Monitor use the same counters.
- Each speed is **bytes moved ÷ exact time between two reads**. The time comes from the high-resolution performance counter, so a late UI timer doesn't skew the result.
- **Auto** mode adds up all connected **physical** adapters. It skips loopback, Windows filter layers (WFP/QoS), and virtual adapters (Hyper-V, WSL, Docker, VPN), because their traffic also passes through the physical adapter and would be counted twice.
- Adapters that disconnect or reset during a read are handled, so you don't get false spikes.

**Checking it against Task Manager:** switch the units to **Bits**. Then open Task Manager → Performance → Wi-Fi/Ethernet. The Receive and Send numbers should match.

**Why it reads a little higher than speedtest.net:** adapter counters include packet headers (Ethernet, IP and TCP), while speed-test sites count only the data. Expect the widget to be about 3–6% higher during a test. That's normal.

If you use a VPN and want to see only the traffic inside the tunnel, choose the VPN adapter under **Network adapter**.

## License

[MIT](LICENSE)
