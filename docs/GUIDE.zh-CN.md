# 宿舍之音使用与开发指南

[English](GUIDE.en.md) · [首页](../README.md)

## 安装与首次使用

支持 Windows 10 22H2 / Windows 11 x64。下载 Release 中 `DormCast-0.5.2-Setup-x64.exe` 安装到当前用户。安装包含 .NET、WinUI、Opus 所需本地运行文件，无需另外安装运行时。便携 ZIP 也可使用，需要完整解压目录，不能只复制 EXE。安装包尚未购买代码签名证书。

同步主机声音需要虚拟声卡隔离原始音频与延迟后的扬声器声音。安装向导有专用准备页面；完成页可打开 `Drivers` 文件夹。首次打开软件若没有检测到虚拟声卡，也会显示步骤：

1. 解压 `Drivers/VBCABLE_Driver_Pack46.zip`。
2. 右键 `VBCABLE_Setup_x64.exe`，以管理员身份运行，点击 Install Driver。
3. 按官方提示重启电脑。宿舍之音首次识别到驱动后会选择 CABLE Input 采集、默认实际扬声器播放；可在“同步设置”修改。

官方原包完整保留。依据其许可，本安装包不自动执行或改造驱动安装流程；已安装 VB-CABLE 可跳过此步骤。驱动与 DormCast 分开卸载，升级应用不需要重复安装驱动。

## 广播、加入和音量

选择局域网网卡、端口和 10/20ms 音频帧，点击“开始广播”。广播时临时把 Windows Console / Multimedia 默认输出切到虚拟声卡，捕获共享模式 WASAPI Loopback，延迟后送到设置的实际扬声器。正常停止或下次启动故障恢复时，只有默认设备仍属于本程序的路由才会恢复。软件明确指定输出设备的播放器需自行选择系统默认或 CABLE Input。

其他设备接入同一局域网，扫描二维码或访问完整链接；自动播放被阻止时点“开始收听”。网页只控制本地播放/暂停，不控制主机的切歌。点击窗口关闭按钮隐藏到托盘，托盘左键恢复、右键停止或退出。退出会释放采集、连接和端口。

**系统与广播音量**：在虚拟声卡主机同步模式下，Windows 默认输出音量、实际扬声器音量和软件音量条双向联动。发送端按系统音量百分比调整音频幅度，避免将某款电脑扬声器的 dB 曲线用于手机后导致中低档音量过小；100% 保持原始幅度，静音为零。手机网页在后台也跟随，不依赖网页控制消息。主机使用另一条未衰减的本地编码，再由实际扬声器系统音量处理一次，避免重复降低音量。静音也联动。网页没有独立音量条；手机硬件音量仍然有效，不同设备的实际响度不会因此完全相等。网络设备听到音量变化会带有当前播放延迟。非虚拟声卡模式保留正常回环捕获行为。

## 固定链接与网络

链接包含每台电脑的稳定 `.local` 主机名与随机 256bit 加入密钥。新身份的主机名源自 Windows 安装标识的单向摘要，不采集手机 MAC；MAC 随机化和浏览器权限也使其不适合生成网页设备身份。已有链接会迁移保留，身份同时保存在 `%LOCALAPPDATA%/DormCast/join-identity.json` 与当前用户 `HKCU/Software/DrivingGodJ/DormCast/Identity`。重装应用不清理这两处，单独清理应用数据也可恢复；重装 Windows、换用户或删除两份记录不能保证相同完整链接。

`.local` 依赖局域网 mDNS；网卡、IP 变化后重新广播会重新发布地址。某些 Android、企业 Wi-Fi 或禁用组播的网络无法解析时使用“IP 备用”链接。端口保持相同才能保持完整固定网址。请只把含密钥的完整链接分享给可信听众。

默认绑定所选私网 IPv4，HTTP/WebSocket 只接受同子网来源，WebRTC 使用该网卡并禁用外部 STUN/TURN。不创建公网映射。音频使用 DTLS-SRTP；HTTP 页面/信令没有 TLS，适合可信局域网。

页面打不开时，先确认同一 Wi-Fi、Windows 网络为专用网络、防火墙放行该 EXE 的服务 TCP 端口及 WebRTC UDP；管理员可运行附带 `firewall.ps1`，它仅为专用网络 LocalSubnet 添加程序规则。页面打开但无声时检查 UDP、浏览器播放许可和路由器 AP/客户端隔离。程序不会自动改变防火墙设置。

## 同步与后台播放

