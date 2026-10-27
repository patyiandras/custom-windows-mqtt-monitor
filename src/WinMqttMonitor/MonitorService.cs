using System;
using System.ServiceProcess;
using System.Threading;

namespace WinMqttMonitor
{
    public class MonitorService : ServiceBase
    {
        public const string Name = "WinMqttMonitor";
        Agent agent;
        Thread thread;

        public MonitorService()
        {
            ServiceName = Name;
            CanShutdown = true;
            CanHandlePowerEvent = true;
            CanStop = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            var cfg = Config.Load();
            agent = new Agent(cfg) { LaunchTray = true };
            thread = new Thread(() =>
            {
                try { agent.Run(); }
                catch (Exception ex)
                {
                    Log.Error("Végzetes hiba", ex);
                    Environment.Exit(1); // a szolgáltatás-helyreállítás újraindítja
                }
            }) { Name = "Agent" };
            thread.Start();
        }

        protected override void OnStop()
        {
            agent?.Stop();
            thread?.Join(TimeSpan.FromSeconds(10));
        }

        protected override void OnShutdown() => OnStop();

        protected override bool OnPowerEvent(PowerBroadcastStatus status)
        {
            switch (status)
            {
                case PowerBroadcastStatus.Suspend:
                    agent?.OnSuspend();
                    break;
                case PowerBroadcastStatus.ResumeSuspend:
                case PowerBroadcastStatus.ResumeAutomatic:
                    agent?.OnResume();
                    break;
            }
            return true;
        }
    }
}
