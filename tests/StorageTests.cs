using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MotronicRecorder
{
    public static class StorageTests
    {
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Storage test: " + message); }
        public static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "MotronicRecorderTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string path;
            string oldDirectory = Path.Combine(root, "legacy-schema1"); Directory.CreateDirectory(oldDirectory);
            SessionMetadata oldMetadata = new SessionMetadata { magic = SessionStorage.Magic, schemaVersion = 1,
                sensorKeys = SessionStorage.LegacySensorKeys, mode = "LIVE_EXPERIMENTAL", readOnly = true,
                cleanClose = true, sampleCount = 1, eventCount = 0 };
            using (FileStream legacyJson = File.Create(Path.Combine(oldDirectory, "session.json")))
                new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(SessionMetadata)).WriteObject(legacyJson, oldMetadata);
            Sample oldSample = DemoSource.Sample(1000, 1, false);
            List<string> oldFields = new List<string> { oldSample.Utc.ToUniversalTime().ToString("o"), "1000", "1", "00", "F4" };
            foreach (string key in SessionStorage.LegacySensorKeys) { double value; oldFields.Add(oldSample.Values.TryGetValue(key, out value) ? Sensors.Number(value) : ""); }
            File.WriteAllText(Path.Combine(oldDirectory, "samples.csv"), Csv.Join(SessionStorage.SampleHeader(SessionStorage.LegacySensorKeys)) + "\r\n" + Csv.Join(oldFields) + "\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(oldDirectory, "events.csv"), Csv.Join(SessionStorage.EventHeader()) + "\r\n", new UTF8Encoding(false));
            Recording legacy = RecordingReader.Load(oldDirectory);
            Assert(legacy.Samples.Count == 1 && legacy.Samples[0].Values.ContainsKey("rpm"), "schema 1 from 0.1.1/0.2.0 remains readable");
            foreach (SensorDefinition sensor in Sensors.All) if (Array.IndexOf(SessionStorage.LegacySensorKeys, sensor.Key) < 0)
                Assert(!legacy.Samples[0].Values.ContainsKey(sensor.Key), "legacy log must not fabricate newly added sensor " + sensor.Key);
            string message = "Отметка; \"провал\"\r\nвторая строка";
            using (SessionStorage storage = new SessionStorage(root, "DEMO", "")) {
                path = storage.DirectoryPath;
                storage.WriteSample(DemoSource.Sample(1000, 0, false));
                storage.WriteSample(DemoSource.Sample(1100, 0, true));
                storage.WriteEvent(new LogEvent { Utc = DateTimeOffset.UtcNow, ElapsedMs = 1100, Epoch = 0, Kind = "bookmark", Message = message });
                storage.WritePacket(new PacketRecord { Utc = DateTimeOffset.UtcNow, ElapsedMs = 1100, Epoch = 0, Direction = "RX", Kind = "test", Hex = "01 EE 03" });
                storage.WriteWireTrace(new WireTraceRecord[] {
                    new WireTraceRecord { Utc = DateTimeOffset.UtcNow, ElapsedMs = 1101.25, Direction = "RX", Value = 0x55, Stage = "Синхронизация", Detail = "Ожидался 55" },
                    new WireTraceRecord { Utc = DateTimeOffset.UtcNow, ElapsedMs = 1102.75, Direction = "ERROR", Stage = "Ключевой байт", Detail = "Таймаут\r\nвторая строка" }
                }, 3);
                storage.Flush();
                Recording open = RecordingReader.Load(path);
                Assert(open.Samples.Count == 2, "read while recording");
                Assert(open.Note.Contains("не имеет отметки"), "open recording explicitly marked unfinished");
                storage.Complete("тест");
            }
            Recording saved = RecordingReader.Load(path);
            Assert(saved.Mode == "DEMO" && saved.Note.Contains("ТЕСТОВЫЕ"), "test provenance");
            string communication = File.ReadAllText(Path.Combine(path, "communication.txt"), new UTF8Encoding(false, true));
            Assert(communication.Contains("UTF-8") && communication.Contains("НЕ точные времена") && communication.Contains("| 1101.25 | 3 | RX | 55 | Синхронизация"), "UTF8 text trace has readable byte, monotonic time, epoch and timing caveat");
            Assert(communication.Contains("| ERROR | -- | Ключевой байт | Таймаут\\r\\nвторая строка") && communication.Contains("| EVENT | -- | bookmark | Отметка"), "errors and events retain readable escaped details on one line");
            Assert(saved.UtcOffsetMinutes.HasValue && saved.UtcOffsetMinutes.Value == (int)DateTimeOffset.Now.Offset.TotalMinutes, "recording computer's wall-clock offset round trip");
            Assert(saved.Samples[0].Values.ContainsKey("rpm") && !saved.Samples[0].Values.ContainsKey("injection"), "F4 has no carried-forward EE values");
            Assert(!saved.Samples[1].Values.ContainsKey("rpm") && saved.Samples[1].Values.ContainsKey("injection"), "EE has no carried-forward F4 values");
            Assert(saved.Events.Count == 1 && saved.Events[0].Message == message, "quoted semicolon, quote, multiline event round trip");
            Assert(!saved.Note.Contains("не имеет отметки"), "clean close persisted");
            // Offset metadata is optional: a recording written by 0.1.1 remains readable.
            string legacyMetadata = Path.Combine(path, "session.json");
            string originalMetadata = File.ReadAllText(legacyMetadata);
            string withoutOffset = System.Text.RegularExpressions.Regex.Replace(originalMetadata, ",?\"utcOffsetMinutes\":-?[0-9]+,?", delegate(System.Text.RegularExpressions.Match match) { return match.Value.StartsWith(",") && match.Value.EndsWith(",") ? "," : ""; });
            File.WriteAllText(legacyMetadata, withoutOffset, new UTF8Encoding(false));
            Assert(!RecordingReader.Load(path).UtcOffsetMinutes.HasValue, "legacy schema 1 recording without wall-clock offset");
            File.WriteAllText(legacyMetadata, originalMetadata, new UTF8Encoding(false));
            string csv = Path.Combine(path, "samples.csv");
            string goodRow = File.ReadAllLines(csv, Encoding.UTF8)[1];
            string[] badFields = goodRow.Split(';'); badFields[5] = "NaN";
            File.AppendAllText(csv, String.Join(";", badFields) + "\r\n" + "unfinished;fragment", new UTF8Encoding(false));
            Recording damaged = RecordingReader.Load(path);
            Assert(damaged.Samples.Count == 2 && damaged.Note.Contains("измерения 2"), "nonfinite and trailing fragment skipped with explicit count");
            using (FileStream truncatedEvent = new FileStream(Path.Combine(path, "events.csv"), FileMode.Append, FileAccess.Write)) {
                byte[] prefix = Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("o") + ";1200;0;bookmark;");
                truncatedEvent.Write(prefix, 0, prefix.Length); truncatedEvent.WriteByte(0xD0);
            }
            Recording badEncoding = RecordingReader.Load(path);
            Assert(badEncoding.Events.Count == 1 && badEncoding.Note.Contains("события 1"), "truncated UTF8 event does not discard valid prior data");
            File.WriteAllText(csv, "wrong;header\r\n", new UTF8Encoding(false));
            bool badHeader = false; try { RecordingReader.Load(path); } catch (InvalidDataException) { badHeader = true; }
            Assert(badHeader, "unknown CSV schema rejected");
            using (SessionStorage storage = new SessionStorage(root, "DEMO", "")) {
                Sample invalid = DemoSource.Sample(0, 0, false); invalid.Values["rpm"] = Double.PositiveInfinity;
                bool invalidRejected = false; try { storage.WriteSample(invalid); } catch (ArgumentException) { invalidRejected = true; }
                Assert(invalidRejected, "writer rejects infinity");
                Sample invalidContact = DemoSource.Sample(0, 0, false); invalidContact.Values["throttle_switch"] = 4;
                invalidRejected = false; try { storage.WriteSample(invalidContact); } catch (ArgumentException) { invalidRejected = true; }
                Assert(invalidRejected, "writer rejects unknown contact code");
                path = storage.DirectoryPath;
            }
            Assert(RecordingReader.Load(path).Note.Contains("не имеет отметки"), "Dispose alone never marks complete");
            string metadataPath = Path.Combine(path, "session.json");
            File.WriteAllText(metadataPath, File.ReadAllText(metadataPath).Replace("MotronicRecorderSession", "UnrelatedSession"), new UTF8Encoding(false));
            bool badMagic = false; try { RecordingReader.Load(path); } catch (InvalidDataException) { badMagic = true; }
            Assert(badMagic, "unrelated metadata magic rejected");
            Assert(DemoSource.Sample(14999, 0, false).Values["rpm"] > 800 && DemoSource.Sample(15000, 0, false).Values["rpm"] < 500 && DemoSource.Sample(15399, 0, false).Values["rpm"] < 500 && DemoSource.Sample(15400, 0, false).Values["rpm"] > 800, "known 400 ms synthetic dip");
            string hour = DemoSource.WriteDemoRecording(root, 3600, 100);
            Recording replay = RecordingReader.Load(hour);
            Assert(replay.Samples.Count == 43202, "complete one-hour replay sample count");
            Assert(replay.Samples[replay.Samples.Count - 1].ElapsedMs == 3600000, "one-hour timeline preserved");
            Assert(!replay.Note.Contains("повреждённые") && !replay.Note.Contains("отличается"), "complete one-hour recording has no corruption/count warning");
            Console.WriteLine("Storage tests passed. One-hour synthetic recording: " + hour);
        }
    }
}
