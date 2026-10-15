using System;
using System.IO;
using System.Threading;

namespace MotronicRecorder
{
    public static class PollingTests
    {
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Polling test: " + message); }
        private static void Near(double actual, double expected, string message) { Assert(Math.Abs(actual - expected) < 0.000001, message); }
        private static string ReadShared(string path)
        {
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(input)) return reader.ReadToEnd();
        }

        private sealed class FlushOrderWire : IByteTransport
        {
            private readonly FakeTransport wire = new FakeTransport();
            private int reads;
            public Action BeforeRequest, RequestComplete;
            public FlushOrderWire()
            {
                foreach (byte b in new byte[] { 0x55, 0xC7, 0x38, 0x80, 0x7F }) wire.Input.Enqueue(b);
                byte[] frame = new byte[17]; frame[0] = 16; frame[2] = 0xF4;
                frame[13] = 75; frame[16] = 3;
                wire.FeedReceivedFrame(frame);
                wire.FeedSendAcknowledgements(FrameProtocol.BuildRequest(ReadRequest.MainParameters, 1));
            }
            public int ReadByte()
            {
                int result = wire.ReadByte(); reads++;
                // Five keyword bytes + 33 receive bytes + seven request echo/ACK bytes.
                if (reads == 45 && RequestComplete != null) RequestComplete();
                return result;
            }
            public void WriteByte(byte value)
            {
                if (reads == 38 && BeforeRequest != null) { Action callback = BeforeRequest; BeforeRequest = null; callback(); }
                wire.WriteByte(value);
            }
            public void DiscardBuffers() { wire.DiscardBuffers(); }
            public bool BreakState { set { wire.BreakState = value; } }
            public bool DtrEnable { set { wire.DtrEnable = value; } }
            public void Dispose() { wire.Dispose(); }
        }

        private static void CheckDurableFlushBeforeRequest(bool manual)
        {
            string root = Path.Combine(Path.GetTempPath(), "MotronicFlushOrder_" + Guid.NewGuid().ToString("N"));
            bool sawRequest = false, sampleWasFlushed = false, rxWasFlushed = false;
            FlushOrderWire wire = new FlushOrderWire();
            using (ManualResetEvent completed = new ManualResetEvent(false))
            using (SessionController controller = new SessionController(delegate(string name)
            {
                // Advance the real controller clock past its periodic-flush boundary;
                // this is an in-memory wire and cannot open an OS serial port.
                if (!manual) Thread.Sleep(1100); return wire;
            }, delegate(int milliseconds, CancellationToken token) { token.ThrowIfCancellationRequested(); }))
            {
                wire.BeforeRequest = delegate
                {
                    sawRequest = true;
                    sampleWasFlushed = ReadShared(Path.Combine(controller.SessionDirectory, "samples.csv")).Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length == 2;
                    rxWasFlushed = ReadShared(Path.Combine(controller.SessionDirectory, "packets.jsonl")).Contains("\"direction\":\"RX\",\"kind\":\"frame\"");
                };
                if (manual) controller.SampleReceived += delegate(Sample sample) { controller.Mark("manual_between_frames"); };
                wire.RequestComplete = delegate { controller.Mark("pending_mark_before_stop"); controller.Stop(); completed.Set(); };
                controller.Start(new SessionOptions { Demo = false, PortName = "COM123", OutputDirectory = root,
                    DurationMinutes = 1, ExperimentalConfirmed = true });
                Assert(completed.WaitOne(5000), "mock request did not complete");
                for (int i = 0; controller.IsRunning && i < 250; i++) Thread.Sleep(20);
                Assert(!controller.IsRunning, "session did not finish");
                Assert(sawRequest && sampleWasFlushed && rxWasFlushed, "sample and RX trace must be durable before next request begins");
                Recording saved = RecordingReader.Load(controller.SessionDirectory);
                Assert(saved.Samples.Count == 1 && saved.Samples[0].Values["rpm"] == 3000, "flush ordering changed recorded sample");
                Assert(!saved.Events.Exists(delegate(LogEvent e) { return e.Kind == "fatal_error" || e.Kind == "link_lost"; }), "normal request flow failed");
                Assert(saved.Events.Exists(delegate(LogEvent e) { return e.Kind == "manual" && e.Message == "pending_mark_before_stop"; }), "stop lost a pending manual mark");
                if (manual) Assert(saved.Events.Exists(delegate(LogEvent e) { return e.Kind == "manual" && e.Message == "manual_between_frames"; }), "manual mark did not survive worker flush");
            }
        }

