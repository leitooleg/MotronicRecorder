using System;
using System.Collections.Generic;
using System.Globalization;

namespace MotronicRecorder
{
    public sealed class SensorDefinition
    {
        public string Key, Name, Unit;
        public readonly byte SourceReply;
        public bool Graphable { get { return Key != "lambda_enabled"; } }
        public bool IsDiscrete { get { return Key == "throttle_switch" || Key == "lambda_enabled"; } }
        public string Description {
            get {
                switch (Key) {
                    case "co_potentiometer": return "CO-потенциометр: альтернативная интерпретация канала F4[8] в конфигурации без лямбды. Отдельный запрос не добавляется.";
                    case "lambda_mv": return "Лямбда, мВ: канал F4[8], только когда ЭБУ сообщает конфигурацию с лямбдой. Физическое наличие и исправность датчика не проверяются. Отдельный запрос не добавляется.";
                    case "lambda_enabled": return "Флаг конфигурации F4[11], бит 0x20: ЭБУ сообщает, предусмотрена ли лямбда. Это не проверка проводов или исправности датчика.";
                    case "engine_load": return "Нагрузка по формуле исходной программы: F4[3] × 0,05 мс. Использует тот же байт, что и расходомер; это не отдельное измерение и не процент нагрузки.";
                    case "throttle_switch": return "Контакты дросселя F4[11] & 3: 0 — частичный газ, 1 — полный газ, 2 — холостой ход, 3 — ошибка контактов. Только состояние входа, без управления дросселем.";
                    default: return SourceReply == 0xEE ? "Показание из дополнительного EE. Для получения включите чтение впрыска и адсорбера; запрос не чаще раза в две секунды." : "Показание из основного F4. Выбор графика не изменяет число запросов к ЭБУ.";
                }
            }
        }
        public SensorDefinition(string key, string name, string unit) {
            Key = key; Name = name; Unit = unit;
            SourceReply = key == "injection" || key == "purge" ? (byte)0xEE : (byte)0xF4;
        }
    }

    public static class Sensors
    {
        public static readonly SensorDefinition[] All = {
            new SensorDefinition("rpm", "Обороты", "об/мин"),
            new SensorDefinition("voltage", "Напряжение ЭБУ", "В"),
            new SensorDefinition("airflow", "Расходомер", "В"),
            new SensorDefinition("throttle", "Дроссель", "В"),
            new SensorDefinition("ignition", "Угол зажигания", "°"),
            new SensorDefinition("coolant", "Температура ОЖ", "°C"),
            new SensorDefinition("air_temperature", "Температура воздуха", "°C"),
            new SensorDefinition("injection", "Длительность впрыска", "мс"),
            new SensorDefinition("purge", "Адсорбер", "%"),
            new SensorDefinition("co_potentiometer", "CO-потенциометр", "В"),
            new SensorDefinition("lambda_mv", "Лямбда-зонд", "мВ"),
            new SensorDefinition("engine_load", "Нагрузка ЭБУ", "мс"),
            new SensorDefinition("throttle_switch", "Контакты дросселя", ""),
            new SensorDefinition("lambda_enabled", "Лямбда в конфигурации ЭБУ", "")
        };
        public static string Number(double value) { return value.ToString("0.######", CultureInfo.InvariantCulture); }
        public static string FormatValue(SensorDefinition definition, double value) {
            if (definition.Key == "lambda_enabled" && (value == 0 || value == 1)) return value == 1 ? "Есть (ЭБУ)" : "Нет (ЭБУ)";
            if (definition.Key == "throttle_switch") {
                if (value == 0) return "Частичный газ";
                if (value == 1) return "Полный газ";
                if (value == 2) return "Холостой ход";
                if (value == 3) return "Ошибка контактов";
            }
            return value.ToString("0.###", CultureInfo.InvariantCulture) + (definition.Unit.Length > 0 ? " " + definition.Unit : "");
        }
        public static bool ValidValue(string key, double value) {
            if (Double.IsNaN(value) || Double.IsInfinity(value)) return false;
            if (key == "lambda_enabled") return value == 0 || value == 1;
            if (key == "throttle_switch") return value >= 0 && value <= 3 && Math.Truncate(value) == value;
            return true;
        }
    }

    public sealed class Sample
    {
        public DateTimeOffset Utc;
        public double ElapsedMs;
        public int Epoch;
        public byte Counter, Reply;
        // Only values actually present in THIS reply. Never carry old values into a new row.
        public readonly Dictionary<string, double> Values = new Dictionary<string, double>();
    }

    public sealed class LogEvent
    {
        public DateTimeOffset Utc;
        public double ElapsedMs;
        public int Epoch;
        public string Kind, Message;
    }

    public sealed class PacketRecord
    {
        public DateTimeOffset Utc;
        public double ElapsedMs;
        public int Epoch;
        public string Direction, Kind, Hex;
    }

    // Captured in memory by the protocol. Writing these records must happen only
    // between complete exchanges, never while an ECU byte is waiting for its ACK.
    public sealed class WireTraceRecord
    {
        public DateTimeOffset Utc;
        public double ElapsedMs;
        public string Direction, Stage, Detail;
        public byte? Value;
    }

    public sealed class Recording
    {
        public string Directory, Mode, Note;
        // Preserve the wall-clock offset used on the recording computer. Legacy
        // recordings have no offset and are displayed in this computer's local zone.
        public int? UtcOffsetMinutes;
        public readonly List<Sample> Samples = new List<Sample>();
        public readonly List<LogEvent> Events = new List<LogEvent>();
    }

    public sealed class SessionOptions
    {
        public bool Demo = true;
        public string PortName = "";
        public string OutputDirectory = "";
        public int DurationMinutes = 60;
        public bool IncludeInjection;
        public bool ExperimentalConfirmed;
    }
}
