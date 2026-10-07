using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Extensions.Logging;
using STranslate.Plugin;

namespace PaddleOcrSmoke;

/// <summary>仅提供生产插件 smoke 所需的宿主服务，不创建窗口或清理模型缓存。</summary>
public sealed class SmokeContext(PluginMetaData meta, IHttpService http, SmokeLogger logger) : IPluginContext
{
    private readonly Dictionary<Type, object> _settings = [];

    public PluginMetaData MetaData { get; } = meta;
    public ILogger Logger { get; } = logger;
    public IHttpService HttpService { get; } = http;
    public IAudioPlayer AudioPlayer => throw Unsupported();
    public ISnackbar Snackbar => throw Unsupported();
    public INotification Notification => throw Unsupported();
    public ImageQuality ImageQuality => throw Unsupported();

    public string GetTranslation(string key) => key;

    public T LoadSettingStorage<T>() where T : new()
    {
        lock (_settings)
        {
            if (!_settings.TryGetValue(typeof(T), out var settings))
                _settings.Add(typeof(T), settings = new T()!);
            return (T)settings;
        }
    }

    public void SaveSettingStorage<T>() where T : new() => _ = LoadSettingStorage<T>();

    public Window GetPromptEditWindow(ObservableCollection<Prompt> prompts, List<string>? roles = default) => throw Unsupported();
    public void ApplyTheme(Window window) => throw Unsupported();
    public void Dispose() { }

    private static NotSupportedException Unsupported() => new("OCR smoke 不提供 UI、音频或通知服务。");
}

public sealed class SmokeLogger : ILogger
{
    private int _inferenceStartCount;

    public Action? OnInferenceStarted { get; set; }
    public int InferenceStartCount => Volatile.Read(ref _inferenceStartCount);

    public bool IsEnabled(LogLevel logLevel) => true;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => EmptyScope.Instance;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        Console.WriteLine($"[{logLevel}] {message}");
        if (exception is not null) Console.WriteLine(exception);
        if (message.Contains("PaddleOCR V6 ARM64 开始本地识别", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _inferenceStartCount);
            OnInferenceStarted?.Invoke();
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
