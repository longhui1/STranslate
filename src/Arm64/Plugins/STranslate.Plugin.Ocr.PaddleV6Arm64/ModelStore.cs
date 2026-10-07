using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace STranslate.Plugin.Ocr.PaddleV6Arm64;

public sealed record ModelAsset(string Role, string FileName, long Size, string Sha256, string[] Urls);
public sealed record ModelManifest(int SchemaVersion, string Family, ModelAsset[] Files)
{
    public static ModelManifest Load(string path)
    {
        var manifest = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("模型清单为空。");
        if (manifest.SchemaVersion != 1 || manifest.Family != "PP-OCRv6-small" ||
            manifest.Files is not { Length: 4 } || manifest.Files.Any(x => x is null) ||
            !manifest.Files.Select(x => x.Role).Order().SequenceEqual(new[] { "cls", "det", "dict", "rec" }))
            throw new InvalidDataException("模型清单必须包含 PP-OCRv6 Small 的 det、cls、rec 和字典。");
        if (manifest.Files.Select(x => x.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length)
            throw new InvalidDataException("模型清单的文件名不得重复。");
        foreach (var file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.FileName) || file.FileName is "." or ".." ||
                file.FileName != Path.GetFileName(file.FileName) || file.FileName.Contains('\\') ||
                file.Size <= 0 || file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit) ||
                file.Urls is not { Length: > 0 } || file.Urls.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"))
                throw new InvalidDataException("模型清单包含无效文件或下载源。");
        }
        return manifest;
    }
}

public sealed record ModelDownloadStatus(string Message, double Percentage, bool IsReady = false);
public delegate Task ModelDownload(string url, string destinationPath, IProgress<long> progress, CancellationToken cancellationToken);

/// <summary>所有服务共用已校验的模型缓存，临时下载文件不会被识别引擎读取。</summary>
public sealed class ModelStore(string directory, ModelManifest manifest, ModelDownload download)
{
    private sealed class CacheState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, (string Hash, long Length, DateTime Written)> Verified { get; } = [];
    }

    private static readonly ConcurrentDictionary<string, CacheState> Caches = new(StringComparer.OrdinalIgnoreCase);
    public string DirectoryPath { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    public ModelManifest Manifest { get; } = manifest;
    public string GetPath(string role) => Path.Combine(DirectoryPath, Manifest.Files.Single(x => x.Role == role).FileName);

    public async Task EnsureAsync(IProgress<ModelDownloadStatus>? progress, CancellationToken cancellationToken)
    {
        var state = Caches.GetOrAdd(DirectoryPath, _ => new CacheState());
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            long completed = 0;
            var total = Manifest.Files.Sum(x => x.Size);
            foreach (var file in Manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(DirectoryPath, file.FileName);
                if (!await IsValidAsync(path, file, state, cancellationToken).ConfigureAwait(false))
                {
                    state.Verified.Remove(path);
                    if (File.Exists(path)) File.Delete(path);
                    Exception? lastError = null;
                    foreach (var url in file.Urls)
                    {
                        var temporary = Path.Combine(DirectoryPath, $".{file.FileName}.{Guid.NewGuid():N}.download");
                        try
                        {
                            progress?.Report(new($"正在下载 {file.FileName}", 100.0 * completed / total));
                            var bytesProgress = new InlineProgress<long>(bytes => progress?.Report(new(
                                $"正在下载 {file.FileName}", 100.0 * (completed + Math.Clamp(bytes, 0, file.Size)) / total)));
                            await download(url, temporary, bytesProgress, cancellationToken).ConfigureAwait(false);
                            if (!await MatchesHashAsync(temporary, file, cancellationToken).ConfigureAwait(false))
                                throw new InvalidDataException($"模型 {file.FileName} 的大小或 SHA-256 校验失败，请重试下载。");
                            cancellationToken.ThrowIfCancellationRequested();
                            File.Move(temporary, path, overwrite: true);
                            RecordVerified(path, file, state);
                            lastError = null;
                            break;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception error) { lastError = error; }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                    if (lastError != null)
                        throw new IOException($"无法下载模型 {file.FileName}，请检查代理或网络后重试：{lastError.Message}", lastError);
                }
                completed += file.Size;
            }
            progress?.Report(new("模型已就绪，后续识别无需联网。", 100, true));
        }
        finally { state.Gate.Release(); }
    }

    private static async Task<bool> IsValidAsync(string path, ModelAsset asset, CacheState state, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return false;
        if (state.Verified.TryGetValue(path, out var verified) && verified.Hash == asset.Sha256 &&
            verified.Length == info.Length && verified.Written == info.LastWriteTimeUtc) return true;
        if (!await MatchesHashAsync(path, asset, token).ConfigureAwait(false)) return false;
        RecordVerified(path, asset, state);
        return true;
    }

    private static async Task<bool> MatchesHashAsync(string path, ModelAsset asset, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != asset.Size) return false;
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void RecordVerified(string path, ModelAsset asset, CacheState state)
    {
        var info = new FileInfo(path);
        state.Verified[path] = (asset.Sha256, info.Length, info.LastWriteTimeUtc);
    }
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
