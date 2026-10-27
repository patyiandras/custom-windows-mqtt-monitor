using System;
using System.Globalization;
using System.ServiceProcess;

namespace WinMqttMonitor
{
    static class Program
    {
        /// <summary>
        /// Szolgáltatásként indul (install.ps1 regisztrálja). Kézi futtatás, hibakereséshez:
        ///   WinMqttMonitor.exe --console [config.json]   valódi MQTT-kapcsolattal, a konzolra is naplóz
        ///   WinMqttMonitor.exe --dump [config.json]      MQTT nélkül kiírja a discovery-t és egy adatkört
        ///   WinMqttMonitor.exe --remove [config.json]    törli az eszközt a HA-ból (uninstall.ps1 hívja)
        /// </summary>
        static int Main(string[] args)
        {
            // A JSON-ba és a szöveges állapotokba tizedespont kerüljön, ne a magyar tizedesvessző.
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            if (args.Length > 0 && args[0] == "--remove")
            {
                Log.ToConsole = true;
                new Agent(Config.Load(args.Length > 1 ? args[1] : null)).RemoveDiscovery();
                return 0;
            }
            if (args.Length > 0 && (args[0] == "--console" || args[0] == "--dump"))
            {
                Log.ToConsole = true;
                var cfg = Config.Load(args.Length > 1 ? args[1] : null);
                var agent = new Agent(cfg, dryRun: args[0] == "--dump");
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; agent.Stop(); };
                agent.Run();
                return 0;
            }
            ServiceBase.Run(new MonitorService());
            return 0;
        }
    }
}
