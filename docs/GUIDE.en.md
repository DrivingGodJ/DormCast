# DormCast user and developer guide

[简体中文](GUIDE.zh-CN.md) · [Home](../README.en.md)

## Installation

Requires Windows 10 22H2 or Windows 11 x64. Install `DormCast-0.5.2-Setup-x64.exe` from Releases for the current user. The package includes .NET, WinUI and the native runtime files. The portable ZIP also works when fully extracted; copying only the EXE does not. The installer is not code-signed.

Synchronized host playback requires a virtual cable to separate the original signal from the delayed physical output. The installer has a dedicated preparation page and can open the `Drivers` folder when finished. The app also explains these steps when no virtual endpoint is detected:

1. Extract `Drivers/VBCABLE_Driver_Pack46.zip`.
2. Right-click `VBCABLE_Setup_x64.exe`, run as administrator and select Install Driver.
3. Reboot as instructed. On first setup, DormCast selects CABLE Input for capture and the default physical speaker for playback. Change these in sync settings if needed.

The official package is distributed unchanged. Its license requires manual installation rather than integration of the vendor's driver installation procedure. Skip this step if VB-CABLE is already installed. The driver is uninstalled separately and does not need reinstalling for app upgrades.

## Broadcasting and volume

Select the LAN adapter, port and 10/20ms frame size, then start broadcasting. While broadcasting, Windows Console / Multimedia default output is temporarily routed to the virtual cable. Shared-mode WASAPI loopback captures it, and the host renders delayed audio to the selected physical speaker. Stop or next-start crash recovery restores previous defaults only if DormCast still owns the route. Players that explicitly select a different output must be changed to the system default or CABLE Input.

Other devices join the same LAN and scan the QR code or open the full link. Tap the listening button if autoplay is blocked. Website play/pause affects only that listener. Closing the host window hides it to the tray; left-click restores it, and the menu can stop or exit. Exiting releases capture, peers and network ports.

In virtual-cable host playback mode, the Windows default endpoint volume, physical speaker volume and app slider are linked in both directions. Network amplitude follows the shared Windows volume percentage, avoiding excessive attenuation from the dB curve of a particular PC speaker. At 100% it keeps the original amplitude, and mute produces zero. Background listeners follow without JavaScript control messages. A separate unscaled local packet avoids applying the master attenuation twice on the host. Mute is linked too. There is no website volume slider; phone hardware volume remains independent, and this does not equalize acoustic loudness across different speakers. Remote volume changes follow the current playback delay. Direct physical-loopback mode retains normal capture behavior.

## Persistent links and networking

Each host has a stable `.local` name and random 256-bit join credential. New host names use a one-way digest of the Windows installation identity; phone MAC addresses are not collected. Browser access restrictions and randomized MACs make them unsuitable for this identity. Existing links migrate unchanged. Records are stored in `%LOCALAPPDATA%/DormCast/join-identity.json` and backed up under the current user's `HKCU/Software/DrivingGodJ/DormCast/Identity`. Reinstallation preserves both, and the backup recovers deleted app data. Reinstalling Windows, changing users or deleting both records cannot preserve the same full link.

The `.local` address uses LAN mDNS and is republished after changing adapters or IP addresses and restarting the broadcast. Some Android, enterprise Wi-Fi and multicast-blocking networks require the IP fallback. Keep the same port to preserve the complete URL. Share credential-bearing links only with trusted listeners.

The server binds the chosen private IPv4 address and filters HTTP/WebSocket sources to the same subnet. WebRTC uses that interface with no external STUN/TURN or port mapping. DTLS-SRTP protects audio; the HTTP page and signaling do not use TLS. Use a trusted LAN.

If the page will not open, check the Wi-Fi network, Windows Private network profile and firewall rules for this EXE's TCP port and WebRTC UDP. Administrators may run the included `firewall.ps1`; it adds program rules only for Private networks and LocalSubnet. If the page opens but audio fails, check UDP, autoplay permission and AP/client isolation. DormCast does not silently alter firewall settings.

## Synchronization and background playback

