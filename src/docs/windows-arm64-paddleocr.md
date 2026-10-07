# Windows ARM64 PaddleOCR V6

## 使用方式

`2.0.10-arm64.4` 起，ARM64 安装包内置 **PaddleOCR V6 (ARM64)**。在 OCR 服务中添加这个插件即可；截图、普通 OCR 和图片翻译复用宿主既有接口，返回原图像素坐标的四顶点文本框。

模型不随 Setup、Portable 或更新包预装。首次识别前会在线获取 PP-OCRv6 Small 检测、识别、文本行方向模型及字符字典，也可先在插件设置中下载。下载复用宿主 HTTP 服务与网络设置，显示进度，支持取消和失败重试。模型放在该插件的用户缓存目录中，校验成功后保留；后续识别在本地执行，不上传图片。

四个文件合计 `32,257,432` 字节，约 31 MiB。下载源和后备源固定在 `model-manifest.json`；权重源不可用时自动尝试后备源，仍须匹配同一大小和 SHA-256。发布 CI 的 ModelScope 权重请求曾返回 HTTP 403，而固定 GitHub commit 后备源成功，这不代表用户设备上的 ModelScope 一定不可访问。

下载和识别采用分别的超时。模型下载允许较长时间，避免将首次下载误判为识别超时；模型下载完成后再计识别准备与推理超时。首次创建 ONNX Session 的原生初始化没有取消 API；此时取消会在初始化返回后生效，不能宣称硬中断。Session 就绪后的实际推理使用 ONNX Runtime `RunOptions.Terminate` 中止，后续可再次识别。取消或失败时删除本次临时文件，已完整下载并通过校验的文件继续保留，下次只补齐缺失文件。缓存坏文件需要重新下载；更换模型版本时同步更新模型清单中的来源和 SHA-256。

本版使用 Small 模型的 CPU 推理，支持中文、英文及模型字典中的多语言。识别结果受字号、图片清晰度和模型训练数据影响。模型下载依赖网络；下载完成后的本地识别可离线使用。系统截图、热键及图片翻译的完整交互仍需在设备上验证。

## 原社区插件与身份

