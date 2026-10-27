using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;

namespace WinMqttMonitor
{
    /// <summary>
    /// Telepítés / frissítés / eltávolítás magából a programból (korábban install.ps1 + install.cmd volt — a
    /// „PowerShell -ExecutionPolicy Bypass -Verb RunAs” indítót a vírusirtók gépi tanulásos heurisztikája
    /// trójainak jelölte). Dupla kattintás a WinMqttMonitor.exe-re = telepítés/frissítés (UAC után).
    ///   - binárisok: C:\Program Files\WinMqttMonitor; beállítás, napló, IPC: C:\ProgramData\WinMqttMonitor
    ///   - szolgáltatás: WinMqttMonitor (automatikus, hibánál újraindul); a tálcás segéd: HKLM\...\Run
    ///   - a régi „TCP Listener service” és a ShutdownDaemon automatikus indítása kivezetve
    /// </summary>
    static class Installer
    {
        static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinMqttMonitor");
        const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string FirewallRule = "WinMqttMonitor - SmartThings PC Control";
        static readonly string[] Files = { "WinMqttMonitor.exe", "WinMqttMonitor.exe.config", "WinMqttMonitorTray.exe", "WinMqttMonitorTray.exe.config", "MQTTnet.dll" };

        public static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        /// <summary>Ha nem rendszergazda: újraindítja magát emelt jogokkal (UAC), és true-t ad (a hívó lépjen ki).</summary>
        public static bool Elevate(string args)
        {
            if (IsAdmin()) { return false; }
            try
            {
                Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, args) { Verb = "runas", UseShellExecute = true });
            }
            catch (Exception) { Console.WriteLine("A telepítéshez rendszergazdai jóváhagyás kell."); Pause(); }
            return true;
        }

        public static int Install()
        {
            var src = AppDomain.CurrentDomain.BaseDirectory;
            Say($"Windows MQTT Monitor {Agent.Version} telepítése / frissítése");
            Directory.CreateDirectory(Ipc.Dir);
            Directory.CreateDirectory(Path.Combine(Ipc.DataDir, "logs"));

            // --- Beállítás ---
            if (!File.Exists(Config.FilePath))
            {
                var example = Path.Combine(src, "config.example.json");
                if (!File.Exists(example)) { return Fail($"Hiányzik: {example}"); }
                File.Copy(example, Config.FilePath);
                ProtectConfig();
                Say($"Létrehozva: {Config.FilePath}");
                Say("Írd bele az MQTT-jelszót (és ha kell, a gép nevét), mentsd el, majd indítsd újra a telepítést.");
                Process.Start("notepad.exe", $"\"{Config.FilePath}\"");
                Pause();
                return 1;
            }
            if (File.ReadAllText(Config.FilePath).Contains("IDE_A_JELSZO")) { return Fail($"A {Config.FilePath} még a minta-jelszót tartalmazza."); }
            Config cfg;
            try { cfg = Config.Load(); }
            catch (Exception ex) { return Fail("A config.json hibás: " + ex.Message); }
            ProtectConfig();
            // Az ipc mappába a tálcás segéd (sima felhasználó) is ír: Users = módosítás
            var ipcAcl = Directory.GetAccessControl(Ipc.Dir);
            ipcAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(Ipc.Dir, ipcAcl);

            // --- A régi megoldás kivezetése ---
            if (ServiceExists("TCP Listener service"))
            {
                Say("Régi „TCP Listener service” leállítása és törlése");
                StopService("TCP Listener service");
                Run("sc.exe", "delete \"TCP Listener service\"");
            }
            Kill("ShutdownDaemon", "TcpListener");
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using (var key = hive.OpenSubKey(RunKey, true))
                {
                    foreach (var name in key?.GetValueNames() ?? new string[0])
                    {
                        if ((key.GetValue(name) as string ?? "").IndexOf("ShutdownDaemon", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            key.DeleteValue(name);
                            Say($"Régi automatikus indítás törölve: {name}");
                        }
                    }
                }
            }

            // --- Binárisok ---
            bool exists = ServiceExists(MonitorService.Name);
            if (exists) { StopService(MonitorService.Name); }
            Kill("WinMqttMonitorTray");
            if (!string.Equals(Path.GetFullPath(src).TrimEnd('\\'), InstallDir, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(InstallDir);
                foreach (var f in Files)
                {
                    var from = Path.Combine(src, f);
                    if (!File.Exists(from)) { return Fail($"Hiányzik a csomagból: {f}"); }
                    CopyWithRetry(from, Path.Combine(InstallDir, f));
                }
            }
            // A letöltött csomag „internetről származik” jelölése (Zone.Identifier) ne kerüljön a telepített fájlokra
            foreach (var f in Files) { DeleteFile(Path.Combine(InstallDir, f) + ":Zone.Identifier"); }
            var exe = Path.Combine(InstallDir, "WinMqttMonitor.exe");

            // --- Szolgáltatás ---
            if (!exists)
            {
                Run("sc.exe", $"create {MonitorService.Name} binPath= \"\\\"{exe}\\\"\" start= auto DisplayName= \"Windows MQTT Monitor (Home Assistant)\"");
                Run("sc.exe", $"description {MonitorService.Name} \"A gép állapotát MQTT-n küldi a Home Assistantnak, és fogadja a leállítás/zárolás parancsokat. {Agent.RepoUrl}\"");
            }
            Run("sc.exe", $"failure {MonitorService.Name} reset= 86400 actions= restart/10000/restart/30000/restart/60000");

            // --- Tűzfal a régi SmartThings-végponthoz ---
            Run("netsh.exe", $"advfirewall firewall delete rule name=\"{FirewallRule}\"", quiet: true);
            if (cfg.LegacyHttp)
            {
                Run("netsh.exe", $"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow protocol=TCP localport={cfg.LegacyHttpPort} profile=private,domain");
            }

            // --- Tálcás segéd: minden bejelentkezéskor; most a szolgáltatás indítja el (a felhasználó jogaival) ---
            using (var key = Registry.LocalMachine.OpenSubKey(RunKey, true))
            {
                key.SetValue("WinMqttMonitorTray", $"\"{Path.Combine(InstallDir, "WinMqttMonitorTray.exe")}\"");
            }
            using (var sc = new ServiceController(MonitorService.Name))
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }
            Say("Kész: a szolgáltatás fut, a tálcás segéd pár másodpercen belül megjelenik.");
            Say($"Napló: {Path.Combine(Ipc.DataDir, "logs", "monitor.log")}");
            Pause();
            return 0;
        }

        public static int Uninstall(bool purge)
        {
            Say("Windows MQTT Monitor eltávolítása");
            if (ServiceExists(MonitorService.Name))
            {
                StopService(MonitorService.Name);
                try { new Agent(Config.Load()).RemoveDiscovery(); } catch (Exception ex) { Say("A HA-eszköz törlése nem sikerült: " + ex.Message); }
                Run("sc.exe", $"delete {MonitorService.Name}");
            }
            Kill("WinMqttMonitorTray");
            using (var key = Registry.LocalMachine.OpenSubKey(RunKey, true)) { key?.DeleteValue("WinMqttMonitorTray", false); }
            Run("netsh.exe", $"advfirewall firewall delete rule name=\"{FirewallRule}\"", quiet: true);
            // A futó exe saját magát nem tudja törölni: ha a telepített példányból fut, a mappa a következő indításkor marad
            foreach (var f in Files) { try { File.Delete(Path.Combine(InstallDir, f)); } catch (Exception) { } }
            try { Directory.Delete(InstallDir); } catch (Exception) { }
            if (purge) { try { Directory.Delete(Ipc.DataDir, true); } catch (Exception) { } }
            Say("Eltávolítva.");
            Pause();
            return 0;
        }

        static void ProtectConfig()
        {
            // A config.json-ban MQTT-jelszó van: csak a SYSTEM és a rendszergazdák olvashatják
            var acl = new FileSecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            File.SetAccessControl(Config.FilePath, acl);
        }

        static bool ServiceExists(string name) => ServiceController.GetServices().Any(s => s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase));

        static void StopService(string name)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    if (sc.Status == ServiceControllerStatus.Stopped) { return; }
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
            }
            catch (Exception ex) { Say($"{name} leállítása: {ex.Message}"); }
        }

        static void Kill(params string[] names)
        {
            foreach (var p in names.SelectMany(Process.GetProcessesByName))
            {
                try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { }
            }
        }

        static void CopyWithRetry(string from, string to)
        {
            for (int i = 0; ; i++)
            {
                try { File.Copy(from, to, true); return; }
                catch (IOException) when (i < 10) { Thread.Sleep(500); } // a leállított folyamat még fogja a fájlt
            }
        }

        static void Run(string file, string args, bool quiet = false)
        {
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using (var p = Process.Start(psi))
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0 && !quiet) { Say($"{file} {args.Split(' ')[0]}: {output.Trim()}"); }
            }
        }

        static int Fail(string msg)
        {
            Say("HIBA: " + msg);
            Pause();
            return 1;
        }

        static void Say(string msg) { Console.WriteLine(msg); Log.Info("Telepítő: " + msg); }

        static void Pause()
        {
            if (!Console.IsInputRedirected) { Console.WriteLine("Nyomj Entert a bezáráshoz..."); Console.ReadLine(); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool DeleteFile(string path);
    }
}
