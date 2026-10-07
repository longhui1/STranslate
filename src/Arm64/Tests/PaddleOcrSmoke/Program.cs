using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Encodings.Web;
using STranslate.Core;
using STranslate.Plugin;

namespace PaddleOcrSmoke;

internal static class Program
{
    private const string PluginId = "c67c0e3de45b48f6a852ffa8f0aae2f2";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [STAThread]
    private static async Task<int> Main(string[] arguments)
    {
        var options = ParseArguments(arguments);
        var reportPath = options["--report"];
        var report = new ValidationReport
        {
            Version = options["--version"],
            FullPackageSHA256 = options["--package-sha256"],
            SourceCommit = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local",
            WorkflowRunId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "local",
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            OSDescription = RuntimeInformation.OSDescription,
            RuntimeDescription = RuntimeInformation.FrameworkDescription,
            Scope = "真实 Windows ARM64 发布插件、在线模型缓存及 native OCR；不验证桌面交互或 Setup 安装。"
        };
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        IOcrPlugin? plugin = null;
        SmokeHttpAdapter? adapter = null;
        try
        {
            Require(OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64 &&
                RuntimeInformation.OSArchitecture == Architecture.Arm64, "必须在原生 Windows ARM64 进程上执行，禁止 x64 模拟或替身测试。");
            var appDirectory = Path.GetFullPath(options["--app"]);
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                var candidate = Path.Combine(appDirectory, name.Name + ".dll");
                return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
            };
            var sdk = typeof(IOcrPlugin).Assembly;
            var packagedSdkPath = Path.Combine(appDirectory, "STranslate.Plugin.dll");
            Require(sdk.FullName == AssemblyName.GetAssemblyName(packagedSdkPath).FullName &&
                Hash(sdk.Location) == Hash(packagedSdkPath), "测试 SDK 必须与实际发布的 SDK 身份和字节相同。");
            report.SdkIdentity = sdk.FullName!;
            report.SdkSHA256 = Hash(packagedSdkPath);
            var pluginDirectory = Directory.EnumerateFiles(Path.Combine(appDirectory, "Plugins"), "plugin.json", SearchOption.AllDirectories)
                .Select(path => (Path: path, Meta: JsonSerializer.Deserialize<PluginMetaData>(File.ReadAllText(path), JsonOptions)!))
                .Single(item => item.Meta.PluginID == PluginId).Path;
            pluginDirectory = Path.GetDirectoryName(pluginDirectory)!;
            Require(!Directory.EnumerateFiles(pluginDirectory, "*.onnx", SearchOption.AllDirectories).Any(), "模型不能预装在发布插件中。");
            var manifestPath = Path.Combine(pluginDirectory, "model-manifest.json");
            var manifest = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(manifestPath), JsonOptions)!;
            Require(manifest.Family == "PP-OCRv6-small" && manifest.Files.Length == 4, "必须使用完整 PP-OCRv6 Small 模型清单。");
            Require(manifest.Files.All(file => !Directory.EnumerateFiles(pluginDirectory, file.FileName,
                SearchOption.AllDirectories).Any()), "模型和字典必须在线下载，不能预装在发布插件中。");
            report.ModelManifestSHA256 = Hash(manifestPath);
            var native = new NativeRuntime(appDirectory, pluginDirectory);
            var metadata = LoadMetadata(pluginDirectory, options["--cache"]);
            var loader = new PluginAssemblyLoader(metadata.ExecuteFilePath);
            var assembly = loader.LoadAssemblyAndDependencies();
            var pluginType = loader.FromAssemblyGetTypeOfInterface(assembly, typeof(IOcrPlugin));
            Require(pluginType != null, "宿主 PluginAssemblyLoader 没有找到共享 SDK 的 IOcrPlugin。");
            plugin = (IOcrPlugin)Activator.CreateInstance(pluginType!)!;
            report.PluginId = metadata.PluginID;
            report.PluginAssemblySHA256 = Hash(metadata.ExecuteFilePath);
            var (http, activeAdapter) = SmokeHttpAdapter.Create();
            adapter = activeAdapter;
            var logger = new SmokeLogger();
            var context = new SmokeContext(metadata, http, logger);
            plugin.Init(context);
            Require(adapter.Downloads.Count == 0, "插件 Init 不得触发模型下载。");
            Require(plugin.SupportBoxPoints(), "图片翻译需要真实原图坐标。");
            var fixturesDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            var fixtures = JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(Path.Combine(fixturesDirectory, "fixtures.json")), JsonOptions)!;
            foreach (var fixture in fixtures)
                Require(Hash(Path.Combine(fixturesDirectory, fixture.FileName)) == fixture.SHA256, "测试样图字节不匹配。");

            // 走插件下载按钮的生产 EnsureModelsAsync；只在 HTTP 边界注入失败。
            adapter.RejectAllDownloads = true;
            await ExpectFailure(() => EnsureModelsAsync(plugin, default));
            Require(adapter.Downloads.Count > 0 && adapter.Downloads.All(item => item.InjectedFailure), "首次失败应来自受控 HTTP 503，而非模型或原生初始化错误。");
            RequireNoPartialFiles(metadata.PluginCacheDirectoryPath);
            report.Checks["ModelDownloadFailure"] = "PASS（HTTP 边界注入 503，生产模型管理器返回可重试失败）";
            adapter.RejectAllDownloads = false;
            using (var cancellation = new CancellationTokenSource())
            {
                adapter.CancelAfterFirstChunk = cancellation;
                await ExpectCancelled(() => EnsureModelsAsync(plugin, cancellation.Token));
            }
            Require(adapter.Downloads.Any(item => item.Cancelled && item.Bytes > 0), "取消必须发生在真实模型响应体已下载后。");
            RequireNoPartialFiles(metadata.PluginCacheDirectoryPath);
            report.Checks["ModelDownloadCancellation"] = "PASS（真实 HTTP 传输中取消且临时文件清理）";
            await EnsureModelsAsync(plugin, default);
            report.ModelInputs = VerifyModelFiles(metadata, manifest);
            report.Checks["OnlineModelsAndRetry"] = "PASS（生产插件在线下载成功，四个文件的大小和 SHA256 匹配清单）";

            // 已校验缓存损坏后，生产管理器必须重新下载，不能继续使用坏字典。
            var dictionary = manifest.Files.Single(file => file.Role == "dict");
            var dictionaryPath = Path.Combine(metadata.PluginCacheDirectoryPath, "Models", dictionary.FileName);
            var corrupt = File.ReadAllBytes(dictionaryPath);
            corrupt[0] ^= 1;
            File.WriteAllBytes(dictionaryPath, corrupt);
            File.SetLastWriteTimeUtc(dictionaryPath, DateTime.UtcNow.AddSeconds(-5));
            var downloadsBeforeRepair = adapter.Downloads.Count;
            await EnsureModelsAsync(plugin, default);
            Require(adapter.Downloads.Count > downloadsBeforeRepair, "缓存 SHA256 错误应触发重新下载。");
            report.ModelInputs = VerifyModelFiles(metadata, manifest);
            report.Checks["CorruptedCacheRecovery"] = "PASS（损坏字典检测后重新下载并通过哈希校验）";
            adapter.DenyNetwork = true;
            var requestCount = adapter.Downloads.Count;
            foreach (var fixture in fixtures)
            {
                var result = await plugin.RecognizeAsync(Request(fixturesDirectory, fixture));
                report.Recognitions.Add(VerifyRecognition(fixture, result));
            }
            Require(adapter.Downloads.Count == requestCount, "有效缓存后识别不得重复下载模型。");
            report.Checks["ChineseEnglishAndOriginalCoordinates"] = "PASS";
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var startCount = logger.InferenceStartCount;
                await CaptureCancelled(() => plugin.RecognizeAsync(Request(fixturesDirectory, fixtures[0]), cancelled.Token), requireTaskCancellation: true);
                Require(logger.InferenceStartCount == startCount, "预取消请求不得进入 native 推理。");
            }
            report.Checks["PreCancellation"] = "PASS";

            // 模型已预热；取消时间有限探测，必须观察到 ORT RunOptions.Terminate 的原生异常证据。
            foreach (var delay in new[] { 1, 5, 20, 75, 150, 300, 600, 1200 })
            {
                using var cancellation = new CancellationTokenSource();
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                logger.OnInferenceStarted = () => started.TrySetResult();
                var stress = new OcrRequest(File.ReadAllBytes(Path.Combine(fixturesDirectory, "native-cancel.png")), LangEnum.Auto, 1280, 2048);
                var running = plugin.RecognizeAsync(stress, cancellation.Token);
                await WaitForStarted(started.Task, running);
                var queued = plugin.RecognizeAsync(Request(fixturesDirectory, fixtures[1]));
                await Task.Delay(delay);
                if (running.IsCompleted)
                {
                    Require((await running).IsSuccess, "取消探测之前的识别已失败。");
                    VerifyRecognition(fixtures[1], await queued);
                    logger.OnInferenceStarted = null;
                    report.CancellationAttempts.Add(new(delay, 0, "CompletedBeforeCancellation", null, null));
                    continue;
                }
                var cancellationTime = Stopwatch.StartNew();
                cancellation.Cancel();
                var exception = await CaptureCancelled(() => running, requireTaskCancellation: true);
                cancellationTime.Stop();
                VerifyRecognition(fixtures[1], await queued);
                logger.OnInferenceStarted = null;
                // 宿主约定要求插件把 OperationCanceledException 包装为 TaskCanceledException。
                // ORT 的原生异常可能位于更深一层，必须检查异常链而非只读直接 InnerException。
                var nativeException = FindNativeCancellation(exception);
                var evidence = new NativeCancellationObservation(delay, cancellationTime.Elapsed.TotalMilliseconds,
                    exception.GetType().FullName!, nativeException?.GetType().FullName, nativeException?.Message);
                report.CancellationAttempts.Add(evidence);
                if (nativeException?.GetType().FullName == "Microsoft.ML.OnnxRuntime.OnnxRuntimeException" &&
                    nativeException.Message.Contains("terminate", StringComparison.OrdinalIgnoreCase))
                    break;
            }
            Require(report.CancellationAttempts.Any(attempt => attempt.NativeExceptionType == "Microsoft.ML.OnnxRuntime.OnnxRuntimeException" &&
                attempt.NativeMessage!.Contains("terminate", StringComparison.OrdinalIgnoreCase)),
                "未观察到 ONNX Runtime 原生 Run 的终止异常，不能把预处理或阶段间取消当作 native 取消。");
            report.Checks["InFlightNativeCancellationAndQueuedRecovery"] = "PASS（捕获 ONNX Runtime terminate 原生异常，并验证下一请求）";
            using (var other = (IOcrPlugin)Activator.CreateInstance(pluginType!)!)
            {
                other.Init(new SmokeContext(metadata.Clone(), http, logger));
                var concurrent = await Task.WhenAll(plugin.RecognizeAsync(Request(fixturesDirectory, fixtures[0])),
                    other.RecognizeAsync(Request(fixturesDirectory, fixtures[1])));
                VerifyRecognition(fixtures[0], concurrent[0]);
                VerifyRecognition(fixtures[1], concurrent[1]);
            }
            report.Checks["ConcurrentPluginInstances"] = "PASS（共享同一真实缓存，独立 native engine 同时识别）";
            Require(adapter.Downloads.Count == requestCount, "并发识别不得重复下载已校验模型。");
            report.NativeModules = native.VerifyLoaded(pluginDirectory);
            report.HttpDownloads = adapter.Downloads;
            report.FixtureInputs = fixtures;
            report.NativeCancellationFixtureSHA256 = Hash(Path.Combine(fixturesDirectory, "native-cancel.png"));
            plugin.Dispose();
            await ExpectDisposed(() => plugin.RecognizeAsync(Request(fixturesDirectory, fixtures[0])));
            plugin = null;
            report.Checks["DisposedInstanceRejectsWork"] = "PASS";
            report.Success = true;
            return 0;
        }
        catch (Exception error)
        {
            report.Error = error.ToString();
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            plugin?.Dispose();
            if (adapter != null) report.HttpDownloads = adapter.Downloads;
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions));
            Console.WriteLine($"ARM64 OCR 验证报告：{reportPath}");
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] arguments)
    {
        if (arguments.Length % 2 != 0) throw new ArgumentException("参数必须成对。");
        var result = new Dictionary<string, string>();
        for (var index = 0; index < arguments.Length; index += 2) result.Add(arguments[index], arguments[index + 1]);
        foreach (var required in new[] { "--app", "--cache", "--report", "--version", "--package-sha256" })
            if (!result.ContainsKey(required)) throw new ArgumentException($"缺少参数：{required}");
        return result;
    }

    private static PluginMetaData LoadMetadata(string directory, string cacheDirectory)
    {
        var metadata = JsonSerializer.Deserialize<PluginMetaData>(File.ReadAllText(Path.Combine(directory, "plugin.json")), JsonOptions)!;
        foreach (var (name, value) in new[]
                 {
                     (nameof(PluginMetaData.PluginDirectory), directory),
                     (nameof(PluginMetaData.AssemblyName), Path.GetFileNameWithoutExtension(metadata.ExecuteFileName)),
                     (nameof(PluginMetaData.PluginCacheDirectoryPath), Path.GetFullPath(cacheDirectory)),
                     (nameof(PluginMetaData.PluginSettingsDirectoryPath), Path.Combine(cacheDirectory, "Settings"))
                 })
            typeof(PluginMetaData).GetProperty(name)!.SetValue(metadata, value);
        return metadata;
    }

    private static Task EnsureModelsAsync(IOcrPlugin plugin, CancellationToken token) =>
        (Task)plugin.GetType().GetMethod("EnsureModelsAsync")!.Invoke(plugin, [null, token])!;

    private static OcrRequest Request(string directory, Fixture fixture) =>
        new(File.ReadAllBytes(Path.Combine(directory, fixture.FileName)), LangEnum.Auto, fixture.Width, fixture.Height);

    private static RecognitionObservation VerifyRecognition(Fixture fixture, OcrResult result)
    {
        Require(result.IsSuccess, $"{fixture.FileName} 识别失败：{result.ErrorMessage}");
        static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        Require(Normalize(result.Text).Contains(Normalize(fixture.Text), StringComparison.Ordinal),
            $"{fixture.FileName} 实际文字“{result.Text}”没有包含预期“{fixture.Text}”。");
        Require(result.OcrContents.Count > 0, "没有返回原图文本框。");
        foreach (var content in result.OcrContents)
        {
            Require(content.BoxPoints.Count == 4, "每个文本框必须有四个点。");
            foreach (var point in content.BoxPoints)
                Require(float.IsFinite(point.X) && float.IsFinite(point.Y) && point.X >= 0 && point.X <= fixture.Width &&
                    point.Y >= 0 && point.Y <= fixture.Height, "文本框不是有限的原图像素坐标。");
            Require(content.BoxPoints.Max(point => point.X) > content.BoxPoints.Min(point => point.X) &&
                content.BoxPoints.Max(point => point.Y) > content.BoxPoints.Min(point => point.Y), "文本框退化为零面积。");
        }
        var points = result.OcrContents.SelectMany(content => content.BoxPoints).ToArray();
        var box = new[] { points.Min(point => point.X), points.Min(point => point.Y), points.Max(point => point.X), points.Max(point => point.Y) };
        var intersection = Math.Max(0, Math.Min(box[2], fixture.Bounds[2]) - Math.Max(box[0], fixture.Bounds[0])) *
            Math.Max(0, Math.Min(box[3], fixture.Bounds[3]) - Math.Max(box[1], fixture.Bounds[1]));
        var expectedArea = (fixture.Bounds[2] - fixture.Bounds[0]) * (fixture.Bounds[3] - fixture.Bounds[1]);
        Require(intersection / expectedArea > 0.7, "文本框未覆盖样图原始文字区域，可能返回了缩放后的坐标。");
        return new(fixture.FileName, result.Text, result.Duration.TotalMilliseconds,
            result.OcrContents.Select(content => new { content.Text, Points = content.BoxPoints.Select(point => new { point.X, point.Y }).ToArray() }).ToArray());
    }

    private static List<ModelObservation> VerifyModelFiles(PluginMetaData metadata, ModelManifest manifest) =>
        manifest.Files.Select(file =>
        {
            var path = Path.Combine(metadata.PluginCacheDirectoryPath, "Models", file.FileName);
            Require(File.Exists(path) && new FileInfo(path).Length == file.Size && Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase),
                $"实际模型不符合公开清单：{file.FileName}");
            return new ModelObservation(file.Role, file.FileName, file.Size, Hash(path), file.Urls);
        }).ToList();

    private static void RequireNoPartialFiles(string directory) =>
        Require(!Directory.Exists(directory) || !Directory.EnumerateFiles(directory, "*.download", SearchOption.AllDirectories).Any(), "取消或失败后遗留未完成模型。");

    private static async Task WaitForStarted(Task started, Task running)
    {
        var complete = await Task.WhenAny(started, running, Task.Delay(TimeSpan.FromSeconds(10)));
        Require(complete == started, "未观察到预热后生产 native 推理开始。");
    }

    private static async Task ExpectFailure(Func<Task> action)
    {
        try { await action(); } catch (IOException) { return; }
        throw new InvalidOperationException("预期模型下载失败未出现。");
    }

    private static async Task ExpectCancelled(Func<Task> action)
    {
        _ = await CaptureCancelled(action);
    }

    private static async Task<OperationCanceledException> CaptureCancelled(Func<Task> action, bool requireTaskCancellation = false)
    {
        try { await action(); }
        catch (OperationCanceledException error)
        {
            Require(!requireTaskCancellation || error is TaskCanceledException,
                "识别取消必须遵循宿主 TaskCanceledException 约定，避免被 UI 当作识别失败。");
            return error;
        }
        throw new InvalidOperationException("预期取消未出现。");
    }

    private static Exception? FindNativeCancellation(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current.GetType().FullName == "Microsoft.ML.OnnxRuntime.OnnxRuntimeException" &&
                current.Message.Contains("terminate", StringComparison.OrdinalIgnoreCase))
                return current;
        return null;
    }

    private static async Task ExpectDisposed(Func<Task> action)
    {
        try { await action(); } catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("已释放插件继续接受工作。");
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}

