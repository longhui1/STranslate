using RapidOcrNet;
using SkiaSharp;
using System.IO;

namespace STranslate.Plugin.Ocr.PaddleV6Arm64;

/// <summary>复用 RapidOcrNet 的完整 DB/CTC 流水线，不维护独立推理算法。</summary>
public sealed class PaddleV6Engine : IDisposable
{
    private readonly RapidOcr _ocr = new();

    public PaddleV6Engine(ModelStore models, CancellationToken token = default)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var set = RapidOcrModelSet.PPOCRv6Small with
            {
                DetModelPath = models.GetPath("det"),
                ClsModelPath = models.GetPath("cls"),
                RecModelPath = models.GetPath("rec"),
                KeysPath = models.GetPath("dict")
            };
            using var options = RapidOcr.GetDefaultSessionOptions(Math.Min(4, Environment.ProcessorCount));
            _ocr.InitModels(set, options);
            // ORT 创建 Session 没有中断接口；完成后先响应取消，再允许开始推理。
            token.ThrowIfCancellationRequested();
        }
        catch { _ocr.Dispose(); throw; }
    }

    public async Task<RapidOcrNet.OcrResult> RecognizeAsync(byte[] image, bool detect180Degrees, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var bitmap = SKBitmap.Decode(image) ?? throw new InvalidDataException("无法解码待识别图片。");
        // RapidOcrNet 会把 token 传到 ONNX Runtime 的 RunOptions.Terminate。
        return await _ocr.DetectAsync(bitmap, RapidOcrOptions.PPOCRv6 with { DoAngle = detect180Degrees }, token)
            .ConfigureAwait(false);
    }

    public void Dispose() => _ocr.Dispose();
}
