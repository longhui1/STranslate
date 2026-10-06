# Windows ARM64 构建、发布与维护

## 第一阶段范围

ARM64 版本复用上游 WPF 主程序、Rust helper、插件 SDK 和 Velopack OTA。目标是可安装、可持续升级的发行版，而不只是改变主程序的 RID。内置 DeepL API 与纯 .NET 社区 MiMo TTS 原包保持现有接口；微信内置 OCR 在发布和运行时排除；PaddleOCR / PP-OCRv6 不作为第一阶段交付条件。

源码基线是此 fork 的提交 `75f616a257bcf34d78d8929152fb579886d1d8e5`，保留 fork 已有功能。上游最近正式发行版为 `v2.0.10`。初始包版本使用 `2.0.10-arm64.1`，ARM64 修订递增后缀为 `2.0.10-arm64.2`；同步新版上游后使用例如 `2.0.11-arm64.1`。`vpk 1.2.0` 与原生更新组件要求标准三段 SemVer，不能使用四段 `packVersion`。采用这个后缀无需修改打包工具；正式 GitHub Release 的 `prerelease=false`，仍由稳定更新源读取。应用的数字程序集 / 文件版本分别对应 `2.0.10.1`、`2.0.10.2`，信息版本保留完整包版本；OTA 始终以 Velopack 安装版本比较，不能用数字程序集版本与包版本混比。

## 第一阶段下载与实机 OTA

