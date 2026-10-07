using Microsoft.Extensions.Logging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;

namespace STranslate.Plugin.Ocr.PaddleV6Arm64;

public sealed class Main : IOcrPlugin
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private IPluginContext _context = null!;
    private Settings _settings = null!;
    private ModelStore _models = null!;
    private PaddleV6Engine? _engine;
    private SettingsView? _view;
    private volatile bool _disposed;

    public IEnumerable<LangEnum> SupportedLanguages =>
        [LangEnum.Auto, LangEnum.ChineseSimplified, LangEnum.ChineseTraditional, LangEnum.English, LangEnum.Korean, LangEnum.Japanese];
    public bool SupportBoxPoints() => true;

    public void Init(IPluginContext context)
    {
        _context = context;
        _settings = context.LoadSettingStorage<Settings>();
        var manifest = ModelManifest.Load(Path.Combine(context.MetaData.PluginDirectory, "model-manifest.json"));
        _models = new ModelStore(Path.Combine(context.MetaData.PluginCacheDirectoryPath, "Models"), manifest,
            async (url, destination, progress, token) =>
            {
                await context.HttpService.DownloadFileAsync(url, Path.GetDirectoryName(destination)!, Path.GetFileName(destination),
                    new Options { Timeout = TimeSpan.FromMinutes(10) },
                    new InlineProgress<DownloadProgress>(value => progress.Report(value.DownloadedBytes)), token).ConfigureAwait(false);
            });
    }

    public Control GetSettingUI() => _view ??= new SettingsView(this, _context, _settings, _models.DirectoryPath);

    public async Task EnsureModelsAsync(IProgress<ModelDownloadStatus>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token, timeout.Token);
        try { await _models.EnsureAsync(progress, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("模型下载超过 10 分钟，请检查网络或代理后重试。");
        }
    }

    public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            return new OcrResult().Fail("PaddleOCR V6 (ARM64) 需要原生 Windows ARM64 进程。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var enteredGate = false;
        try
        {
            await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
            enteredGate = true;
            await EnsureModelsAsync(cancellationToken: linked.Token).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var inference = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, timeout.Token);
            try
            {
                _engine ??= await Task.Run(() => new PaddleV6Engine(_models, inference.Token), inference.Token).ConfigureAwait(false);
                _context.Logger.LogDebug("PaddleOCR V6 ARM64 开始本地识别");
                var output = await _engine.RecognizeAsync(request.ImageData, _settings.Detect180Degrees, inference.Token).ConfigureAwait(false);
                var result = new OcrResult { Duration = TimeSpan.FromMilliseconds(output.DetectTime) };
                foreach (var block in output.TextBlocks.Where(x => !string.IsNullOrWhiteSpace(x.Text)))
                    result.OcrContents.Add(new OcrContent
                    {
                        Text = block.Text,
                        BoxPoints = block.BoxPoints.Select(point => new BoxPoint(point.X, point.Y)).ToList()
                    });
                return result.OcrContents.Count == 0 ? result.Fail("未识别到文字。") : result;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !linked.IsCancellationRequested)
            {
                return new OcrResult().Fail("识别超过 60 秒，请缩小截图范围后重试。");
            }
        }
        catch (OperationCanceledException error)
        {
            _context.Logger.LogDebug("PaddleOCR V6 ARM64 识别已取消");
            // 宿主 OCR 链路以 TaskCanceledException 识别正常取消，不将其显示为识别失败。
            throw new TaskCanceledException(error.Message, error,
                error.CancellationToken.CanBeCanceled ? error.CancellationToken : linked.Token);
        }
        catch (Exception error)
        {
            _context.Logger.LogError(error, "PaddleOCR V6 ARM64 识别失败");
            return new OcrResult().Fail(error.Message);
        }
        finally { if (enteredGate) _operationGate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _view?.CancelDownload();
        // 取消运行中的 native 推理，再等它离开临界区，避免释放正在使用的 Session。
        _ = DisposeEngineAsync();
    }

    private async Task DisposeEngineAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try { _engine?.Dispose(); _engine = null; }
        finally { _operationGate.Release(); }
    }
}
