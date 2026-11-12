using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Automation;
using PaperDesk.Desktop.Services;

namespace PaperDesk.Desktop.Views;

public partial class ManagerWindow
{
    private CancellationTokenSource? updateLifetime;
    private ReleaseUpdate? availableUpdate;

    private void ConfigureUpdates()
    {
        if (Application.Current is not App) return;
        bool installed;
        try { installed = ProfileStorage.IsInstalled(AppContext.BaseDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        if (!installed && !PortableUpdate.IsPortable(AppContext.BaseDirectory)) return;
        var lifetime = updateLifetime = new CancellationTokenSource();
        Loaded += CheckUpdate;
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); updateLifetime = null; };

        async void CheckUpdate(object sender, RoutedEventArgs args)
        {
            Loaded -= CheckUpdate;
            if (updateLifetime == null) return;
            var token = updateLifetime.Token;
            _ = Task.Run(PortableUpdate.CleanFinished);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                var release = await ReleaseUpdates.Shared.FindAsync(ReleaseUpdates.CurrentVersion, installed, timeout.Token);
                if (!token.IsCancellationRequested) ShowUpdate(release);
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or IOException) { }
        }
    }

    internal void ShowUpdate(ReleaseUpdate? release)
    {
        availableUpdate = release;
        UpdateButton.Visibility = release == null ? Visibility.Collapsed : Visibility.Visible;
        if (release == null) return;
        var label = L.F("Скачать обновление {0}", release.Tag);
        UpdateButton.ToolTip = label;
        AutomationProperties.SetName(UpdateButton, label);
    }

    private async void DownloadUpdate(object sender, RoutedEventArgs args)
    {
        if (availableUpdate is not { } release || updateLifetime == null || !UpdateButton.IsEnabled) return;
        var token = updateLifetime.Token;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        UpdateButton.IsEnabled = false;
        ToolTipService.SetShowOnDisabled(UpdateButton, true);
        string? workspace = null;
        var handedOff = false;
        try
        {
            workspace = PortableUpdate.CreateWorkspace();
            var progress = new Progress<int>(percent =>
            {
                if (UpdateButton.IsEnabled || token.IsCancellationRequested) return;
                UpdateButton.ToolTip = L.F("Загрузка обновления {0}%", percent);
            });
            var package = await ReleaseUpdates.Shared.DownloadAsync(release, workspace, progress, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            EditBody.EndEdit();
            desk.FlushWindows();
            if (release.Installed)
            {
                var start = new ProcessStartInfo(package) { UseShellExecute = true };
                start.ArgumentList.Add("/PaperDeskData=" + desk.DataRoot);
                using var setup = Process.Start(start) ?? throw new IOException("Unable to start PaperDesk Setup.");
                PortableUpdate.MarkFinished(workspace, setup);
            }
            else
            {
                await Task.Run(() => PortableUpdate.StageAndStart(workspace, package, AppContext.BaseDirectory,
                    desk.DataRoot, release.Version, timeout.Token), timeout.Token);
            }
            handedOff = true;
            desk.Exit();
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or
            Win32Exception or InvalidDataException)
        {
            if (!token.IsCancellationRequested)
            {
                ErrorLog.Write(desk.DataRoot, ErrorLog.Area.Update, error);
                MessageBox.Show(this, L.T("Не удалось скачать или запустить обновление PaperDesk."), "PaperDesk",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            if (!handedOff && workspace != null) await Task.Run(() => PortableUpdate.DeleteWorkspace(workspace));
            if (!handedOff && !token.IsCancellationRequested)
            {
                UpdateButton.IsEnabled = true;
                ShowUpdate(release);
            }
        }
    }
}
