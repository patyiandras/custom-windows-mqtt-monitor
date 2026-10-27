using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WinMqttMonitor
{
    /// <summary>config.json (helye: %ProgramData%\WinMqttMonitor\config.json). Hiányzó kulcsnál az itteni alapérték él.</summary>
    public class Config
    {
        public string MqttHost = "192.168.0.137";
        public int MqttPort = 1883;
        public string MqttUser = "";
        public string MqttPassword = "";
        public bool MqttTls = false;
        public bool MqttTlsInsecure = false;
        public int MqttKeepalive = 30;
        public string DiscoveryPrefix = "homeassistant";
        public string BaseTopic = "win-mqtt-monitor";

        public string DeviceId = "pc";
        public string DeviceName = "Számítógép";
        public string WolMac = ""; // üres = az alapértelmezett átjárós hálózati kártya MAC-je

        public int FastInterval = 30;
        public int SlowInterval = 300;
        public int ProcessInterval = 60;
        public int UpdatesInterval = 14400;
        public int SessionInterval = 5;

        public int Countdown = 30;
        public bool AllowShutdown = true;
        public bool AllowRestart = true;
        public bool AllowSleep = true;
        public bool AllowHibernate = true;
        public int CommandGrace = 60;
        public int CommandCooldown = 10;
        public bool Simulate = false; // csak teszteléshez: az energiaművelet naplózódik, de nem hajtódik végre

        public List<string> Services = new List<string>();
        public bool ServiceRestart = true;

        public bool LegacyHttp = true;
        public int LegacyHttpPort = 7268;

        public bool CpuTemp = true;
        public bool Gpu = true;
        public bool WindowsUpdate = true;

        public static readonly string FilePath = Path.Combine(Ipc.DataDir, "config.json");

        public static Config Load(string path = null)
        {
            path = path ?? FilePath;
            var c = new Config();
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Nincs beállítófájl: {path} (minta: config.example.json)");
            }
            var root = Ipc.FromJson(File.ReadAllText(path));
            var m = Section(root, "mqtt");
            c.MqttHost = m.Str("host", c.MqttHost);
            c.MqttPort = (int)m.Num("port", c.MqttPort);
            c.MqttUser = m.Str("username", c.MqttUser);
            c.MqttPassword = m.Str("password", c.MqttPassword);
            c.MqttTls = m.Bool("tls", c.MqttTls);
            c.MqttTlsInsecure = m.Bool("tls_insecure", c.MqttTlsInsecure);
            c.MqttKeepalive = (int)m.Num("keepalive", c.MqttKeepalive);
            c.DiscoveryPrefix = m.Str("discovery_prefix", c.DiscoveryPrefix);
            c.BaseTopic = m.Str("base_topic", c.BaseTopic);

            var d = Section(root, "device");
            c.DeviceId = d.Str("id", c.DeviceId);
            c.DeviceName = d.Str("name", c.DeviceName);
            c.WolMac = d.Str("wol_mac", c.WolMac);

            var i = Section(root, "intervals");
            c.FastInterval = (int)i.Num("fast", c.FastInterval);
            c.SlowInterval = (int)i.Num("slow", c.SlowInterval);
            c.ProcessInterval = (int)i.Num("processes", c.ProcessInterval);
            c.UpdatesInterval = (int)i.Num("updates", c.UpdatesInterval);
            c.SessionInterval = (int)i.Num("session", c.SessionInterval);

            var p = Section(root, "power");
            c.Countdown = (int)p.Num("countdown", c.Countdown);
            c.AllowShutdown = p.Bool("allow_shutdown", c.AllowShutdown);
            c.AllowRestart = p.Bool("allow_restart", c.AllowRestart);
            c.AllowSleep = p.Bool("allow_sleep", c.AllowSleep);
            c.AllowHibernate = p.Bool("allow_hibernate", c.AllowHibernate);
            c.CommandGrace = (int)p.Num("command_grace", c.CommandGrace);
            c.CommandCooldown = (int)p.Num("command_cooldown", c.CommandCooldown);
            c.Simulate = p.Bool("simulate", c.Simulate);

            var s = Section(root, "services");
            if (s != null && s.TryGetValue("names", out var names) && names is IEnumerable list)
            {
                c.Services = list.Cast<object>().Select(Convert.ToString).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            }
            c.ServiceRestart = s.Bool("restart", c.ServiceRestart);

            var l = Section(root, "legacy_http");
            c.LegacyHttp = l.Bool("enabled", c.LegacyHttp);
            c.LegacyHttpPort = (int)l.Num("port", c.LegacyHttpPort);

            var se = Section(root, "sensors");
            c.CpuTemp = se.Bool("cpu_temp", c.CpuTemp);
            c.Gpu = se.Bool("gpu", c.Gpu);
            c.WindowsUpdate = se.Bool("windows_update", c.WindowsUpdate);
            return c;
        }

        static Dictionary<string, object> Section(Dictionary<string, object> root, string name) =>
            root.TryGetValue(name, out var v) ? v as Dictionary<string, object> : null;
    }
}
