using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using PaperDesk.Desktop.Services;

namespace PaperDesk.Desktop;

public partial class App : Application
{
    private Mutex? mutex;
    private EventWaitHandle? activate;
    private bool ownsMutex;
    public DeskController Controller { get; private set; } = null!;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataIndex = Array.IndexOf(e.Args, "--data");
        var root = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[dataIndex + 1]) : Path.Combine(AppContext.BaseDirectory, "data");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..20];
        mutex = new Mutex(true, "Local\\PaperDesk-" + hash, out ownsMutex);
        activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\PaperDesk-open-" + hash);
        if (!ownsMutex) { activate.Set(); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) => { MessageBox.Show(args.Exception.Message, "PaperDesk", MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true; };
        try
        {
            Controller = new DeskController(root);
            Controller.Start();
            if (!e.Args.Contains("--tray") && (!e.Args.Contains("--startup") || !Controller.Settings.StartInTray))
                Controller.ShowManager();
            _ = Task.Run(() => { while (activate.WaitOne()) Dispatcher.BeginInvoke(() => Controller.ShowManager()); });
        }
        catch (Exception ex) { MessageBox.Show(L.T("Не удалось открыть данные. Исходные файлы не изменены.\n\n") + ex.Message, "PaperDesk"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        Controller?.Dispose(); if (ownsMutex) mutex?.ReleaseMutex(); mutex?.Dispose(); base.OnExit(e);
    }
}
