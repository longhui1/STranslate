using System.Runtime.InteropServices;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace STranslate.Core;

/// <summary>
/// 按主程序进程架构选择发布源，防止 ARM64 安装误更新到上游 x64 包。
/// </summary>
internal sealed record UpdateFeedPolicy(string RepositoryUrl, string? Channel)
{
    internal const string Arm64Repository = "https://github.com/longhui1/STranslate";
    internal const string Arm64Channel = "win-arm64";

    internal static UpdateFeedPolicy Current => ForArchitecture(RuntimeInformation.ProcessArchitecture);

    internal string? ReleaseNotesUrl => Channel == Arm64Channel ? $"{RepositoryUrl}/releases" : null;

    internal static UpdateFeedPolicy ForArchitecture(Architecture architecture) =>
        architecture == Architecture.Arm64
            ? new(Arm64Repository, Arm64Channel)
            : new(Constant.Github, null);

    internal UpdateManager CreateUpdateManager(IFileDownloader? downloader = null, IVelopackLocator? locator = null) =>
        new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false, downloader: downloader),
            new UpdateOptions { ExplicitChannel = Channel }, locator);
}
