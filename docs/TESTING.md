# DormCast 0.5 validation / 验证范围

验证环境：Windows x64，真实 VB-CABLE 与物理扬声器端点；浏览器测试使用 Chromium。完整日志包含本机设备和会话信息，仅留在开发工作区，公开包不附带个人诊断文件。

Validated on Windows x64 with real VB-CABLE and a physical output endpoint. Browser checks use Chromium. Raw diagnostic logs contain machine/session details and are kept out of public packages.

| 检查 / Check | 结果 / Result |
|---|---|
| 真实 WASAPI → 48kHz stereo Opus 10/20ms → 解码 / real capture and decode | 通过，非零 PCM、连续 RTP 时间线 / Passed, nonzero PCM and monotonic RTP |
| 系统音量 ↔ 实际扬声器 / system ↔ physical volume | 双向联动通过，短暂测试后恢复 / Passed in both directions, restored afterward |
| 虚拟端点静音后的网络音频 / network mute | 实际编码输出静音，本地未衰减音频仍存在 / Passed, local unscaled signal remains available |
| 0.5.2 传输响度 / network volume | Windows 14% 时，经真实 WASAPI 回采、Opus 编解码得到网络/本地振幅比约 0.140，未改变端点音量；这是数字幅度比，不是手机声学响度 / At Windows 14%, real loopback and Opus yielded about 0.140 network/local amplitude ratio without changing endpoint volume; this is not a phone loudness measurement |
| 稳定目标 / stable target | 噪声不改变 250ms 目标；持续慢路径升到 400ms；退出后稳定 60 秒回落到 250ms / Deterministic trace passed |
| 目标过渡 / target transition | 执行目标变化不超过每秒 20ms / Deterministic bound passed |
| 后台主机补偿 / host background compensation | 网页应用计时器及应用信令回调停用约 190 秒，播放持续；受控慢路径令目标 250→750→250ms、该连接补偿约 149→649→149ms / Playback continued for about 190s with page timers and app message handling disabled; controlled slow-path rise and automatic fall passed |
| 五条浏览器连接 / five browser peers | 同机五条真实 Opus/WebRTC 连接建立，单连接退出不影响其他连接；这不是五台物理设备 / Five real local-browser peers connected; one leaving preserved the others; not five physical devices |
| 完整固定链接 / persistent full join URL | 旧链接迁移、备份恢复、机器区分、启停重启通过 / Migration, backup, machine separation and restart passed |
| 0.5.3 双接入 / parallel access | 默认 mDNS、IPv4 来回切换，原生窗口两种二维码解码、显示网址和复制内容一致；mDNS 与 IPv4 两条浏览器 WebRTC 连接同时接收，切换后未重连、收包持续；IPv4 网页资源没有请求 .local / Default mDNS, native QR decoding and copy consistency passed; simultaneous mDNS/IPv4 WebRTC peers continued receiving without reconnects, and the IPv4 page requested no .local HTTP resources |
| 0.6.0 浏览器远程控制 / browser remote control | WebSocket 认证后控制主机播放、暂停、上一首、下一首、音量、步进与静音并通过 `host-state` 多端实时同步；旧配置迁移默认开启；关闭远程控制与越界参数拒绝；桌面端与 390px 手机端布局及 CSP 验证通过 / Authenticated WebSocket play/pause/prev/next/volume/step/mute with multi-client `host-state` broadcast, legacy settings default migration, disabled/out-of-bounds rejection, and desktop/390px mobile UI & CSP verification passed |
| 0.6.1 网页封面动态主题色与启动静默更新 / dynamic cover theme & silent startup update | 浏览器端从同源封面 `blob:` 实时提取主色并生成满足 WCAG 对比度约束的暗色主题与 `<meta name="theme-color">`，黑白封面切换为中性银灰暗色，无封面/失败/断连回退默认绿色；桌面端运行时版本号从程序集读取并支持启动后后台静默检查更新（有新版弹通知，无更新或断网完全静默） / Client-side dominant-color extraction derives WCAG-calibrated dark palettes and `<meta name="theme-color">` (neutral silver for monochrome covers, default green when no cover); desktop host reads normalized assembly version and checks updates silently in the background on startup (notifying only when a newer release exists, silent when up to date or unreachable) |
| 0.7.0 强制虚拟声卡安装/卸载、输出单选与更快设备对齐 / mandatory virtual cable install/uninstall, output-only selection & faster device convergence | 安装程序检测未安装 VB-CABLE 时强制安装（已安装自动跳过），卸载时与本体一并卸载；软件内仅可选择物理输出设备并自动绑定虚拟声卡，缺失虚拟声卡时锁定广播并提示安装指引；单设备平滑追赶速度提升至 50ms/s（主机扬声器 35ms/s），而统一延迟目标变化速度维持 20ms/s 与 8s/60s 观察窗口不变 / Installer requires VB-CABLE when absent (skipping when already installed) and removes it on uninstall; desktop app auto-selects the virtual cable, exposes physical output devices only, and blocks broadcast with an install guide if missing; per-device smooth convergence increased to 50ms/s (35ms/s host monitor) while common target transition remains 20ms/s with 8s/60s windows |
| 0.7.1 后台直连保活、iOS PWA 引导、安卓接收端 APK 与更快对齐 / direct background audio, iOS PWA guide, Android receiver APK & faster sync | 网页后台模式在主机补偿下直连原生 `<audio>` 绕开 `AudioContext` 后台冻结并补全 `MediaSession` 图标；完善 `standalone` PWA 与 Token 记忆及 iOS Safari 添加主屏幕引导；提供含 `mediaPlayback` 前台服务与内置扫码配对/局域网扫描的 Android 接收端 APK；单设备对齐进一步提速至 80ms/s（主机 50ms/s，首测 300ms 上报，目标变化仍维持 20ms/s），桌面端与收听端明确提示所有设备需在同一 Wi-Fi / 局域网 / Direct native `<audio>` playback bypasses `AudioContext` suspension under host compensation with fallback `MediaSession` artwork; standalone PWA with token persistence and iOS Safari home-screen guide; Android receiver APK with `mediaPlayback` foreground service, in-app QR pairing and LAN discovery; faster per-device alignment (80ms/s sender, 50ms/s host monitor, 300ms initial reports, target still 20ms/s) plus explicit same-Wi-Fi prompts |
| 0.7.2 修复 iOS 桌面图标无法连接与同步算法优化 / fix iOS home-screen connection & improved sync convergence | 移除触发 iOS 隔离 standalone 沙盒与截断 `#join` 的 `start_url`/`apple-mobile-web-app-capable`，恢复 `display: browser` 并增加 Cookie + `localStorage` 双重 Token 兜底；同步算法新增首包预置发送缓冲（消除连接初期 RTP 静音缺口引发的 WebRTC NetEQ 抖动缓冲膨胀）、将 `PathLatency` 中值窗口由 9 缩短至 5（1.5s EMA + 2ms 量化）与 500ms 稳态上报，同时将单设备追赶速率校准至 NetEQ 无膨胀甜点 40ms/s（目标速率维持 20ms/s 不变） / Removed `start_url` and `apple-mobile-web-app-capable` that stripped `#join` and forced iOS into an isolated HTTP-restricted WebApp sandbox, restoring `display: browser` with Cookie + `localStorage` token fallback; pre-seeded initial sender hold before packet #0 to eliminate NetEQ jitter-buffer inflation, shortened `PathLatency` median window from 9 to 5 with 1.5s EMA and 2ms quantization, sped up steady-state reports to 500ms, and tuned slew rate to the NetEQ-safe 40ms/s sweet spot (target rate still 20ms/s) |
| 0.7.3 新设备同步对齐后再出声与固定高音质 Opus / sync-locked unmute & locked high-quality Opus | 新增新设备接入与暂停恢复时的同步锁定门控：主机在补偿收敛前按 `sync-report` 周期（250ms）即时推送 `plannedMs` 与 `ready` 收敛状态，收听端保持静音直到主机补偿收敛且实测偏差连续 300ms 稳定在 ±25ms 以内后再平滑淡入出声（兼容 iOS `muted` 解锁，附 10 秒安全兜底与实时进度提示）；同时移除冗余的 10ms/20ms 帧长切换并锁定最高品质 `20ms / 256kbps`（Complexity 8）立体声 Opus 音乐模式 / Added a sync-lock gate for newly joining and resuming listeners: host pushes `plannedMs` and convergence `ready` every 250ms until converged, while the listener stays muted until host compensation converges and measured alignment stays within ±25ms for 300ms continuously before smoothly fading in (iOS-safe `muted` unmute with 10s safety timeout and live progress hints); removed redundant 10/20ms frame toggle and locked audio to high-quality `20ms / 256kbps` (Complexity 8) stereo Opus music mode |
| 更新保护 / update verification | 版本比较、预发布排除、外部地址拒绝、正确 SHA-256、损坏/取消清理通过 / Version and download checks passed |
| 匿名限流 / anonymous rate limit | 403 限流自动转向 Release 内更新信息的检查通过 / Automatic fallback to the public Release manifest passed |
| 实际在线更新下载 / live update download | 在真实匿名 API 403 限流下，成功读取 Release 更新信息、比较版本并完整下载官方安装包，通过 SHA-256；未自动启动安装器 / Actual anonymous 403 fallback, version comparison and full verified installer download passed; installer launch was not exercised |
| 网络与生命周期 / network and lifecycle | 子网、Host/Origin、认证、启动回滚、停止后端口释放通过 / Filtering, auth, rollback and port release passed |

