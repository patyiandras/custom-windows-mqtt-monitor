using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace WinMqttMonitor
{
    /// <summary>
    /// A monitor magja: adatgyűjtés, MQTT (Home Assistant discovery), parancsok, függő energiaműveletek.
    /// Egyetlen fő szálon fut; az MQTT-üzenetek, az energiaesemények és a régi HTTP-végpont sorba kerülnek.
    /// </summary>
    public class Agent
    {
        public const string Version = "0.1.7";
        public const string RepoUrl = "https://github.com/patyiandras/custom-windows-mqtt-monitor";

        readonly Config cfg;
        readonly SystemInfo sys;
        readonly string dev, baseTopic, availTopic, discPrefix;
        readonly BlockingCollection<(string ev, string arg, string payload)> queue = new BlockingCollection<(string, string, string)>();
        readonly Dictionary<string, string> lastPayload = new Dictionary<string, string>();
        readonly Dictionary<string, DateTime> lastCmd = new Dictionary<string, DateTime>();
        readonly DateTime started = DateTime.UtcNow;
        readonly bool dryRun;
        IMqttClient client;
        MqttClientOptions options;
        volatile bool running = true;
        DateTime nextConnect = DateTime.MinValue;
        int connectBackoff = 1;
        DateTime nextFast, nextSlow, nextProc, nextSession, nextUpdates;
        readonly DateTime kernelBoot = SystemInfo.BootTime(); // a monitor indulásakor; gyorsindításnál régi lehet
        Dictionary<string, Dictionary<string, object>> discovery = new Dictionary<string, Dictionary<string, object>>();
        string discoveryShape;
        List<Dictionary<string, object>> disks = new List<Dictionary<string, object>>();
        Dictionary<string, string> serviceKeys = new Dictionary<string, string>(); // kulcs -> szolgáltatásnév
        PowerAction pending;
        DateTime? revertOnlineAt; // ha a leállítást egy program megakasztotta, újra „online”
        Task<List<string>> updatesTask;
        LegacyHttp legacy;

        class PowerAction
        {
            public string Action, Label;
            public DateTime Deadline;
        }

        public Agent(Config cfg, bool dryRun = false)
        {
            this.cfg = cfg;
            this.dryRun = dryRun;
            dev = Regex.Replace(cfg.DeviceId.ToLowerInvariant(), "[^a-z0-9_]", "_");
            baseTopic = $"{cfg.BaseTopic}/{dev}";
            availTopic = $"{baseTopic}/status";
            discPrefix = cfg.DiscoveryPrefix;
            sys = new SystemInfo(cfg);
            disks = SystemInfo.Disks();
            foreach (var name in cfg.Services) { serviceKeys[Slug(name)] = name; }
        }

        static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');

        static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss+00:00");

        // --- Életciklus --------------------------------------------------------------
        public void Stop()
        {
            running = false;
            queue.Add(("stop", null, null));
        }

        public void OnSuspend()
        {
            // Alvás/hibernálás előtt: a HA azonnal „kikapcsolt”-at lásson, ne csak a keepalive lejártakor.
            Log.Info("Energiaesemény: alvó állapot");
            PublishNow(availTopic, "offline");
        }

        public void OnResume() => queue.Add(("resume", null, null));

        public void Run()
        {
            Log.Info($"custom-windows-mqtt-monitor {Version} indul ({cfg.DeviceName}, {sys.OsName} {sys.OsVersion})");
            Directory.CreateDirectory(Ipc.Dir);
            Ipc.DeleteSafe(Ipc.PendingFile); // indulás előtti, félbemaradt művelet nem futhat le utólag
            if (cfg.LegacyHttp)
            {
                legacy = new LegacyHttp(cfg.LegacyHttpPort, cmd => queue.Add(("legacy", cmd, null)));
                legacy.Start();
            }
            BuildDiscovery();
            if (dryRun)
            {
                foreach (var kv in discovery) { Console.WriteLine($"{kv.Key}\n  {Ipc.ToJson(kv.Value)}"); }
                CollectAll();
                if (cfg.WindowsUpdate)
                {
                    StartUpdateCheck();
                    try { updatesTask.Wait(); } catch (AggregateException) { }
                    PublishUpdates();
                }
                return;
            }
            client = new MqttFactory().CreateMqttClient();
            client.ApplicationMessageReceivedAsync += e =>
            {
                var msg = e.ApplicationMessage;
                var payload = msg.PayloadSegment.Count > 0 ? Encoding.UTF8.GetString(msg.PayloadSegment.Array, msg.PayloadSegment.Offset, msg.PayloadSegment.Count) : "";
                if (msg.Topic == $"{discPrefix}/status")
                {
                    if (payload == "online") { queue.Add(("ha_online", null, null)); }
                }
                else if (msg.Retain)
                {
                    Log.Warn("Megőrzött (retained) parancs eldobva: " + msg.Topic);
                }
                else
                {
                    queue.Add(("cmd", msg.Topic.Substring(baseTopic.Length + 5), payload));
                }
                return Task.CompletedTask;
            };
            client.DisconnectedAsync += e =>
            {
                if (running) { Log.Warn("MQTT-kapcsolat megszakadt: " + (e.Exception?.Message ?? e.Reason.ToString())); }
                return Task.CompletedTask;
            };
            options = BuildOptions();
            try
            {
                MainLoop();
            }
            finally
            {
                legacy?.Stop();
                try
                {
                    if (client.IsConnected)
                    {
                        PublishNow(availTopic, "offline");
                        client.DisconnectAsync().Wait(2000);
                    }
                }
                catch (Exception) { }
                Log.Info("Leállt.");
            }
        }

        /// <summary>Eltávolítás előtt: a HA-ból törli az eszközt (üres, megőrzött discovery-üzenetek).</summary>
        public void RemoveDiscovery()
        {
            BuildDiscovery();
            client = new MqttFactory().CreateMqttClient();
            client.ConnectAsync(BuildOptions()).GetAwaiter().GetResult();
            var topics = new HashSet<string>(discovery.Keys);
            try
            {
                var arr = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(DiscoveryStateFile)) as object[];
                foreach (var t in arr ?? new object[0]) { topics.Add(t.ToString()); }
            }
            catch (Exception) { }
            foreach (var t in topics) { Publish(t, "", force: true); }
            foreach (var t in new[] { "system", "system_slow", "session", "processes", "disks", "services", "updates", "pending", "last_command", "status", "wol" })
            {
                Publish($"{baseTopic}/{t}", "", force: true);
            }
            client.DisconnectAsync().Wait(2000);
            Ipc.DeleteSafe(DiscoveryStateFile);
            Log.Info($"{topics.Count} discovery-téma törölve a HA-ból");
        }

        MqttClientOptions BuildOptions()
        {
            var b = new MqttClientOptionsBuilder()
                .WithTcpServer(cfg.MqttHost, cfg.MqttPort)
                .WithClientId($"win-mqtt-monitor-{dev}")
                .WithCleanSession(true)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(cfg.MqttKeepalive))
                .WithTimeout(TimeSpan.FromSeconds(10))
                .WithWillTopic(availTopic)
                .WithWillPayload("offline")
                .WithWillRetain(true)
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
            if (!string.IsNullOrEmpty(cfg.MqttUser)) { b = b.WithCredentials(cfg.MqttUser, cfg.MqttPassword); }
            if (cfg.MqttTls)
            {
                b = b.WithTlsOptions(o =>
                {
                    o.UseTls();
                    if (cfg.MqttTlsInsecure) { o.WithCertificateValidationHandler(_ => true); }
                });
            }
            return b.Build();
        }

        void MainLoop()
        {
            while (running)
            {
                var now = DateTime.UtcNow;
                if (!client.IsConnected && now >= nextConnect) { Connect(); }
                if (client.IsConnected)
                {
                    try
                    {
                        if (now >= nextSession) { nextSession = now.AddSeconds(cfg.SessionInterval); CollectSession(); }
                        if (now >= nextFast) { nextFast = now.AddSeconds(cfg.FastInterval); CollectFast(); }
                        if (now >= nextProc) { nextProc = now.AddSeconds(cfg.ProcessInterval); CollectProcesses(); }
                        if (now >= nextSlow) { nextSlow = now.AddSeconds(cfg.SlowInterval); CollectSlow(); }
                        if (cfg.WindowsUpdate && now >= nextUpdates && updatesTask == null)
                        {
                            nextUpdates = now.AddSeconds(cfg.UpdatesInterval);
                            StartUpdateCheck();
                        }
                    }
                    catch (Exception ex) { Log.Error("Gyűjtési hiba", ex); } // egy hibás gyűjtés ne állítsa le
                }
                CheckPending();
                if (revertOnlineAt.HasValue && now >= revertOnlineAt.Value)
                {
                    revertOnlineAt = null;
                    Log.Warn("A gép a parancs után sem állt le — újra online");
                    Publish(availTopic, "online", force: true);
                }

                var next = new[] { nextSession, nextFast, nextProc, nextSlow, nextConnect }.Where(t => t > now).DefaultIfEmpty(now.AddSeconds(1)).Min();
                var wait = (int)Math.Max(100, Math.Min((next - DateTime.UtcNow).TotalMilliseconds, pending != null || updatesTask != null ? 500 : 5000));
                if (!queue.TryTake(out var item, wait)) { continue; }
                try
                {
                    switch (item.ev)
                    {
                        case "cmd": HandleCommand(item.arg, item.payload); break;
                        case "legacy": HandleCommand(item.arg, ""); break;
                        case "updates_done": PublishUpdates(); break;
                        case "ha_online": OnConnected(); break;
                        case "resume":
                            Log.Info("Energiaesemény: felébredés");
                            nextSlow = DateTime.UtcNow.AddSeconds(20); // a napló ébredés-bejegyzése kicsit később jelenik meg
                            try { client.DisconnectAsync().Wait(2000); } catch (Exception) { }
                            nextConnect = DateTime.MinValue;
                            break;
                    }
                }
                catch (Exception ex) { Log.Error($"Hiba az esemény feldolgozásában: {item.ev} {item.arg}", ex); }
            }
        }

        void Connect()
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    client.ConnectAsync(options, cts.Token).GetAwaiter().GetResult();
                }
                Log.Info($"MQTT csatlakozva: {cfg.MqttHost}:{cfg.MqttPort}");
                connectBackoff = 1;
                var subs = new MqttFactory().CreateSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic($"{baseTopic}/cmd/#").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .WithTopicFilter(f => f.WithTopic($"{discPrefix}/status").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .Build();
                client.SubscribeAsync(subs).GetAwaiter().GetResult();
                OnConnected();
            }
            catch (Exception ex)
            {
                Log.Warn($"MQTT-csatlakozás sikertelen ({cfg.MqttHost}:{cfg.MqttPort}): {ex.GetBaseException().Message}; újra {connectBackoff} mp múlva");
                nextConnect = DateTime.UtcNow.AddSeconds(connectBackoff);
                connectBackoff = Math.Min(60, connectBackoff * 2);
            }
        }

        /// <summary>Csatlakozáskor és a HA újraindulásakor: minden állapot és discovery újraküldése.</summary>
        void OnConnected()
        {
            lastPayload.Clear();
            Publish(availTopic, "online", force: true);
            SyncDiscovery();
            nextFast = nextSlow = nextProc = nextSession = DateTime.MinValue;
            PublishPending();
        }

        // --- MQTT-küldés -----------------------------------------------------------------
        void Publish(string topic, object payload, bool retain = true, bool force = false)
        {
            var data = payload as string ?? Ipc.ToJson(payload);
            if (!force && lastPayload.TryGetValue(topic, out var prev) && prev == data) { return; }
            if (dryRun) { Console.WriteLine($"{topic} {data}"); return; }
            if (client == null || !client.IsConnected) { return; }
            try
            {
                var msg = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(data).WithRetainFlag(retain)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();
                client.PublishAsync(msg).GetAwaiter().GetResult();
                lastPayload[topic] = data;
            }
            catch (Exception ex) { Log.Warn($"Küldés sikertelen ({topic}): {ex.Message}"); }
        }

        /// <summary>Más szálról (energiaesemény) hívható, rövid időkorláttal.</summary>
        void PublishNow(string topic, string payload)
        {
            try
            {
                if (client == null || !client.IsConnected) { return; }
                var msg = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithRetainFlag(true)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();
                client.PublishAsync(msg).Wait(2000);
            }
            catch (Exception) { }
        }

        // --- Discovery ------------------------------------------------------------------
        Dictionary<string, object> Device()
        {
            var d = new Dictionary<string, object>
            {
                ["identifiers"] = new[] { $"win_mqtt_monitor_{dev}" },
                ["name"] = cfg.DeviceName,
                ["manufacturer"] = sys.Manufacturer ?? "Microsoft",
                ["model"] = sys.Model ?? "PC",
                ["sw_version"] = $"{sys.OsName} {sys.OsVersion}",
                ["hw_version"] = sys.CpuName,
            };
            if (sys.Serial != null) { d["serial_number"] = sys.Serial; }
            var mac = SystemInfo.Mac(SystemInfo.PrimaryInterface());
            if (mac != null) { d["connections"] = new[] { new[] { "mac", mac } }; }
            return d;
        }

        void Entity(string component, string key, string name, Dictionary<string, object> opts, bool availability = true)
        {
            var uid = $"{dev}_{key}";
            var c = new Dictionary<string, object>
            {
                ["name"] = name, ["unique_id"] = uid, ["default_entity_id"] = $"{component}.{uid}",
                ["device"] = Device(),
                ["origin"] = new Dictionary<string, object> { ["name"] = "custom-windows-mqtt-monitor", ["sw_version"] = Version, ["support_url"] = RepoUrl },
            };
            if (availability) { c["availability_topic"] = availTopic; }
            foreach (var kv in opts) { if (kv.Value != null) { c[kv.Key] = kv.Value; } }
            discovery[$"{discPrefix}/{component}/{dev}/{key}/config"] = c;
        }

        static Dictionary<string, object> O(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) { d[(string)kv[i]] = kv[i + 1]; }
            return d;
        }

        static Dictionary<string, object> With(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            var d = new Dictionary<string, object>(a);
            foreach (var kv in b) { d[kv.Key] = kv.Value; }
            return d;
        }

        void Sensor(string key, string name, string topic, string field, Dictionary<string, object> opts = null) =>
            Entity("sensor", key, name, With(O("state_topic", topic, "value_template", $"{{{{ value_json.{field} }}}}"), opts ?? O()));

        void Binary(string key, string name, string topic, string field, Dictionary<string, object> opts = null) =>
            Entity("binary_sensor", key, name, With(O("state_topic", topic,
                "value_template", $"{{{{ 'ON' if value_json.{field} else 'OFF' }}}}"), opts ?? O()));

        void Button(string key, string name, string command, Dictionary<string, object> opts = null) =>
            Entity("button", key, name, With(O("command_topic", $"{baseTopic}/cmd/{command}", "payload_press", "PRESS", "retain", false, "qos", 1), opts ?? O()));

        void BuildDiscovery()
        {
            discovery = new Dictionary<string, Dictionary<string, object>>();
            string fast = $"{baseTopic}/system", slow = $"{baseTopic}/system_slow", sess = $"{baseTopic}/session";
            var meas = O("state_class", "measurement");
            var diag = O("entity_category", "diagnostic");

            // A „Computer” kapcsoló: be = a monitor online; ki = leállítás (visszaszámlálóval). Bekapcsolni (WOL) a HA tud.
            Entity("switch", "bekapcsolva", "Bekapcsolva", O(
                "state_topic", availTopic, "state_on", "online", "state_off", "offline",
                "command_topic", $"{baseTopic}/cmd/power", "payload_on", "ON", "payload_off", "OFF",
                "optimistic", false, "retain", false, "icon", "mdi:desktop-tower-monitor"), availability: false);

            Sensor("cpu_terheles", "CPU-terhelés", fast, "cpu_usage", With(meas, O("unit_of_measurement", "%", "icon", "mdi:cpu-64-bit")));
            if (sys.HasCpuTemp)
            {
                Sensor("cpu_homerseklet", "CPU hőmérséklet (ACPI)", fast, "cpu_temp", With(meas, O("unit_of_measurement", "°C",
                    "device_class", "temperature", "suggested_display_precision", 1)));
            }
            Sensor("memoria", "Memória-használat", fast, "mem_usage", With(meas, O("unit_of_measurement", "%", "icon", "mdi:memory",
                "json_attributes_topic", fast,
                "json_attributes_template", "{{ {'hasznalt_gb': value_json.mem_used_gb, 'osszes_gb': value_json.mem_total_gb} | tojson }}")));
            Sensor("memoria_veglegesitett", "Véglegesített memória", fast, "commit_usage", With(meas, O("unit_of_measurement", "%",
                "icon", "mdi:memory-arrow-down", "json_attributes_topic", fast,
                "json_attributes_template", "{{ {'hasznalt_gb': value_json.commit_used_gb, 'korlat_gb': value_json.commit_limit_gb} | tojson }}")));
            if (sys.HasGpu)
            {
                Sensor("gpu_homerseklet", "GPU hőmérséklet", fast, "gpu_temp", With(meas, O("unit_of_measurement", "°C", "device_class", "temperature")));
                Sensor("gpu_terheles", "GPU-terhelés", fast, "gpu_usage", With(meas, O("unit_of_measurement", "%", "icon", "mdi:expansion-card",
                    "json_attributes_topic", fast, "json_attributes_template", "{{ {'gpu': value_json.gpu_name} | tojson }}")));
                Sensor("gpu_memoria", "GPU-memória", fast, "gpu_mem_usage", With(meas, O("unit_of_measurement", "%", "icon", "mdi:expansion-card-variant",
                    "json_attributes_topic", fast,
                    "json_attributes_template", "{{ {'hasznalt_mb': value_json.gpu_mem_used_mb, 'osszes_mb': value_json.gpu_mem_total_mb} | tojson }}")));
            }
            Sensor("halozat_le", "Hálózat le", fast, "net_rx", With(meas, O("unit_of_measurement", "kB/s", "device_class", "data_rate")));
            Sensor("halozat_fel", "Hálózat fel", fast, "net_tx", With(meas, O("unit_of_measurement", "kB/s", "device_class", "data_rate")));
            Sensor("ip_cim", "IP-cím", fast, "ip", With(diag, O("icon", "mdi:ip-network", "json_attributes_topic", fast,
                "json_attributes_template", "{{ {'mac': value_json.mac, 'csatolo': value_json.iface, 'leiras': value_json.iface_desc, 'link_mbps': value_json.link_mbps} | tojson }}")));
            // A Wake-on-LAN-hoz: availability nélkül, megőrzött üzenetből — kikapcsolt gépnél is olvasható a HA-ban.
            Entity("sensor", "mac_cim", "MAC-cím (Wake-on-LAN)", With(diag, O("state_topic", $"{baseTopic}/wol",
                "value_template", "{{ value_json.mac }}", "icon", "mdi:lan-connect")), availability: false);
            if (sys.HasWifi)
            {
                Sensor("wifi_jel", "WiFi-jel", fast, "wifi_signal", With(With(meas, diag), O("unit_of_measurement", "%", "icon", "mdi:wifi",
                    "json_attributes_topic", fast, "json_attributes_template", "{{ {'ssid': value_json.wifi_ssid} | tojson }}")));
            }
            if (sys.HasBattery)
            {
                Sensor("akkumulator", "Akkumulátor", fast, "battery", With(meas, O("unit_of_measurement", "%", "device_class", "battery")));
                Binary("toltes", "Töltés", fast, "charging", O("device_class", "battery_charging"));
            }
            Sensor("bekapcsolas_ideje", "Bekapcsolás ideje", slow, "boot_time", O("device_class", "timestamp",
                "json_attributes_topic", slow,
                "json_attributes_template", "{{ {'mod': value_json.power_on_kind, 'kernel_indulas': value_json.kernel_boot} | tojson }}"));

            Sensor("felhasznalo", "Bejelentkezett felhasználó", sess, "user", O("icon", "mdi:account", "json_attributes_topic", sess,
                "json_attributes_template", "{{ {'session': value_json.session, 'tavoli': value_json.remote, 'talcas_seged': value_json.tray} | tojson }}"));
            Binary("hasznalatban", "Használatban", sess, "in_use", O("device_class", "occupancy"));
            Binary("zarolva", "Zárolva", sess, "locked", O("icon", "mdi:monitor-lock"));
            Entity("binary_sensor", "kijelzo", "Kijelző", O("state_topic", sess, "device_class", "power",
                "value_template", "{% if value_json.display in ['on', 'dimmed'] %}ON{% elif value_json.display == 'off' %}OFF{% else %}None{% endif %}",
                "json_attributes_topic", sess, "json_attributes_template", "{{ {'allapot': value_json.display} | tojson }}"));
            if (cfg.Foreground)
            {
                Sensor("elotter", "Előtérben lévő program", sess, "fg_app", O("icon", "mdi:application-outline",
                    "json_attributes_topic", sess, "json_attributes_template",
                    "{{ {'folyamat': value_json.fg_process, 'ablakcim': value_json.fg_title, 'teljes_kepernyo': value_json.fullscreen} | tojson }}"));
            }
            Sensor("tetlenseg", "Tétlenség", sess, "idle_min", With(meas, O("unit_of_measurement", "min", "device_class", "duration",
                "suggested_display_precision", 0)));

            var procs = $"{baseTopic}/processes";
            Sensor("memoria_top", "Memória top program", procs, "top", O("icon", "mdi:format-list-numbered", "json_attributes_topic", procs,
                "json_attributes_template", "{{ {'programok': value_json.processes, 'frissitve': value_json.updated} | tojson }}"));

            foreach (var d in disks)
            {
                var k = (string)d["key"];
                var t = $"{baseTopic}/disks";
                Sensor($"lemez_{k}", $"Lemez {((string)d["drive"])}", t, $"{k}.usage", With(meas, O("unit_of_measurement", "%", "icon", "mdi:harddisk",
                    "json_attributes_topic", t, "json_attributes_template", $"{{{{ value_json.{k} | tojson }}}}")));
            }

            if (cfg.WindowsUpdate)
            {
                var u = $"{baseTopic}/updates";
                Sensor("windows_frissitesek", "Windows-frissítések", u, "count", With(meas, O("icon", "mdi:microsoft-windows",
                    "json_attributes_topic", u, "json_attributes_template", "{{ {'frissitesek': value_json.titles, 'ellenorizve': value_json.checked} | tojson }}")));
            }
            Binary("ujrainditas_kell", "Újraindítás szükséges", slow, "reboot_required", O("icon", "mdi:restart-alert"));
            Sensor("windows_verzio", "Windows-verzió", slow, "os", With(diag, O("icon", "mdi:microsoft-windows")));
            Sensor("monitor_verzio", "Monitor verzió", slow, "agent_version", With(diag, O("icon", "mdi:tag")));
            Sensor("utolso_parancs", "Utolsó parancs", $"{baseTopic}/last_command", "state", O("icon", "mdi:console",
                "json_attributes_topic", $"{baseTopic}/last_command"));
            Sensor("fuggo_muvelet", "Függő művelet", $"{baseTopic}/pending", "state", O("icon", "mdi:timer-sand",
                "json_attributes_topic", $"{baseTopic}/pending"));

            if (cfg.AllowShutdown) { Button("leallitas", "Leállítás", "shutdown", O("icon", "mdi:power")); }
            if (cfg.AllowRestart) { Button("ujrainditas", "Újraindítás", "restart", O("device_class", "restart")); }
            if (cfg.AllowSleep && Native.IsPwrSuspendAllowed()) { Button("alvo_mod", "Alvó mód", "sleep", O("icon", "mdi:power-sleep")); }
            if (cfg.AllowHibernate && Native.IsPwrHibernateAllowed()) { Button("hibernalas", "Hibernálás", "hibernate", O("icon", "mdi:snowflake")); }
            Button("megszakitas", "Leállítás megszakítása", "cancel", O("icon", "mdi:cancel"));
            Button("zarolas", "Zárolás", "lock", O("icon", "mdi:monitor-lock"));
            Button("kepernyo_ki", "Képernyő ki", "monitor_off", O("icon", "mdi:monitor-off"));
            Button("frissites", "Adatok frissítése", "refresh", With(diag, O("icon", "mdi:refresh")));
            Entity("text", "uzenet", "Üzenet a képernyőre", O("command_topic", $"{baseTopic}/cmd/notify", "mode", "text",
                "min", 0, "max", 255, "retain", false, "icon", "mdi:message-text"));

            foreach (var kv in serviceKeys)
            {
                var t = $"{baseTopic}/services";
                Binary($"szolg_{kv.Key}", $"Szolgáltatás {kv.Value}", t, $"{kv.Key}.running", O("device_class", "running",
                    "json_attributes_topic", t, "json_attributes_template", $"{{{{ value_json.{kv.Key} | tojson }}}}"));
                if (cfg.ServiceRestart)
                {
                    Button($"szolg_{kv.Key}_ujrainditas", $"{kv.Value} újraindítása", $"service/{kv.Key}/restart", O("device_class", "restart"));
                }
            }
            discoveryShape = string.Join(",", disks.Select(d => (string)d["key"]));
        }

        static readonly string DiscoveryStateFile = Path.Combine(Ipc.DataDir, "discovery.json");

        /// <summary>Discovery küldése; ami az előző futásban még volt, most nincs (pl. kihúzott lemez), azt törli a HA-ból.</summary>
        void SyncDiscovery()
        {
            var previous = new List<string>();
            try
            {
                if (File.Exists(DiscoveryStateFile))
                {
                    var arr = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(DiscoveryStateFile)) as object[];
                    previous = (arr ?? new object[0]).Select(x => x.ToString()).ToList();
                }
            }
            catch (Exception) { }
            foreach (var kv in discovery) { Publish(kv.Key, kv.Value, force: true); }
            foreach (var old in previous.Except(discovery.Keys)) { Publish(old, "", force: true); }
            try { File.WriteAllText(DiscoveryStateFile, Ipc.ToJson(discovery.Keys.ToList())); } catch (Exception) { }
        }

        // --- Gyűjtés ---------------------------------------------------------------------
        void CollectAll()
        {
            CollectSession(); CollectFast(); CollectProcesses(); CollectSlow();
        }

        void CollectFast()
        {
            var d = new Dictionary<string, object> { ["cpu_usage"] = sys.CpuUsage() ?? 0 };
            if (sys.HasCpuTemp) { d["cpu_temp"] = sys.CpuTemp(); }
            foreach (var kv in sys.Memory()) { d[kv.Key] = kv.Value; }
            foreach (var kv in sys.Network()) { d[kv.Key] = kv.Value; }
            if (!d.ContainsKey("net_rx")) { d["net_rx"] = 0; d["net_tx"] = 0; }
            if (sys.HasGpu) { foreach (var kv in sys.Gpu() ?? new Dictionary<string, object>()) { d[kv.Key] = kv.Value; } }
            foreach (var kv in sys.Battery()) { d[kv.Key] = kv.Value; }
            Publish($"{baseTopic}/system", d);
        }

        void CollectProcesses()
        {
            var top = SystemInfo.TopProcesses(10);
            Publish($"{baseTopic}/processes", new Dictionary<string, object>
            {
                ["top"] = top.Count > 0 ? $"{top[0]["name"]} ({top[0]["mem_mb"]} MB)" : "–",
                ["processes"] = top,
                ["updated"] = Iso(DateTime.UtcNow),
            });
        }

        void CollectSlow()
        {
            disks = SystemInfo.Disks();
            if (string.Join(",", disks.Select(d => (string)d["key"])) != discoveryShape)
            {
                Log.Info("A lemezek köre megváltozott — discovery újraküldése");
                BuildDiscovery();
                SyncDiscovery();
            }
            Publish($"{baseTopic}/disks", disks.ToDictionary(d => (string)d["key"], d => (object)d));
            var powerOn = SystemInfo.LastPowerOn();
            Publish($"{baseTopic}/system_slow", new Dictionary<string, object>
            {
                ["boot_time"] = Iso(powerOn?.utc ?? kernelBoot),
                ["power_on_kind"] = powerOn?.kind ?? "ismeretlen",
                ["kernel_boot"] = Iso(kernelBoot),
                ["reboot_required"] = SafeBool(SystemInfo.RebootRequired),
                ["os"] = $"{sys.OsName} {sys.OsVersion}",
                ["agent_version"] = Version,
                ["hostname"] = sys.Hostname,
            });
            var mac = string.IsNullOrWhiteSpace(cfg.WolMac) ? SystemInfo.Mac(SystemInfo.PrimaryInterface()) : cfg.WolMac.Trim().ToLowerInvariant().Replace('-', ':');
            if (mac != null) { Publish($"{baseTopic}/wol", new Dictionary<string, object> { ["mac"] = mac }); }
            CollectServices();
        }

        static bool SafeBool(Func<bool> f)
        {
            try { return f(); } catch (Exception) { return false; }
        }

        void CollectServices()
        {
            if (serviceKeys.Count == 0) { return; }
            var d = new Dictionary<string, object>();
            foreach (var kv in serviceKeys)
            {
                try
                {
                    using (var sc = new ServiceController(kv.Value))
                    {
                        d[kv.Key] = new Dictionary<string, object>
                        {
                            ["name"] = sc.ServiceName, ["display_name"] = sc.DisplayName,
                            ["status"] = sc.Status.ToString(), ["running"] = sc.Status == ServiceControllerStatus.Running,
                        };
                    }
                }
                catch (Exception)
                {
                    d[kv.Key] = new Dictionary<string, object> { ["name"] = kv.Value, ["status"] = "NotFound", ["running"] = false };
                }
            }
            Publish($"{baseTopic}/services", d);
        }

        /// <summary>A session tálcás segédjének utolsó jelentése, ha friss (a segéd 10 mp-enként ír); egyébként null.</summary>
        static Dictionary<string, object> FreshTray(int session)
        {
            var tray = session >= 0 ? Ipc.ReadJson(Ipc.SessionFile(session)) : null;
            return tray != null && DateTime.TryParse(tray.Str("time"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                   && (DateTime.UtcNow - t.ToUniversalTime()).TotalSeconds < 60 ? tray : null;
        }

        /// <summary>Szolgáltatásként igaz: ilyenkor a hiányzó tálcás segédet a felhasználó sessionjében elindítja.</summary>
        public bool LaunchTray;
        readonly Dictionary<int, DateTime> trayLaunched = new Dictionary<int, DateTime>();

        void EnsureTray(int session)
        {
            if (!LaunchTray || session <= 0) { return; }
            if (trayLaunched.TryGetValue(session, out var last) && (DateTime.UtcNow - last).TotalMinutes < 2) { return; }
            trayLaunched[session] = DateTime.UtcNow;
            try
            {
                TrayLauncher.Launch(session);
                Log.Info($"Tálcás segéd elindítva a(z) {session}. sessionben");
            }
            catch (Exception ex) { Log.Warn($"A tálcás segéd nem indítható a(z) {session}. sessionben: {ex.Message}"); }
        }

        void CollectSession()
        {
            var (session, user, remote) = SystemInfo.ActiveUser();
            var tray = FreshTray(session);
            bool trayFresh = tray != null;
            if (user != null && !trayFresh) { EnsureTray(session); }
            bool locked = trayFresh && tray.Bool("locked");
            double idleSec = trayFresh ? tray.Num("idle_sec") : 0;
            // Teljes képernyős app (film, játék, prezentáció) bevitel nélkül is használat
            bool fullscreen = trayFresh && !locked && tray.Bool("fullscreen");
            // A kijelző (a Windows energiagazdálkodása vagy a „Képernyő ki” kapcsolta le): ilyenkor senki nem nézi a gépet,
            // a teljes képernyős program vagy egy friss bevitel ellenére sem „használatban”. Kivétel a távoli asztali
            // session: ott a felhasználó a saját gépén dolgozik, a konzol kijelzője közömbös.
            var display = trayFresh ? tray.Str("display") ?? "unknown" : "unknown";
            bool displayOff = display == "off" && !remote;
            bool active = user != null && trayFresh && !locked;
            string Cut(string v) => v == null ? null : v.Length > 250 ? v.Substring(0, 250) : v;
            Publish($"{baseTopic}/session", new Dictionary<string, object>
            {
                ["user"] = user ?? "nincs bejelentkezve",
                ["logged_in"] = user != null,
                ["session"] = session,
                ["remote"] = remote,
                ["tray"] = trayFresh,
                ["locked"] = user != null && locked,
                ["idle_min"] = user != null ? Math.Floor(idleSec / 60) : 0,
                ["in_use"] = user != null && !locked && !displayOff && (idleSec < 300 || fullscreen),
                ["display"] = display,
                ["fullscreen"] = fullscreen,
                ["fg_app"] = active ? Cut(tray.Str("fg_app")) ?? "–" : "–",
                ["fg_process"] = active ? tray.Str("fg_process") : null,
                ["fg_title"] = active && cfg.ForegroundTitle ? Cut(tray.Str("fg_title")) : null,
            });
        }

        void StartUpdateCheck()
        {
            updatesTask = Task.Run(() => SystemInfo.PendingUpdates());
            updatesTask.ContinueWith(_ => queue.Add(("updates_done", null, null)));
        }

        void PublishUpdates()
        {
            var task = updatesTask;
            updatesTask = null;
            if (task.IsFaulted)
            {
                Log.Warn("Windows Update lekérdezés sikertelen: " + task.Exception?.GetBaseException().Message);
                return;
            }
            Publish($"{baseTopic}/updates", new Dictionary<string, object>
            {
                ["count"] = task.Result.Count, ["titles"] = task.Result, ["checked"] = Iso(DateTime.UtcNow),
            });
        }

        // --- Parancsok -------------------------------------------------------------------
        void Report(string command, bool ok, string detail = "")
        {
            var state = $"{command} – {(ok ? "ok" : "hiba")}";
            if (ok) { Log.Info($"Parancs: {state} {detail}"); } else { Log.Warn($"Parancs: {state} {detail}"); }
            Publish($"{baseTopic}/last_command", new Dictionary<string, object>
            {
                ["state"] = state.Length > 250 ? state.Substring(0, 250) : state, ["command"] = command, ["ok"] = ok,
                ["detail"] = detail.Length > 500 ? detail.Substring(0, 500) : detail, ["time"] = Iso(DateTime.UtcNow),
            }, force: true);
        }

        static readonly Dictionary<string, string> PowerLabels = new Dictionary<string, string>
        {
            ["shutdown"] = "Leállítás", ["restart"] = "Újraindítás", ["sleep"] = "Alvó mód", ["hibernate"] = "Hibernálás",
        };

        void HandleCommand(string target, string payload)
        {
            var now = DateTime.UtcNow;
            if (target != "cancel" && target != "notify" && lastCmd.TryGetValue(target, out var last) && (now - last).TotalSeconds < cfg.CommandCooldown)
            {
                Log.Warn("Parancs túl sűrűn, eldobva: " + target);
                return;
            }
            lastCmd[target] = now;
            if (target == "power")
            {
                if (payload.Trim().ToUpperInvariant() == "OFF") { target = "shutdown"; }
                else { Report("Bekapcsolás", true, "a gép már be van kapcsolva"); return; }
            }
            if (PowerLabels.TryGetValue(target, out var label))
            {
                RequestPower(target, label);
                return;
            }
            var parts = target.Split('/');
            switch (target)
            {
                case "cancel":
                    if (pending == null) { Report("Megszakítás", false, "nincs függő művelet"); return; }
                    Ipc.DeleteSafe(Ipc.PendingFile);
                    CheckPending();
                    return;
                case "lock":
                    TrayCommand("Zárolás", new Dictionary<string, object> { ["action"] = "lock" });
                    return;
                case "monitor_off":
                    TrayCommand("Képernyő ki", new Dictionary<string, object> { ["action"] = "monitor_off" });
                    return;
                case "notify":
                    if (string.IsNullOrWhiteSpace(payload)) { return; }
                    // „Cím | szöveg” formában saját cím adható; egyébként az alapcím
                    var bar = payload.IndexOf('|');
                    var title = bar > 0 ? payload.Substring(0, bar).Trim() : "🏠 Üzenet otthonról";
                    var text = bar > 0 ? payload.Substring(bar + 1).Trim() : payload.Trim();
                    TrayCommand("Üzenet", new Dictionary<string, object> { ["action"] = "notify", ["title"] = title, ["text"] = text });
                    return;
                case "refresh":
                    lastPayload.Clear();
                    nextFast = nextSlow = nextProc = nextSession = DateTime.MinValue;
                    if (cfg.WindowsUpdate && updatesTask == null) { StartUpdateCheck(); }
                    Report("Adatok frissítése", true);
                    return;
            }
            if (parts.Length == 3 && parts[0] == "service" && parts[2] == "restart")
            {
                if (!serviceKeys.TryGetValue(parts[1], out var name)) { Report($"{parts[1]} újraindítás", false, "ismeretlen szolgáltatás"); return; }
                if (!cfg.ServiceRestart) { Report($"{name} újraindítás", false, "a config tiltja"); return; }
                try
                {
                    using (var sc = new ServiceController(name))
                    {
                        if (sc.Status != ServiceControllerStatus.Stopped)
                        {
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                        }
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    }
                    Report($"{name} újraindítás", true);
                }
                catch (Exception ex) { Report($"{name} újraindítás", false, ex.GetBaseException().Message); }
                CollectServices();
                return;
            }
            Log.Warn("Ismeretlen parancs: " + target);
        }

        /// <summary>Parancs a konzol-session tálcás segédjének (csak az tud zárolni, képernyőt kapcsolni, értesítést mutatni).</summary>
        void TrayCommand(string label, Dictionary<string, object> cmd)
        {
            var (session, user, _) = SystemInfo.ActiveUser();
            if (user == null) { Report(label, false, "nincs bejelentkezett felhasználó"); return; }
            if (FreshTray(session) == null)
            {
                EnsureTray(session);
                Report(label, false, "nem fut a tálcás segéd a felhasználó sessionjében (indítás folyamatban, próbáld újra)");
                return;
            }
            cmd["session"] = session;
            cmd["time"] = Iso(DateTime.UtcNow);
            Ipc.WriteJson(Path.Combine(Ipc.Dir, $"cmd-{DateTime.UtcNow.Ticks}.json"), cmd);
            Report(label, true, user);
        }

        void RequestPower(string action, string label)
        {
            bool allowed = action == "shutdown" ? cfg.AllowShutdown : action == "restart" ? cfg.AllowRestart
                : action == "sleep" ? cfg.AllowSleep : cfg.AllowHibernate;
            if (!allowed) { Report(label, false, "a config tiltja"); return; }
            if ((DateTime.UtcNow - started).TotalSeconds < cfg.CommandGrace)
            {
                Report(label, false, $"indulás utáni {cfg.CommandGrace} mp-ben nem fogadom");
                return;
            }
            if (pending != null) { Report(label, false, $"már folyamatban: {pending.Label}"); return; }
            pending = new PowerAction { Action = action, Label = label, Deadline = DateTime.UtcNow.AddSeconds(cfg.Countdown) };
            // A tálcás segéd ebből tudja, mit és mikorra mutasson; a „Mégse” törli a fájlt.
            Ipc.WriteJson(Ipc.PendingFile, new Dictionary<string, object>
            {
                ["action"] = action, ["label"] = label, ["deadline"] = Iso(pending.Deadline),
            });
            Report(label, true, $"{cfg.Countdown} mp múlva, a gépnél megszakítható");
            PublishPending();
        }

        void PublishPending()
        {
            Publish($"{baseTopic}/pending", pending == null
                ? new Dictionary<string, object> { ["state"] = "nincs", ["action"] = null, ["deadline"] = null }
                : new Dictionary<string, object> { ["state"] = pending.Label, ["action"] = pending.Action, ["deadline"] = Iso(pending.Deadline) });
        }

        void CheckPending()
        {
            if (pending == null) { return; }
            if (!File.Exists(Ipc.PendingFile))
            {
                Report($"{pending.Label} megszakítva", true);
                pending = null;
                PublishPending();
                return;
            }
            if (DateTime.UtcNow < pending.Deadline) { return; }
            var action = pending;
            pending = null;
            Ipc.DeleteSafe(Ipc.PendingFile);
            PublishPending();
            ExecutePower(action);
        }

        void ExecutePower(PowerAction a)
        {
            Report(a.Label, true, "végrehajtás");
            Publish(availTopic, "offline", force: true);
            if (dryRun || cfg.Simulate)
            {
                Log.Warn($"Szimulált mód: {a.Label} nem hajtódik végre");
                revertOnlineAt = DateTime.UtcNow.AddSeconds(5);
                return;
            }
            try
            {
                switch (a.Action)
                {
                    case "shutdown":
                        Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                        revertOnlineAt = DateTime.UtcNow.AddMinutes(3);
                        break;
                    case "restart":
                        Process.Start(new ProcessStartInfo("shutdown", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
                        revertOnlineAt = DateTime.UtcNow.AddMinutes(3);
                        break;
                    case "sleep":
                    case "hibernate":
                        // Blokkol a felébredésig — külön szálon, hogy a fő ciklus feldolgozhassa a felébredést.
                        bool hib = a.Action == "hibernate";
                        Task.Run(() =>
                        {
                            Thread.Sleep(1000);
                            if (!Native.SetSuspendState(hib, false, false)) { Log.Warn("SetSuspendState sikertelen"); }
                        });
                        break;
                }
            }
            catch (Exception ex)
            {
                Publish(availTopic, "online", force: true);
                Report(a.Label, false, ex.Message);
            }
        }
    }
}
