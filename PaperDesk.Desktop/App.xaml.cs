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
        string? dataRoot = null;
        string? localAppData = null;
        DispatcherUnhandledException += (_, args) =>
            ErrorLog.Write(dataRoot, ErrorLog.Area.Dispatcher, args.Exception, localAppData);
        try
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var (root, legacyRoot) = ProfileStorage.Resolve(e.Args, AppContext.BaseDirectory, localAppData);
            dataRoot = root;
            var hash = ProfileStorage.Key(root);
            mutex = new Mutex(true, "Local\\PaperDesk-" + hash, out ownsMutex);
            activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\PaperDesk-open-" + hash);
            if (!ownsMutex) { activate.Set(); Shutdown(); return; }
            ProfileStorage.Migrate(root, legacyRoot);
            Controller = new DeskController(root);
            Controller.Start();
            if (!e.Args.Contains("--tray") && (!e.Args.Contains("--startup") || !Controller.Settings.StartInTray))
                Controller.ShowManager();
            _ = Task.Run(() => { while (activate.WaitOne()) Dispatcher.BeginInvoke(() => Controller.ShowManager()); });
        }
        catch (Exception ex)
        {
            ErrorLog.Write(dataRoot, ErrorLog.Area.Startup, ex, localAppData);
            MessageBox.Show(L.T("Не удалось открыть данные. Исходные файлы не изменены.\n\n") + ex.Message, "PaperDesk");
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        Controller?.Dispose(); if (ownsMutex) mutex?.ReleaseMutex(); mutex?.Dispose(); base.OnExit(e);
    }
}