internal sealed class ValidationReport
{
    public bool Success { get; set; }
    public string Version { get; set; } = "";
    public string FullPackageSHA256 { get; set; } = "";
    public string SourceCommit { get; set; } = "";
    public string WorkflowRunId { get; set; } = "";
    public string ProcessArchitecture { get; set; } = "";
    public string OSArchitecture { get; set; } = "";
    public string OSDescription { get; set; } = "";
    public string RuntimeDescription { get; set; } = "";
    public string Scope { get; set; } = "";
    public string PluginId { get; set; } = "";
    public string PluginAssemblySHA256 { get; set; } = "";
    public string SdkIdentity { get; set; } = "";
    public string SdkSHA256 { get; set; } = "";
    public string ModelManifestSHA256 { get; set; } = "";
    public string NativeCancellationFixtureSHA256 { get; set; } = "";
    public Dictionary<string, string> Checks { get; set; } = [];
    public List<ModelObservation> ModelInputs { get; set; } = [];
    public List<NativeModuleObservation> NativeModules { get; set; } = [];
    public List<DownloadObservation> HttpDownloads { get; set; } = [];
    public List<RecognitionObservation> Recognitions { get; set; } = [];
    public List<NativeCancellationObservation> CancellationAttempts { get; set; } = [];
    public Fixture[] FixtureInputs { get; set; } = [];
    public string? Error { get; set; }
}
internal sealed record ModelManifest(string Family, ModelAsset[] Files);
internal sealed record ModelAsset(string Role, string FileName, long Size, string Sha256, string[] Urls);
internal sealed record ModelObservation(string Role, string FileName, long Size, string SHA256, string[] Sources);
internal sealed record Fixture(string FileName, string Text, int Width, int Height, int[] Bounds, string SHA256);
internal sealed record RecognitionObservation(string FileName, string Text, double Milliseconds, object[] Contents);
internal sealed record NativeCancellationObservation(int DelayMilliseconds, double ResponseMilliseconds,
    string ExceptionType, string? NativeExceptionType, string? NativeMessage);
