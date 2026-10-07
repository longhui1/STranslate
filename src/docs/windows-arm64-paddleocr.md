# Windows ARM64 PaddleOCR V6

## 使用方式

`2.0.10-arm64.4` 起，ARM64 安装包内置 **PaddleOCR V6 (ARM64)**。在 OCR 服务中添加这个插件即可；截图、普通 OCR 和图片翻译复用宿主既有接口，返回原图像素坐标的四顶点文本框。

模型不随 Setup、Portable 或更新包预装。首次识别前会在线获取 PP-OCRv6 Small 检测、识别、文本行方向模型及字符字典，也可先在插件设置中下载。下载复用宿主 HTTP 服务与网络设置，显示进度，支持取消和失败重试。模型放在该插件的用户缓存目录中，校验成功后保留；后续识别在本地执行，不上传图片。

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
| VC++ 运行库 | 从 Visual Studio 官方 ARM64 Redist 文件夹取得所需 app-local DLL，随包提供并记录哈希，用户无需额外安装 |
| 模型 | PP-OCRv6 Small det / rec 与兼容的 PP-LCNet 文本行方向分类器；字符字典必须与 rec 模型匹配 |

每个模型下载后同时检查固定大小与 SHA-256，临时文件校验成功后才替换缓存正式文件。使用固定模型清单，避免可变 URL 后续换内容而静默加载不同模型。模型与推理库的许可证、来源和具体哈希以源码清单及发布 provenance 为准。

NuGet 的 ONNX Runtime build props 会按 AnyCPU 默认选择 x64 Content，因此仅对 OCR 项目排除该包的 build / buildTransitive 资产，并排除 RapidOcrNet 的默认 v5 模型复制资产，由 .NET 的 RID 解析选择 `win-arm64`；完整 PE 校验仍是发布前置条件。托管 SDK 继续维持 `AssemblyVersion=1.0.0.0`，避免影响 MiMo 等已有插件。

## 构建与后续维护

项目位于 `src/Arm64/Plugins/STranslate.Plugin.Ocr.PaddleV6Arm64`，由 ARM64 脚本显式发布，不进入上游 x64 solution 和原内置插件遍历目录。新增 native 准备脚本处理运行库和来源记录，不下载或打包模型；RapidOcrNet NuGet 自带的默认 v5 模型也从发布目录清除。

发布前检查 native 架构、完整依赖、SDK 身份、插件 ID、模型清单和包内无模型文件。Windows ARM64 runner 从实际完整包加载内置插件，在线获取模型并执行中文 / 英文 OCR、文本坐标、取消和缓存行为验证。只有构建与此运行验证均通过，才上传完整 Release 并公开；模型缓存及 CI 临时目录不会进入发布包。

升级库或模型时，应保持插件 ID，不用社区 x64 ID；更新固定版本、下载清单与许可归属，然后完整运行 CI。若两个旧插件后来提供原生 ARM64 版本，可重新审核后调整宿主拒绝规则。不要绕过架构或运行检查，仅用托管 DLL 的 AnyCPU 标志宣称 native 已适配。

## 设备验收

1. 从 `arm64.3` 应用内更新，确认 About 项目和问题链接指向本仓库，且没有捐赠入口。
2. 添加内置 **PaddleOCR V6 (ARM64)** 服务，首次下载查看进度；中途取消，再次下载能补齐模型；错误网络设置时能显示原因。
3. 下载后断网，使用中文、英文、混排截图识别；图片翻译中文字框与原图对齐，多屏 DPI 不导致坐标偏移。
4. 使用较大截图，测试取消、再次识别和多个 OCR 服务；重启后使用已缓存模型。
5. 后续 OTA 保留设置与模型缓存，且不提示安装社区 x64 PaddleOCR 更新。DeepL、MiMo 和历史记录继续正常工作。

CI 的 ARM64 OCR 运行证据与这些桌面交互验证分别记录；不能用云端 smoke 声称所有设备和界面流程都已通过。
