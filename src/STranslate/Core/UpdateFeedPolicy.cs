using System.Runtime.InteropServices;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace STranslate.Core;

/// <summary>
/// 按主程序进程架构选择发布源，防止 ARM64 安装误更新到上游 x64 包。
/// </summary>
internal sealed record UpdateFeedPolicy(string RepositoryUrl, string? Channel)
{
    internal const string Arm64Repository = "https://github.com/longhui1/STranslate";
    internal const string Arm64Channel = "win-arm64";
    internal const string Arm64PackageId = "STranslate-ARM64";

    internal static UpdateFeedPolicy Current => ForArchitecture(RuntimeInformation.ProcessArchitecture);

    internal string? ReleaseNotesUrl => Channel == Arm64Channel ? $"{RepositoryUrl}/releases" : null;

    internal static UpdateFeedPolicy ForArchitecture(Architecture architecture) =>
        architecture == Architecture.Arm64
            ? new(Arm64Repository, Arm64Channel)
            : new(Constant.Github, null);

    // GitHub latest 文件入口只指向正式发布，避免匿名 Release API 的共享出口限流。
    internal IUpdateSource CreateUpdateSource(IFileDownloader? downloader = null) =>
        Channel == Arm64Channel
            ? new Arm64WebSource(RepositoryUrl, downloader)
            : new GithubSource(RepositoryUrl, accessToken: null, prerelease: false, downloader: downloader);

    internal UpdateManager CreateUpdateManager(IFileDownloader? downloader = null, IVelopackLocator? locator = null) =>
        new(CreateUpdateSource(downloader), new UpdateOptions { ExplicitChannel = Channel }, locator);

    /// <summary>
    /// 从 latest 检查稳定更新，按目标版本的固定 tag 下载，避免发布切换改变下载目标。
    /// </summary>
    private sealed class Arm64WebSource(string repository, IFileDownloader? downloader)
        : SimpleWebSource($"{repository}/releases/latest/download/", downloader)
    {
        public override Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry,
            string localFile, Action<int> progress, CancellationToken cancelToken)
        {
            ArgumentNullException.ThrowIfNull(releaseEntry);
            ArgumentNullException.ThrowIfNull(localFile);
            if (releaseEntry.PackageId != Arm64PackageId || releaseEntry.Version is null ||
                string.IsNullOrWhiteSpace(releaseEntry.FileName) || releaseEntry.FileName is "." or ".." ||
                releaseEntry.FileName.IndexOfAny(['/', '\\', ':']) >= 0)
                throw new ArgumentException("ARM64 更新包标识或文件名无效。", nameof(releaseEntry));

            var tag = Uri.EscapeDataString($"arm64-v{releaseEntry.Version}");
            var fileName = Uri.EscapeDataString(releaseEntry.FileName);
            var url = $"{repository}/releases/download/{tag}/{fileName}";
            logger.LogInformation($"从固定版本下载 ARM64 更新包：{url}");
            return Downloader.DownloadFile(url, localFile, progress, timeout: Timeout, cancelToken: cancelToken);
        }
    }
}
