# DormCast 0.5 validation / 验证范围

验证环境：Windows x64，真实 VB-CABLE 与物理扬声器端点；浏览器测试使用 Chromium。完整日志包含本机设备和会话信息，仅留在开发工作区，公开包不附带个人诊断文件。

Validated on Windows x64 with real VB-CABLE and a physical output endpoint. Browser checks use Chromium. Raw diagnostic logs contain machine/session details and are kept out of public packages.

| 检查 / Check | 结果 / Result |
|---|---|
| 真实 WASAPI → 48kHz stereo Opus 10/20ms → 解码 / real capture and decode | 通过，非零 PCM、连续 RTP 时间线 / Passed, nonzero PCM and monotonic RTP |
| 系统音量 ↔ 实际扬声器 / system ↔ physical volume | 双向联动通过，短暂测试后恢复 / Passed in both directions, restored afterward |
| 虚拟端点静音后的网络音频 / network mute | 实际编码输出静音，本地未衰减音频仍存在 / Passed, local unscaled signal remains available |
| 稳定目标 / stable target | 噪声不改变 250ms 目标；持续慢路径升到 400ms；退出后稳定 60 秒回落到 250ms / Deterministic trace passed |
| 目标过渡 / target transition | 执行目标变化不超过每秒 20ms / Deterministic bound passed |
| 后台主机补偿 / host background compensation | 网页应用计时器及应用信令回调停用约 190 秒，播放持续；受控慢路径令目标 250→750→250ms、该连接补偿约 149→649→149ms / Playback continued for about 190s with page timers and app message handling disabled; controlled slow-path rise and automatic fall passed |
| 五条浏览器连接 / five browser peers | 同机五条真实 Opus/WebRTC 连接建立，单连接退出不影响其他连接；这不是五台物理设备 / Five real local-browser peers connected; one leaving preserved the others; not five physical devices |
| 完整固定链接 / persistent full join URL | 旧链接迁移、备份恢复、机器区分、启停重启通过 / Migration, backup, machine separation and restart passed |
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