- 直接使用：[最新 ARM64 Setup（2.0.10-arm64.3）](https://github.com/longhui1/STranslate/releases/download/arm64-v2.0.10-arm64.3/STranslate-ARM64-win-arm64-Setup.exe)。
- 完整资产、更新元数据和校验文件：[ARM64 Release](https://github.com/longhui1/STranslate/releases/tag/arm64-v2.0.10-arm64.3)。
- `arm64.1` / `arm64.2` 的更新检查使用匿名 GitHub API。实机已报告 `403 rate limit exceeded`，发生在发现新版本之前。如果旧版仍被限额阻断，先退出程序并运行本版 Setup 覆盖安装一次；`arm64.3` 起检查与下载不再调用 GitHub Release API，无需个人 token。安装身份、目录和配置路径不变，设备上需确认数据保留。
- 测试后续 OTA：保留安装 `arm64.3` 的设备，配置 DeepL / MiMo 并产生历史记录，从关于页面更新到下一次更高版本，确认退出、替换、重启和数据保留。旧版 API 恢复后也可升级到本版；不能要求仍被限流的旧检查器自行修复。

安装包未代码签名，Windows 可能提示发布者未知。运行和功能验证清单见本文末尾；云端的构建与包校验不能代替这些设备验证。

## 安装与更新身份

| 项目 | ARM64 值 | 作用 |
| --- | --- | --- |
| 主程序 RID | `win-arm64`，自包含 | 用户无需预装 .NET，随包包含 ARM64 .NET / WPF |
| Velopack package ID | `STranslate-ARM64` | 与官方 `STranslate` 的安装位置、包身份区分 |
| 更新仓库 | `https://github.com/longhui1/STranslate` | 固定使用 fork 的 ARM64 Release |
| 更新 feed | `https://github.com/longhui1/STranslate/releases/latest/download/releases.win-arm64.json` | 公开文件入口，免 GitHub API / token |
| 更新 channel | `win-arm64` | 只读取 `releases.win-arm64.json`，不回退 `releases.win.json` |
| Git tag | `arm64-v2.0.10-arm64.1` | 触发独立 ARM64 发布 workflow |
| GitHub Release | 稳定、已发布，非 draft / prerelease | 保持现有稳定更新通道的筛选行为 |

`UpdateFeedPolicy` 按**进程架构**选择源：原生 ARM64 进程固定使用上述仓库与显式 channel；官方 x64 进程即使运行在 ARM64 Windows 上，也保持上游更新规则。源不存入用户设置，迁移旧配置无法将 ARM64 改回官方 x64 更新源。已有手动更新、后台通知、下载、退出后替换与重启流程不变。ARM64 更新日志来自打包时的发行说明，避免显示另一发行版的日志。

ARM64 从 `arm64.3` 起复用 Velopack `SimpleWebSource` 读取 latest 正式 Release 的公开 feed，不通过 GitHub API 枚举发行版。一个小型子类仅将包下载固定到 `arm64-v{Version}` tag，保留默认下载器、超时、进度和取消行为，避免用户确认期间 latest 变更导致目标包丢失；x64 仍使用上游 `GithubSource`。

本仓库的 latest 正式 Release 必须含 ARM64 feed 及其引用的全量 / delta 包；缺少 feed 时检查失败，不回退到 x64。不要让仅含其他架构的 Release 成为 latest。最新全量包允许较旧 ARM64 安装直接升级；历史 tag 和更新包应保持可下载且不可变，用于已选中目标的下载、delta 基准、回溯与设备验证。channel 不是二进制架构校验：构建流程还必须检查元数据身份、原生 PE 和实际包内容。

独立安装标识允许与 x64 安装共存，但上游用户配置目录仍可能共享。第一轮安装建议先退出 x64 版本并备份配置；单实例、计划任务及旧快捷方式迁移需要在设备上验证。此阶段不自动卸载旧 x64 安装，不创建第二套插件市场。

## 可重复构建

在 Windows 上安装 PowerShell 7、Git、.NET 10 SDK、Rust，以及 Visual Studio 2022 的 C++ ARM64 构建工具和 Windows SDK。执行 `rustup target add aarch64-pc-windows-msvc`，初始化 `amd64_arm64` MSVC 开发环境，然后从仓库根目录执行：

```powershell
./build-arm64.ps1 -Version 2.0.10-arm64.1
```

脚本以失败即停止的方式完成以下工作：

1. 从原有 `Cargo.lock` 编译 `z_stranslate_host` 的 `aarch64-pc-windows-msvc` 目标，使用静态 CRT，避免安装后缺少 ARM64 VC Redist。
2. 对主程序自包含发布，所有内置插件单独发布并合并到 `Plugins`，排除微信 OCR。插件仍为 AnyCPU，由 ARM64 CLR 执行。
3. ARM64 构建禁用 Costura，保留标准 .NET 发布目录和依赖，避免嵌入器的 native / runtime 处理影响 ARM64 加载；不更改上游 x64 打包策略。
4. 使用与应用 `Velopack 1.2.0` 一致的 `vpk 1.2.0` 和 ARM64 Setup / update / stub 组件打包。
5. 检查原生 PE、AnyCPU CLR 标志、Setup、全量包、便携包以及 feed 引用文件与哈希，检查通过后才允许发布。

Velopack 1.2.0 的工具包默认 vendor helpers 不是 ARM64，仅传 `--runtime win-arm64` 不足以满足本发行版目标。`scripts/Build-VelopackArm64.ps1` 从固定上游提交 `f2edcbcafb81da5b3c884aaea330e225ad91d8b6` 构建相同版本的 ARM64 helpers，并核验实际 Git 提交；只替换本次构建私有工具目录中的 vendor helpers，不修改公共工具安装，也不维护 Velopack fork。升级 Velopack 时应同步应用 NuGet 版本、工具清单、源码提交和架构验证规则，先完整验证一轮 Setup 和 OTA。生成的 provenance JSON 记录源码身份和组件 SHA-256。

本地构建保留 `ARM64-RELEASE.md` 作为包内更新日志。应用版本只临时写入原有程序集版本文件，结束后恢复；不将应用 `Version` 属性传播给公共 SDK 或内置插件。公共 SDK 保持上游 `AssemblyVersion=1.0.0.0`，发布校验对此进行检查，以继续加载 MiMo 原始包。不要求维护者为每个上游版本手工改项目。所有外部命令必须检查退出码。

## GitHub Actions 发布

独立 workflow 位于 `.github/workflows/arm64.yml`。Windows runner 负责完整原生构建与打包；分支 / PR 校验产物以 workflow artifact 提供，`arm64-v*` tag 发布为 GitHub Release。正式发布串行执行，先检查所有已发布 ARM64 tag / feed 的版本，拒绝覆写已发布版本，再将完整资产上传至 draft，成功后正式发布。原 x64 workflow 跳过 ARM64 tag，避免同一标签混入官方架构。

构建仓库需要 Actions 已启用，并允许 workflow 的 `GITHUB_TOKEN` 写入 Contents。推送 workflow 文件的连接还需要对应的 Workflows 权限。运行成功后应同时发布：

- ARM64 Setup；
- `STranslate-ARM64` ARM64 全量 `.nupkg`；
- `releases.win-arm64.json`；
- `assets.win-arm64.json` 等 vpk 生成的配套元数据；
- 可选 delta 包、便携 ZIP、SHA-256 校验文件和架构验证报告。

以后同步上游并完成检查后，只需更新 `ARM64-RELEASE.md` 的发行说明并选择递增包版本，无需逐个修改项目。可在 GitHub Actions 的 **Windows ARM64 → Run workflow** 中选择对应源码分支，输入例如 `2.0.11-arm64.1`，勾选 `publish` 后运行；不勾选时只生成 CI artifact。也可从已提交的源码推送发布标签：

```bash
git tag -a arm64-v2.0.11-arm64.1 -m "Release native Windows ARM64 2.0.11-arm64.1"
git push origin arm64-v2.0.11-arm64.1
```

同一上游版本的修正只递增 `arm64.N`；同步新的上游三段版本后可从 `arm64.1` 开始。不要混发无后缀的 ARM64 包版本，也不要使用 `+arm64.N` 的 build metadata，它不参与 SemVer 升级排序。正式发布使用 tag 或显式勾选 `publish`，普通分支构建不会自动公开 Release。

更新不是通过下载 Setup 完成，而是通过 feed 定位全量 / delta `.nupkg`，再由已安装的 ARM64 `Update.exe` 替换程序。第一次没有旧 ARM64 包时生成全量包；后续可从相同 fork / channel 获取旧全量包作为 delta 基准。CI 使用 workflow 的短期 `GITHUB_TOKEN` 下载基准，避免 runner 共享 IP 的匿名 API 限流；本地没有 token 也可构建。下载旧包失败时仍保留全量 OTA，不能因此切到官方源，也不能清理掉新 feed 或新包。

存在 delta 时，发布前还会用同版本 `vpk delta patch` 从上一版 full 实际重建新包，并逐文件比较 ZIP 路径、长度和 SHA-256；生成的 `arm64-delta-validation.json` 记录输入包哈希和结果。重建 ZIP 的压缩字节可能与原 full 不同，因此不能比较整个 ZIP 的哈希代替内容检查。此检查在 Windows CI 中运行，涵盖 Windows MSDelta 格式；它证明增量包内容可重建，不能代替 ARM64 `Update.exe` 在设备上的退出替换和重启验证。

发布后应直接下载公开 feed，确认其中 `PackageId`、版本、包名、哈希与 Release 资产一致。最新版设备无更新可用是正常状态；必须保留一台安装旧 ARM64 版本的设备，用下一次更高版本实际验证 OTA，不能把模拟 feed 测试写成真实重启升级验证。保留历史 Setup 用于回归；后续 OTA 验证优先使用 `arm64.3` 或更高版本作为起点，添加 DeepL / MiMo 和历史记录后升级。`arm64.1` / `arm64.2` 的检查仍可能受到 GitHub API 限流，不能将该环境前提忽略。

## 必要修改清单

| 修改 | 原因与同步注意事项 |
| --- | --- |
| 主 csproj 条件 RID 和 helper 路径 | 默认 x64 保留；ARM64 构建从 Rust target 目录取 helper，不拷贝 Resources 中的 x64 EXE |
| `UpdateFeedPolicy` 和更新服务 | 按 ARM64 进程固定 fork / channel；公开 latest feed 绕开匿名 API 限额，包下载固定版本 tag；版本比较以 Velopack 安装版本为准 |
| 更新日志对话框的可选发行说明 | ARM64 展示同一包的日志；x64 继续使用原日志地址 |
| `PluginManager` 微信稳定 ID 拒绝规则 | 避免旧配置 / 本地导入重新加载已知不兼容的 native 插件 |
| 独立 ARM64 构建、验证脚本和 workflow | 与上游 x64 脚本隔离，锁定工具、源码和渠道；原 workflow 只增加 ARM64 事件隔离 |
| 更新 / 插件策略回归测试 | 检查 API 403 时 ARM64 零 API 调用、无 x64 回退、固定 tag 下载、取消 / 进度传递、版本递增与 MiMo 允许加载；x64 原更新行为保持 |

无需修改 DeepL、MiMo、ScreenGrab、NAudio 或 Rust helper 的功能源码。详细证据见 [插件审计](arm64-plugin-compatibility.md) 和 [原生依赖审计](arm64-native-dependencies.md)。同步上游后保留这几个小型补丁，重新运行 workflow；若上游调整插件 manifest / SDK、native 依赖、helper 命令或 Velopack，应重新审核对应边界。

当前 NuGet restore 对上游传递依赖 `SQLitePCLRaw.lib.e_sqlite3 2.1.11` 报告 `NU1903 / GHSA-2m69-gcr7-jv3q`。此阶段保持上游依赖以限制差异；这是现有依赖维护事项，应随上游安全更新处理，不应误写为 ARM64 专有兼容性问题。

## 已验证与待验证

完整 [Windows CI 构建](https://github.com/longhui1/STranslate/actions/runs/37455184846) 已通过：主程序 ARM64 自包含 publish、20 个内置插件、同源 Rust helper 和三个 Velopack ARM64 原生组件全部编译成功；699 个 PE 文件通过程序树架构检查，真实 Setup、更新组件、便携包、全量包和 OTA 元数据均通过完整发布校验。12 个策略测试在 Windows 上全部通过，使用真实 Velopack 库与模拟网络覆盖不读取 x64 feed、忽略 GitHub 预发布、ARM64 修订升级、上游升级及不降级，插件策略覆盖微信拒绝和 MiMo 允许加载。MiMo 作者原始包已下载并检查 AnyCPU、依赖与完整文件结构；Linux CLI 备份 / 恢复 smoke 通过。

首个 [公开 Release](https://github.com/longhui1/STranslate/releases/tag/arm64-v2.0.10-arm64.1) 已由 [正式发布 CI](https://github.com/longhui1/STranslate/actions/runs/37492362805) 成功生成并公开，包含 Setup、全量 `.nupkg`、`releases.win-arm64.json`、校验文件和 Velopack 原生组件来源记录。公开 feed 已确认包身份 `STranslate-ARM64` 与 `win-arm64` 通道一致。

第二个 [公开 Release](https://github.com/longhui1/STranslate/releases/tag/arm64-v2.0.10-arm64.2) 由 [同一发布流程](https://github.com/longhui1/STranslate/actions/runs/37493813424) 再次成功生成，包含全量包和 `475499` 字节的 delta，并保留与第一版 SHA-256 完全相同的基准 full。Windows CI 用真实 `vpk delta patch` 重建后，1135 个文件的路径、解压长度和 SHA-256 全部一致；[公开重建报告](https://github.com/longhui1/STranslate/releases/download/arm64-v2.0.10-arm64.2/arm64-delta-validation.json) 记录包哈希与验证边界。两版公开下载的 Setup / 更新包 / feed 均已独立核对大小与 SHA-1 / SHA-256，关键 native PE 确认为 ARM64。使用原始公开 GitHub API / feed 和实际第一版 full 离线回放真实 Velopack 的 3 项检查也通过：第一版选中新版及 delta、最新版无更新、缺少本地基准时可使用 full OTA。这些验证没有执行 ARM64 Update.exe 或真实设备重启。

第三个 [公开 Release（arm64.3）](https://github.com/longhui1/STranslate/releases/tag/arm64-v2.0.10-arm64.3) 的 [Windows 发布 CI](https://github.com/longhui1/STranslate/actions/runs/37499748393) 已成功完成。修复实机报告的 GitHub 匿名 API `403 rate limit exceeded`，19 个更新策略测试覆盖零 API 检查、缺 feed 不回退、固定 tag 下载与参数传递；插件策略继续检查微信拒绝和 MiMo 允许。公开下载的 Setup、full、delta、feed 和来源记录已独立校验 SHA-256，feed 包同时核对大小和 SHA-1；Setup 确认为原生 ARM64，全量包程序树的 700 个 PE 与 20 个内置插件再次通过校验，包内程序集包含新的静态 feed / 固定 tag 实现。

本版保留上一版 full，delta 为 `681090` 字节。Windows CI 的 [真实 delta 重建报告](https://github.com/longhui1/STranslate/releases/download/arm64-v2.0.10-arm64.3/arm64-delta-validation.json) 确认 1135 个文件的路径、长度和 SHA-256 一致。最新公开 feed 回放的 3 项真实 Velopack 检查通过：上一版真实 full 选中本版 delta、本版无更新、首版没有本地基准时选中本版 full；全过程零 GitHub API 调用。另用未修改的 Velopack 默认 `HttpClientFileDownloader` 实际读取公开 latest feed，从固定 `arm64-v2.0.10-arm64.3` tag 下载 delta 并核对 SHA-256，无 token、无自定义 TLS / HTTP handler。这些云端检查没有执行 ARM64 Update.exe 或实机覆盖安装、退出替换和重启。

完整 Windows 构建与发布结果以 workflow 成功产物为准，必须通过 Setup、全量包、便携包和 OTA 元数据的完整检查。云端不能运行 ARM64 WPF、系统音频、Windows 安装或真实重启 OTA；架构验证、模拟 feed 测试与设备运行验证应分别记录。

MSVC 的 ARM64 开发环境会设置 `Platform=arm64`，因此 workflow 中运行 x64 测试时显式传入 `Platform=AnyCPU`，避免 .NET SDK 将测试程序集也判定为 ARM64。主程序发布同样显式设置 AnyCPU，原生进程架构由 `win-arm64` apphost 和运行时决定。不要把未经完整架构和安装包校验的编译目录上传为正式 ARM64 发行版。

真实 Windows ARM64 设备需验证：

1. Setup 全新安装、正常启动、卸载、快捷方式和 ARM64 进程架构；无预装 .NET / VC Redist 的设备也能使用。
2. DeepL API 翻译、取消请求及设置持久化；MiMo 从市场和本地原包安装、界面加载、真实 API 合成、MP3 播放、停止与再次播放。
3. 多屏 / DPI 截图、图片翻译、剪贴板图像、二维码；在线 OCR 服务配置后能识别，微信 OCR 明确禁用。
4. 热键、Ctrl+CC、鼠标划词、前台激活、托盘、通知、历史数据库；分别从 ARM64 与模拟 x64 应用触发。
5. 普通 / 管理员 / 免 UAC 启动、计划任务、重启、备份恢复、便携配置迁移及中文 / 空格路径。
6. 旧 ARM64 版通过应用内更新到下一版，确认下载、退出、替换、重启成功，仍为 ARM64，配置、历史和 MiMo 保留。
7. 无更新、网络中断、取消下载和错误 feed 的用户体验；官方 x64 Release 发布时 ARM64 不误提示或下载 x64 包。

请记录 Windows 版本、设备型号、安装包 SHA-256、版本号和日志。设备验证未通过前，不将架构检查表述为全部功能已验证。
