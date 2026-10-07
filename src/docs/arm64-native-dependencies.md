# Windows ARM64 原生依赖审计

本说明记录第一阶段的非插件依赖、原生辅助程序及实机验证边界。发布流程见 [Windows ARM64 维护与发布](windows-arm64.md)，插件说明见 [ARM64 插件兼容性](arm64-plugin-compatibility.md)。插件适配不要求修改这些通用依赖；微信 OCR 和 PaddleOCR / PP-OCRv6 不作为第一阶段交付条件。`arm64.4` 增加原生 ARM64 ONNX Runtime / SkiaSharp 和 app-local CRT，供内置 V6 OCR 使用；模型按用户要求在线下载，详见 [OCR 维护文档](windows-arm64-paddleocr.md)。

## 架构判断方法

原生 EXE / DLL 必须具有 ARM64 PE machine (`0xAA64`)。托管 AnyCPU DLL 通常具有 I386 PE machine (`0x014C`)，但 CLR header 的 `ILONLY` 标志为真，且 `32BITREQUIRED` 为假；这类文件会由 ARM64 CLR 原生执行，不能仅凭 I386 machine 判定为不兼容。依赖审计同时检查 NuGet 内容、CLR header、运行时 RID 资产及应用实际使用的 API。

构建使用 `win-arm64` 自包含发布，.NET、WPF、Windows Forms 和 SQLite 原生资产随 RID 选择 ARM64 版本。发布目录中的 native 文件与安装器、Velopack 更新组件也必须接受架构校验，避免混入仓库现有的 x64 helper。

## 依赖结论

| 组件 | 当前依赖与证据 | ARM64 处理与验证边界 |
| --- | --- | --- |
| `z_stranslate_host.exe` | 源码在 `src/STranslate.Host`，仓库 `Resources` 中的现有 EXE 是 x64；Rust 无 x86 / x64 专属实现 | 直接从同一份源码构建 `aarch64-pc-windows-msvc`，发布时使用 ARM64 EXE |
| SQLite / 历史记录 | `Microsoft.Data.Sqlite 10.0.9` 的 `SQLitePCLRaw.lib.e_sqlite3 2.1.11` 提供 `runtimes/win-arm64/native/e_sqlite3.dll`，PE 已确认 ARM64 | 保留上游依赖，检查发布输出为 ARM64；实机验证历史写入、检索及升级后的数据库保留 |
| 截图与多屏 | `ScreenGrab 1.0.17` 和 `WpfScreenHelper 2.1.1` 只有 AnyCPU 托管 DLL，CLR flags 为 `0x1`，无独立 native 库；依赖 Windows 屏幕 / GDI API 和 `System.Drawing.Common` | 保留上游实现；实机验证多显示器、不同 DPI、框选截图、剪贴板图像及图片翻译 |
| 语音播放 | `NAudio 2.3.0` 各模块为 AnyCPU 托管 DLL，CLR flags 为 `0x9`，无随包 native 库；应用用 `WaveOutEvent`、`Mp3FileReader`、`WaveFileReader`、`RawSourceWaveStream` | WAV / PCM 读取保持原实现，输出使用 Windows WinMM；MP3 解码依赖系统 ACM。实机验证 MP3、WAV、裸 PCM、停止 / 暂停 / 恢复；此结论不等同于已验证设备驱动和所有系统音频编解码器 |
| 热键与鼠标钩子 | `ChefKeys 0.1.2`、`MouseKeyHook 5.7.1`、`NHotkey 4.0.0` 和 `H.InputSimulator 1.5.0` 为 AnyCPU 托管 DLL；应用的低级鼠标钩子使用 CsWin32 生成的句柄 / 结构，窗口指针适配使用 `nint` 与 `sizeof(nint)` | 保留 Win32 实现；实机分别验证原生 ARM64 与模拟 x64 应用中的热键、Ctrl+CC、鼠标划词和前台激活 |
| 托盘与通知 | `Hardcodet.NotifyIcon.Wpf 2.0.1`、`Microsoft.Toolkit.Uwp.Notifications 7.1.3` 为 AnyCPU 托管 DLL，CLR flags 为 `0x9`；调用系统托盘、WinRT / COM 通知 API | 保留上游实现；验证托盘菜单、通知展示和点击回调，尤其验证安装 / OTA 后的可执行文件路径变化 |
| Win32 调用 | 本仓库显式 `DllImport` 指向 `user32.dll`、`shcore.dll` 等系统 DLL；CsWin32 负责其他系统 API 声明，未发现应用自行加载其他 x64 native DLL | 使用 ARM64 Windows 自带系统组件；保留调用代码并执行真机功能检查 |
| 内置 PaddleOCR V6 (ARM64) | RapidOcrNet 4.2.0 + 官方 ONNX Runtime 1.29.0 / SkiaSharp 3.119.1；模型首次在线下载 | RID 选择原生 ARM64，附官方 app-local ARM64 CRT；包内不预装模型。`arm64.4` 正式构建的真实 Windows ARM64 runner 已从完整包加载 native 库并识别中英文，验证范围见下文 |
| Velopack | 独立更新器 / 安装器是发布工具生成的 native 组件，不属于主程序托管 DLL | 必须用 ARM64 打包目标和独立 ARM64 更新频道；架构及 OTA 验证由发布流程负责 |

