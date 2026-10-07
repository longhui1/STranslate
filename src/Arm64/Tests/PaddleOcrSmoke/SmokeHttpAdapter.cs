using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using STranslate.Plugin;

namespace PaddleOcrSmoke;

public sealed record DownloadObservation(string Url, string Destination, long Bytes, bool Success,
    bool Cancelled, bool InjectedFailure, HttpStatusCode? HttpStatusCode, string? SHA256);

/// <summary>转接真实 HTTP 下载；故障注入不会替代生产模型管理器的清理及校验。</summary>
public class SmokeHttpAdapter : DispatchProxy
{
    private readonly ConcurrentQueue<DownloadObservation> _downloads = new();
    private CancellationTokenSource? _cancelAfterFirstChunk;

    public bool RejectAllDownloads { get; set; }
    public bool DenyNetwork { get; set; }
    public List<DownloadObservation> Downloads => _downloads.ToList();
    public CancellationTokenSource? CancelAfterFirstChunk
    {
        get => Volatile.Read(ref _cancelAfterFirstChunk);
        set => Interlocked.Exchange(ref _cancelAfterFirstChunk, value);
    }

    public static (IHttpService Http, SmokeHttpAdapter Adapter) Create()
    {
        var http = DispatchProxy.Create<IHttpService, SmokeHttpAdapter>();
        return (http, (SmokeHttpAdapter)http);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (DenyNetwork) throw new InvalidOperationException("离线 OCR smoke 禁止调用 HTTP 服务。");
        var parameters = targetMethod.GetParameters();
        args ??= [];
        if (targetMethod.IsGenericMethod || parameters.Any(parameter => parameter.Name == "content"))
            throw Unsupported(targetMethod);

        var url = Argument<string?>(parameters, args, "url") ?? throw Unsupported(targetMethod);
        var options = Argument<Options?>(parameters, args, "options");
        var token = Argument<CancellationToken>(parameters, args, "cancellationToken");
        return targetMethod.Name switch
        {
            nameof(IHttpService.DownloadFileAsync) => DownloadFileAsync(url,
                Argument<string?>(parameters, args, "savePath") ?? throw Unsupported(targetMethod),
                Argument<string?>(parameters, args, "fileName") ?? throw Unsupported(targetMethod),
                options, Argument<IProgress<DownloadProgress>?>(parameters, args, "progress"), token),
            nameof(IHttpService.GetAsync) => GetStringAsync(url, options, token),
            nameof(IHttpService.GetAsBytesAsync) => GetBytesAsync(url, options, token),
            nameof(IHttpService.GetAsStreamAsync) => GetStreamAsync(url, options, token),
            _ => throw Unsupported(targetMethod)
        };
    }

