using System;
using System.Globalization;
using System.Linq;
using System.ServiceProcess;
using System.Text;

namespace WinMqttMonitor
{
    static class Program
    {
        /// <summary>
        /// Szolgáltatásként indul. Kézi futtatás:
        ///   WinMqttMonitor.exe                            (dupla kattintás) telepítés / frissítés — UAC után
        ///   WinMqttMonitor.exe --uninstall [--purge]      eltávolítás (a HA-ból is törli az eszközt; --purge: config, napló is)
        ///   WinMqttMonitor.exe --console [config.json]    valódi MQTT-kapcsolattal, a konzolra is naplóz
        ///   WinMqttMonitor.exe --dump [config.json]       MQTT nélkül kiírja a discovery-t és egy adatkört
        ///   WinMqttMonitor.exe --remove [config.json]     csak a HA-eszköz törlése
        /// </summary>
        static int Main(string[] args)
        {
            // A JSON-ba és a szöveges állapotokba tizedespont kerüljön, ne a magyar tizedesvessző.
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var cmd = args.FirstOrDefault() ?? "";
            if (Environment.UserInteractive && (cmd == "" || cmd == "--install" || cmd == "--uninstall"))
            {
                Console.OutputEncoding = Encoding.UTF8;
                if (Installer.Elevate(string.Join(" ", args))) { return 0; }
                return cmd == "--uninstall" ? Installer.Uninstall(args.Contains("--purge")) : Installer.Install();
            }
            if (cmd == "--remove")
            {
                Log.ToConsole = true;
                new Agent(Config.Load(args.Length > 1 ? args[1] : null)).RemoveDiscovery();
                return 0;
            }
            if (cmd == "--console" || cmd == "--dump")
            {
                Log.ToConsole = true;
                var cfg = Config.Load(args.Length > 1 ? args[1] : null);
                var agent = new Agent(cfg, dryRun: cmd == "--dump");
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; agent.Stop(); };
                agent.Run();
                return 0;
            }
            ServiceBase.Run(new MonitorService());
            return 0;
        }
    }
}
