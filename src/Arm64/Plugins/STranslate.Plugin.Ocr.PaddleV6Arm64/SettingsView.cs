using System.Windows;
using System.Windows.Controls;

namespace STranslate.Plugin.Ocr.PaddleV6Arm64;

/// <summary>仅控制模型准备和实际识别选项，下载沿用宿主的代理配置。</summary>
public sealed class SettingsView : UserControl
{
    private readonly Main _plugin;
    private readonly TextBlock _status = new() { Text = "首次使用需下载约 31 MB 模型。", TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 8, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _download = new() { Content = "下载 / 校验模型", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _cancel = new() { Content = "取消下载", IsEnabled = false };
    private CancellationTokenSource? _downloadCancellation;

    public SettingsView(Main plugin, IPluginContext context, Settings settings, string cacheDirectory)
    {
        _plugin = plugin;
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = "本地 PP-OCRv6 Small，多语言识别，无需 API 密钥。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "模型使用 STranslate 的网络代理下载，完成后识别无需联网。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        panel.Children.Add(_status);
        panel.Children.Add(_progress);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_download);
        buttons.Children.Add(_cancel);
        panel.Children.Add(buttons);
        var angle = new CheckBox { Content = "识别倒置文字（180°）", IsChecked = settings.Detect180Degrees, Margin = new Thickness(0, 16, 0, 8) };
        angle.Checked += (_, _) => SaveAngle(true);
        angle.Unchecked += (_, _) => SaveAngle(false);
        panel.Children.Add(angle);
        panel.Children.Add(new TextBlock { Text = "模型缓存：" + cacheDirectory, TextWrapping = TextWrapping.Wrap });
        Content = panel;
        _download.Click += DownloadAsync;
        _cancel.Click += (_, _) => CancelDownload();
        Unloaded += (_, _) => CancelDownload();

        void SaveAngle(bool value) { settings.Detect180Degrees = value; context.SaveSettingStorage<Settings>(); }
    }

    private async void DownloadAsync(object sender, RoutedEventArgs args)
    {
        _downloadCancellation = new CancellationTokenSource();
        _download.IsEnabled = false;
        _cancel.IsEnabled = true;
        var progress = new Progress<ModelDownloadStatus>(value => { _status.Text = value.Message; _progress.Value = value.Percentage; });
        try { await _plugin.EnsureModelsAsync(progress, _downloadCancellation.Token); }
        catch (OperationCanceledException) { _status.Text = "下载已取消，可点击下载按钮重试。"; }
        catch (Exception error) { _status.Text = error.Message; }
        finally
        {
            _downloadCancellation.Dispose();
            _downloadCancellation = null;
            _download.IsEnabled = true;
            _cancel.IsEnabled = false;
        }
    }

    public void CancelDownload() => _downloadCancellation?.Cancel();
}