`ScreenGrab` 的 NuGet 元数据指向上游 [ZGGSONG/ScreenGrab](https://github.com/ZGGSONG/ScreenGrab)，1.0.17 对应提交 `9acf5b25278c9f27c6372d0477de63b077d6e416`。保持此依赖可直接跟随官方版本，不需要额外 ARM64 fork。

## 原生 helper 的长期构建

`z_stranslate_host` 保留上游的命令协议与全部功能：

- `start`：普通启动、UAC 提升启动、计划任务启动、等待旧 PID 退出及超时处理。
- `task`：管理免 UAC 启动的 Windows 计划任务。
- `backup`：备份 / 恢复配置及文件，随后重启程序。
- `portable`：切换便携模式、迁移数据并重启。
- `update`：保留上游旧 ZIP 更新命令；当前安装版 OTA 使用 Velopack。

ARM64 不改写 helper，也不更改应用到 helper 的参数协议。先初始化 Visual Studio 的 `amd64_arm64` 开发环境，然后执行：

```powershell
rustup target add aarch64-pc-windows-msvc
$env:CARGO_TARGET_AARCH64_PC_WINDOWS_MSVC_RUSTFLAGS = '-C target-feature=+crt-static'
cargo build --manifest-path src/STranslate.Host/Cargo.toml --release --locked --target aarch64-pc-windows-msvc
```

默认产物位于 `src/STranslate.Host/target/aarch64-pc-windows-msvc/release/z_stranslate_host.exe`。如设置 `CARGO_TARGET_DIR`，需要让发布流程显式使用相应路径，不能退回 `Resources` 中的 x64 二进制。

`Cargo.lock` 已锁定的 `winapi 0.3.9` 明确支持 `aarch64-pc-windows-msvc`，其他 Windows Rust 绑定也提供 ARM64 目标。`zip 0.6.6` 默认启用 bzip2 / zstd，相关 C 源码由 `cc-rs` 构建，所以除了 Rust target，还必须具备 ARM64 MSVC 编译器、库工具及 Windows SDK；仅运行 `rustup target add` 不足以构建。保留这些特性以维持上游归档格式兼容性。

上游仓库现有的 x64 helper 导入 `VCRUNTIME140.dll`。ARM64 构建使用 `+crt-static` 静态链接 MSVC 运行库，`cc-rs` 也会按同一目标特性选择 `/MT` 编译 ZIP 的 C 依赖，从而避免要求用户额外安装 ARM64 Visual C++ Redistributable。发布时检查 helper 的导入表不包含 `VCRUNTIME` / `MSVCP` 动态运行库；Windows 系统 DLL 依赖仍由系统提供。

## 已完成检查与实机清单

本阶段在 Linux 云端检查了上述 NuGet 内容、PE / CLR 架构标志、Rust 的 ARM64 目标依赖和架构条件。`cargo check --locked --target aarch64-pc-windows-msvc -p winapi -p clap -p chrono` 已通过，验证 helper 的 Windows API / 命令行 / 时间依赖可编译为该目标。另使用锁文件编译 Linux helper，并通过真实 CLI 验证 ZIP 备份 / 恢复完整保留 Unicode 文本、嵌套目录、二进制内容，以及不存在的目录返回失败。

完整 [Windows CI 构建](https://github.com/longhui1/STranslate/actions/runs/37455184846) 已成功从同一份源码编译 ARM64 helper 与 Velopack Setup / update / stub，并生成实际安装与更新包。架构校验确认这些原生组件为 ARM64，helper 和安装 / 更新组件不依赖外部 `VCRUNTIME` / `MSVCP`；主程序、随包运行时、SQLite 及全部随包 PE 通过检查。Linux 本身不能执行 Windows ARM64 程序，Windows x64 runner 的交叉构建也不能替代 Windows ARM64 设备运行验证。

### `arm64.4` 正式构建与原生运行证据

2026-10-07 的 [正式 CI](https://github.com/longhui1/STranslate/actions/runs/37570069601) 使用源码提交 [`dde1fd1cca9ee119e0fbd60edb0535f4fb479d22`](https://github.com/longhui1/STranslate/commit/dde1fd1cca9ee119e0fbd60edb0535f4fb479d22)。Windows 构建 job `112626469287`、真实 Windows ARM64 验证 job `112628772795` 与 publish job 均成功；[`2.0.10-arm64.4` Release](https://github.com/longhui1/STranslate/releases/tag/arm64-v2.0.10-arm64.4) 于 `2026-10-07T04:21:24Z` 公开发布。

- 发布目录共检查 717 个 PE、21 个内置插件；完整更新包检查 718 个 PE。所有非纯 IL 原生文件均为严格的 ARM64 `0xAA64`，没有将 ARM64EC / ARM64X 或 x64 文件作为合格 ARM64 native 文件。
- 为 ONNX Runtime 新附的 app-local CRT 是 `msvcp140.dll`、`msvcp140_1.dll`、`vcruntime140.dll`，三者 `FileVersion` 均为 `14.44.35211.0`。`vcruntime140_cor3.dll` 是原自包含 .NET 运行时已有组件，不计为本次新增 CRT；Rust helper 仍使用静态 CRT。
- ARM64 runner 的 OS / 进程架构均为 `Arm64`，系统版本 `10.0.26200`，运行时 `.NET 10.0.12`。实际从解压后的完整发布包路径加载 ONNX Runtime、SkiaSharp 及上述 CRT，共记录 6 个 native 模块的路径、SHA-256 与 `0xAA64` machine，避免 runner 预装运行库掩盖随包依赖缺失。
- 使用发布包内真实插件及宿主 SDK 加载器，在线下载并校验四个模型文件，成功识别“中文翻译测试”和“Windows ARM64 OCR”，返回原图坐标四点；下载失败 / 取消 / 重试、损坏缓存修复、缓存后的离线识别、预取消、ONNX Runtime 运行中 terminate 取消及后续请求恢复、双实例并发、释放后拒绝新请求全部通过。

公开发行产物包含 [原生依赖来源报告 `arm64-ocr-provenance.json`](https://github.com/longhui1/STranslate/releases/download/arm64-v2.0.10-arm64.4/arm64-ocr-provenance.json) 与 [真实 ARM64 运行报告 `arm64-ocr-validation.json`](https://github.com/longhui1/STranslate/releases/download/arm64-v2.0.10-arm64.4/arm64-ocr-validation.json)。运行报告绑定以上源码提交、CI run 和完整包 SHA-256 `1702b09bbb9177ad6fb5f57dde5b8e3ebaf4eab097dec68e99fa6feb112e2070`，只验证该完整包中的插件及 native OCR，不涵盖 Setup 交互安装或 WPF 桌面流程。

真实 Windows ARM64 设备应验证：

1. 安装器安装、启动及进程架构，确认主程序和 helper 都为 ARM64。
2. 普通 / 管理员 / 免 UAC 启动、应用重启和单实例激活。
3. 配置备份 / 恢复和便携模式数据迁移，含中文目录及含空格路径。
4. 历史数据库、托盘、通知、热键、鼠标划词、屏幕截图及多屏 DPI。
5. DeepL 实际翻译，安装社区 MiMo TTS 插件并播放它返回的音频。
6. 从较早的 ARM64 安装版本检查更新、下载、退出替换并重启，确认配置、历史和社区插件保留，且始终使用 ARM64 包。

真实 ARM64 runner 已执行内置 OCR，并不意味着以上桌面、设备驱动、真实 API 或 `Update.exe` 退出替换与重启流程已经验证；这些项目仍须在用户设备上检查。
