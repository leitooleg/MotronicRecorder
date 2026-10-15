using System;
using System.IO;
using System.Threading;

namespace MotronicRecorder
{
    public static class LiveSessionTests
    {
        private static void Assert(bool value, string message) { if (!value) throw new Exception("Live session integration: " + message); }
        private static void Keywords(FakeTransport wire)
        {
            foreach (byte value in new byte[] { 0x55, 0xC7, 0x38, 0x80, 0x7F }) wire.Input.Enqueue(value);
        }
        private static byte[] Primary()
        {
            byte[] frame = new byte[17]; frame[0] = 16; frame[1] = 4; frame[2] = 0xF4;
            frame[3] = 128; frame[4] = 204; frame[5] = 100; frame[6] = 80;
            frame[10] = 64; frame[13] = 75; frame[15] = 10; frame[16] = 3; return frame;
        }
        private static void RunCase(bool includeInjection)
        {
            string root = Path.Combine(Path.GetTempPath(), "MotronicLiveWireTest_" + Guid.NewGuid().ToString("N"));
            int attempts = 0;
            using (ManualResetEvent completed = new ManualResetEvent(false))
            using (SessionController controller = new SessionController(delegate(string name)
            {
                Interlocked.Increment(ref attempts);
                Assert(name == "COM123", "unexpected test port");
                FakeTransport wire = new FakeTransport(); Keywords(wire);
                wire.FeedReceivedFrame(new byte[] { 3, 0, 0xF6, 3 });
                wire.FeedSendAcknowledgements(FrameProtocol.BuildRequest(ReadRequest.KeepAlive, 1));
                wire.FeedReceivedFrame(new byte[] { 3, 2, 0x09, 3 });
                wire.FeedSendAcknowledgements(FrameProtocol.BuildRequest(ReadRequest.MainParameters, 3));
                wire.FeedReceivedFrame(Primary());
                if (includeInjection)
                {
                    wire.FeedSendAcknowledgements(FrameProtocol.BuildRequest(ReadRequest.InjectionAndPurge, 5));
                    wire.FeedReceivedFrame(new byte[] { 10, 6, 0xEE, 0, 0x84, 0xE2, 0, 0, 0, 128, 3 });
                }
                return wire;
            }, delegate(int milliseconds, CancellationToken token) { token.ThrowIfCancellationRequested(); }))
            {
                controller.SampleReceived += delegate(Sample sample)
                {
                    if ((!includeInjection && sample.Reply == 0xF4) || sample.Reply == 0xEE)
                    { controller.Stop(); completed.Set(); }
                };
                controller.Start(new SessionOptions { Demo = false, PortName = "COM123", OutputDirectory = root,
                    DurationMinutes = 1, ExperimentalConfirmed = true, IncludeInjection = includeInjection });
                Assert(completed.WaitOne(6000), "mock ECU did not reach required live response");
                for (int i = 0; controller.IsRunning && i < 250; i++) Thread.Sleep(20);
                Assert(!controller.IsRunning, "session did not stop/flush");
                Recording recording = RecordingReader.Load(controller.SessionDirectory);
                Assert(recording.Mode == "LIVE_EXPERIMENTAL", "live path substituted DEMO mode");
                Assert(recording.Samples.Exists(delegate(Sample s) { return s.Reply == 0xF4 && s.Values["rpm"] == 3000; }), "live decoder output absent");
                Assert(!recording.Events.Exists(delegate(LogEvent e) { return e.Kind == "fatal_error"; }), "protocol damage became fatal");
                Assert(recording.Events.Exists(delegate(LogEvent e) { return e.Kind == "link_restored"; }), "no successful live-frame event");
                Assert(attempts == 1, "manual stop must not reconnect");
                Assert(!recording.Events.Exists(delegate(LogEvent e) { return e.Kind == "link_lost"; }), "manual stop was mislabeled as link loss");
                string communication = File.ReadAllText(Path.Combine(controller.SessionDirectory, "communication.txt"), System.Text.Encoding.UTF8);
                Assert(communication.Contains("| RX | 55 | Инициализация") && communication.Contains("| TX | 38 | Инициализация"), "live text log lacks byte-level keyword handshake");
                Assert(communication.Contains("| RX | F4 | Приём байта кадра") && communication.Contains("| TX | 0B | Приём байта кадра"), "live text log lacks interleaved frame/ACK bytes");
                Assert(recording.Samples.Count == (includeInjection ? 2 : 1), "missing or invented samples");
                int outgoingFrames = 0;
                foreach (string line in File.ReadAllLines(Path.Combine(controller.SessionDirectory, "packets.jsonl")))
                {
                    if (!line.Contains("\"direction\":\"TX\"") || !line.Contains("\"kind\":\"frame\"")) continue;
                    outgoingFrames++;
                    Assert(line.Contains("03 01 09 03") || line.Contains("03 03 12 03") || (includeInjection && line.Contains("03 05 1C 03")), "non-read command frame transmitted");
                }
                Assert(outgoingFrames == (includeInjection ? 3 : 2), "outgoing frame trace incomplete");
            }
        }

