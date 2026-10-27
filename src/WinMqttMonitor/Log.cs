using System;
using System.IO;

namespace WinMqttMonitor
{
    /// <summary>Egyszerű fájlnapló: %ProgramData%\WinMqttMonitor\logs\monitor.log, 1 MB felett egy előző példány marad.</summary>
    public static class Log
    {
        static readonly object Lock = new object();
        static readonly string Dir = Path.Combine(Ipc.DataDir, "logs");
        static readonly string FilePath = Path.Combine(Dir, "monitor.log");
        public static bool ToConsole;

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg, Exception ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

        static void Write(string level, string msg)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level,-5} {msg}";
            lock (Lock)
            {
                if (ToConsole) { Console.WriteLine(line); }
                try
                {
                    Directory.CreateDirectory(Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        var old = FilePath + ".1";
                        if (File.Exists(old)) { File.Delete(old); }
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                }
                catch (Exception) { }
            }
        }
    }
}
