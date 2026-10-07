# Windows ARM64 插件兼容性审计

第一阶段沿用上游插件 SDK、官方插件市场和 `.spkg` 安装流程，不引入独立 ARM64 插件市场。应用更新源与插件下载源是不同的链路；ARM64 应用更新源的隔离见 [Windows ARM64 构建与维护](windows-arm64.md)，宿主原生依赖见 [原生依赖审计](arm64-native-dependencies.md)。

## 兼容性结论

| 插件 | 第一阶段处理 | 实际依赖与限制 |
| --- | --- | --- |
| 内置 DeepL | 随 ARM64 主程序提供 | `Main.TranslateAsync()` 通过 `Context.HttpService.PostAsync()` 请求 DeepL API。项目只引用公共插件 SDK，没有 libcurl、P/Invoke 或专用 native DLL；仍需用户提供可用的 API 配置。 |
| 社区 DeepL Web Translate | 复用社区原包 | 本次审计的 `boxi-wangji/STranslate.Plugin.Translate.DeepLWeb` 同样使用宿主 HTTP 服务，无 curl 原生依赖。免费网页端点的可用性由服务端决定，需要实机网络验证。 |
| 社区 MiMo TTS | 从现有市场或原始 `.spkg` 安装，无独立 fork | AnyCPU 托管插件，调用 MiMo HTTP API，将 Base64 MP3 交给宿主音频播放器。真实 API 密钥、合成与 Windows ARM64 音频播放需要实机验证。 |
| 微信内置 OCR | ARM64 发布排除，运行时拒绝加载及重新安装 | `WeChatOcr` 的原生组件没有本阶段所需的 ARM64 支持。原生 ARM64 进程按其稳定 `PluginID` 跳过已存在插件，并记录原因；安装入口在移动文件之前明确拒绝该包。x64 宿主保持原有行为。 |
| PaddleOCR / PP-OCRv6 | `arm64.4` 起内置独立 ARM64 插件 | 官方 ARM64 ONNX Runtime + SkiaSharp，复用 RapidOcrNet 算法；Small 模型首次在线下载并校验，缓存后本地识别。两个已知 x64 社区包拒绝加载及安装，新 ID 不被其市场更新覆盖。详见 [OCR 文档](windows-arm64-paddleocr.md)。 |

## 插件系统为何可复用

- `PluginManager` 扫描预装目录和用户目录，通过 `plugin.json`、稳定 `PluginID` 和版本选择插件，不使用 x64 专属命名或架构标签。
- `PluginAssemblyLoader` 使用 `AssemblyDependencyResolver` 解析托管程序集与非托管库。它优先复用默认加载上下文中相同 `AssemblyName.FullName` 的宿主依赖，避免加载第二份 SDK 或 WPF 库。
- `PluginViewModel` 从上游 `STranslate-doc/vitepress/plugins.json` 发现社区仓库，从对应仓库的 `v{Version}` Release 下载原始 `.spkg`。MiMo 已在该索引中，无须增加 fork 条目或修改下载 URL。
- `.spkg` 的 manifest 和 DLL 仍位于包根目录；新装、重启升级、卸载标记逻辑不变。微信内置 OCR 与两个已知 x64 PaddleOCR 包增加明确拒绝规则，不改变纯 .NET 插件的默认兼容行为。

宿主使用原生 ARM64 .NET 运行时，AnyCPU 插件中的 IL 由同一运行时执行。PE 文件显示 `Intel i386` 不能单独证明它是 x86 专用插件；还必须检查 CLR 标志中的 `ILONLY`、`32BITREQUIRED` 和 `32BITPREFERRED`。带有 native 依赖的其他社区插件仍需逐个审计，不能从其托管入口 DLL 推断整个包兼容。

## MiMo 原始发布包的证据

本次审计时间：2026-10-06。