        private static void WaitStopped(SessionController controller)
        {
            for (int i = 0; controller.IsRunning && i < 250; i++) Thread.Sleep(20);
            Assert(!controller.IsRunning, "link failure must terminate instead of waiting for automatic retry");
        }

        private static void CheckFailureStopsAndManualRestart(bool incompleteInitialization)
        {
            string root = Path.Combine(Path.GetTempPath(), "MotronicNoAutoRetry_" + Guid.NewGuid().ToString("N"));
            int attempts = 0;
            FakeTransport failedWire = null;
            using (ManualResetEvent sampled = new ManualResetEvent(false))
            using (SessionController controller = new SessionController(delegate(string name)
            {
                FakeTransport wire = new FakeTransport();
                if (Interlocked.Increment(ref attempts) == 1) {
                    failedWire = wire;
                    if (incompleteInitialization) foreach (byte value in new byte[] { 0x55, 0xC7, 0x38 }) wire.Input.Enqueue(value);
                    else { Keywords(wire); wire.Input.Enqueue(2); wire.Input.Enqueue(253); }
                }
                else { Keywords(wire); wire.FeedReceivedFrame(Primary()); }
                return wire;
            }, delegate(int milliseconds, CancellationToken token) { token.ThrowIfCancellationRequested(); }))
            {
                controller.SampleReceived += delegate { controller.Stop(); sampled.Set(); };
                SessionOptions options = new SessionOptions { Demo = false, PortName = "COM123", OutputDirectory = root,
                    DurationMinutes = 1, ExperimentalConfirmed = true };
                controller.Start(options); WaitStopped(controller);
                Assert(attempts == 1 && failedWire.Breaks.Count == 4, "failure initiated an extra port opening/BREAK sequence");
                Recording failed = RecordingReader.Load(controller.SessionDirectory);
                Assert(failed.Samples.Count == 0 && failed.Events.Exists(delegate(LogEvent item) { return item.Kind == "link_lost"; }), "failure must not fabricate measurements");
                Assert(failed.Events.Exists(delegate(LogEvent item) { return item.Kind == "polling_stopped" && item.Message.Contains("кнопкой «Подключиться и записывать»"); }), "manual retry guidance absent");
                Assert(!failed.Note.Contains("не имеет отметки") && failed.Note.Contains("после потери связи"), "link failure recording must be cleanly finalized with its reason");
                Assert(File.ReadAllText(Path.Combine(controller.SessionDirectory, "session.json")).Contains("\"completionReason\":\"link_lost\""), "link_lost completion reason absent");
                string text = File.ReadAllText(Path.Combine(controller.SessionDirectory, "communication.txt"), System.Text.Encoding.UTF8);
                Assert(text.Contains("| ERROR | -- |") && text.Contains(incompleteInitialization ? "второй ключевой" : "Недопустимая длина пакета: 2"), "partial failed exchange is missing");
                Assert(!text.Contains("| TX | 12 | Запрос"), "read request transmitted after initialization/frame error");
                string firstDirectory = controller.SessionDirectory;
                controller.Start(options);
                Assert(sampled.WaitOne(2000), "explicit manual restart did not acquire a valid sample");
                WaitStopped(controller);
                Assert(attempts == 2 && controller.SessionDirectory != firstDirectory, "manual restart must create a separate recording");
            }
        }
        public static void Run()
        {
            RunCase(false);
            RunCase(true);
            CheckFailureStopsAndManualRestart(false);
            CheckFailureStopsAndManualRestart(true);
            Console.WriteLine("Live path tests: passed (mock ECU init/identity/09/12/F4, optional1C/EE, failure stops without automatic BREAK/reconnect, explicit manual new session, partial-byte text logs, stop/reload, no DEMO substitution, outgoing read-only frame audit). No OS COM port opened.");
        }
    }
}
