# 参考项目核对（2026-11-09）

本次查阅的是公开上游源码，以下判断不代表在真实手机或局域网中复测了上游软件的效果。未把 GPL 项目代码复制进 DormCast。

## Audio Link V2

项目：[Haiyang-Zhou/win-audio-to-browser](https://github.com/Haiyang-Zhou/win-audio-to-browser)，仓库许可证为 MIT。

- `app/server.py`：WASAPI 捕获、深度为 1 的最新帧队列、aiortc Opus/WebRTC。
- `app/pcm_transport.py` 与 `web/pcm-worklet.js`：有界 PCM 队列、80ms 初始缓冲、500ms 上限、采样率不同时线性插值；出错时显式报告，而非把旧数据无限排队。
- `web/receiver.js`：用户手势中同步恢复 AudioContext、连接状态和格式回报、失效状态清理。
- `docs/ARCHITECTURE.md` 明确声明 PCM 路径没有时钟漂移补偿。文档中的设置/语言同步不是多个扬声器的音频相位同步。

可借鉴：有界缓存、清楚区分播放缓冲与端到端延迟、保留设备遥测、在浏览器手势中及时激活播放、针对环形缓冲及重采样做测试。DormCast 保持 Opus/WebRTC，不直接迁移它的 PCM 主传输或反向麦克风功能。

## DoBrowserSpeaker

项目：[dogukansahil/DoBrowserSpeaker](https://github.com/dogukansahil/DoBrowserSpeaker)，仓库许可证为 GPL-3.0。

- `static/index.html`：初始 60ms / 目标 80ms / 上限 500ms；按缓冲误差缓慢调整播放速率，限制在 ±2%。
- MediaSession 和 MediaStreamDestination → audio 元素路径有助于手机把播放识别为媒体，但不能保证所有系统锁屏播放。
- `server.py`：直接发送 Float32 PCM；消息中不包含用于跨设备统一播放的源时间戳，接收端各自维持自己的缓冲。它不提供以最慢设备为基准的共同播放截止时间。

可借鉴：平滑追踪漂移、过期缓存上限、静音期间维持媒体流、播放器生命周期管理。DormCast 自行实现同类控制思路，不复制 GPL 源码。

## DormCast 0.2 的额外实现（历史）

四时间戳校时 + 全体相同的 RTP 源采样时间线 + 浏览器源播放时间戳 + 输出延迟估计 → 计算每台的基础延迟 → 主机取最慢稳定值加 40ms 余量 → 浏览器 DelayNode 补偿、主机单独 WASAPI 播放同一组 Opus 解码结果。虚拟声卡隔离采集和主机输出，广播时临时切换默认输出，正常停止或下次启动恢复。

这些步骤超出了上述两个项目当前的共同扬声器同步能力。手机硬件、蓝牙与浏览器输出延迟报告仍可能有误差，必须以实际声音测量确认。

0.5 已把统一补偿迁到主机发送端，最低目标改为 250ms，余量为 60ms，并加入稳定观察、自动回落与系统音量联动。当前行为以 [技术架构](architecture.md) 为准。
