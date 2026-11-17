using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace WinMqttMonitor
{
    /// <summary>
    /// CPU- (és nem NVIDIA GPU-) hőmérséklet a LibreHardwareMonitor könyvtárral. A processzor saját hőszenzorát
    /// (Intel: CPU Package, AMD: Tctl/Tdie) csak kernel-meghajtón át lehet olvasni: a könyvtár a PawnIO-t használja
    /// (aláírt, külön telepítendő — a telepítő felajánlja). PawnIO nélkül a CPU-hő hiányzik, a többi működik.
    /// </summary>
    sealed class HardwareSensors : IDisposable
    {
        readonly Computer computer;
        public bool Available { get; }

        public HardwareSensors()
        {
            try
            {
                computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true };
                computer.Open();
                Available = true;
            }
            catch (Exception ex)
            {
                Log.Warn("LibreHardwareMonitor nem indult: " + ex.Message);
            }
        }

        static readonly string[] CpuPreferred = { "CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "Core (Tctl)", "Tdie", "Tctl", "Core Max", "Core Average" };

        /// <summary>Friss mérés. CPU: hőmérséklet (°C), a forrás-szenzor neve, csomag-teljesítmény (W); GPU: név, hő, terhelés, memória.</summary>
        public Dictionary<string, object> Read()
        {
            var d = new Dictionary<string, object>();
            if (!Available) { return d; }
            try
            {
                foreach (var hw in computer.Hardware)
                {
                    hw.Update();
                    foreach (var sub in hw.SubHardware) { sub.Update(); }
                }
                var cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                if (cpu != null)
                {
                    var temps = cpu.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is float v && v > 0 && v < 125).ToList();
                    var pick = CpuPreferred.Select(n => temps.FirstOrDefault(s => s.Name == n)).FirstOrDefault(s => s != null)
                               ?? temps.OrderByDescending(s => s.Value).FirstOrDefault();
                    // Ha a processzor nem adja (nincs PawnIO), az alaplapi Super I/O „CPU” hőszenzora is jó közelítés
                    if (pick == null)
                    {
                        pick = computer.Hardware.Where(h => h.HardwareType == HardwareType.Motherboard)
                            .SelectMany(h => h.SubHardware).SelectMany(h => h.Sensors)
                            .FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value is float v && v > 0 && v < 125
                                                 && s.Name.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                    if (pick != null)
                    {
                        d["cpu_temp"] = Math.Round((double)pick.Value.Value, 1);
                        d["cpu_temp_source"] = pick.Name;
                    }
                    var power = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Value.HasValue
                                                               && (s.Name == "Package" || s.Name == "CPU Package"));
                    if (power != null) { d["cpu_power"] = Math.Round((double)power.Value.Value, 1); }
                }
                var gpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia
                    || h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuIntel);
                if (gpu != null)
                {
                    float? Val(SensorType t, params string[] names) =>
                        names.Select(n => gpu.Sensors.FirstOrDefault(s => s.SensorType == t && s.Name == n)?.Value).FirstOrDefault(v => v.HasValue);
                    d["gpu_name"] = gpu.Name;
                    if (Val(SensorType.Temperature, "GPU Core", "GPU Hot Spot") is float gt) { d["gpu_temp"] = Math.Round((double)gt, 0); }
                    if (Val(SensorType.Load, "GPU Core", "D3D 3D") is float gl) { d["gpu_usage"] = Math.Round((double)gl, 0); }
                    var used = Val(SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
                    var total = Val(SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
                    if (used.HasValue) { d["gpu_mem_used_mb"] = Math.Round((double)used.Value, 0); }
                    if (total.HasValue) { d["gpu_mem_total_mb"] = Math.Round((double)total.Value, 0); }
                    if (used.HasValue && total.HasValue && total.Value > 0) { d["gpu_mem_usage"] = Math.Round(100.0 * used.Value / total.Value, 1); }
                }
            }
            catch (Exception ex) { Log.Warn("Hardverszenzor-olvasás: " + ex.Message); }
            return d;
        }

        public void Dispose()
        {
            try { computer?.Close(); } catch (Exception) { }
        }
    }
}
