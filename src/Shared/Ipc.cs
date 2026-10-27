using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace WinMqttMonitor
{
    /// <summary>
    /// A szolgáltatás (session 0, LocalSystem) és a tálcás segéd (a felhasználó sessionjében) közti
    /// fájl-alapú csatorna. A szolgáltatás nem tud ablakot nyitni, zárolni vagy értesítést mutatni,
    /// a tálcás segéd viszont nem lát minden rendszeradatot — ezért kettő van.
    ///
    ///   pending.json        függő leállítás/újraindítás/alvás: a segéd visszaszámlálót mutat,
    ///                       a „Mégse” gomb törli a fájlt; ha a határidőkor még létezik, a szolgáltatás végrehajtja.
    ///   cmd-*.json          a szolgáltatás parancsa a segédnek (zárolás, képernyő ki, értesítés).
    ///   session-&lt;id&gt;.json  a segéd jelentése a sessionjéről (felhasználó, tétlenség, zárolás).
    /// </summary>
    public static class Ipc
    {
        /// <summary>%ProgramData%\WinMqttMonitor; teszteléshez a WINMQTTMONITOR_DATA környezeti változó felülírja.</summary>
        public static readonly string DataDir = Environment.GetEnvironmentVariable("WINMQTTMONITOR_DATA") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinMqttMonitor");
        public static readonly string Dir = Path.Combine(DataDir, "ipc");
        public static readonly string PendingFile = Path.Combine(Dir, "pending.json");

        public static string SessionFile(int sessionId) => Path.Combine(Dir, $"session-{sessionId}.json");

        static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static string ToJson(object value) => Serializer.Serialize(value);

        public static Dictionary<string, object> FromJson(string text) =>
            Serializer.Deserialize<Dictionary<string, object>>(text);

        public static Dictionary<string, object> ReadJson(string path)
        {
            try
            {
                return File.Exists(path) ? FromJson(File.ReadAllText(path)) : null;
            }
            catch (Exception)
            {
                return null; // éppen íródik / sérült: a következő körben újra próbáljuk
            }
        }

        /// <summary>Atomikus írás: temp fájl + átnevezés, hogy a másik fél ne olvasson félkész fájlt.</summary>
        public static void WriteJson(string path, object value)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson(value));
            if (File.Exists(path)) { File.Delete(path); }
            File.Move(tmp, path);
        }

        public static void DeleteSafe(string path)
        {
            try
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
            catch (Exception) { }
        }

        public static string Str(this Dictionary<string, object> d, string key, string def = null) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : def;

        public static double Num(this Dictionary<string, object> d, string key, double def = 0) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) : def;

        public static bool Bool(this Dictionary<string, object> d, string key, bool def = false) =>
            d != null && d.TryGetValue(key, out var v) && v is bool b ? b : def;
    }
}
