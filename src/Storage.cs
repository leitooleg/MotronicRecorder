using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace MotronicRecorder
{
    [DataContract]
    internal sealed class SessionMetadata
    {
        [DataMember(IsRequired = true)] public string magic;
        [DataMember(IsRequired = true)] public int schemaVersion;
        [DataMember(IsRequired = true)] public string mode;
        [DataMember(IsRequired = true)] public bool readOnly;
        [DataMember(IsRequired = true)] public bool cleanClose;
        [DataMember(IsRequired = true)] public string[] sensorKeys;
        [DataMember] public string startedUtc, endedUtc, portName, vehicle, ecu, adapter, warning, completionReason;
        [DataMember] public long sampleCount, eventCount, packetCount;
        [DataMember(EmitDefaultValue = false)] public int? utcOffsetMinutes;
    }

    public sealed class SessionStorage : IDisposable
    {
        internal const string Magic = "MotronicRecorderSession";
        internal const int SchemaVersion = 2;
        internal static readonly string[] LegacySensorKeys = { "rpm", "voltage", "airflow", "throttle", "ignition", "coolant", "air_temperature", "injection", "purge" };
        private readonly object gate = new object();
        private readonly FileStream samplesFile, eventsFile, packetsFile, communicationFile;
        private readonly StreamWriter samples, events, packets, communication;
        private readonly SessionMetadata metadata;
        private bool disposed, completed;
        public string DirectoryPath { get; private set; }

        public SessionStorage(string baseDirectory, string mode, string portName)
        {
            if (String.IsNullOrWhiteSpace(baseDirectory)) throw new ArgumentException("Не задана папка записей.");
            if (String.IsNullOrWhiteSpace(mode)) throw new ArgumentException("Не задан режим записи.");
            string suffix = String.Equals(mode, "DEMO", StringComparison.OrdinalIgnoreCase) ? "DEMO" : "EXPERIMENTAL";
            DirectoryPath = Path.Combine(Path.GetFullPath(baseDirectory), DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "_" + suffix + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(DirectoryPath);
            metadata = new SessionMetadata {
                magic = Magic, schemaVersion = SchemaVersion, mode = mode, readOnly = true, cleanClose = false,
                utcOffsetMinutes = (int)DateTimeOffset.Now.Offset.TotalMinutes,
                sensorKeys = SensorKeys(), startedUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                endedUtc = "", portName = portName ?? "", vehicle = "Opel Vectra A 1990 / 20NE (со слов владельца)",
                ecu = "Bosch Motronic M1.5 (аппаратная идентификация ещё не подтверждена)", adapter = "VAG-KKL",
                warning = suffix == "DEMO" ? "ТЕСТОВЫЕ СИНТЕТИЧЕСКИЕ ДАННЫЕ. Не измерения автомобиля." : "ЭКСПЕРИМЕНТАЛЬНОЕ ЧТЕНИЕ. Протокол восстановлен анализом EXE; подтверждение на автомобиле требуется. Отсутствие события в логе не исключает краткий провал.",
                completionReason = "Запись ещё не завершена; при аварийном закрытии последние строки могут отсутствовать."
            };
            try {
                samplesFile = OpenLog("samples.csv"); samples = NewWriter(samplesFile);
                eventsFile = OpenLog("events.csv"); events = NewWriter(eventsFile);
                packetsFile = OpenLog("packets.jsonl"); packets = NewWriter(packetsFile);
                communicationFile = OpenLog("communication.txt"); communication = NewWriter(communicationFile);
                communication.WriteLine("Motronic Recorder — журнал обмена, UTF-8");
                communication.WriteLine("UTC | elapsed_ms (монотонное время с начала записи) | epoch (попытка соединения) | направление | байт HEX | стадия | пояснение");
                communication.WriteLine("TX — возврат вызова записи в драйвер адаптера; RX — возврат чтения из драйвера. Это НЕ точные времена электрических фронтов K-Line. Эхо KKL включено в RX.");
                communication.WriteLine("Записи байтов сначала собираются в памяти и сохраняются между завершёнными обменами либо после ошибки. При аварии приложения последние записи могут отсутствовать.");
                communication.WriteLine("События и группы байтов могут добавляться позже своего времени; для сравнения используйте elapsed_ms и epoch. Последняя строка RX — последний прочитанный байт, ERROR поясняет, что ожидалось дальше.");
                communication.WriteLine("Режим: " + mode + "; COM: " + (portName ?? "") + "; в ДЕМО реальный порт не открывается.");
                samples.WriteLine(Csv.Join(SampleHeader()));
                events.WriteLine(Csv.Join(EventHeader()));
                FlushCore();
                SaveMetadata();
            } catch {
                if (samples != null) samples.Dispose();
                if (events != null) events.Dispose();
                if (packets != null) packets.Dispose();
                if (communication != null) communication.Dispose();
                if (samplesFile != null) samplesFile.Dispose();
                if (eventsFile != null) eventsFile.Dispose();
                if (packetsFile != null) packetsFile.Dispose();
                if (communicationFile != null) communicationFile.Dispose();
                throw;
            }
        }

        private FileStream OpenLog(string name) { return new FileStream(Path.Combine(DirectoryPath, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096); }
        private static StreamWriter NewWriter(Stream stream) { return new StreamWriter(stream, new UTF8Encoding(false, true), 4096, true); }
        internal static string[] SensorKeys() { string[] keys = new string[Sensors.All.Length]; for (int i = 0; i < keys.Length; i++) keys[i] = Sensors.All[i].Key; return keys; }
        internal static string[] SampleHeader() { return SampleHeader(SensorKeys()); }
        internal static string[] SampleHeader(string[] keys) {
            List<string> header = new List<string>(new string[] { "utc", "elapsed_ms", "epoch", "counter_hex", "reply_hex" });
            header.AddRange(keys); return header.ToArray();
        }
        internal static string[] EventHeader() { return new string[] { "utc", "elapsed_ms", "epoch", "kind", "message" }; }
        internal static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private void EnsureWritable() { if (disposed) throw new ObjectDisposedException("SessionStorage"); if (completed) throw new InvalidOperationException("Запись уже завершена."); }
        private static void ValidateTime(double elapsedMs, int epoch) { if (!Finite(elapsedMs) || elapsedMs < 0 || epoch < 0) throw new ArgumentException("Недопустимое время или номер подключения."); }

        public void WriteSample(Sample sample)
        {
            if (sample == null) throw new ArgumentNullException("sample");
            lock (gate) {
                EnsureWritable(); ValidateTime(sample.ElapsedMs, sample.Epoch);
                foreach (KeyValuePair<string, double> pair in sample.Values) {
                    bool found = false; foreach (SensorDefinition sensor in Sensors.All) if (sensor.Key == pair.Key) { found = true; break; }
                    if (!found || !Sensors.ValidValue(pair.Key, pair.Value)) throw new ArgumentException("Неизвестный датчик или недопустимое значение: " + pair.Key);
                }
                List<string> fields = new List<string>();
                fields.Add(sample.Utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                fields.Add(Format(sample.ElapsedMs)); fields.Add(sample.Epoch.ToString(CultureInfo.InvariantCulture));
                fields.Add(sample.Counter.ToString("X2", CultureInfo.InvariantCulture)); fields.Add(sample.Reply.ToString("X2", CultureInfo.InvariantCulture));
                foreach (SensorDefinition sensor in Sensors.All) {
                    double value; fields.Add(sample.Values.TryGetValue(sensor.Key, out value) ? Format(value) : "");
                }
                samples.WriteLine(Csv.Join(fields)); metadata.sampleCount++;
            }
        }

        public void WriteEvent(LogEvent entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            lock (gate) {
                EnsureWritable(); ValidateTime(entry.ElapsedMs, entry.Epoch);
                if (String.IsNullOrWhiteSpace(entry.Kind)) throw new ArgumentException("Не задан тип события.");
                events.WriteLine(Csv.Join(new string[] { entry.Utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), Format(entry.ElapsedMs), entry.Epoch.ToString(CultureInfo.InvariantCulture), entry.Kind, entry.Message ?? "" }));
                CommunicationLine(entry.Utc, entry.ElapsedMs, entry.Epoch, "EVENT", null, entry.Kind, entry.Message);
                metadata.eventCount++;
            }
        }

        public void WritePacket(PacketRecord packet)
        {
            if (packet == null) throw new ArgumentNullException("packet");
            lock (gate) {
                EnsureWritable(); ValidateTime(packet.ElapsedMs, packet.Epoch);
                packets.WriteLine("{\"utc\":" + Json(packet.Utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)) + ",\"elapsed_ms\":" + Format(packet.ElapsedMs) + ",\"epoch\":" + packet.Epoch.ToString(CultureInfo.InvariantCulture) + ",\"direction\":" + Json(packet.Direction) + ",\"kind\":" + Json(packet.Kind) + ",\"hex\":" + Json(packet.Hex) + "}");
                metadata.packetCount++;
            }
        }

        public void WriteWireTrace(WireTraceRecord[] records, int epoch)
        {
            if (records == null) throw new ArgumentNullException("records");
            lock (gate) {
                EnsureWritable();
                foreach (WireTraceRecord item in records) {
                    if (item == null) throw new ArgumentException("Пустая запись обмена.");
                    ValidateTime(item.ElapsedMs, epoch);
                    CommunicationLine(item.Utc, item.ElapsedMs, epoch, item.Direction, item.Value, item.Stage, item.Detail);
                }
            }
        }

        private static string OneLine(string value) { return (value ?? "").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t"); }
        private void CommunicationLine(DateTimeOffset utc, double elapsed, int epoch, string direction, byte? value, string stage, string detail)
        {
            communication.WriteLine(utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + " | " + Format(elapsed) + " | " + epoch.ToString(CultureInfo.InvariantCulture) + " | " + OneLine(direction) + " | " + (value.HasValue ? value.Value.ToString("X2", CultureInfo.InvariantCulture) : "--") + " | " + OneLine(stage) + " | " + OneLine(detail));
        }

        private static string Format(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        private static string Json(string value) {
            if (value == null) return "null";
            StringBuilder result = new StringBuilder("\"");
            foreach (char c in value) {
                if (c == '"') result.Append("\\\""); else if (c == '\\') result.Append("\\\\");
                else if (c < 0x20) result.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture)); else result.Append(c);
            }
            return result.Append('"').ToString();
        }

        public void Flush() { lock (gate) { if (disposed) throw new ObjectDisposedException("SessionStorage"); FlushCore(); } }
        private void FlushCore() {
            samples.Flush(); events.Flush(); packets.Flush(); communication.Flush();
            samplesFile.Flush(true); eventsFile.Flush(true); packetsFile.Flush(true); communicationFile.Flush(true);
        }

        public void Complete(string reason)
        {
            lock (gate) {
                if (disposed) throw new ObjectDisposedException("SessionStorage"); if (completed) return;
                FlushCore(); metadata.cleanClose = true; metadata.endedUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture); metadata.completionReason = reason ?? "";
                try { SaveMetadata(); completed = true; }
                catch { metadata.cleanClose = false; throw; }
            }
        }

        private void SaveMetadata()
        {
            string path = Path.Combine(DirectoryPath, "session.json"), temporary = path + ".tmp";
            try {
                using (FileStream output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) {
                    new DataContractJsonSerializer(typeof(SessionMetadata)).WriteObject(output, metadata); output.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null, true); else File.Move(temporary, path);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public void Dispose()
        {
            lock (gate) {
                if (disposed) return;
                try { FlushCore(); }
                finally {
                    disposed = true;
                    try { samples.Dispose(); } finally { try { events.Dispose(); } finally { try { packets.Dispose(); } finally { try { communication.Dispose(); } finally { samplesFile.Dispose(); eventsFile.Dispose(); packetsFile.Dispose(); communicationFile.Dispose(); } } } }
                }
            }
        }
    }

    public static class RecordingReader
    {
        public static Recording Load(string sessionDirectory)
        {
            string directory = Path.GetFullPath(sessionDirectory);
            SessionMetadata metadata;
            try {
                using (FileStream input = new FileStream(Path.Combine(directory, "session.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                    if (input.Length > 1024 * 1024) throw new InvalidDataException("Слишком большой файл метаданных.");
                    metadata = (SessionMetadata)new DataContractJsonSerializer(typeof(SessionMetadata)).ReadObject(input);
                }
            } catch (Exception ex) {
                if (!(ex is IOException) && !(ex is SerializationException) && !(ex is FormatException)) throw;
                throw new InvalidDataException("Не удалось прочитать session.json: " + ex.Message, ex);
            }
            if (metadata == null || metadata.magic != SessionStorage.Magic || !metadata.readOnly || String.IsNullOrWhiteSpace(metadata.mode) ||
                !(metadata.schemaVersion == 1 && Same(metadata.sensorKeys, SessionStorage.LegacySensorKeys) ||
                  metadata.schemaVersion == SessionStorage.SchemaVersion && Same(metadata.sensorKeys, SessionStorage.SensorKeys())))
                throw new InvalidDataException("Неизвестный формат записи или несовместимая схема датчиков.");
            if (metadata.utcOffsetMinutes.HasValue && (metadata.utcOffsetMinutes.Value < -840 || metadata.utcOffsetMinutes.Value > 840))
                throw new InvalidDataException("Недопустимое смещение часового пояса записи.");
            Recording recording = new Recording { Directory = directory, Mode = metadata.mode, UtcOffsetMinutes = metadata.utcOffsetMinutes };
            int badSamples = 0, badEvents = 0; bool missingEvents = false;
            ReadCsv(Path.Combine(directory, "samples.csv"), SessionStorage.SampleHeader(metadata.sensorKeys), delegate(List<string> row) {
                Sample sample; if (ParseSample(row, metadata.sensorKeys, out sample)) recording.Samples.Add(sample); else badSamples++;
            }, delegate { badSamples++; });
            string eventsPath = Path.Combine(directory, "events.csv");
            if (File.Exists(eventsPath)) ReadCsv(eventsPath, SessionStorage.EventHeader(), delegate(List<string> row) {
                LogEvent entry; if (ParseEvent(row, out entry)) recording.Events.Add(entry); else badEvents++;
            }, delegate { badEvents++; }); else missingEvents = true;
            List<string> notes = new List<string>();
            if (String.Equals(metadata.mode, "DEMO", StringComparison.OrdinalIgnoreCase)) notes.Add("ТЕСТОВЫЕ ДАННЫЕ: это демонстрация, не измерения автомобиля.");
            else notes.Add("Экспериментальная запись; отсутствие аномалии не исключает краткий провал.");
            if (!metadata.cleanClose) notes.Add("Сессия не имеет отметки штатного завершения; последние данные могли не сохраниться.");
            if (String.Equals(metadata.completionReason, "error", StringComparison.OrdinalIgnoreCase)) notes.Add("Запись остановлена из-за ошибки; смотрите события и исходные пакеты.");
            if (String.Equals(metadata.completionReason, "link_lost", StringComparison.OrdinalIgnoreCase)) notes.Add("Опрос остановлен после потери связи; сохранённые измерения доступны. Причина и частичный обмен — в communication.txt.");
            if (badSamples > 0 || badEvents > 0) notes.Add("Пропущены повреждённые/незавершённые строки: измерения " + badSamples + ", события " + badEvents + ".");
            if (missingEvents) notes.Add("Файл событий отсутствует.");
            if (metadata.cleanClose && (metadata.sampleCount != recording.Samples.Count || (!missingEvents && metadata.eventCount != recording.Events.Count))) notes.Add("Число прочитанных строк отличается от итоговых метаданных.");
            if (recording.Samples.Count == 0) notes.Add("Пригодных измерений нет.");
            recording.Note = String.Join(" ", notes.ToArray()); return recording;
        }

        private static bool Same(string[] first, string[] second) { if (first == null || first.Length != second.Length) return false; for (int i = 0; i < first.Length; i++) if (first[i] != second[i]) return false; return true; }
        private static void ReadCsv(string path, string[] expectedHeader, Action<List<string>> row, Action malformed)
        {
            bool hasHeader = false;
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(input, new UTF8Encoding(false, false), true)) {
                Csv.Read(reader, delegate(List<string> fields) {
                    bool damagedEncoding = false;
                    foreach (string field in fields) if (field.IndexOf('\uFFFD') >= 0) { damagedEncoding = true; break; }
                    if (damagedEncoding) { if (!hasHeader) throw new InvalidDataException("Повреждена кодировка заголовка " + Path.GetFileName(path) + "."); malformed(); return; }
                    if (!hasHeader) { if (!Same(fields.ToArray(), expectedHeader)) throw new InvalidDataException("Неверный заголовок " + Path.GetFileName(path) + "."); hasHeader = true; }
                    else row(fields);
                }, delegate { if (!hasHeader) throw new InvalidDataException("Повреждён заголовок " + Path.GetFileName(path) + "."); malformed(); });
            }
            if (!hasHeader) throw new InvalidDataException("Нет заголовка " + Path.GetFileName(path) + ".");
        }
        private static bool Time(string value, out DateTimeOffset utc) { return DateTimeOffset.TryParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out utc); }
        private static bool Number(string value, out double number) { return Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && SessionStorage.Finite(number); }
        private static bool Epoch(string value, out int epoch) { return Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out epoch) && epoch >= 0; }
        private static bool Hex(string value, out byte number) { number = 0; return value.Length == 2 && Byte.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out number); }
        private static bool ParseSample(List<string> row, string[] sensorKeys, out Sample sample)
        {
            sample = null; DateTimeOffset utc; double elapsed; int epoch; byte counter = 0, reply = 0;
            if (row.Count != 5 + sensorKeys.Length || !Time(row[0], out utc) || !Number(row[1], out elapsed) || elapsed < 0 || !Epoch(row[2], out epoch) || !Hex(row[3], out counter) || !Hex(row[4], out reply)) return false;
            Sample parsed = new Sample { Utc = utc.ToUniversalTime(), ElapsedMs = elapsed, Epoch = epoch, Counter = counter, Reply = reply };
            for (int i = 0; i < sensorKeys.Length; i++) {
                if (row[i + 5] == "") continue;
                double value; if (!Number(row[i + 5], out value) || !Sensors.ValidValue(sensorKeys[i], value)) return false;
                parsed.Values.Add(sensorKeys[i], value);
            }
            sample = parsed; return true;
        }
        private static bool ParseEvent(List<string> row, out LogEvent entry)
        {
            entry = null; DateTimeOffset utc; double elapsed; int epoch;
            if (row.Count != 5 || !Time(row[0], out utc) || !Number(row[1], out elapsed) || elapsed < 0 || !Epoch(row[2], out epoch) || String.IsNullOrWhiteSpace(row[3])) return false;
            entry = new LogEvent { Utc = utc.ToUniversalTime(), ElapsedMs = elapsed, Epoch = epoch, Kind = row[3], Message = row[4] }; return true;
        }
    }

    internal static class Csv
    {
        internal static string Join(IEnumerable<string> fields) {
            StringBuilder line = new StringBuilder(); bool first = true;
            foreach (string value in fields) {
                if (!first) line.Append(';'); first = false; string field = value ?? "";
                if (field.IndexOfAny(new char[] { ';', '"', '\r', '\n' }) >= 0) line.Append('"').Append(field.Replace("\"", "\"\"")).Append('"'); else line.Append(field);
            }
            return line.ToString();
        }
        internal static void Read(TextReader reader, Action<List<string>> row, Action malformed)
        {
            List<string> fields = new List<string>(); StringBuilder field = new StringBuilder();
            bool quoted = false, afterQuote = false, started = false, bad = false; int code;
            while ((code = reader.Read()) >= 0) {
                char c = (char)code;
                if (quoted) {
                    if (c == '"') { if (reader.Peek() == '"') { reader.Read(); field.Append('"'); } else { quoted = false; afterQuote = true; } }
                    else field.Append(c);
                    continue;
                }
                if (c == ';') { fields.Add(field.ToString()); field.Length = 0; afterQuote = false; started = false; continue; }
                if (c == '\r' || c == '\n') {
                    if (c == '\r' && reader.Peek() == '\n') reader.Read();
                    fields.Add(field.ToString()); if (bad) malformed(); else row(fields);
                    fields = new List<string>(); field.Length = 0; afterQuote = false; started = false; bad = false; continue;
                }
                if (afterQuote) { bad = true; continue; }
                if (c == '"') { if (!started && field.Length == 0) { quoted = true; started = true; } else bad = true; }
                else { field.Append(c); started = true; }
            }
            // Every writer row ends in a newline. A remaining fragment is never promoted to a measurement.
            if (quoted || afterQuote || started || fields.Count > 0 || field.Length > 0 || bad) malformed();
        }
    }
}
