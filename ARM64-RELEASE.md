# STranslate Windows ARM64

`2.0.10-arm64.3` 修复点击“检查更新”时 GitHub 匿名 API 返回 `403 rate limit exceeded` 导致检查失败的问题。ARM64 现在直接读取本仓库最新正式 Release 的公开 `releases.win-arm64.json`，并从目标版本的固定 tag 下载更新包，检查与下载均不调用 GitHub Release API，也无需个人 token。

已安装 `arm64.1` / `arm64.2` 的用户，如果旧版检查仍被 API 限额阻断，请退出程序后运行 [本版 Setup](https://github.com/longhui1/STranslate/releases/download/arm64-v2.0.10-arm64.3/STranslate-ARM64-win-arm64-Setup.exe) 覆盖安装一次。包身份、安装位置与配置路径保持一致；安装后可继续通过应用内更新升级后续 ARM64 版本。设备上仍需确认配置、历史和 MiMo 保留。

第一阶段版本基于 fork 中的 STranslate 源码，首个 ARM64 包版本为 `2.0.10-arm64.1`。这是社区维护的原生 Windows ARM64 版本；后续沿用上游三段版本并递增 ARM64 修订号，例如 `2.0.10-arm64.2`，再随上游升级到 `2.0.11-arm64.1`。这些正式 GitHub Release 使用稳定更新通道；版本后缀用于区分社区修订。

- 主程序、随包 .NET / WPF、SQLite、辅助程序及 Velopack 安装与更新组件使用 ARM64。
- 提供 Setup 安装包，安装版继续使用应用内检查、下载、退出替换和重启更新流程。
- 应用更新固定使用 `longhui1/STranslate` 的 `win-arm64` 通道；安装标识为 `STranslate-ARM64`，与官方 x64 安装区分。
- 保留内置 DeepL API 插件、现有插件市场和社区 MiMo TTS 原始安装包，不维护 MiMo ARM64 fork。
- 微信内置 OCR 排除并禁用。PaddleOCR / PP-OCRv6 不属于此阶段支持范围；可选用随包的在线 OCR 插件。

本次云端检查与 Windows ARM64 设备运行验证是不同的证据。下载后请优先验证首次安装、进程 ARM64 架构、DeepL 实际翻译、MiMo 安装和播放、截图、热键、历史记录、备份恢复及应用内 OTA。设备验证清单、构建方式和上游维护记录见 [Windows ARM64 维护文档](https://github.com/longhui1/STranslate/blob/main/src/docs/windows-arm64.md)。

安装包未进行代码签名；Windows 可能显示发布者未知。稳定版 OTA 要求本仓库 latest 正式 Release 同时包含 `releases.win-arm64.json` 与其引用的全量 `.nupkg`，请勿仅上传 Setup，或让不含 ARM64 feed 的发行版成为 latest；保留历史 tag 和更新包，以便已选中旧目标的客户端下载。
