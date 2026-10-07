using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using STranslate.Plugin.Ocr.PaddleV6Arm64;

namespace STranslate.Arm64.Tests;

public sealed class ModelStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "STranslate-ModelStoreTests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> _contents = new()
    {
        ["det"] = Encoding.UTF8.GetBytes("检测模型"),
        ["cls"] = Encoding.UTF8.GetBytes("方向分类模型"),
        ["rec"] = Encoding.UTF8.GetBytes("文字识别模型"),
        ["dict"] = Encoding.UTF8.GetBytes("文\n字\n词\n典\n")
    };

    private ModelManifest CreateManifest() => new(1, "PP-OCRv6-small", _contents.Select(pair =>
        new ModelAsset(pair.Key, pair.Key == "dict" ? "dictionary.txt" : $"{pair.Key}.onnx", pair.Value.Length,
            Convert.ToHexString(SHA256.HashData(pair.Value)),
            [$"https://primary.invalid/{pair.Key}", $"https://fallback.invalid/{pair.Key}"])).ToArray());

    private byte[] GetContents(string url) => _contents[new Uri(url).AbsolutePath.TrimStart('/')];

    [Fact]
    public async Task ConcurrentServicesShareDownloadsAndWarmCacheNeedsNoNetwork()
    {
        var calls = 0;
        var active = 0;
        var concurrencyLevels = new ConcurrentBag<int>();
        ModelDownload download = async (url, destination, progress, token) =>
        {
            Interlocked.Increment(ref calls);
            concurrencyLevels.Add(Interlocked.Increment(ref active));
            try
            {
                await Task.Delay(20, token);
                var contents = GetContents(url);
                await File.WriteAllBytesAsync(destination, contents, token);
                progress.Report(contents.Length);
            }
            finally { Interlocked.Decrement(ref active); }
        };
        var first = new ModelStore(_directory, CreateManifest(), download);
        var second = new ModelStore(Path.Combine(_directory, "."), CreateManifest(), download);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Task.WhenAll(first.EnsureAsync(null, timeout.Token), second.EnsureAsync(null, timeout.Token));

        Assert.Equal(4, calls);
        Assert.Equal(1, concurrencyLevels.Max());
        AssertModelsReady(first);
        var warmCache = new ModelStore(_directory, CreateManifest(), (_, _, _, _) =>
            throw new InvalidOperationException("有效缓存不应发起下载。"));
        await warmCache.EnsureAsync(null, timeout.Token);
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task ExistingValidatedModelsWorkOfflineAfterRestart()
    {
        Directory.CreateDirectory(_directory);
        var manifest = CreateManifest();
        foreach (var file in manifest.Files)
            await File.WriteAllBytesAsync(Path.Combine(_directory, file.FileName), _contents[file.Role]);
        var store = new ModelStore(_directory, manifest, (_, _, _, _) =>
            throw new InvalidOperationException("磁盘中的完整模型应校验后离线使用。"));
        var statuses = new List<ModelDownloadStatus>();

        await store.EnsureAsync(new InlineProgress<ModelDownloadStatus>(statuses.Add), CancellationToken.None);

        AssertModelsReady(store);
        Assert.True(Assert.Single(statuses).IsReady);
        Assert.Equal(100, statuses[0].Percentage);
    }

    [Fact]
    public async Task SameLengthWrongHashUsesFallbackAndRemovesTemporaryFiles()
    {
        var requested = new List<string>();
        var store = new ModelStore(_directory, CreateManifest(), async (url, destination, _, token) =>
        {
            requested.Add(url);
            var contents = GetContents(url).ToArray();
            if (new Uri(url).Host == "primary.invalid") contents[0] ^= 0xff;
            await File.WriteAllBytesAsync(destination, contents, token);
        });

        await store.EnsureAsync(null, CancellationToken.None);

        Assert.Equal(8, requested.Count);
        Assert.Equal(4, requested.Count(url => new Uri(url).Host == "fallback.invalid"));
        AssertModelsReady(store);
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task HttpTimeoutFallbackContinuesWhenCallerHasNotCancelled()
    {
        var requested = new List<string>();
        var store = new ModelStore(_directory, CreateManifest(), async (url, destination, _, token) =>
        {
            requested.Add(url);
            if (new Uri(url).Host == "primary.invalid")
            {
                await File.WriteAllBytesAsync(destination, GetContents(url)[..2], token);
                using var timeout = new CancellationTokenSource();
                timeout.Cancel();
                throw new TaskCanceledException("下载器单次 HTTP 超时。", new TimeoutException(), timeout.Token);
            }
            await File.WriteAllBytesAsync(destination, GetContents(url), token);
        });
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await store.EnsureAsync(null, caller.Token);

        Assert.False(caller.IsCancellationRequested);
        Assert.Equal(8, requested.Count);
        Assert.Equal(4, requested.Count(url => new Uri(url).Host == "fallback.invalid"));
        AssertModelsReady(store);
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task ModifiedCacheIsRevalidatedAndOnlyCorruptModelIsDownloadedAgain()
    {
        var requests = new List<string>();
        var store = new ModelStore(_directory, CreateManifest(), async (url, destination, _, token) =>
        {
            requests.Add(new Uri(url).AbsolutePath.TrimStart('/'));
            await File.WriteAllBytesAsync(destination, GetContents(url), token);
        });
        await store.EnsureAsync(null, CancellationToken.None);
        requests.Clear();
        var path = store.GetPath("det");
        var previousWritten = File.GetLastWriteTimeUtc(path);
        var corrupted = _contents["det"].ToArray();
        corrupted[0] ^= 0xff;
        await File.WriteAllBytesAsync(path, corrupted);
        File.SetLastWriteTimeUtc(path, previousWritten.AddSeconds(2));

        await store.EnsureAsync(null, CancellationToken.None);

        Assert.Equal(new[] { "det" }, requests);
        AssertModelsReady(store);
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task CancelledPartialDownloadPreservesCompleteModelsAndReleasesGateForRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var interrupted = new ModelStore(_directory, CreateManifest(), async (url, destination, _, token) =>
        {
            var contents = GetContents(url);
            if (++calls == 2)
            {
                await File.WriteAllBytesAsync(destination, contents[..2], token);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            await File.WriteAllBytesAsync(destination, contents, token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.EnsureAsync(null, cancellation.Token));

        Assert.True(File.Exists(interrupted.GetPath("det")));
        Assert.False(File.Exists(interrupted.GetPath("cls")));
        AssertNoPartialFiles();
        var retriedRoles = new List<string>();
        var retry = new ModelStore(_directory, CreateManifest(), async (url, destination, _, token) =>
        {
            retriedRoles.Add(new Uri(url).AbsolutePath.TrimStart('/'));
            await File.WriteAllBytesAsync(destination, GetContents(url), token);
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await retry.EnsureAsync(null, timeout.Token);

        Assert.Equal(new[] { "cls", "rec", "dict" }, retriedRoles);
        AssertModelsReady(retry);
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task CancellingAnotherServiceWaitingForCacheDoesNotReleaseOwnersGate()
    {
        var enteredDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var first = new ModelStore(_directory, CreateManifest(), async (url, destination, _, token) =>
        {
            Interlocked.Increment(ref calls);
            enteredDownload.TrySetResult();
            await resumeDownload.Task.WaitAsync(token);
            await File.WriteAllBytesAsync(destination, GetContents(url), token);
        });
        var waiting = new ModelStore(_directory, CreateManifest(), (_, _, _, _) =>
            throw new InvalidOperationException("等待者不应进入下载临界区。"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var owner = first.EnsureAsync(null, timeout.Token);
        await enteredDownload.Task.WaitAsync(timeout.Token);
        using var cancelled = new CancellationTokenSource();
        var waiter = waiting.EnsureAsync(null, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(owner.IsCompleted);
        Assert.Equal(1, calls);
        resumeDownload.SetResult();
        await owner;
        await waiting.EnsureAsync(null, timeout.Token);

        Assert.Equal(4, calls);
        AssertModelsReady(first);
    }

    [Theory]
    [InlineData("missing-files")]
    [InlineData("null-files")]
    [InlineData("null-file")]
    [InlineData("duplicate-role")]
    [InlineData("duplicate-filename")]
    [InlineData("path-traversal")]
    [InlineData("windows-path-traversal")]
    [InlineData("plain-http")]
    [InlineData("null-urls")]
    [InlineData("null-hash")]
    [InlineData("short-hash")]
    [InlineData("zero-size")]
    [InlineData("wrong-family")]
    public void MalformedManifestIsRejectedBeforeDownloading(string invalidCase)
    {
        Directory.CreateDirectory(_directory);
        var document = JsonSerializer.SerializeToNode(CreateManifest())!.AsObject();
        var files = document["Files"]!.AsArray();
        switch (invalidCase)
        {
            case "missing-files": document.Remove("Files"); break;
            case "null-files": document["Files"] = null; break;
            case "null-file": files[0] = null; break;
            case "duplicate-role": files[0]!["Role"] = "cls"; break;
            case "duplicate-filename": files[0]!["FileName"] = files[1]!["FileName"]!.GetValue<string>(); break;
            case "path-traversal": files[0]!["FileName"] = "../model.onnx"; break;
            case "windows-path-traversal": files[0]!["FileName"] = "..\\model.onnx"; break;
            case "plain-http": files[0]!["Urls"] = new JsonArray("http://example.invalid/model.onnx"); break;
            case "null-urls": files[0]!["Urls"] = null; break;
            case "null-hash": files[0]!["Sha256"] = null; break;
            case "short-hash": files[0]!["Sha256"] = "1234"; break;
            case "zero-size": files[0]!["Size"] = 0; break;
            case "wrong-family": document["Family"] = "PP-OCRv5"; break;
        }
        var path = Path.Combine(_directory, "model-manifest.json");
        File.WriteAllText(path, document.ToJsonString());

        Assert.Throws<InvalidDataException>(() => ModelManifest.Load(path));
    }

    private void AssertModelsReady(ModelStore store)
    {
        foreach (var pair in _contents)
            Assert.Equal(pair.Value, File.ReadAllBytes(store.GetPath(pair.Key)));
    }

    private void AssertNoPartialFiles() => Assert.Empty(Directory.EnumerateFiles(_directory, "*.download"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