共同目标最低 **250ms**、最高 2000ms。对有效基础路径作 9 样本中值、约 6 秒滤波和 10ms 死区；最慢路径加 60ms 余量后按 50ms 档位选择目标。上调需持续观察 8 秒，下调需稳定 60 秒；播放目标以最多每秒 20ms 过渡，初次加入先完成测量再出声。暂停、离线或高时钟不确定度不用于抬高目标，仍连接的后台设备保留最后有效基础路径。

展开“设备与同步详情”查看每台的路径、当前/计划发送补偿、正在执行的共同目标、推算偏差、浏览器播放时间线、RTT、抖动、缓冲、丢包、输出延迟报告、硬件校准、校时 RTT/不确定度及发送/丢弃帧。首页显示稳定的目标档位，详情的执行目标在切换时平滑变化。网页偏差采用平滑显示，原始测量仍在遥测中；这些都不是实测声学延迟。

手机默认使用后台媒体输出与 MediaSession。主机负责发送补偿，所以网页定时器停止后仍可追踪共同目标；浏览器原生抖动缓冲仍可能变化，硬件与蓝牙延迟可能不报告。恢复前台会重新校时和测量。iOS/Android 可以冻结网页或暂停 Web Audio，无法承诺锁屏或后台长期播放；被暂停后回到页面点“继续收听”，必要时关闭省电。iOS 添加到主屏幕会使用内置图标，页面不额外强调此功能。后台媒体或蓝牙偏差可在网页详情中保存本机额外硬件校准。

## 检查更新

在主机窗口点击“检查更新”，匿名读取 `DrivingGodJ/DormCast` 的最新正式 GitHub Release，按版本号比较。只在用户主动检查/下载时访问互联网，音频始终走局域网。新版本会说明当前广播将停止；选择“下载并更新”后下载匹配的 x64 安装包，校验 Release 提供的大小和 SHA-256。校验失败或取消会移除未完成文件。Windows 更新助手等待本程序完全退出后再次校验并打开安装向导，保留当前用户设置与身份。没有可用校验信息的旧 Release 请手动下载。公司策略禁止更新助手或 GitHub 不可达时可从官方 Release 页面手动安装。

## 开发和发布

`dotnet run --project tests/DormCast.Checks -c Release -- --volume-gain-check` 只读采集当前实际音频，比较网络与主机本地编码后的幅度比，不改音量或默认设备。没有音频时可加 `--stimulus`，会真实播放约 4 秒低幅测试音，再经 WASAPI 回采验证。`--feature-checks` 可单独运行同步、身份、音量边界与更新检查，不播放测试音。

GitHub 匿名 API 被限流或暂时不可达时，软件会自动读取最新正式 Release 中的 `update.json`。该文件采用相同的版本、固定仓库下载地址、大小及 SHA-256 校验字段，用户无需配置 GitHub 令牌。构建脚本会在安装包生成后自动生成它，每次发布必须一并上传。

源码结构：`src/DormCast/Audio` 采集/编码/主机播放/音量，`Synchronization` 时钟与延迟估计，`Transport` WebRTC 调度，`Networking` HTTP/信令/mDNS/身份，`Updates` GitHub 更新，`wwwroot` 浏览器，`src/DormCast.Desktop` WinUI/托盘，`scripts` 构建/安装，`tests` 核心与浏览器数学检查。

Windows 需要 .NET SDK 10.0.403（见 global.json）、Windows SDK 与 Inno Setup 7。不使用 .tools 时将 dotnet/ISCC 加入 PATH。打包前把官方原包放到 `.tools/drivers/VBCABLE_Driver_Pack46.zip`，构建脚本不替用户自动下载驱动。

```powershell
./scripts/build.ps1
```

构建会执行真实系统音频回环检查，请在允许短暂测试音的环境运行。独立复核可用 `dotnet run --project tests/DormCast.Checks -c Release` 与 `node tests/sync-math-checks.cjs`。已配置 VB-CABLE 的开发电脑可额外运行 `dotnet run --project tests/DormCast.Checks -c Release -- --volume-link-check`，会暂时改变并恢复端点音量/静音。安装测试使用单独 AppId，避免覆盖开发者已有安装。

发布前更新两份项目版本、桌面版本/标题和安装器版本，构建、验证后提交并打标签，在同一仓库 Release 上传安装包、便携包、源码和 SHA256SUMS.txt。检查更新要求资产名 `DormCast-X.Y.Z-Setup-x64.exe`、标签 `vX.Y.Z`、正式公开 Release 和 GitHub SHA-256 digest。详见 [测试范围](TESTING.md) 与 [第三方许可](../THIRD-PARTY-NOTICES.md)。