        private sealed class SecondaryLimiterWire : IByteTransport
        {
            private readonly FakeTransport wire = new FakeTransport();
            private int reads;
            public Action Completed;
            public SecondaryLimiterWire()
            {
                foreach (byte b in new byte[] { 0x55, 0xC7, 0x38, 0x80, 0x7F }) wire.Input.Enqueue(b);
                byte[] frame = new byte[17]; frame[0] = 16; frame[2] = 0xF4; frame[13] = 75; frame[16] = 3;
                wire.FeedReceivedFrame(frame);
                wire.FeedSendAcknowledgements(FrameProtocol.BuildRequest(ReadRequest.InjectionAndPurge, 1));
                wire.FeedReceivedFrame(new byte[] { 10, 2, 0xEE, 0, 0, 0, 0, 0, 0, 0, 3 });
                wire.FeedSendAcknowledgements(FrameProtocol.BuildRequest(ReadRequest.MainParameters, 3));
                frame[1] = 4; wire.FeedReceivedFrame(frame);
                // The old limiter selected 1C here after a slow first disk flush.
                wire.FeedSendAcknowledgements(FrameProtocol.BuildRequest(ReadRequest.MainParameters, 5));
            }
            public int ReadByte() { int result = wire.ReadByte(); if (++reads == 113 && Completed != null) Completed(); return result; }
            public void WriteByte(byte value) { wire.WriteByte(value); }
            public void DiscardBuffers() { wire.DiscardBuffers(); }
            public bool BreakState { set { wire.BreakState = value; } }
            public bool DtrEnable { set { wire.DtrEnable = value; } }
            public void Dispose() { wire.Dispose(); }
        }

        private static void CheckSlowDiskSecondaryLimiter()
        {
            string root = Path.Combine(Path.GetTempPath(), "MotronicSecondaryLimit_" + Guid.NewGuid().ToString("N"));
            SecondaryLimiterWire wire = new SecondaryLimiterWire();
            bool slowed = false;
            using (ManualResetEvent completed = new ManualResetEvent(false))
            using (SessionController controller = new SessionController(delegate(string name) { return wire; }, delegate(int ms, CancellationToken token) { token.ThrowIfCancellationRequested(); }))
            {
                controller.BeforeStorageFlush = delegate { if (!slowed) { slowed = true; Thread.Sleep(2100); } };
                controller.SampleReceived += delegate(Sample sample) { if (sample.Reply == 0xF4 && sample.Counter == 0) controller.Mark("force slow disk before first1C"); };
                wire.Completed = delegate { controller.Stop(); completed.Set(); };
                controller.Start(new SessionOptions { Demo = false, PortName = "COM123", OutputDirectory = root,
                    DurationMinutes = 1, ExperimentalConfirmed = true, IncludeInjection = true });
                Assert(completed.WaitOne(6000), "slow flush caused premature repeated1C instead of normal12");
                for (int i = 0; controller.IsRunning && i < 250; i++) Thread.Sleep(20);
                Assert(!controller.IsRunning && slowed, "slow flush test did not finish");
                string packets = File.ReadAllText(Path.Combine(controller.SessionDirectory, "packets.jsonl"));
                Assert(packets.Contains("03 01 1C 03") && packets.Contains("03 03 12 03") && packets.Contains("03 05 12 03"), "secondary limiter must use actual post-flush send time");
                Recording recording = RecordingReader.Load(controller.SessionDirectory);
                Assert(!recording.Events.Exists(delegate(LogEvent item) { return item.Kind == "link_lost" || item.Kind == "fatal_error"; }), "slow disk exchange failed");
            }
        }

        public static void Run()
        {
            RecentFrameRate rate = new RecentFrameRate(4);
            rate.Add(1000); Near(rate.Hertz, 0, "one frame is not a measured frequency");
            rate.Add(1250); rate.Add(1500); rate.Add(2000);
            Near(rate.Hertz, 3, "frequency uses all real intervals, including slower gaps");
            rate.Add(2250); Near(rate.Hertz, 3, "rolling window evicts oldest frame");
            rate.Add(2300); Near(rate.Hertz, 3.75, "rolling frequency updates without rendering FPS");
            rate.Add(2300); Assert(rate.Count == 1 && rate.Hertz == 0, "equal timestamp resets invalid interval");
            rate.Add(100); Assert(rate.Count == 1 && rate.Hertz == 0, "new connection clock cannot span old gap");
            SecondaryRequestGate secondary = new SecondaryRequestGate();
            Assert(secondary.IsDue(0), "first optional request is allowed");
            secondary.AttemptCompleted(2500);
            Assert(!secondary.IsDue(4499.999) && secondary.IsDue(4500), "secondary interval is at least 2000ms after completed send");
            // An unsuccessful1C attempt also restarts its guard because the ECU
            // may have seen it. Production stops the session after a link error.
            secondary.AttemptCompleted(6000);
            Assert(!secondary.IsDue(7000) && secondary.IsDue(8000), "failed attempt cannot reset secondary history");
            CheckDurableFlushBeforeRequest(false);
            CheckDurableFlushBeforeRequest(true);
            CheckSlowDiskSecondaryLimiter();
            Console.WriteLine("Polling tests: passed (fresh-frame rolling Hz, periodic/manual flush before next read request, pending mark on stop, optional1C>=2000ms despite slow disk and retained guard). No OS COM port opened.");
        }
    }
}
