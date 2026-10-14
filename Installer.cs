using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace MacLook;

/// <summary>
/// Installs the single-file MacLook.exe for the current user (no admin rights needed):
/// %LOCALAPPDATA%\Programs\MacLook, a Start menu shortcut and an entry under Settings → Apps to remove it.
/// </summary>
internal static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MacLook";

    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MacLook");
    static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MacLook.lnk");
    public static string InstalledExe => Path.Combine(Folder, "MacLook.exe");

    public static bool RunningInstalled => string.Equals(Environment.ProcessPath, InstalledExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>Only the single-file release build can be copied as one file.</summary>
    public static bool CanInstall => Environment.ProcessPath is string exe
                                     && !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "MacLook.dll"));

    public static void Install()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.Copy(Environment.ProcessPath!, InstalledExe, overwrite: true);

            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link = shell.CreateShortcut(Shortcut);
            link.TargetPath = InstalledExe;
            link.WorkingDirectory = Folder;
            link.Description = L.T("Windows'a macOS görünümü");
            link.Save();

            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "MacLook");
                key.SetValue("DisplayVersion", Updater.CurrentText);
                key.SetValue("Publisher", "MacLook");
                key.SetValue("DisplayIcon", InstalledExe);
                key.SetValue("InstallLocation", Folder);
                key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
                key.SetValue("NoModify", 1);
                key.SetValue("NoRepair", 1);
                key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024));
            }
            if (Autostart.Enabled) Autostart.Point(InstalledExe);
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.T("Kurulum tamamlanamadı:") + "\n" + ex.Message, "MacLook", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Carry on from the installed copy; it waits until this one has exited.
        Process.Start(new ProcessStartInfo(InstalledExe, "--relaunch") { UseShellExecute = true });
        App.Quit("kuruldu");
    }

    /// <summary>MacLook.exe --uninstall (from Settings → Apps).</summary>
    public static void Uninstall()
    {
        if (MessageBox.Show(L.T("MacLook bu bilgisayardan kaldırılsın mı?"), "MacLook", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        Taskbar.Restore();
        Autostart.Enabled = false;
        try { File.Delete(Shortcut); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        // A running program can't delete itself: a hidden command removes the folder once we have exited.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{Folder}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        MessageBox.Show(L.T("MacLook kaldırıldı. Ayarların %APPDATA%\\MacLook klasöründe duruyor; istersen silebilirsin."), "MacLook");
    }
}
