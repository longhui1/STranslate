using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using STranslate.Core;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace STranslate.Tests;

public class UpdateFeedPolicyTests
{
    [Fact]
    public async Task Arm64FindsRevisionUpdatesWithoutCallingTheRateLimitedGithubApi()
    {
        using var feed = new ReleaseFeed(UpdateFeedPolicy.Arm64Repository) { RejectGithubApi = true };
        feed.AddRelease("stable", false, ("win-x64", "999.0.0"), ("win-arm64", "2.0.3-arm64.2"));
        var manager = feed.CreateManager(Architecture.Arm64, "2.0.3-arm64.1", "win-x64");

        var update = await manager.CheckForUpdatesAsync();

        Assert.NotNull(update);
        Assert.Equal("2.0.3-arm64.2", update.TargetFullRelease.Version.ToString());
        Assert.True(update.TargetFullRelease.Version > manager.CurrentVersion!);
        Assert.Equal("ARM64 发布日志", update.TargetFullRelease.NotesMarkdown);
        Assert.Equal(new[] { feed.LatestFeedUrl("win-arm64") }, feed.DownloadedFeeds);
        Assert.Empty(feed.GithubApiRequests);
    }

    [Fact]
    public async Task Arm64FailsClosedWhenTheLatestReleaseOnlyContainsX64()
    {
        using var feed = new ReleaseFeed(UpdateFeedPolicy.Arm64Repository) { RejectGithubApi = true };
        feed.AddRelease("x64-only", false, ("win-x64", "999.0.0"), ("win", "999.0.0"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            feed.CreateManager(Architecture.Arm64, "2.0.3-arm64.1", "win-x64").CheckForUpdatesAsync());

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal(new[] { feed.LatestFeedUrl("win-arm64") }, feed.DownloadedFeeds);
        Assert.Empty(feed.GithubApiRequests);
        Assert.Empty(feed.DownloadedPackages);
    }

    [Fact]
    public async Task Arm64UsesTheLatestStableFileEndpointWithoutListingPreviewReleases()
    {
        using var feed = new ReleaseFeed(UpdateFeedPolicy.Arm64Repository) { RejectGithubApi = true };
        feed.AddRelease("stable", false, ("win-arm64", "2.0.3-arm64.2"));
        feed.AddRelease("preview", true, ("win-arm64", "999.0.0"));
        var policy = UpdateFeedPolicy.ForArchitecture(Architecture.Arm64);
        var source = Assert.IsAssignableFrom<SimpleWebSource>(policy.CreateUpdateSource(feed));

        var update = await feed.CreateManager(Architecture.Arm64, "2.0.3-arm64.1", "win-arm64").CheckForUpdatesAsync();

        Assert.Equal($"{feed.Repository}/releases/latest/download/", source.BaseUri.ToString());
        Assert.NotNull(update);
        Assert.Equal("2.0.3-arm64.2", update.TargetFullRelease.Version.ToString());
        Assert.Equal(new[] { feed.LatestFeedUrl("win-arm64") }, feed.DownloadedFeeds);
        Assert.Empty(feed.GithubApiRequests);
    }

    [Theory]
    [InlineData("2.0.3-arm64.1")]
    [InlineData("2.0.3-arm64.2")]
    public async Task Arm64DoesNotDowngradeOrReinstallTheCurrentVersion(string installedVersion)
    {
        using var feed = new ReleaseFeed(UpdateFeedPolicy.Arm64Repository) { RejectGithubApi = true };
        feed.AddRelease("stable", false, ("win-arm64", "2.0.3-arm64.1"));

        Assert.Null(await feed.CreateManager(Architecture.Arm64, installedVersion, "win-arm64").CheckForUpdatesAsync());
        Assert.Empty(feed.GithubApiRequests);
    }

    [Fact]
    public async Task Arm64FindsANewUpstreamVersionWithAResetRevision()
    {
        using var feed = new ReleaseFeed(UpdateFeedPolicy.Arm64Repository) { RejectGithubApi = true };
        feed.AddRelease("stable", false, ("win-arm64", "2.0.4-arm64.1"));

        var update = await feed.CreateManager(Architecture.Arm64, "2.0.3-arm64.99", "win-arm64").CheckForUpdatesAsync();

        Assert.NotNull(update);
        Assert.Equal("2.0.4-arm64.1", update.TargetFullRelease.Version.ToString());
    }

    [Fact]
    public async Task Arm64ComparesRevisionNumbersNumerically()
    {
        using var feed = new ReleaseFeed(UpdateFeedPolicy.Arm64Repository) { RejectGithubApi = true };
        feed.AddRelease("stable", false, ("win-arm64", "2.0.3-arm64.10"));

        var update = await feed.CreateManager(Architecture.Arm64, "2.0.3-arm64.2", "win-arm64").CheckForUpdatesAsync();

        Assert.NotNull(update);
        Assert.Equal("2.0.3-arm64.10", update.TargetFullRelease.Version.ToString());
    }

    [Theory]
    [InlineData(VelopackAssetType.Full, "full")]
    [InlineData(VelopackAssetType.Delta, "delta")]
    public async Task Arm64DownloadsTheSelectedVersionFromItsFixedTagWhenLatestChanges(VelopackAssetType type, string suffix)
    {
        using var feed = new ReleaseFeed(UpdateFeedPolicy.Arm64Repository) { RejectGithubApi = true };
        feed.AddRelease("stable", false, ("win-arm64", "2.0.3-arm64.2"));
        var update = await feed.CreateManager(Architecture.Arm64, "2.0.3-arm64.1", "win-arm64").CheckForUpdatesAsync();
        Assert.NotNull(update);
        var selectedPackage = update.TargetFullRelease with
        {
            Type = type,
            FileName = $"STranslate-ARM64-2.0.3-arm64.2-win-arm64-{suffix}.nupkg"
        };
        var source = Assert.IsAssignableFrom<SimpleWebSource>(UpdateFeedPolicy.ForArchitecture(Architecture.Arm64).CreateUpdateSource(feed));
        source.Timeout = 12;
        using var cancellation = new CancellationTokenSource();
        var reportedProgress = -1;
        // 用户确认下载前，服务器的 latest 已连续切换到两个新版本。
        feed.AddRelease("newer", false, ("win-arm64", "2.0.3-arm64.3"));
        feed.AddRelease("newest", false, ("win-arm64", "2.0.3-arm64.4"));

        await source.DownloadReleaseEntry(NullVelopackLogger.Instance, selectedPackage,
            feed.DownloadPath, value => reportedProgress = value, cancellation.Token);

        var download = Assert.Single(feed.DownloadedPackages);
        Assert.Equal($"{feed.Repository}/releases/download/arm64-v2.0.3-arm64.2/{selectedPackage.FileName}", download.Url);
        Assert.Equal(feed.DownloadPath, download.Path);
        Assert.Equal(cancellation.Token, download.CancellationToken);
        Assert.Equal(12, download.Timeout);
        Assert.Equal(100, reportedProgress);
        Assert.Empty(feed.GithubApiRequests);
    }

    [Theory]
    [InlineData("STranslate", "x64-full.nupkg")]
    [InlineData("STranslate-ARM64", "../x64-full.nupkg")]
    [InlineData("STranslate-ARM64", "..\\x64-full.nupkg")]
    [InlineData("STranslate-ARM64", "https://example.com/x64-full.nupkg")]
    [InlineData("STranslate-ARM64", "..")]
    [InlineData("STranslate-ARM64", "")]
    public async Task Arm64RejectsOtherPackageIdsAndNonLocalFileNames(string packageId, string fileName)
    {
        using var feed = new ReleaseFeed(UpdateFeedPolicy.Arm64Repository);
        feed.AddRelease("stable", false, ("win-arm64", "2.0.3-arm64.2"));
        var update = await feed.CreateManager(Architecture.Arm64, "2.0.3-arm64.1", "win-arm64").CheckForUpdatesAsync();
        Assert.NotNull(update);
        var package = update.TargetFullRelease with { PackageId = packageId, FileName = fileName };
        var source = UpdateFeedPolicy.ForArchitecture(Architecture.Arm64).CreateUpdateSource(feed);

        await Assert.ThrowsAsync<ArgumentException>(() => source.DownloadReleaseEntry(
            NullVelopackLogger.Instance, package, feed.DownloadPath, _ => { }, default));

        Assert.Empty(feed.DownloadedPackages);
    }

    [Theory]
    [InlineData("win")]
    [InlineData("win-x64")]
    public async Task X64KeepsTheGithubApiUpstreamRepositoryAndInstalledChannel(string installedChannel)
    {
        using var feed = new ReleaseFeed(Constant.Github);
        feed.AddRelease("stable", false, (installedChannel, "2.0.4"), ("win-arm64", "999.0.0"));
        var policy = UpdateFeedPolicy.ForArchitecture(Architecture.X64);

        var update = await feed.CreateManager(Architecture.X64, "2.0.3", installedChannel).CheckForUpdatesAsync();

        Assert.IsType<GithubSource>(policy.CreateUpdateSource(feed));
        Assert.Null(policy.Channel);
        Assert.NotNull(update);
        Assert.Equal("2.0.4", update.TargetFullRelease.Version.ToString());
        Assert.Equal(new[] { $"{Constant.Github}/releases/download/stable/releases.{installedChannel}.json" }, feed.DownloadedFeeds);
        Assert.Single(feed.GithubApiRequests);
    }

    [Fact]
    public async Task X64StillIgnoresGithubPrereleases()
    {
        using var feed = new ReleaseFeed(Constant.Github);
        feed.AddRelease("stable", false, ("win-x64", "2.0.4"));
        feed.AddRelease("preview", true, ("win-x64", "999.0.0"));

        var update = await feed.CreateManager(Architecture.X64, "2.0.3", "win-x64").CheckForUpdatesAsync();

        Assert.NotNull(update);
        Assert.Equal("2.0.4", update.TargetFullRelease.Version.ToString());
        Assert.DoesNotContain(feed.DownloadedFeeds, url => url.Contains("/preview/"));
        Assert.Single(feed.GithubApiRequests);
    }

    [Fact]
    public async Task X64StillPropagatesAGithubApiFailure()
    {
        using var feed = new ReleaseFeed(Constant.Github) { RejectGithubApi = true };
        feed.AddRelease("stable", false, ("win-x64", "2.0.4"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            feed.CreateManager(Architecture.X64, "2.0.3", "win-x64").CheckForUpdatesAsync());

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Single(feed.GithubApiRequests);
        Assert.Empty(feed.DownloadedFeeds);
    }

    /// <summary>
    /// 用本地 GitHub API 与 latest 文件响应运行真实 Velopack，不访问网络或安装程序。
    /// latest 别名由模拟文件服务器选择稳定发布，预发布不改变该别名。
    /// </summary>
    private sealed class ReleaseFeed(string repository) : IFileDownloader, IDisposable
    {
        private readonly string _packagesDirectory = Directory.CreateTempSubdirectory("STranslate-update-tests-").FullName;
        private readonly List<object> _releases = [];
        private readonly Dictionary<string, string> _feeds = [];
        private readonly Dictionary<string, string> _latestFeeds = [];

        internal string Repository => repository;
        internal string DownloadPath => Path.Combine(_packagesDirectory, "download.nupkg");
        internal bool RejectGithubApi { get; init; }
        internal List<string> GithubApiRequests { get; } = [];
        internal List<string> DownloadedFeeds { get; } = [];
        internal List<(string Url, string Path, double Timeout, CancellationToken CancellationToken)> DownloadedPackages { get; } = [];

        internal string LatestFeedUrl(string channel) => $"{Repository}/releases/latest/download/releases.{channel}.json";

        internal void AddRelease(string name, bool prerelease, params (string Channel, string Version)[] feeds)
        {
            if (!prerelease)
                _latestFeeds.Clear();

            var assets = feeds.Select(feed =>
            {
                var fileName = $"releases.{feed.Channel}.json";
                var downloadUrl = $"{Repository}/releases/download/{name}/{fileName}";
                var packageId = Repository == Constant.Github ? "STranslate" : UpdateFeedPolicy.Arm64PackageId;
                var json = JsonSerializer.Serialize(new
                {
                    Assets = new[]
                    {
                        new
                        {
                            PackageId = packageId,
                            Version = feed.Version,
                            Type = "Full",
                            FileName = $"{packageId}-{feed.Version}-{feed.Channel}-full.nupkg",
                            SHA1 = "",
                            SHA256 = "",
                            Size = 0,
                            NotesMarkdown = "ARM64 发布日志"
                        }
                    }
                });
                _feeds.Add(downloadUrl, json);
                if (!prerelease)
                    _latestFeeds.Add(LatestFeedUrl(feed.Channel), json);

                return new { name = fileName, browser_download_url = downloadUrl };
            }).ToArray();

            _releases.Add(new { name, prerelease, published_at = DateTimeOffset.UtcNow, assets });
        }

        internal UpdateManager CreateManager(Architecture architecture, string installedVersion, string installedChannel)
        {
            var appId = architecture == Architecture.Arm64 ? "STranslate-ARM64" : "STranslate";
            var locator = new TestVelopackLocator(appId, installedVersion, _packagesDirectory,
                appDir: null, rootDir: null, updateExe: null, channel: installedChannel);
            return UpdateFeedPolicy.ForArchitecture(architecture).CreateUpdateManager(this, locator);
        }

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            var uri = new Uri(url);
            if (uri.Host == "api.github.com")
            {
                GithubApiRequests.Add(url);
                var repositoryPath = new Uri(Repository).AbsolutePath;
                Assert.Equal($"https://api.github.com/repos{repositoryPath}/releases?per_page=10&page=1", url);
                if (RejectGithubApi)
                    throw new HttpRequestException("API rate limit exceeded", null, HttpStatusCode.Forbidden);

                return Task.FromResult(JsonSerializer.Serialize(_releases));
            }

            // SimpleWebSource 会附加系统和本地包查询参数，静态服务器按路径返回 feed。
            var pathUrl = uri.GetLeftPart(UriPartial.Path);
            DownloadedFeeds.Add(pathUrl);
            if (!_latestFeeds.TryGetValue(pathUrl, out var json))
                throw new HttpRequestException("ARM64 release feed not found", null, HttpStatusCode.NotFound);

            return Task.FromResult(json);
        }

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            DownloadedFeeds.Add(url);
            return Task.FromResult(Encoding.UTF8.GetBytes(_feeds[url]));
        }

        public Task DownloadFile(string url, string targetFile, Action<int> progress,
            IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
        {
            DownloadedPackages.Add((url, targetFile, timeout, cancelToken));
            progress(100);
            return Task.CompletedTask;
        }

        public void Dispose() => Directory.Delete(_packagesDirectory, recursive: true);
    }
}
