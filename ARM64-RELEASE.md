# STranslate Windows ARM64

`2.0.10-arm64.4` 更新 About 页面并新增内置 **PaddleOCR V6 (ARM64)**。

- About 的项目主页、问题反馈、发行版和维护者入口指向 `longhui1/STranslate`，移除捐赠入口，保留上游版权与致谢。
- 新增 PP-OCRv6 Small 本地 OCR，使用官方原生 Windows ARM64 ONNX Runtime / SkiaSharp 和现有插件 SDK。模型不预装，首次识别或在插件设置中点击下载时在线获取，校验完整性后缓存；后续识别在本地完成。
- 下载模型支持进度、取消和重试，复用应用网络设置。请在 OCR 服务中选择 **PaddleOCR V6 (ARM64)**。已知仅含 x64 原生库的社区 PaddleOCR 包在 ARM64 上拒绝加载及安装，避免误覆盖或执行；旧社区插件服务不自动迁移为新插件。
- 沿用 `arm64.3` 的公开 ARM64 feed 和固定版本包下载，应用内 OTA 不调用 GitHub Release API，无需个人 token。已安装 `arm64.3` 的用户可直接检查更新到本版。

[本版 Setup](https://github.com/longhui1/STranslate/releases/download/arm64-v2.0.10-arm64.4/STranslate-ARM64-win-arm64-Setup.exe)。`arm64.1` / `arm64.2` 如果仍被旧匿名 API 限额阻断，请退出旧版后运行本版 Setup 覆盖安装一次；包身份和配置路径保持一致。

第一阶段版本基于 fork 中的 STranslate 源码，首个 ARM64 包版本为 `2.0.10-arm64.1`。这是社区维护的原生 Windows ARM64 版本；后续沿用上游三段版本并递增 ARM64 修订号，例如 `2.0.10-arm64.2`，再随上游升级到 `2.0.11-arm64.1`。这些正式 GitHub Release 使用稳定更新通道；版本后缀用于区分社区修订。

- 主程序、随包 .NET / WPF、SQLite、辅助程序及 Velopack 安装与更新组件使用 ARM64。
- 提供 Setup 安装包，安装版继续使用应用内检查、下载、退出替换和重启更新流程。
- 应用更新固定使用 `longhui1/STranslate` 的 `win-arm64` 通道；安装标识为 `STranslate-ARM64`，与官方 x64 安装区分。
- 保留内置 DeepL API 插件、现有插件市场和社区 MiMo TTS 原始安装包，不维护 MiMo ARM64 fork。
- 微信内置 OCR 排除并禁用。内置 PaddleOCR V6 (ARM64) 使用在线下载的 Small 模型；不包含 GPU / NPU 加速或 Tiny / Medium 选择。

本次云端检查与 Windows ARM64 设备运行验证是不同的证据。下载后请优先验证首次安装、进程 ARM64 架构、DeepL 实际翻译、MiMo 安装和播放、截图、热键、历史记录、备份恢复及应用内 OTA。PaddleOCR 使用与维护见 [PaddleOCR ARM64 文档](https://github.com/longhui1/STranslate/blob/main/src/docs/windows-arm64-paddleocr.md)。设备验证清单、构建方式和上游维护记录见 [Windows ARM64 维护文档](https://github.com/longhui1/STranslate/blob/main/src/docs/windows-arm64.md)。

安装包未进行代码签名；Windows 可能显示发布者未知。稳定版 OTA 要求本仓库 latest 正式 Release 同时包含 `releases.win-arm64.json` 与其引用的全量 `.nupkg`，请勿仅上传 Setup，或让不含 ARM64 feed 的发行版成为 latest；保留历史 tag 和更新包，以便已选中旧目标的客户端下载。
