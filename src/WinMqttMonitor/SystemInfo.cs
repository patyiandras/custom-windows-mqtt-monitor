using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinMqttMonitor
{
    /// <summary>Rendszeradatok gyűjtése. Minden metódus hibatűrő: ami nem olvasható, az null / kimarad.</summary>
    public class SystemInfo
    {
        public string Hostname = Environment.MachineName;
        public string CpuName, OsName, OsVersion, Manufacturer, Model, Serial;
        public bool HasCpuTemp, HasGpu, HasBattery, HasWifi;
        string nvidiaSmi;

        public SystemInfo(Config cfg)
        {
            CpuName = RegString(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim();
            var cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            var build = RegString(cv, "CurrentBuild") ?? "";
            var ubr = Registry.LocalMachine.OpenSubKey(cv)?.GetValue("UBR");
            var product = RegString(cv, "ProductName") ?? "Windows";
            // A Windows 11 a ProductName-ben is „Windows 10”-et ír; a build-szám dönt.
            if (int.TryParse(build, out var b) && b >= 22000) { product = product.Replace("Windows 10", "Windows 11"); }
            OsName = product;
            OsVersion = $"{RegString(cv, "DisplayVersion") ?? RegString(cv, "ReleaseId")} (build {build}{(ubr != null ? "." + ubr : "")})";
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject o in s.Get()) { Manufacturer = o["Manufacturer"]?.ToString().Trim(); Model = o["Model"]?.ToString().Trim(); }
                }
                using (var s = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS"))
                {
                    foreach (ManagementObject o in s.Get()) { Serial = o["SerialNumber"]?.ToString().Trim(); }
                }
                if (Placeholder(Manufacturer))
                {
                    using (var s = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_BaseBoard"))
                    {
                        foreach (ManagementObject o in s.Get()) { Manufacturer = o["Manufacturer"]?.ToString().Trim(); }
                    }
                }
                if (Placeholder(Manufacturer)) { Manufacturer = null; }
                if (Placeholder(Model)) { Model = null; }
                if (Placeholder(Serial)) { Serial = null; }
            }
            catch (Exception ex) { Log.Warn("WMI Win32_ComputerSystem: " + ex.Message); }

            HasCpuTemp = cfg.CpuTemp && CpuTemp() != null;
            if (cfg.Gpu)
            {
                nvidiaSmi = new[]
                {
                    Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvidia-smi.exe"),
                }.FirstOrDefault(File.Exists);
                HasGpu = nvidiaSmi != null && Gpu() != null;
            }
            HasBattery = Native.GetSystemPowerStatus(out var ps) && ps.BatteryFlag != 128 && ps.BatteryFlag != 255;
            HasWifi = PrimaryInterface()?.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            CpuUsage(); // első minta, hogy az első valódi érték már különbségből jöjjön
        }

        /// <summary>Az alaplapgyártók kitöltetlen DMI-mezői („To Be Filled By O.E.M.”, „Default string”, …).</summary>
        static bool Placeholder(string v) =>
            string.IsNullOrWhiteSpace(v) || Regex.IsMatch(v, @"o\.?e\.?m|default string|system (serial number|product name|manufacturer)|^none$|^0+$", RegexOptions.IgnoreCase);

        static string RegString(string key, string name)
        {
            try { return Registry.LocalMachine.OpenSubKey(key)?.GetValue(name)?.ToString(); }
            catch (Exception) { return null; }
        }

        // --- CPU ------------------------------------------------------------------
        ulong lastIdle, lastTotal;

        /// <summary>CPU-terhelés % az előző hívás óta (GetSystemTimes: nyelvfüggetlen, nincs perf-counter).</summary>
        public double? CpuUsage()
        {
            if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) { return null; }
            ulong total = kernel.Value + user.Value; // a kernel-idő tartalmazza az idle-t
            ulong dIdle = idle.Value - lastIdle, dTotal = total - lastTotal;
            bool first = lastTotal == 0;
            lastIdle = idle.Value; lastTotal = total;
            if (first || dTotal == 0) { return null; }
            return Math.Round(100.0 * (dTotal - dIdle) / dTotal, 1);
        }

        /// <summary>ACPI hőzóna (°C). Sok asztali alaplap nem adja, vagy fix értéket ad — ezért kikapcsolható.</summary>
        public double? CpuTemp()
        {
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                {
                    double max = double.MinValue;
                    foreach (ManagementObject o in s.Get())
                    {
                        max = Math.Max(max, Convert.ToDouble(o["CurrentTemperature"]) / 10.0 - 273.15);
                    }
                    return max > 0 && max < 150 ? Math.Round(max, 1) : (double?)null;
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>NVIDIA GPU nvidia-smi-vel (ha van). Más gyártóhoz nincs illesztőfüggetlen, rendszergazdai mód.</summary>
        public Dictionary<string, object> Gpu()
        {
            if (nvidiaSmi == null) { return null; }
            var output = Run(nvidiaSmi, "--query-gpu=name,temperature.gpu,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits", 10000);
            var parts = output?.Split('\n')[0].Split(',').Select(x => x.Trim()).ToArray();
            if (parts == null || parts.Length < 5) { return null; }
            double? Num(string x) => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
            return new Dictionary<string, object>
            {
                ["gpu_name"] = parts[0], ["gpu_temp"] = Num(parts[1]), ["gpu_usage"] = Num(parts[2]),
                ["gpu_mem_used_mb"] = Num(parts[3]), ["gpu_mem_total_mb"] = Num(parts[4]),
                ["gpu_mem_usage"] = Num(parts[3]) is double u && Num(parts[4]) is double t && t > 0 ? Math.Round(100 * u / t, 1) : (double?)null,
            };
        }

        // --- Memória, idő, akku ------------------------------------------------------
        public Dictionary<string, object> Memory()
        {
            var m = new Native.MEMORYSTATUSEX();
            if (!Native.GlobalMemoryStatusEx(m)) { return new Dictionary<string, object>(); }
            const double GB = 1024.0 * 1024 * 1024;
            double commitUsed = m.ullTotalPageFile - m.ullAvailPageFile;
            return new Dictionary<string, object>
            {
                ["mem_usage"] = Math.Round(100.0 * (m.ullTotalPhys - m.ullAvailPhys) / m.ullTotalPhys, 1),
                ["mem_used_gb"] = Math.Round((m.ullTotalPhys - m.ullAvailPhys) / GB, 2),
                ["mem_total_gb"] = Math.Round(m.ullTotalPhys / GB, 2),
                ["commit_usage"] = Math.Round(100.0 * commitUsed / m.ullTotalPageFile, 1),
                ["commit_used_gb"] = Math.Round(commitUsed / GB, 2),
                ["commit_limit_gb"] = Math.Round(m.ullTotalPageFile / GB, 2),
            };
        }

        /// <summary>Utolsó (kernel-)indulás. Gyorsindításnál a „leállítás” valójában hibernálás, ilyenkor ez nem változik.</summary>
        public static DateTime BootTime()
        {
            var t = DateTime.UtcNow.AddMilliseconds(-(double)Native.GetTickCount64());
            return new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, DateTimeKind.Utc);
        }

        const string PowerOnQuery =
            "*[System[(Provider[@Name='Microsoft-Windows-Kernel-Boot'] and EventID=27)" +
            " or (Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12)" +
            " or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=107)" +
            " or (Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1)]]";

        /// <summary>
        /// A legutóbbi tényleges bekapcsolás / ébredés a Rendszer-eseménynaplóból — ez a felhasználó szerinti
        /// „bekapcsolás”. A BootTime() (kernel-indulás) gyorsindításnál (Fast Startup) a leállítás után sem
        /// változik, mert a kernel ilyenkor csak hibernálódik; a Kernel-Boot 27 viszont minden indításkor
        /// bekerül a naplóba (BootType: 0 = hidegindítás, 1 = gyorsindítás, 2 = hibernálásból).
        /// Ha a napló nem olvasható: null.
        /// </summary>
        public static (DateTime utc, string kind)? LastPowerOn()
        {
            try
            {
                var query = new EventLogQuery("System", PathType.LogName, PowerOnQuery) { ReverseDirection = true };
                using (var reader = new EventLogReader(query))
                using (var ev = reader.ReadEvent())
                {
                    if (ev?.TimeCreated == null) { return null; }
                    string kind;
                    switch (ev.Id)
                    {
                        case 27:
                            var type = ev.Properties.Count > 0 ? Convert.ToInt32(ev.Properties[0].Value) : 0;
                            kind = type == 1 ? "gyorsindítás (Fast Startup)" : type == 2 ? "hibernálásból" : "hidegindítás";
                            break;
                        case 12: kind = "hidegindítás"; break;
                        default: kind = "felébredés alvásból"; break;
                    }
                    var t = ev.TimeCreated.Value.ToUniversalTime();
                    return (new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, DateTimeKind.Utc), kind);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Eseménynapló (bekapcsolás ideje) nem olvasható: " + ex.Message);
                return null;
            }
        }

        public Dictionary<string, object> Battery()
        {
            if (!HasBattery || !Native.GetSystemPowerStatus(out var ps)) { return new Dictionary<string, object>(); }
            return new Dictionary<string, object>
            {
                ["battery"] = ps.BatteryLifePercent <= 100 ? (int?)ps.BatteryLifePercent : null,
                ["on_ac"] = ps.ACLineStatus == 1,
                ["charging"] = (ps.BatteryFlag & 8) != 0,
            };
        }

        // --- Hálózat ---------------------------------------------------------------
        public static NetworkInterface PrimaryInterface()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                    .ThenByDescending(n => n.Speed)
                    .FirstOrDefault();
            }
            catch (Exception) { return null; }
        }

        string lastIface;
        long lastRx, lastTx;
        DateTime lastNet;

        public Dictionary<string, object> Network()
        {
            var d = new Dictionary<string, object>();
            var nic = PrimaryInterface();
            if (nic == null) { return d; }
            var ip = nic.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            d["ip"] = ip?.Address.ToString();
            d["mac"] = Mac(nic);
            d["iface"] = nic.Name;
            d["iface_desc"] = nic.Description;
            d["link_mbps"] = nic.Speed > 0 ? nic.Speed / 1000000 : 0;
            var stats = nic.GetIPStatistics();
            var now = DateTime.UtcNow;
            if (lastIface == nic.Id && lastNet != default(DateTime))
            {
                var sec = (now - lastNet).TotalSeconds;
                if (sec > 0)
                {
                    d["net_rx"] = Math.Round(Math.Max(0, stats.BytesReceived - lastRx) / 1024.0 / sec, 1);
                    d["net_tx"] = Math.Round(Math.Max(0, stats.BytesSent - lastTx) / 1024.0 / sec, 1);
                }
            }
            lastIface = nic.Id; lastRx = stats.BytesReceived; lastTx = stats.BytesSent; lastNet = now;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            {
                // netsh lokalizált („Signal” / „Jel”), de a százalékjeles sor nyelvfüggetlenül felismerhető.
                var output = Run("netsh", "wlan show interfaces", 5000) ?? "";
                var m = Regex.Match(output, @"^\s*\S[^:\r\n]*:\s*(\d{1,3})\s*%", RegexOptions.Multiline);
                if (m.Success) { d["wifi_signal"] = int.Parse(m.Groups[1].Value); }
                var ssid = Regex.Match(output, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline);
                if (ssid.Success) { d["wifi_ssid"] = ssid.Groups[1].Value.Trim(); }
            }
            return d;
        }

        public static string Mac(NetworkInterface nic)
        {
            var bytes = nic?.GetPhysicalAddress().GetAddressBytes();
            return bytes == null || bytes.Length != 6 ? null : string.Join(":", bytes.Select(x => x.ToString("x2")));
        }

        // --- Lemezek ---------------------------------------------------------------
        public static List<Dictionary<string, object>> Disks()
        {
            var list = new List<Dictionary<string, object>>();
            const double GB = 1024.0 * 1024 * 1024;
            foreach (var di in DriveInfo.GetDrives())
            {
                try
                {
                    if (di.DriveType != DriveType.Fixed || !di.IsReady || di.TotalSize == 0) { continue; }
                    list.Add(new Dictionary<string, object>
                    {
                        ["key"] = di.Name.Substring(0, 1).ToLowerInvariant(),
                        ["drive"] = di.Name.Substring(0, 2),
                        ["label"] = di.VolumeLabel,
                        ["fs"] = di.DriveFormat,
                        ["usage"] = Math.Round(100.0 * (di.TotalSize - di.TotalFreeSpace) / di.TotalSize, 1),
                        ["free_gb"] = Math.Round(di.TotalFreeSpace / GB, 1),
                        ["total_gb"] = Math.Round(di.TotalSize / GB, 1),
                    });
                }
                catch (Exception) { } // pl. BitLockerrel zárolt meghajtó
            }
            return list;
        }

        // --- Folyamatok ------------------------------------------------------------
        /// <summary>A legtöbb memóriát használó programok, név szerint összevonva (a böngésző sok folyamata egy sor).</summary>
        public static List<Dictionary<string, object>> TopProcesses(int n)
        {
            var all = Process.GetProcesses();
            try
            {
                return all.Where(p => p.Id != 0)
                    .Select(p => { try { return new { p.ProcessName, Mem = p.WorkingSet64 }; } catch (Exception) { return null; } })
                    .Where(x => x != null)
                    .GroupBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new { Name = g.Key, Count = g.Count(), Mem = g.Sum(x => x.Mem) })
                    .OrderByDescending(x => x.Mem)
                    .Take(n)
                    .Select(x => new Dictionary<string, object>
                    {
                        ["name"] = x.Name, ["count"] = x.Count, ["mem_mb"] = Math.Round(x.Mem / 1048576.0, 1),
                    })
                    .ToList();
            }
            finally
            {
                foreach (var p in all) { p.Dispose(); }
            }
        }

        // --- Bejelentkezett felhasználó ------------------------------------------------
        /// <summary>
        /// Az aktív felhasználói session: elsőként a konzolé (fizikai képernyő), ha ott nincs bejelentkezve senki,
        /// akkor az első aktív távoli (RDP) session. Bejelentkezés nélkül a user null.
        /// </summary>
        public static (int session, string user, bool remote) ActiveUser()
        {
            var console = Native.WTSGetActiveConsoleSessionId();
            if (console != 0xFFFFFFFF)
            {
                var user = Native.SessionString(console, Native.WTSUserName);
                if (!string.IsNullOrEmpty(user)) { return ((int)console, user, false); }
            }
            foreach (var s in Native.Sessions())
            {
                if (s.State != Native.WTSActive || s.SessionId == 0) { continue; }
                var user = Native.SessionString(s.SessionId, Native.WTSUserName);
                if (!string.IsNullOrEmpty(user)) { return ((int)s.SessionId, user, true); }
            }
            return (console == 0xFFFFFFFF ? -1 : (int)console, null, false);
        }

        // --- Windows Update ----------------------------------------------------------
        public static bool RebootRequired()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            using (var k2 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            {
                return k != null || k2 != null;
            }
        }

        /// <summary>Telepítésre váró frissítések (Windows Update Agent COM). Lassú (akár percek) — külön szálon fut.</summary>
        public static List<string> PendingUpdates()
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
            dynamic session = Activator.CreateInstance(type);
            dynamic searcher = session.CreateUpdateSearcher();
            searcher.Online = false; // a Windows saját, legutóbbi ellenőrzésének eredménye — nem indít letöltést
            dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
            var titles = new List<string>();
            int count = result.Updates.Count;
            for (int i = 0; i < count; i++) { titles.Add((string)result.Updates.Item(i).Title); }
            return titles;
        }

        // --- Segéd -------------------------------------------------------------------
        public static string Run(string file, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                };
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } return null; }
                    return output.Result;
                }
            }
            catch (Exception) { return null; }
        }
    }
}
