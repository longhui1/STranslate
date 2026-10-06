using CommunityToolkit.Mvvm.DependencyInjection;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Extensions.Logging;
using STranslate.Core;
using STranslate.Plugin;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace STranslate.Controls;

/// <summary>
/// 展示更新日志并确认是否下载更新的对话框。
/// </summary>
public partial class UpdateChangelogDialog : ContentDialog, INotifyPropertyChanged
{
    private const string ChangelogUrl = "https://raw.githubusercontent.com/STranslate/STranslate/refs/heads/main/CHANGELOG.md";
    private readonly ILogger<UpdateChangelogDialog> _logger = Ioc.Default.GetRequiredService<ILogger<UpdateChangelogDialog>>();
    private readonly Internationalization _i18n = Ioc.Default.GetRequiredService<Internationalization>();
    private readonly IHttpService _httpService = Ioc.Default.GetRequiredService<IHttpService>();
    private readonly string? _releaseNotesMarkdown;
    private readonly string? _releasePageUrl;

    private string _headerText = string.Empty;
    private bool _isLoading = true;
    private string _markdownContent = string.Empty;

    /// <summary>
    /// 初始化更新日志对话框。
    /// </summary>
    /// <param name="newVersion">检测到的新版本号。</param>
    /// <param name="releaseNotesMarkdown">独立发布源的安装包更新日志。</param>
    /// <param name="releasePageUrl">独立发布源的更新日志回退链接；为空时沿用上游更新日志。</param>
    public UpdateChangelogDialog(string newVersion, string? releaseNotesMarkdown = null, string? releasePageUrl = null)
    {
        InitializeComponent();
        DataContext = this;
        _releaseNotesMarkdown = releaseNotesMarkdown;
        _releasePageUrl = releasePageUrl;

        HeaderText = string.Format(_i18n.GetTranslation("NewVersionFound"), newVersion);
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 对话框标题下方的版本提示文本。
    /// </summary>
    public string HeaderText
    {
        get => _headerText;
        private set
        {
            if (_headerText == value)
            {
                return;
            }

            _headerText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 指示更新日志是否处于加载中。
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value)
            {
                return;
            }

            _isLoading = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 要显示的 Markdown 内容。
    /// </summary>
    public string MarkdownContent
    {
        get => _markdownContent;
        private set
        {
            if (_markdownContent == value)
            {
                return;
            }

            _markdownContent = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 属性变更事件。
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await LoadChangelogAsync();
    }

    /// <summary>
    /// 加载更新日志，失败时显示回退链接。
    /// </summary>
    private async Task LoadChangelogAsync()
    {
        IsLoading = true;
        var changelogContent = _releasePageUrl is null
            ? await TryGetChangelogAsync()
            : _releaseNotesMarkdown;
        MarkdownContent = string.IsNullOrWhiteSpace(changelogContent)
            ? BuildFallbackMarkdown()
            : changelogContent;
        IsLoading = false;
    }

    /// <summary>
    /// 尝试下载远程更新日志文本。
    /// </summary>
    /// <returns>下载成功返回日志内容，失败返回 <c>null</c>。</returns>
    private async Task<string?> TryGetChangelogAsync()
    {
        try
        {
            var content = await _httpService.GetAsync(url: ChangelogUrl, options: null, cancellationToken: default);
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load changelog markdown from remote url.");
            return null;
        }
    }

    /// <summary>
    /// 生成加载失败时的 Markdown 回退文本。
    /// </summary>
    /// <returns>包含可点击链接的提示 Markdown。</returns>
    private string BuildFallbackMarkdown()
        => string.Format(_i18n.GetTranslation("UpdateChangelogLoadFailedMarkdown"), _releasePageUrl ?? ChangelogUrl);

    private void OpenHyperlink(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        var link = e.Parameter?.ToString();
        if (string.IsNullOrWhiteSpace(link))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = link,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open changelog link from markdown viewer.");
        }
    }

    /// <summary>
    /// 将 Markdown 区域的滚轮事件转发给外层滚动容器，避免文本区域无法滚动的问题。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">鼠标滚轮事件参数。</param>
    private void MarkdownViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Markdig 内部文档元素可能先处理滚轮，手动转发到外层可统一滚动体验。
        PART_ContentScrollViewer.ScrollToVerticalOffset(PART_ContentScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
