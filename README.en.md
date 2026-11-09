[简体中文](README.md) | **English**

# 宿舍之音 · DormCast

Play music on Windows and listen together. DormCast broadcasts the computer's actual system audio to phones and computers on the same LAN through an ordinary browser.

[Download the Windows installer](https://github.com/DrivingGodJ/DormCast/releases/latest) · [User guide](docs/GUIDE.en.md)

## One song, several devices

The native WinUI 3 host displays artwork, artist and album supplied by your player. Closing its window keeps broadcasting in the system tray. Listeners scan the QR code or bookmark the persistent join link. No account, receiver installation or external audio server is required.

- **Control the broadcast from your PC**: in virtual-cable synchronized playback mode, Windows volume keys and the app slider control the physical speaker and audio sent to every browser. The website has no separate volume slider; each phone still controls its own hardware volume.
- **A steadier shared timeline**: 48kHz stereo Opus over WebRTC. The common target starts at 250ms and changes in 50ms steps after an observation period, with automatic reduction after 60 seconds of stability. Compensation runs on the host even when a phone suspends page timers.
- **Per-device diagnostics**: inspect base-path estimates, current and planned compensation, estimated alignment error, RTT, jitter, buffering, loss and measurement freshness.
- **Everyday desktop features**: Windows theme and accent integration, translucent window, QR code, iOS home-screen icon, and manual GitHub update checks with installer download.

Delay targets and alignment errors are software estimates. Acoustic testing across multiple physical devices is still outstanding. Browsers and iOS power policies can suspend background playback, and Bluetooth may add unreported hardware delay.

## Start listening

1. Install the latest Release on Windows 10 22H2 or Windows 11 x64. The .NET and WinUI runtime files are included.
2. Follow the first-use guide: extract the bundled, unchanged official VB-CABLE package, manually run its driver installer as administrator, and reboot when prompted.
3. Open DormCast, confirm the virtual capture endpoint and physical speaker, then start broadcasting. Connect other devices to the same LAN, scan the code and tap the listening button if autoplay is blocked.

Each host retains its link, backed up in the current Windows user's registry. Ordinary reinstallation and deletion of application data can recover it. Reinstalling Windows, changing users or deleting both identity records changes the complete join link. Use the host's IP fallback when the network does not support `.local` names.

## Documentation and source

- [Installation, volume, background playback, firewall and updates](docs/GUIDE.en.md)
- [Architecture (Chinese)](docs/architecture.md) · [Test results and limits](docs/TESTING.md) · [Acoustic measurement procedure (Chinese)](docs/measurement.md)
- [Reference projects (Chinese)](docs/reference-projects.md) · [Third-party notices](THIRD-PARTY-NOTICES.md)

DormCast's own code uses the [MIT License](LICENSE). Dependencies and the bundled driver retain their own licenses. SIPSorcery's license includes additional geographic restrictions; users must install the unchanged official VB-CABLE package manually. See the third-party notices.
