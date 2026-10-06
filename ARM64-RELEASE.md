# STranslate Windows ARM64

`2.0.10-arm64.2` 保持首版应用功能，加入发布前的真实 delta 重建校验，并验证同一流水线可重复发布。需要测试 OTA 时，请先安装 [首版 Setup](https://github.com/longhui1/STranslate/releases/download/arm64-v2.0.10-arm64.1/STranslate-ARM64-win-arm64-Setup.exe)，安装 MiMo 并保存设置，再从应用的关于页面检查更新到本修订；无需再次运行新 Setup。

第一阶段版本基于 fork 中的 STranslate 源码，首个 ARM64 包版本为 `2.0.10-arm64.1`。这是社区维护的原生 Windows ARM64 版本；后续沿用上游三段版本并递增 ARM64 修订号，例如 `2.0.10-arm64.2`，再随上游升级到 `2.0.11-arm64.1`。这些正式 GitHub Release 使用稳定更新通道；版本后缀用于区分社区修订。

- 主程序、随包 .NET / WPF、SQLite、辅助程序及 Velopack 安装与更新组件使用 ARM64。
- 提供 Setup 安装包，安装版继续使用应用内检查、下载、退出替换和重启更新流程。
- 应用更新固定使用 `longhui1/STranslate` 的 `win-arm64` 通道；安装标识为 `STranslate-ARM64`，与官方 x64 安装区分。
- 保留内置 DeepL API 插件、现有插件市场和社区 MiMo TTS 原始安装包，不维护 MiMo ARM64 fork。
- 微信内置 OCR 排除并禁用。PaddleOCR / PP-OCRv6 不属于此阶段支持范围；可选用随包的在线 OCR 插件。

本次云端检查与 Windows ARM64 设备运行验证是不同的证据。下载后请优先验证首次安装、进程 ARM64 架构、DeepL 实际翻译、MiMo 安装和播放、截图、热键、历史记录、备份恢复及应用内 OTA。设备验证清单、构建方式和上游维护记录见 [Windows ARM64 维护文档](https://github.com/longhui1/STranslate/blob/main/src/docs/windows-arm64.md)。

安装包未进行代码签名；Windows 可能显示发布者未知。稳定版 OTA 需要 Release 同时包含 `releases.win-arm64.json` 与其引用的全量 `.nupkg`，请勿仅上传 Setup 或删除更新包。