    private async Task<string> DownloadFileAsync(string url, string savePath, string fileName,
        Options? options, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        var destination = Path.GetFullPath(Path.Combine(savePath, fileName));
        var bytes = 0L;
        var success = false;
        var cancelled = false;
        var injectedFailure = false;
        HttpStatusCode? status = null;
        string? sha256 = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (RejectAllDownloads)
            {
                injectedFailure = true;
                status = HttpStatusCode.ServiceUnavailable;
                throw new HttpRequestException("OCR smoke 注入 HTTP 503 下载失败。", null, status);
            }

            using var request = new RequestLease(url, options, token);
            using var response = await request.Client.SendAsync(request.Message,
                HttpCompletionOption.ResponseHeadersRead, request.Token).ConfigureAwait(false);
            status = response.StatusCode;
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(request.Token).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var stopwatch = Stopwatch.StartNew();
            var buffer = new byte[65536];
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(), request.Token).ConfigureAwait(false);
                if (count == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, count), request.Token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, count);
                bytes += count;
                if (response.Content.Headers.ContentLength is long total)
                {
                    progress?.Report(new DownloadProgress
                    {
                        DownloadedBytes = bytes,
                        TotalBytes = total,
                        ElapsedTime = stopwatch.Elapsed,
                        Speed = bytes / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001)
                    });
                }

                // 第一块确实写入临时文件后取消，只注入一次，供生产管理器验证清理和重试。
                Interlocked.Exchange(ref _cancelAfterFirstChunk, null)?.Cancel();
                request.Token.ThrowIfCancellationRequested();
            }
            await output.FlushAsync(request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            success = true;
            return destination;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            throw;
        }
        catch (HttpRequestException error)
        {
            status ??= error.StatusCode;
            throw;
        }
        finally
        {
            _downloads.Enqueue(new DownloadObservation(ObservationUrl(url), destination, bytes, success,
                cancelled, injectedFailure, status, sha256));
        }
    }

    private static async Task<string> GetStringAsync(string url, Options? options, CancellationToken token)
    {
        using var request = new RequestLease(url, options, token);
        using var response = await request.Client.SendAsync(request.Message,
            HttpCompletionOption.ResponseHeadersRead, request.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(request.Token).ConfigureAwait(false);
    }

    private static async Task<byte[]> GetBytesAsync(string url, Options? options, CancellationToken token)
    {
        using var request = new RequestLease(url, options, token);
        using var response = await request.Client.SendAsync(request.Message,
            HttpCompletionOption.ResponseHeadersRead, request.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(request.Token).ConfigureAwait(false);
    }

    private static async Task<Stream> GetStreamAsync(string url, Options? options, CancellationToken token)
    {
        var request = new RequestLease(url, options, token);
        HttpResponseMessage? response = null;
        try
        {
            response = await request.Client.SendAsync(request.Message,
                HttpCompletionOption.ResponseHeadersRead, request.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(request.Token).ConfigureAwait(false);
            return new ResponseStream(stream, response, request);
        }
        catch
        {
            response?.Dispose();
            request.Dispose();
            throw;
        }
    }

    private static T Argument<T>(ParameterInfo[] parameters, object?[] args, string name)
    {
        for (var index = 0; index < parameters.Length; index++)
            if (parameters[index].Name == name && args[index] is T value) return value;
        return default!;
    }

    private static NotSupportedException Unsupported(MethodInfo method) => new($"OCR smoke 未实现 HTTP 接口：{method.Name}");

    // 观察记录不包含 URL 用户信息或查询参数中的令牌；实际请求仍使用原始 URL。
    private static string ObservationUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "无效下载 URL";
        return new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.ToString();
    }

    private sealed class RequestLease : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        public HttpClient Client { get; }
        public HttpRequestMessage Message { get; }
        public CancellationToken Token => _cancellation.Token;

        internal RequestLease(string url, Options? options, CancellationToken token)
        {
            Client = new HttpClient();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                if (options?.Timeout is TimeSpan timeout)
                {
                    Client.Timeout = timeout;
                    if (timeout != Timeout.InfiniteTimeSpan) _cancellation.CancelAfter(timeout);
                }
                var builder = new UriBuilder(url);
                if (options?.QueryParams is { Count: > 0 } query)
                {
                    var suffix = string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
                    builder.Query = string.IsNullOrEmpty(builder.Query) ? suffix : $"{builder.Query.TrimStart('?')}&{suffix}";
                }
                Message = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
                foreach (var header in options?.Headers ?? [])
                    if (!Message.Headers.TryAddWithoutValidation(header.Key, header.Value))
                        throw new ArgumentException($"GET 请求无法设置请求头：{header.Key}", nameof(options));
            }
            catch
            {
                Message?.Dispose();
                Client.Dispose();
                _cancellation.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Message.Dispose();
            Client.Dispose();
            _cancellation.Dispose();
        }
    }

    /// <summary>保持 GET 流的响应和客户端有效，调用方释放流时一起释放。</summary>
    private sealed class ResponseStream(Stream stream, HttpResponseMessage response, RequestLease request) : Stream
    {
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => stream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => stream.Length;
        public override long Position { get => stream.Position; set => stream.Position = value; }
        public override void Flush() => stream.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            request.Token.ThrowIfCancellationRequested();
            return stream.Read(buffer, offset, count);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.Token, cancellationToken);
            return await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        }
        public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                stream.Dispose();
                response.Dispose();
                request.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
