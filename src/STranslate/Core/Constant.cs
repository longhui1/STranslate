using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace STranslate.Core;

public class Constant
{
    public const string AppName = "STranslate";
    public const string Plugins = "Plugins";
    public const string PortableFolderName = "PortableConfig";
    public const string Cache = "Cache";
    public const string Settings = "Settings";
    public const string Logs = "Logs";
    public const string PluginMetaFileName = "plugin.json";
    public const string TmpPluginFolderName = "STranslateTmpPlugins";
    public const string TmpConfigFolderName = "STranslateTmpConfig";
    public const string SystemLanguageCode = "system";
    public const string HttpClientName = "DefaultClient";
    public const string HostExeName = "z_stranslate_host.exe";
    public const string TaskName = "STranslateSkipUAC";
    public const string EmptyHotkey = "None";
    public const string PluginFileExtension = ".spkg";
    public const string NeedDelete = "NeedDelete.txt";
    public const string NeedUpgrade = "_NeedUpgrade";
    public const string InfoFileName = ".INFO";
    public const string BackupFileName = ".BACKUP";

    public const string Github = "https://github.com/STranslate/STranslate";
    public const string Sponsor = "https://github.com/STranslate/STranslate/tree/main?tab=readme-ov-file#donations";
    public const string Dev = "Dev";
    public static readonly string Version = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location.NonNull()).ProductVersion.NonNull();

    public static readonly Uri GithubUri = new(Github);
    public static readonly Uri ReportUri = new("https://github.com/STranslate/STranslate/issues/new/choose");
    public static readonly Uri WebsiteUri = new("https://stranslate.zggsong.com");
    public static readonly Uri GroupUri = new("https://t.me/stranslatewpf");
    public static readonly Uri FlowLauncherUri = new("https://github.com/Flow-Launcher/Flow.Launcher");
    public static readonly Uri UiWpfMordenUri = new("https://github.com/iNKORE-NET/UI.WPF.Modern");
    public static readonly Uri BobUri = new("https://bobtranslate.com/");
    public static readonly Uri Icons8Uri = new("https://icons8.com/");


    /// <summary>
    ///     用户软件根目录
    /// </summary>
    /// <remarks>
    ///     <see cref="Environment.CurrentDirectory" />
    ///     * 使用批处理时获取路径为批处理文件所在目录
    /// </remarks>
    public static readonly string ProgramDirectory = AppDomain.CurrentDomain.BaseDirectory;

    public static readonly string LogDirectory = Path.Combine(ProgramDirectory, Logs);
    public static readonly string PreinstalledDirectory = Path.Combine(ProgramDirectory, Plugins);

    public static readonly List<string> PrePluginIDs =
    [
        "64f812516673408ab5b59e56720bd641", //BaiduOCR
        "8eb80c0391314256a4058b464d77946a", //OpenAIOCR
        "3410e7de989340938301abd6fcf8cc4b", //WeChatOCRBuiltIn
        "c67c0e3de45b48f6a852ffa8f0aae2f2", //PaddleOCR V6 ARM64
        "2e83ee2f5dbf45249a3bd1457a326abf", //GoogleOCR
        "bb65c593ebb04d40bc2c5ad55aecc4e2", //TencentOCR
        "f0c68321faf24e058be2b2e2ed26f90d", //YoudaoOCR
        "86ec10628e754d41921d24387ec6e815", //Baidu
        "474b5fe844d9455ba0c59f75c1424f0d", //BigModel
        "09d6beef9b1f4891a4a0d8a8dbf510d1", //DeepL
        "0b5d84917783415d865032f1d6e2877f", //GoogleBuiltIn
        "0f6892a390a543709926092aba510273", //ICibaDict
        "05e1bf32df8e4d18a9c39bd51d909bef", //ICibaTranslateBuiltIn
        "2d6f6831fd114463bfdebfd8ee85e549", //MicrosoftBuiltIn
        "2cc83275790ba8ce96b31c4fe0655743", //TransmartBuiltIn
        "9e44abfa040e443c9ab48205683082f4", //MTranServer
        "76b14a8d707041c891a2dcd2f74be9c1", //OpenAI
        "2c1b2a2fa1e24ae79b7dc73bdea35159", //YandexBuiltIn
        "6d90a1ae6fce5fe776f57961c5b8eef7", //Youdao
        "7a3ab25875294602b3afc4ae15fec627", //MicrosoftEdgeTts
        "d9537be74d23438ca581fd6d04e1d112", //EudictVocabulary
    ];
}