社区 [PaddleV6](https://github.com/boxi-wangji/STranslate.Plugin.Ocr.PaddleV6) 源提交 `e6ea750fedb707a926362dad54b45ea682352cb1`、官方 [PaddleOCR 插件](https://github.com/STranslate/STranslate.Plugin.Ocr.Paddle) 源提交 `047c7925eab6510595e7535a832e32495287349a` 的 `1.0.7` 均使用 PP-OCRv6，但依赖 x64 OpenVINO / OpenCV 运行库。只编译它们的托管入口不能让这些 native 库在 ARM64 进程中加载。

新插件采用独立稳定 ID `c67c0e3de45b48f6a852ffa8f0aae2f2`，随主程序 OTA 更新，不绑定社区 x64 插件 Release。两个旧社区 ID 在 ARM64 宿主中拒绝加载与安装，并引导使用内置插件：

| 旧插件 | ID |
| --- | --- |
| 官方 PaddleOCR | `c5914774d4854623ad11912676c7007b` |
| 社区 PaddleV6 | `26b37788a09c4999a296255eb0c95129` |

原 x64 宿主行为保留。旧社区服务不自动迁移为新插件；更新后重新添加内置 ARM64 OCR 服务。此处不修改 MiMo、DeepL 或插件市场的下载源。

## 推理与原生依赖

采用 NuGet `RapidOcrNet 4.2.0` 的完整 PP-OCRv6 ONNX 处理流程，复用检测预处理、文本框后处理、方向分类和 CTC 解码。主仓库只维护薄 SDK 适配层，不复制或长期维护这套 OCR 算法。必须明确选择 `RapidOcrModelSet.PPOCRv6Small` 与 `RapidOcrOptions.PPOCRv6`；默认 v5 的预处理不适用于 v6。

| 组件 | 固定版本与处理 |
| --- | --- |
| RapidOcrNet | `4.2.0`，Apache-2.0，NuGet 包中的许可和上游归属随插件提供 |
| ONNX Runtime | `Microsoft.ML.OnnxRuntime 1.29.0`，官方 `win-arm64` native 资产，CPU 推理 |
| 图像处理 | `SkiaSharp 3.119.1` 与官方 Win32 native 包中的 `win-arm64/libSkiaSharp.dll` |
| VC++ 运行库 | 从 Visual Studio 官方 ARM64 Redist 文件夹按导入表递归取得实际所需的 app-local DLL，随包提供并记录哈希，用户无需额外安装 |
| 模型 | PP-OCRv6 Small det / rec 与兼容的 PP-LCNet 文本行方向分类器；字符字典必须与 rec 模型匹配 |

每个模型下载后同时检查固定大小与 SHA-256，临时文件校验成功后才替换缓存正式文件。使用固定模型清单，避免可变 URL 后续换内容而静默加载不同模型。模型与推理库的许可证、来源和具体哈希以源码清单及发布 provenance 为准。

Visual Studio ARM64 Redist 目录可能附带其他兼容组件，因此只从 OCR native DLL 的导入表出发，递归复制其 CRT 依赖闭包；不复制整个 Redist 目录。所有选中的 native DLL 仍须严格通过 ARM64 PE 校验，并在 ARM64 runner 上证明实际从发布包加载，避免无关兼容组件污染程序目录。

NuGet 的 ONNX Runtime build props 会按 AnyCPU 默认选择 x64 Content，因此仅对 OCR 项目排除该包的 build / buildTransitive 资产，并排除 RapidOcrNet 的默认 v5 模型复制资产，由 .NET 的 RID 解析选择 `win-arm64`；完整 PE 校验仍是发布前置条件。托管 SDK 继续维持 `AssemblyVersion=1.0.0.0`，避免影响 MiMo 等已有插件。

## 构建与后续维护

项目位于 `src/Arm64/Plugins/STranslate.Plugin.Ocr.PaddleV6Arm64`，由 ARM64 脚本显式发布，不进入上游 x64 solution 和原内置插件遍历目录。新增 native 准备脚本处理运行库和来源记录，不下载或打包模型；RapidOcrNet NuGet 自带的默认 v5 模型也从发布目录清除。

发布前检查 native 架构、完整依赖、SDK 身份、插件 ID、模型清单和包内无模型文件。Windows ARM64 runner 从实际完整包加载内置插件，在线获取模型并执行中文 / 英文 OCR、文本坐标、取消和缓存行为验证。只有构建与此运行验证均通过，才上传完整 Release 并公开；模型缓存及 CI 临时目录不会进入发布包。

升级库或模型时，应保持插件 ID，不用社区 x64 ID；更新固定版本、下载清单与许可归属，然后完整运行 CI。若两个旧插件后来提供原生 ARM64 版本，可重新审核后调整宿主拒绝规则。不要绕过架构或运行检查，仅用托管 DLL 的 AnyCPU 标志宣称 native 已适配。

## arm64.4 的实际验证记录

2026-10-07 的 [正式 CI run 37570069601](https://github.com/longhui1/STranslate/actions/runs/37570069601) 使用源码 [`dde1fd1cca9ee119e0fbd60edb0535f4fb479d22`](https://github.com/longhui1/STranslate/commit/dde1fd1cca9ee119e0fbd60edb0535f4fb479d22)，构建、真实 ARM64 运行与发布三个 job 均成功，[arm64.4 Release](https://github.com/longhui1/STranslate/releases/tag/arm64-v2.0.10-arm64.4) 已于 `2026-10-07 04:21:24 UTC` 公开。Windows 构建 job 的 20 项模型缓存测试和 30 项更新 / 插件策略测试全部通过；真实 `windows-11-arm` job 从该次完整 `.nupkg` 解包、复用宿主 `PluginAssemblyLoader` 和 SDK，然后执行生产插件。临时模型在用户缓存路径下载，检查结束后删除，没有重新打进安装或更新包。

[ARM64 OCR 运行报告](https://github.com/longhui1/STranslate/releases/download/arm64-v2.0.10-arm64.4/arm64-ocr-validation.json) 的 `Success=true`、操作系统与进程架构均为 `Arm64`，并记录源码提交、CI run ID 和全量包 SHA-256，避免将其他构建的结果当成本次验证。9 项检查全部通过：

| 检查 | 实际验证范围 |
| --- | --- |
| `ModelDownloadFailure` | 仅在 HTTP 边界注入 503，生产缓存管理器返回可重试错误并清理临时文件 |
| `ModelDownloadCancellation` | 真实在线响应已写入部分模型后取消，临时文件清理成功 |
| `OnlineModelsAndRetry` | 取消后重试成功，三个模型及字符字典的大小与 SHA-256 完全匹配固定清单 |
| `CorruptedCacheRecovery` | 人为损坏同长度字典，检测哈希错误后重新在线下载并校验 |
| `ChineseEnglishAndOriginalCoordinates` | 禁止 HTTP 后真实识别“中文翻译测试”和“Windows ARM64 OCR”，每个文本框有四个原图像素点并覆盖文字区域 |
| `PreCancellation` | 已取消请求不进入 native 推理，以宿主要求的 `TaskCanceledException` 返回 |
| `InFlightNativeCancellationAndQueuedRecovery` | 捕获 ORT 原生 `Exiting due to terminate flag being set to true`，下一排队请求仍能成功识别 |
| `ConcurrentPluginInstances` | 两个生产插件实例共享有效缓存，独立引擎同时识别且没有重复 HTTP 下载 |
| `DisposedInstanceRejectsWork` | 释放后的插件拒绝继续接受识别请求 |

报告中的真实 HTTP 记录显示：ModelScope 的 det / cls / rec 权重请求返回 403，三个固定 GitHub commit 后备地址返回 200；ModelScope 字典返回 200。不能把注入的 503 故障测试写成下载源本身失败，也不能据此承诺所有地区的源可用性。该检查使用真实生产模型管理器和在线网络适配器；宿主代理设置的界面操作仍留给设备验收。

[原生依赖来源报告](https://github.com/longhui1/STranslate/releases/download/arm64-v2.0.10-arm64.4/arm64-ocr-provenance.json) 记录官方 ORT / SkiaSharp、许可和模型清单，新增 app-local `msvcp140.dll`、`msvcp140_1.dll`、`vcruntime140.dll` 的文件版本均为 `14.44.35211.0`。ARM64 运行报告进一步核对实际加载路径与字节哈希：ORT、SkiaSharp 和 CRT 均从完整包路径加载，PE machine 严格为 `0xAA64`，没有借用 runner 预装的 CRT 掩盖缺件。报告同时包含 .NET 原有的 `vcruntime140_cor3.dll`，它不是本次新增 CRT 闭包的一项。

同次 Windows 构建还从 `arm64.3` full 与 `arm64.4` delta 真实重建新包，[公开 delta 报告](https://github.com/longhui1/STranslate/releases/download/arm64-v2.0.10-arm64.4/arm64-delta-validation.json) 确认 1164 个文件的路径、解压长度与 SHA-256 一致。以上证据没有执行交互 Setup、WPF 截图 / 图片翻译窗口或真实应用退出后的 OTA 替换与重启，也没有验证 DeepL API 与 MiMo 的安装、合成及音频播放。

## 设备验收

1. 从 `arm64.3` 应用内更新，确认 About 项目和问题链接指向本仓库，且没有捐赠入口。
2. 添加内置 **PaddleOCR V6 (ARM64)** 服务，首次下载查看进度；中途取消，再次下载能补齐模型；错误网络设置时能显示原因。
3. 下载后断网，使用中文、英文、混排截图识别；图片翻译中文字框与原图对齐，多屏 DPI 不导致坐标偏移。
4. 使用较大截图，测试取消、再次识别和多个 OCR 服务；重启后使用已缓存模型。
5. 后续 OTA 保留设置与模型缓存，且不提示安装社区 x64 PaddleOCR 更新。DeepL、MiMo 和历史记录继续正常工作。

CI 的 ARM64 OCR 运行证据与这些桌面交互验证分别记录；不能用云端 smoke 声称所有设备和界面流程都已通过。
