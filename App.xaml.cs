using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Interop;

namespace MacLook;

public partial class App : Application
{
    const string ExitEventName = "MacLook.Exit";

    internal static Config Config { get; private set; } = null!;
    internal static DockWindow Dock { get; private set; } = null!;
    internal static LaunchpadWindow Launchpad { get; private set; } = null!;
    internal static SwitcherWindow Switcher { get; private set; } = null!;
    internal static StartMenuWindow StartMenu { get; private set; } = null!;
    internal static ControlCenterWindow ControlCenter { get; private set; } = null!;
    internal static NotificationCenterWindow NotificationCenter { get; private set; } = null!;
    internal static SettingsWindow Settings { get; private set; } = null!;
    Mutex? singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // MacLook.exe --exit            closes a running instance cleanly
        // MacLook.exe --restore-taskbar brings the Windows taskbar back after a crash
        if (e.Args.Contains("--exit"))
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var running)) running.Set();
            Shutdown();
            return;
        }
        if (e.Args.Contains("--restore-taskbar"))
        {
            Taskbar.Restore();
            Shutdown();
            return;
        }
        // MacLook.exe --snapshot <folder>  renders the panels to PNGs without showing anything
        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot")
        {
            Theme.Init(Resources);
            Wallpaper.Init();
            Config = Config.Load();
            _ = Snapshot.RunAsync(e.Args[1]);
            return;
        }

        singleInstance = new Mutex(true, "MacLook.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        new Thread(() =>
        {
            exitEvent.WaitOne();
            Dispatcher.InvokeAsync(() => Quit("--exit"));
        }) { IsBackground = true }.Start();

        // Never leave the user without a taskbar.
        DispatcherUnhandledException += (_, a) => { Log("crash: " + a.Exception); TrayHost.Stop(); Taskbar.Restore(); };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Taskbar.Restore();
        SystemEvents.SessionEnding += (_, _) => { TrayHost.Stop(); Taskbar.Restore(); };

        Theme.Init(Resources);
        Wallpaper.Init();
        Config = Config.Load();
        Taskbar.Hide();
        TrayHost.Start(); // after hiding: it adds its own tray window that apps then talk to

        new MenuBarWindow().Show();
        Dock = new DockWindow();
        Dock.Show();
        Launchpad = new LaunchpadWindow();
        new WindowInteropHelper(Launchpad).EnsureHandle(); // so the first open is instant
        Switcher = new SwitcherWindow();
        StartMenu = new StartMenuWindow();
        new WindowInteropHelper(StartMenu).EnsureHandle();
        ControlCenter = new ControlCenterWindow();
        new WindowInteropHelper(ControlCenter).EnsureHandle();
        NotificationCenter = new NotificationCenterWindow();
        new WindowInteropHelper(NotificationCenter).EnsureHandle();
        Settings = new SettingsWindow();

        AppCatalog.Refresh();
        Shortcuts.Warm();
        KeyboardHook.Start(Dispatcher, Config);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        KeyboardHook.Stop();
        TrayHost.Stop();
        Taskbar.Restore();
        base.OnExit(e);
    }

    internal static void Quit(string reason)
    {
        Log("exit: " + reason);
        Current.Shutdown();
    }

    internal static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(Config.Dir, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}\n"); }
        catch { }
    }
}
