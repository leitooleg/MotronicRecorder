using System;
using System.IO;

namespace MotronicRecorder
{
    public static class DemoSource
    {
        private static readonly DateTimeOffset StartUtc = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

        // Synthetic values only. F4 and EE replies intentionally contain different sensor sets.
        public static Sample Sample(double elapsedMs, int epoch, bool secondary)
        {
            if (!SessionStorage.Finite(elapsedMs) || elapsedMs < 0 || epoch < 0) throw new ArgumentOutOfRangeException("elapsedMs");
            double seconds = elapsedMs / 1000.0;
            bool dip = seconds >= 15.0 && seconds < 15.4;
            double baseRpm = seconds < 25.0 ? 850.0 : 1650.0 + 650.0 * Math.Sin((seconds - 25.0) / 17.0);
            double rpm = dip ? 400.0 + 25.0 * Math.Sin(seconds * 20.0) : baseRpm + 12.0 * Math.Sin(seconds * 2.0);
            Sample result = new Sample { Utc = StartUtc.AddMilliseconds(elapsedMs), ElapsedMs = elapsedMs, Epoch = epoch, Counter = unchecked((byte)(Math.Floor(elapsedMs / 100.0) % 256)), Reply = secondary ? (byte)0xEE : (byte)0xF4 };
            if (secondary) {
                result.Values["injection"] = dip ? 4.7 : 2.4 + Math.Max(0.0, rpm - 850.0) / 1000.0;
                result.Values["purge"] = seconds < 25.0 ? 0.0 : 12.0 + 8.0 * Math.Sin(seconds / 13.0);
            } else {
                result.Values["rpm"] = rpm;
                result.Values["voltage"] = dip ? 13.55 : 13.98 + 0.04 * Math.Sin(seconds / 3.0);
                result.Values["airflow"] = dip ? 0.62 : 0.85 + Math.Max(0.0, rpm - 850.0) / 1800.0;
                result.Values["throttle"] = seconds < 25.0 ? 0.60 : 0.85 + Math.Max(0.0, rpm - 850.0) / 3000.0;
                result.Values["ignition"] = dip ? 18.0 : 10.0 + Math.Max(0.0, rpm - 850.0) / 150.0;
                result.Values["coolant"] = 82.0 + 6.0 * (1.0 - Math.Exp(-seconds / 500.0)) + 0.15 * Math.Sin(seconds / 50.0);
                result.Values["air_temperature"] = 24.0 + 1.5 * Math.Sin(seconds / 120.0);
                result.Values["engine_load"] = result.Values["airflow"] * 2.56;
                result.Values["throttle_switch"] = seconds < 25 ? 2 : 0;
                // Model configuration without lambda. Missing lambda values remain missing.
                result.Values["lambda_enabled"] = 0;
                result.Values["co_potentiometer"] = 1.25;
            }
            return result;
        }

        public static string WriteDemoRecording(string baseDirectory, double seconds, double stepMs)
        {
            if (!SessionStorage.Finite(seconds) || seconds < 0 || seconds > 24 * 3600 || !SessionStorage.Finite(stepMs) || stepMs < 1) throw new ArgumentOutOfRangeException("seconds");
            using (SessionStorage storage = new SessionStorage(baseDirectory, "DEMO", "")) {
                storage.WriteEvent(new LogEvent { Utc = StartUtc, ElapsedMs = 0, Epoch = 0, Kind = "demo_start", Message = "Синтетический тест: провал оборотов 15,0–15,4 с. Не показания автомобиля." });
                long index = 0;
                bool dipMarked = false;
                for (double elapsed = 0; elapsed <= seconds * 1000.0; elapsed = (++index) * stepMs) {
                    if (!dipMarked && elapsed >= 15000) {
                        dipMarked = true;
                        storage.WriteEvent(new LogEvent { Utc = StartUtc.AddMilliseconds(15000), ElapsedMs = 15000, Epoch = 0,
                            Kind = "demo_dip", Message = "Искусственный провал на 0,4 секунды. Двойной щелчок по событию приближает график. Не неисправность автомобиля." });
                    }
                    storage.WriteSample(Sample(elapsed, 0, false));
                    if (index % 5 == 0) storage.WriteSample(Sample(elapsed, 0, true));
                    if (index % 1000 == 0) storage.Flush();
                }
                storage.Complete("Синтетическая тестовая запись завершена."); return storage.DirectoryPath;
            }
        }
    }
}