The common target ranges from **250ms** to 2000ms. Valid base paths use a nine-sample median, roughly six-second smoothing and a 10ms deadband. The slowest path plus a 60ms margin is rounded up to 50ms target steps. Increasing requires eight seconds of sustained evidence; decreasing requires 60 seconds of stability. Actual playout transitions at no more than 20ms per second, and new listeners measure before becoming audible. Paused, disconnected and uncertain-clock reports cannot raise the target. Connected background devices retain their last valid path estimate.

Expand device details for each path, current/planned sender compensation, executing common target, estimated alignment error, browser timeline, RTT, jitter, buffering, loss, reported output latency, hardware correction, clock RTT/uncertainty and sent/dropped frames. The main card displays the steady target step; the executing target transitions smoothly. Website error text is smoothed while raw telemetry remains available. None of these values is an acoustic measurement.

Phones default to a media-element background output with MediaSession support. Sender compensation continues when page timers stop, but native receiver buffering can still drift, and Bluetooth/hardware delays can be unreported. Returning to the foreground resets clock and timing measurements. iOS and Android can freeze pages or suspend Web Audio; long-term lock-screen/background playback is not guaranteed. Return and tap resume if paused, and check power-saving settings. iOS uses the included icon when added to the home screen without an extra promotional section on the page. A local hardware offset can be saved in website details for background-media or Bluetooth delay.

## Updates

Click the host's update button to anonymously check the latest stable GitHub Release in `DrivingGodJ/DormCast` and compare numeric versions. Internet access happens only for explicit update checks/downloads; audio stays on the LAN. The update dialog explains that broadcasting will stop. Download and update verifies the x64 installer's declared size and SHA-256 digest; cancellation and failed verification remove incomplete files. A Windows helper waits for DormCast to fully exit, verifies the file again and opens the installer. Settings and identity survive upgrades. Older Releases lacking digest metadata require manual download. If organizational policy blocks the helper or GitHub is unavailable, install manually from the official Releases page.

## Development and publishing

`dotnet run --project tests/DormCast.Checks -c Release -- --volume-gain-check` reads actual loopback audio and compares encoded network/local amplitudes without changing endpoint volume or defaults. If no audio is playing, add `--stimulus` to play about four seconds of a low-amplitude test signal and capture it through WASAPI. `--feature-checks` runs synchronization, identity, volume-boundary and update checks without playing a test sound.

If GitHub's anonymous API is rate-limited or temporarily unreachable, the app automatically reads `update.json` from the latest stable Release. It uses the same validated version, fixed repository URL, size and SHA-256 fields, with no personal token. The build generates this document after creating the installer; upload it with every Release.

`src/DormCast/Audio` owns capture, encoding, local playback and volume; `Synchronization` owns delay estimation; `Transport` owns WebRTC scheduling; `Networking` owns HTTP, signaling, mDNS and identity; `Updates` owns GitHub checks; `wwwroot` is the browser client. `src/DormCast.Desktop` contains WinUI and tray code. `scripts` builds/packages and `tests` verifies behavior.

Use .NET SDK 10.0.403 (global.json), the Windows SDK and Inno Setup 7. When not using `.tools`, place dotnet and ISCC on PATH. Before packaging, obtain the unchanged official driver ZIP and place it at `.tools/drivers/VBCABLE_Driver_Pack46.zip`; the script does not download it automatically.

```powershell
./scripts/build.ps1
```

The build runs real system-loopback checks, including a short test sound. Individual checks are `dotnet run --project tests/DormCast.Checks -c Release` and `node tests/sync-math-checks.cjs`. A development PC configured with VB-CABLE can also run `dotnet run --project tests/DormCast.Checks -c Release -- --volume-link-check`, which briefly changes and restores endpoint volume/mute. Installer smoke tests use a distinct AppId to preserve existing installations.

For releases, update both project versions, the desktop version/title and installer version; build and verify before committing and tagging. Upload the installer, portable ZIP, source ZIP and SHA256SUMS.txt to the same repository. The updater requires an asset named `DormCast-X.Y.Z-Setup-x64.exe`, a `vX.Y.Z` tag, a public stable Release and GitHub's SHA-256 digest. See [test coverage](TESTING.md) and [third-party notices](../THIRD-PARTY-NOTICES.md).