- 仓库：[Rockytkg/STranslate.Plugin.Tts.MiMo](https://github.com/Rockytkg/STranslate.Plugin.Tts.MiMo)，审计源码提交 `7c42069424e87a54f848f9e77b138750f1d582f7`。
- 包版本：`1.1.0`；[原始发布包](https://github.com/Rockytkg/STranslate.Plugin.Tts.MiMo/releases/download/v1.1.0/STranslate.Plugin.Tts.MiMo.spkg)。这是作者已发布的社区包，不是 ARM64 重新编译包。
- `PluginID`：`f508b0c220774c569db4c6133ff41cca`。
- 包 SHA-256：`fd0b7080da02709987d4ff76cad9858fb864c19fe58ba30df842aad4cb169057`。
- 包内共 14 个文件：插件 DLL、`.deps.json`、`plugin.json`、图标和 10 个语言文件，无 native DLL 或 EXE。
- DLL 的 PE Machine 为 `0x014c`，CLR `CorFlags=0x00000001`，即 `ILONLY=true`、`32BITREQUIRED=false`、`32BITPREFERRED=false`，符合 AnyCPU。
- `.deps.json` 的运行目标是 `.NETCoreApp,Version=v10.0`，未绑定 `win-x64` 或其他 RID，也没有 native 资产。
- 项目唯一直接 NuGet 依赖为 `STranslate.Plugin 1.0.8`。发布包依赖中 SDK 的 `AssemblyVersion` 为 `1.0.0.0`，与当前宿主 SDK 保持相同程序集标识；所调用的 `IAudioPlayer.PlayAsync(byte[], CancellationToken)` 在当前宿主仍然存在。
- `Main.PlayAudioAsync()` 使用宿主 `HttpService` 请求 MiMo，解析 Base64 音频后调用宿主 `AudioPlayer`；插件本身没有 P/Invoke、外部进程或平台专用路径。

以上已验证包结构、CLR 架构标志和接口引用；云端没有 Windows ARM64 WPF/音频设备，尚不能将这些检查表述为已完成真实安装、界面加载或播放验证。

社区 DeepLWeb 审计源码提交为 `bb5874318a2fd65edb56396f834647cb7d3da0c9`，manifest 版本为 `1.0.1`。本阶段不将其源码或其他社区仓库复制进主仓库，也不对其长期兼容性做超出本次审计的保证。

## 必要修改与上游同步

第一阶段插件层只有 `PluginManager.IsPluginSupported()` 中的微信内置 OCR 规则：稳定 ID `3410e7de989340938301abd6fcf8cc4b` 在 `RuntimeInformation.ProcessArchitecture == Architecture.Arm64` 时不可用。启动扫描会跳过它，本地导入和市场安装入口也会拒绝它；该判断不依赖插件目录名称或操作系统架构，所以官方 x64 进程运行在 Windows on ARM 上时仍保留上游行为。

`arm64.4` 另拒绝 PaddleOCR 官方 ID `c5914774d4854623ad11912676c7007b` 和 PaddleV6 社区 ID `26b37788a09c4999a296255eb0c95129` 的 x64 原生实现，并以独立 ID 内置 ARM64 版本；如果这些项目后来发布 ARM64 包，需要重新检查后调整规则。

同步上游时，确认微信插件 ID 是否改变、native 组件是否新增官方 ARM64 支持；若它获得完整 ARM64 支持，应同时移除发布排除与运行时拒绝规则。DeepL 和 MiMo 不需要长期维护的 ARM64 分支。`PluginPlatformCompatibilityTests` 覆盖微信在 ARM64/x64 的行为，以及 MiMo 的原始 ID 在 ARM64 上仍允许加载。

## Windows ARM64 实机验证

1. 全新安装 Setup，确认内置 DeepL 可添加服务、打开设置、使用有效 API 配置翻译，并测试取消请求。
2. 打开插件市场，安装 MiMo `1.1.0` 原始包；确认设置页加载、服务创建、保存 API 密钥后重启仍可用。也通过本地导入原始 `.spkg` 验证一次。
3. 使用 MiMo 合成短中文和英文，确认 MP3 解码、扬声器输出、停止及再次播放正常；验证错误 API 密钥时能显示错误。
4. 执行一次 ARM64 应用内 OTA 升级，确认已安装的 MiMo 和保存的设置仍可使用；插件自身升级仍走社区原始发布包。
5. 若数据目录已有微信内置 OCR，确认 ARM64 启动日志说明跳过原因、程序不加载其 DLL；重新导入微信包应显示明确不支持提示。其旧服务不会在 ARM64 上执行。
6. 如使用社区 DeepLWeb，验证实际网络环境下的免费端点；API 返回限流或禁用时区分服务端限制与插件加载失败。
