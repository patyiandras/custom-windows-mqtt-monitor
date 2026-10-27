using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
//sc create "TCP Listener service" binpath= "D:\Temp\Shutdown\TcpListener.exe" start= auto
namespace TcpListener
{
    public partial class TcpListenerService : ServiceBase
    {
        public TcpListenerService()
        {
            InitializeComponent();
        }

        internal void TestStartupAndStop(string[] args)
        {
            this.OnStart(args);
            Console.ReadLine();
            this.OnStop();
        }

        Thread MainThread;
        HttpListener Listener;
        protected override void OnStart(string[] args)
        {
            //StartListening();
            MainThread = DoAsync(() => StartListening2());
        }

        private void StartListening2()
        {
            if (!HttpListener.IsSupported)
            {
                Console.WriteLine("Windows XP SP2 or Server 2003 is required to use the HttpListener class.");
                return;
            }
            // URI prefixes are required,
            // for example "http://contoso.com:8080/index/".

            // Create a listener.
            Listener = new HttpListener();
            //// Add the prefixes.
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    Listener.Prefixes.Add($"http://{ip.ToString()}:7268/");
                }
            }
            try
            {
                Listener.Start();
                Debug.WriteLine("Listening...");
                while (true)
                {
                    // Note: The GetContext method blocks while waiting for a request. 
                    HttpListenerContext context = Listener.GetContext();
                    Response(context);
                    if (Exit) { break; }
                }
                Listener.Stop();
            }
            catch (Exception ex)
            {

            }
        }

        class CommandResponse
        {
            public string Response;
            public Action After;
        }

        private void Response(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;

            // Obtain a response object.
            HttpListenerResponse response = context.Response;
            // Construct a response.
            var sb = new StringBuilder();
            var actions = new List<Action>();
            sb.AppendLine($"You queryed: {ConvertNVCToString(request.QueryString)}");
            var commands = request.QueryString[null]?.Split(',');
            if (commands != null)
            {
                foreach (var command in commands)
                {
                    try
                    {
                        var result = DoCommand(command, request.QueryString);
                        if ((result?.Response ?? "") != "")
                        {
                            sb.AppendLine(result.Response);
                        }
                        if (result?.After != null)
                        {
                            actions.Add(result.After);
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine(ex.Message);
                    }
                }
            }
            var responseString = sb.ToString();
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
            // Get a response stream and write the response to it.
            response.ContentLength64 = buffer.Length;
            System.IO.Stream output = response.OutputStream;
            output.Write(buffer, 0, buffer.Length);
            Debug.WriteLine("response: ", responseString);
            // You must close the output stream.
            output.Close();
            foreach (var item in actions)
            {
                DoAsync(item);
            }
        }

        private static Thread DoAsync(Action item)
        {
            var thread = new Thread(new ThreadStart(() =>
            {
                item();
            }));
            thread.Start();
            return thread;
        }

        private CommandResponse DoCommand(string command, NameValueCollection variables)
        {
            switch (command)
            {
                case "ST.PCPower.Shutdown":
                    var a = CreateShutdownFile(10, true);
                    return new CommandResponse { Response = "{command}: PC is shutting down...", After = a };
                case "Ping":
                    return new CommandResponse { Response = "{command}: Pong!" };
                default:
                    break;
            }
            return new CommandResponse { Response = $"{command}: Unknown command" };
        }

        private Action CreateShutdownFile(int timeout, bool force)
        {
            var path = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var shutdownfile = path + "\\shutdown.xml";
            File.WriteAllText(shutdownfile, "");
            var desiredtime = DateTime.Now.AddSeconds(timeout);
            return () =>
            {
                var tminus = (desiredtime - DateTime.Now).TotalSeconds;
                while (tminus > 0 && File.Exists(shutdownfile))
                {
                    Thread.Sleep(100);
                    tminus = (desiredtime - DateTime.Now).TotalSeconds;
                }
                if (File.Exists(shutdownfile))
                {
                    try { File.Delete(shutdownfile); } catch { }
                    ShutdownPC();
                }
                else
                {
                    
                }
            };
        }

        private void ShutdownPC()
        {
            var psi = new ProcessStartInfo("shutdown", "/s /t 0");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process.Start(psi);
        }

        private string ConvertNVCToString(NameValueCollection nvc)
        {
            var sb = new StringBuilder();
            foreach (var item in nvc.AllKeys)
            {
                sb.AppendLine($"{item}: {nvc[item]}");
            }
            return sb.ToString();
        }

        bool Exit;
        protected override void OnStop()
        {
            Exit = true;
            Listener.Abort();
            //MainThread.Abort();
        }
    }
}
