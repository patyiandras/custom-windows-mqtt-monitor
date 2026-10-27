using System;
using System.Net;
using System.Text;
using System.Threading;

namespace WinMqttMonitor
{
    /// <summary>
    /// A régi TcpListener HTTP-végpontja (7268-as port) a SmartThings „PC Control” Edge-drivernek:
    /// <c>/?Ping</c> és <c>/?ST.PCPower.Shutdown</c>. Az átállás végéig marad; a configban kikapcsolható.
    /// </summary>
    public class LegacyHttp
    {
        readonly int port;
        readonly Action<string> onCommand;
        HttpListener listener;
        Thread thread;

        public LegacyHttp(int port, Action<string> onCommand)
        {
            this.port = port;
            this.onCommand = onCommand;
        }

        public void Start()
        {
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add($"http://+:{port}/");
                listener.Start();
            }
            catch (Exception ex)
            {
                Log.Error($"A régi HTTP-végpont nem indult a {port}-es porton", ex);
                listener = null;
                return;
            }
            thread = new Thread(Loop) { IsBackground = true, Name = "LegacyHttp" };
            thread.Start();
            Log.Info($"Régi HTTP-végpont (SmartThings) figyel: {port}");
        }

        public void Stop()
        {
            try { listener?.Abort(); } catch (Exception) { }
        }

        void Loop()
        {
            while (listener != null && listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch (Exception) { return; }
                try
                {
                    var sb = new StringBuilder();
                    var query = ctx.Request.QueryString;
                    sb.AppendLine("You queryed: ");
                    foreach (var key in query.AllKeys) { sb.AppendLine($"{key}: {query[key]}"); }
                    foreach (var command in query[null]?.Split(',') ?? new string[0])
                    {
                        switch (command)
                        {
                            case "ST.PCPower.Shutdown":
                                sb.AppendLine($"{command}: PC is shutting down...");
                                onCommand("shutdown");
                                break;
                            case "Ping":
                                sb.AppendLine($"{command}: Pong!");
                                break;
                            default:
                                sb.AppendLine($"{command}: Unknown command");
                                break;
                        }
                    }
                    var buffer = Encoding.UTF8.GetBytes(sb.ToString());
                    ctx.Response.ContentLength64 = buffer.Length;
                    ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                    ctx.Response.OutputStream.Close();
                }
                catch (Exception ex) { Log.Warn("Régi HTTP-kérés hiba: " + ex.Message); }
            }
        }
    }
}
