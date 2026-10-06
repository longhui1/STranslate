using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using STranslate.Core;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace STranslate.Tests;

public class UpdateFeedPolicyTests
{
    [Fact]
    public async Task Arm64OverridesAnInstalledX64ChannelAndFindsRevisionUpdates()
    {
        using var feed = new ReleaseFeed(UpdateFeedPolicy.Arm64Repository);
        feed.AddRelease("stable", false, ("win-x64", "999.0.0"), ("win-arm64", "2.0.3-arm64.2"));
        var manager = feed.CreateManager(Architecture.Arm64, "2.0.3-arm64.1", "win-x64");

        var update = await manager.CheckForUpdatesAsync();

        Assert.NotNull(update);
        Assert.Equal("2.0.3-arm64.2", update.TargetFullRelease.Version.ToString());
        Assert.True(update.TargetFullRelease.Version > manager.CurrentVersion!);
        Assert.Equal("ARM64 发布日志", update.TargetFullRelease.NotesMarkdown);
        Assert.Equal(new[] { $"{feed.Repository}/releases/download/stable/releases.win-arm64.json" }, feed.DownloadedFeeds);
    }

    [Fact]
    public async Task Arm64DoesNotFallBackWhenAReleaseOnlyContainsX64()
    {
        using var feed = new ReleaseFeed(UpdateFeedPolicy.Arm64Repository);
        feed.AddRelease("x64-only", false, ("win-x64", "999.0.0"), ("win", "999.0.0"));

        var update = await feed.CreateManager(Architecture.Arm64, "2.0.3-arm64.1", "win-x64").CheckForUpdatesAsync();

        Assert.Null(update);
        Assert.Empty(feed.DownloadedFeeds);
    }

    [Fact]
    public async Task Arm64StableUpdatesIgnoreGithubPrereleases()
    {
        using var feed = new ReleaseFeed(UpdateFeedPolicy.Arm64Repository);
        feed.AddRelease("stable", false, ("win-arm64", "2.0.3-arm64.2"));
        feed.AddRelease("preview", true, ("win-arm64", "999.0.0"));

        var update = await feed.CreateManager(Architecture.Arm64, "2.0.3-arm64.1", "win-arm64").CheckForUpdatesAsync();

        Assert.NotNull(update);
        Assert.Equal("2.0.3-arm64.2", update.TargetFullRelease.Version.ToString());
        Assert.DoesNotContain(feed.DownloadedFeeds, url => url.Contains("/preview/"));
    }

    [Theory]
    [InlineData("2.0.3-arm64.1")]
    [InlineData("2.0.3-arm64.2")]
    public async Task Arm64DoesNotDowngradeOrReinstallTheCurrentVersion(string installedVersion)
    {
        using var feed = new ReleaseFeed(UpdateFeedPolicy.Arm64Repository);
        feed.AddRelease("stable", false, ("win-arm64", "2.0.3-arm64.1"));

        Assert.Null(await feed.CreateManager(Architecture.Arm64, installedVersion, "win-arm64").CheckForUpdatesAsync());
    }

    [Fact]
    public async Task Arm64FindsANewUpstreamVersionWithAResetRevision()
    {
        using var feed = new ReleaseFeed(UpdateFeedPolicy.Arm64Repository);
        feed.AddRelease("stable", false, ("win-arm64", "2.0.4-arm64.1"));

        var update = await feed.CreateManager(Architecture.Arm64, "2.0.3-arm64.99", "win-arm64").CheckForUpdatesAsync();

        Assert.NotNull(update);
        Assert.Equal("2.0.4-arm64.1", update.TargetFullRelease.Version.ToString());
    }

    [Theory]
    [InlineData("win")]
    [InlineData("win-x64")]
    public async Task X64KeepsTheUpstreamRepositoryAndInstalledChannel(string installedChannel)
    {
        using var feed = new ReleaseFeed(Constant.Github);
        feed.AddRelease("stable", false, (installedChannel, "2.0.4"), ("win-arm64", "999.0.0"));

        var update = await feed.CreateManager(Architecture.X64, "2.0.3", installedChannel).CheckForUpdatesAsync();

        Assert.NotNull(update);
        Assert.Equal("2.0.4", update.TargetFullRelease.Version.ToString());
        Assert.Equal(new[] { $"{Constant.Github}/releases/download/stable/releases.{installedChannel}.json" }, feed.DownloadedFeeds);
    }

    /// <summary>
    /// 用本地 GitHub API 与 feed 响应运行真实 Velopack 选择逻辑，不访问网络或安装程序。
    /// </summary>
    private sealed class ReleaseFeed(string repository) : IFileDownloader, IDisposable
    {
        private readonly string _packagesDirectory = Directory.CreateTempSubdirectory("STranslate-update-tests-").FullName;
        private readonly List<object> _releases = [];
        private readonly Dictionary<string, string> _feeds = [];

        internal string Repository => repository;
        internal List<string> DownloadedFeeds { get; } = [];

        internal void AddRelease(string name, bool prerelease, params (string Channel, string Version)[] feeds)
        {
            var assets = feeds.Select(feed =>
            {
                var fileName = $"releases.{feed.Channel}.json";
                var downloadUrl = $"{Repository}/releases/download/{name}/{fileName}";
                _feeds.Add(downloadUrl, JsonSerializer.Serialize(new
                {
                    Assets = new[]
                    {
                        new
                        {
                            PackageId = "STranslate-ARM64",
                            Version = feed.Version,
                            Type = "Full",
                            FileName = $"STranslate-ARM64-{feed.Version}-{feed.Channel}-full.nupkg",
                            SHA1 = "",
                            SHA256 = "",
                            Size = 0,
                            NotesMarkdown = "ARM64 发布日志"
                        }
                    }
                }));
                return new { name = fileName, browser_download_url = downloadUrl };
            }).ToArray();

            _releases.Add(new { name, prerelease, published_at = DateTimeOffset.UtcNow, assets });
        }

        internal UpdateManager CreateManager(Architecture architecture, string installedVersion, string installedChannel)
        {
            var locator = new TestVelopackLocator("STranslate-ARM64", installedVersion, _packagesDirectory,
                appDir: null, rootDir: null, updateExe: null, channel: installedChannel);
            return UpdateFeedPolicy.ForArchitecture(architecture).CreateUpdateManager(this, locator);
        }

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            var repositoryPath = new Uri(Repository).AbsolutePath;
            Assert.Equal($"https://api.github.com/repos{repositoryPath}/releases?per_page=10&page=1", url);
            return Task.FromResult(JsonSerializer.Serialize(_releases));
        }

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            DownloadedFeeds.Add(url);
            return Task.FromResult(Encoding.UTF8.GetBytes(_feeds[url]));
        }

        public Task DownloadFile(string url, string targetFile, Action<int> progress,
            IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) =>
            throw new NotSupportedException();

        public void Dispose() => Directory.Delete(_packagesDirectory, recursive: true);
    }
}
