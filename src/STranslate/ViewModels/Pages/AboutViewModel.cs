using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.Plugin;
using STranslate.Services;
using STranslate.Views;
using System.Diagnostics;
using System.IO;

namespace STranslate.ViewModels.Pages;

public partial class AboutViewModel(
    Settings settings,
    HotkeySettings hotkeySettings,
    ServiceSettings serviceSettings,
    DataProvider dataProvider,
    INotification notification,
    ISnackbar snackbar,
    Internationalization i18n,
    UpdaterService updaterService,
    BackupService backupService) : ObservableObject
{
    private const int PortableModeOperationDelaySeconds = 3;
    private readonly UpdateFeedPolicy _aboutFeed = UpdateFeedPolicy.Current;

    public Settings Settings { get; } = settings;
    public DataProvider DataProvider { get; } = dataProvider;
    public bool IsPortableMode => DataLocation.PortableDataLocationInUse();
    public bool IsArm64Version => _aboutFeed.Channel == UpdateFeedPolicy.Arm64Channel;
    public string ApplicationName => IsArm64Version ? $"{Constant.AppName} ARM64" : Constant.AppName;
    public string Attribution => IsArm64Version
        ? $"Windows ARM64 fork: {MaintainerName}\n© 2020 zggsong & STranslate Contributors"
        : "© 2020 zggsong & STranslate Contributors";
    public Uri ProjectUri => new(_aboutFeed.RepositoryUrl);
    public Uri IssueUri => IsArm64Version ? new($"{_aboutFeed.RepositoryUrl}/issues") : Constant.ReportUri;
    public Uri ReleaseNotesUri => new(_aboutFeed.ReleaseNotesUrl ?? $"{_aboutFeed.RepositoryUrl}/releases");
    public string MaintainerName => ProjectUri.Segments[1].Trim('/');
    public Uri MaintainerUri => new($"{ProjectUri.GetLeftPart(UriPartial.Authority)}/{MaintainerName}");
    public string Version => Constant.Version switch
    {
        "1.0.0" => Constant.Dev,
        _ => Constant.Version
    };

    #region ICommand

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (Version == Constant.Dev)
        {
            snackbar.ShowWarning(i18n.GetTranslation("NoCheckUpdataInDev"));
            return;
        }
        await updaterService.UpdateAppAsync();
    }

    [RelayCommand]
    private async Task OpenWizardAsync()
        => await SingletonWindowOpener.OpenAsync<WelcomeSetupWindow>();

    [RelayCommand]
    private void LocateUserData() => Locate(Path.GetDirectoryName(Path.Combine(DataLocation.SettingsDirectory)));

    [RelayCommand]
    private void LocateSettings() => Locate(DataLocation.SettingsDirectory);

    [RelayCommand]
    private void LocateLog() => Locate(Path.Combine(DataLocation.LogDirectory, Constant.Version));

    [RelayCommand]
    private void LocateCache() => Locate(DataLocation.CacheDirectory);

    [RelayCommand]
    private async Task TogglePortableModeAsync()
    {
        var isPortableMode = IsPortableMode;

        if (await new ContentDialog
        {
            Title = i18n.GetTranslation("Prompt"),
            PrimaryButtonText = i18n.GetTranslation("Confirm"),
            CloseButtonText = i18n.GetTranslation("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = i18n.GetTranslation(isPortableMode
                ? "ConfirmRestartAndDisablePortableMode"
                : "ConfirmRestartAndEnablePortableMode")
        }.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (isPortableMode && Directory.Exists(DataLocation.RoamingDataPath))
        {
            notification.ShowWithButton(
                i18n.GetTranslation("Prompt"),
                i18n.GetTranslation("OpenDirectory"),
                () => FilesFolders.OpenPath(DataLocation.RoamingDataPath),
                i18n.GetTranslation("PortableModeDisableRoamingConflict"));
            return;
        }

        Settings.Save();
        hotkeySettings.Save();
        serviceSettings.Save();

        var mode = isPortableMode ? "disable" : "enable";
        var sourcePath = isPortableMode ? DataLocation.PortableDataPath : DataLocation.RoamingDataPath;
        var targetPath = isPortableMode ? DataLocation.RoamingDataPath : DataLocation.PortableDataPath;
        var successMessage = i18n.GetTranslation(isPortableMode
            ? "PortableModeDisableSuccess"
            : "PortableModeEnableSuccess");
        var failurePrefix = i18n.GetTranslation(isPortableMode
            ? "PortableModeDisableFailed"
            : "PortableModeEnableFailed");

        var args = new List<string>
        {
            "portable",
            "-m", mode,
            "-s", sourcePath,
            "-t", targetPath,
            "-d", PortableModeOperationDelaySeconds.ToString(),
            "-p", DataLocation.AppExePath,
            "-i", DataLocation.InfoFilePath,
            "-w", successMessage,
            "-f", failurePrefix
        };

        var executeResult = Utilities.ExecuteProgram(DataLocation.HostExePath, [.. args]);
        if (!executeResult.Success)
        {
            snackbar.ShowError(string.Format(i18n.GetTranslation("PortableModeHostStartFailed"), executeResult.ErrorMessage));
            return;
        }

        App.RequestShutdown(AppShutdownReason.PortableModeChange);
    }

    private void Locate(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        Process.Start("explorer.exe", folder);
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        if (Settings.Backup.Type == BackupType.Local)
            await backupService.LocalBackupAsync();
        else
            await backupService.PreWebDavBackupAsync();
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (Settings.Backup.Type == BackupType.Local)
            await backupService.LocalRestoreAsync();
        else
            await backupService.WebDavRestoreAsync();
    }

    #endregion
}
