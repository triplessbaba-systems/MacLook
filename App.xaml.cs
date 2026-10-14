using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MacLook;

public partial class App : Application
{
    const string ExitEventName = "MacLook.Exit";

    internal static Config Config { get; private set; } = null!;
    /// <summary>Null before the settings are loaded (e.g. while parsing command-line options).</summary>
    internal static Config? ConfigOrNull => Config;
    internal static readonly List<MenuBarWindow> MenuBars = new();
    internal static readonly List<DockWindow> Docks = new();
    /// <summary>The primary screen's bar.</summary>
    internal static DockWindow Dock => Docks[0];
    internal static LaunchpadWindow Launchpad { get; private set; } = null!;
    internal static SwitcherWindow Switcher { get; private set; } = null!;
    internal static StartMenuWindow StartMenu { get; private set; } = null!;
    internal static ControlCenterWindow ControlCenter { get; private set; } = null!;
    internal static NotificationCenterWindow NotificationCenter { get; private set; } = null!;
    internal static SettingsWindow Settings { get; private set; } = null!;
    internal static SpotlightWindow Spotlight { get; private set; } = null!;
    internal static MissionControlWindow MissionControl { get; private set; } = null!;
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
        // MacLook.exe --snapshot <folder> [tr|en]  renders the panels to PNGs without showing anything
        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot")
        {
            if (e.Args.Length >= 3) L.Override = e.Args[2];
            Theme.Init(Resources);
            Wallpaper.Init();
            Config = Config.Load();
            _ = Snapshot.RunAsync(e.Args[1]);
            return;
        }
        // MacLook.exe --uninstall  (Settings → Apps → MacLook → Uninstall)
        if (e.Args.Contains("--uninstall"))
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var running)) running.Set();
            Thread.Sleep(1500); // let a running copy close and give the taskbar back
            Installer.Uninstall();
            Shutdown();
            return;
        }

        singleInstance = new Mutex(false, "MacLook.SingleInstance");
        // --relaunch (after installing): the old copy is still closing, so wait for it a little.
        bool isFirst;
        try { isFirst = singleInstance.WaitOne(e.Args.Contains("--relaunch") ? 8000 : 0); }
        catch (AbandonedMutexException) { isFirst = true; }
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
        Screens.Init();
        Wallpaper.Init();
        Config = Config.Load();
        Taskbar.Hide();
        TrayHost.Start(); // after hiding: it adds its own tray window that apps then talk to

        RebuildBars();
        Screens.Changed += RebuildBars; // a monitor was plugged in, removed or rearranged
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
        Spotlight = new SpotlightWindow();
        new WindowInteropHelper(Spotlight).EnsureHandle();
        MissionControl = new MissionControlWindow();
        new WindowInteropHelper(MissionControl).EnsureHandle();
        HotCorners.Start();

        AppCatalog.Refresh();
        Shortcuts.Warm();
        KeyboardHook.Start(Dispatcher, Config);

        // Once things have settled: give back start-up memory and look for a newer version.
        var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        later.Tick += async (_, _) =>
        {
            later.Stop();
            TrimMemory();
            await Updater.CheckAsync();
        };
        later.Start();

        if (e.Args.Contains("--settings"))
            Dispatcher.InvokeAsync(() => Settings.Open(), DispatcherPriority.ApplicationIdle);
    }

    static DispatcherTimer? trimTimer;

    /// <summary>Hands memory that big panels (Launchpad, Mission Control) used back to Windows, a few seconds after they close.</summary>
    internal static void TrimMemorySoon()
    {
        trimTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        trimTimer.Tick -= OnTrim;
        trimTimer.Tick += OnTrim;
        trimTimer.Stop();
        trimTimer.Start();
    }

    static void OnTrim(object? sender, EventArgs e)
    {
        trimTimer!.Stop();
        TrimMemory();
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    static extern bool EmptyWorkingSet(IntPtr process);

    static void TrimMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        KeyboardHook.Stop();
        TrayHost.Stop();
        Taskbar.Restore();
        base.OnExit(e);
    }

    /// <summary>(Re)creates a menu bar and a bar / Dock on every screen, e.g. after a settings or monitor change.</summary>
    internal static void RebuildBars()
    {
        foreach (var window in MenuBars.Cast<Window>().Concat(Docks)) window.Close();
        MenuBars.Clear();
        Docks.Clear();
        foreach (var screen in Screens.All())
        {
            var menuBar = new MenuBarWindow(screen, screen.Primary);
            menuBar.Show();
            MenuBars.Add(menuBar);
            var dock = new DockWindow(screen, screen.Primary);
            dock.Show();
            Docks.Add(dock);
        }
    }

    /// <summary>Pinned apps changed: update the bars on every screen.</summary>
    internal static void RefreshDocks()
    {
        foreach (var dock in Docks) dock.Refresh();
    }

    internal static void DismissPanels()
    {
        Launchpad.Dismiss();
        StartMenu.Dismiss();
        ControlCenter.Dismiss();
        NotificationCenter.Dismiss();
        Spotlight?.Dismiss();
    }

    /// <summary>Starts a new copy (it waits for this one to close) and quits, e.g. after changing the language.</summary>
    internal static void Restart(bool openSettings = false)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, openSettings ? "--relaunch --settings" : "--relaunch") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("restart failed: " + ex.Message);
            return;
        }
        Quit("yeniden başlatma");
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