安装包采用单独测试 AppId 进行安装/卸载验证，以保留开发电脑已有安装；正式包与测试包使用相同发布文件和相同安装步骤，只有注册身份、快捷方式与测试文件名不同。安装/卸载均返回 0；禁止查找全局 .NET 后仍能运行采集与 HTTP，所需原生依赖无缺失，官方驱动原包哈希一致，现有用户配置哈希保持不变。

Installer smoke checks use a separate AppId to preserve the existing installation. Payload and installation steps match the release build; registration identity, shortcuts and test filename differ. Install/uninstall returned 0; capture and HTTP worked with global .NET lookup disabled; native dependencies were complete, the official driver ZIP hash matched and existing profile hashes were preserved.

## 尚未取得的结果 / Outstanding measurements

没有发布“已达 200ms”“设备间误差 Xms”或“iOS 锁屏一直播放”的成绩。250ms 是配置下限，并非测得的端到端延迟。主机软件测量的路径、目标、RTT 和缓冲都不能替代实际扬声器声学测量。

No measured claim is made for sub-200ms end-to-end latency, a specific inter-device acoustic error, or uninterrupted iOS lock-screen playback. The 250ms floor is a configuration value, not an acoustic measurement.

仍需按 [测量流程](measurement.md) 测试一、二、五台物理设备、不同 Wi-Fi 信号、锁屏/后台、切歌暂停、断网重连及长时播放。网页应用计时器暂停的测试只能证明主机仍执行补偿，不能证明操作系统继续允许音频输出。

Physical one/two/five-device tests, Wi-Fi signal variation, mobile background/lock-screen behavior, track changes, reconnects and long sessions remain to be measured. Suspending page timers can prove sender compensation continues, but cannot prove that a mobile OS keeps audio output alive.
