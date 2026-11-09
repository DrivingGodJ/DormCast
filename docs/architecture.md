# DormCast 0.5 技术架构

Windows 播放器 → VB-CABLE 默认输出 → 共享 WASAPI Loopback → 有界立体声 PCM → 48kHz Opus → 每听众 WebRTC/SRTP → 浏览器媒体输出。

主机为原生 WinUI 3，核心是独立 .NET 库。HTTP、WebSocket 信令、mDNS 和连接管理均在本地，无外部媒体服务器或 STUN/TURN。

## 模块与音量

- `Audio/LoopbackSource`：共享事件回环，默认 48kHz 立体声，重连输出端点，PCM 缓冲上限 60ms。
- `AudioEngine`：统一采样时间线，Opus 音乐模式 128kbps / 10或20ms。系统音量联动在主机工作线程运行：虚拟默认端点与实际扬声器双向镜像百分比和静音，网络编码前按共享音量百分比调整幅度，不把单款声卡的 dB 衰减曲线套到其他接收设备；100% 为原始幅度，不额外放大。标准 VB-CABLE 回环在端点静音时仍有原始信号，不能假定系统滑块已缩放传输音频。
- `HostMonitor`：主机使用另一条未缩放 Opus 编码，保证相同编解码延迟，再由实际扬声器端点应用音量一次。避免对已损失精度的解码音频除以接近零的增益。时间戳 PCM 历史按统一目标渲染，小幅速率追踪，设备异常重连。
- `SpeakerVolumeController`：独立缓存端点与合并滑块输入，UI 线程不调用声卡枚举或逐次打开端点。
- `DefaultAudioRoute`：广播时切 Console/Multimedia 默认输出，保存原路由并按所有权恢复，支持下次启动恢复异常退出留下的路由。
- `Transport/ListenerPeer`：每听众独立连接与有界队列；有意补偿与网络积压区分。积压超 60ms 丢弃旧帧，客户端独立退出。
- `Networking`：选定私网地址、同子网及 Host/Origin 检查、信令首消息认证、时限和消息上限。SMTC 元数据与受加入密钥保护的限长栅格封面接口。
- `Updates`：仅主动检查 GitHub 正式 Release，严格匹配仓库/版本/资产，流式下载、大小上限、SHA-256 校验；独立更新助手等待主程序关闭后安装。

## 同步控制

所有 RTP 帧共享 48kHz 源时间线。网页通过 WebSocket 四时间戳估计主机单调时钟偏移，结合接收器 RTP 播放时间戳、基础/输出延迟和用户硬件校准估计路径。主机从测量中减去已经施加的发送补偿，避免把补偿本身当作越来越慢的网络。

路径估计使用 9 样本中值、约 6 秒 EMA、10ms 死区和 5ms 内部量化；至少 3 个样本后参与选择。共同目标 = 最慢有效基础路径 +60ms，向上取 50ms 档位，限制 250–2000ms。上调至少观察 8 秒，下调至少观察 60 秒，并有额外上下行死区。显示目标为稳定档位，实际播放目标每秒最多变化 20ms。主机监听与每客户端发送补偿使用同一执行目标。浏览器偏差仅平滑显示，原始遥测保留。

首次补偿在网页静音测量期间直接建立，网页等待主机准备完成和短暂填充后再出声。之后发送补偿由主机时钟独立执行，网页应用计时器或回调暂停不停止发送调整。前台测量超 15 秒失效；仍连接后台客户端保留最后值，恢复前台重新校时和测量。

WebRTC 抖动缓冲提示不是强制硬件播放时刻；手机媒体输出、DAC、蓝牙和系统挂起仍可能产生不可观测偏差。软件路径估计与跨设备声学误差需分开报告，不能把 RTT/2 当作端到端音频延迟。

## 持久身份与网络边界

Windows 安装标识摘要生成新主机名，256bit 随机加入令牌独立生成。保留旧完整链接，应用数据与 HKCU 注册表双备份；拷贝已标记的其他机器身份会重新生成。普通重装稳定，重装 Windows/换用户不保证。mDNS 发布当前选定 IPv4；不支持组播的网络使用 IP 备用。

只绑定所选私网 IPv4，并过滤同子网。没有公网映射或外部音频上传。WebRTC 使用 DTLS-SRTP，HTTP 信令没有 TLS，仅用于可信局域网；路由器主动代理转发不属于同子网过滤可防止的范围。防火墙脚本仅添加专用网络 LocalSubnet 程序规则，不自动执行。

## 依赖、验证与风险

NAudio 2.2.1 / Concentus 2.2.2 / SIPSorcery 10.0.17 / WinUI 3 的版本锁定在项目与 packages.lock.json 中。自包含发布必须包括应用 PRI、App.xbf、WinUI 原生运行文件与 .NET；安装器原样附带 VB-CABLE ZIP 并提供手动安装向导。

真实 WASAPI/Opus 链路、资源释放、音量镜像、身份恢复、同步控制及更新校验均有检查。多台物理手机、信号强弱、iOS 锁屏和声学延迟仍需实际硬件验收，见 [测量方法](measurement.md) 与 [测试报告](TESTING.md)。

上游资料：[Microsoft WASAPI Loopback](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)、[NAudio](https://github.com/naudio/NAudio)、[Concentus](https://github.com/lostromb/concentus)、[SIPSorcery](https://github.com/sipsorcery-org/sipsorcery)、[WebRTC 统计](https://www.w3.org/TR/webrtc-stats/)、[GitHub Releases API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release)。
