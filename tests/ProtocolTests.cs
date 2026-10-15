using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace MotronicRecorder
{
    internal sealed class FakeTransport : IByteTransport
    {
        public readonly Queue<byte> Input = new Queue<byte>();
        public readonly List<byte> Output = new List<byte>();
        public readonly List<bool> Breaks = new List<bool>();
        public int Discards;
        public bool Dtr;
        public int ReadByte() { if (Input.Count == 0) throw new TimeoutException("simulated missing byte"); return Input.Dequeue(); }
        public void WriteByte(byte value) { Output.Add(value); }
        public void DiscardBuffers() { Discards++; }
        public bool BreakState { set { Breaks.Add(value); } }
        public bool DtrEnable { set { Dtr = value; } }
        public void Dispose() { }
        public void FeedReceivedFrame(byte[] frame)
        {
            for (int i = 0; i < frame.Length - 1; i++) { Input.Enqueue(frame[i]); Input.Enqueue((byte)(frame[i] ^ 255)); }
            Input.Enqueue(frame[frame.Length - 1]);
        }
        public void FeedSendAcknowledgements(byte[] frame)
        {
            for (int i = 0; i < frame.Length - 1; i++) { Input.Enqueue(frame[i]); Input.Enqueue((byte)(frame[i] ^ 255)); }
            Input.Enqueue(frame[frame.Length - 1]);
        }
    }

    public static class ProtocolTests
    {
        private sealed class WriteFailureTransport : IByteTransport
        {
            public int ReadByte() { throw new Exception("no read should follow failed write"); }
            public void WriteByte(byte value) { throw new TimeoutException("simulated write timeout"); }
            public void DiscardBuffers() { }
            public bool BreakState { set { } }
            public bool DtrEnable { set { } }
            public void Dispose() { }
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Protocol test: " + message); }
        private static void Near(double actual, double expected, string message) { Assert(Math.Abs(actual - expected) < 0.000001, message); }
        private static void MustFail(Action action, string message)
        {
            try { action(); } catch (InvalidDataException) { return; } catch (TimeoutException) { return; } catch (ArgumentOutOfRangeException) { return; }
            throw new Exception("Expected rejection: " + message);
        }
        private static void CheckByteTrace()
        {
            FakeTransport wire = new FakeTransport();
            wire.FeedSendAcknowledgements(FrameProtocol.BuildRequest(ReadRequest.MainParameters, 7));
            FrameProtocol protocol = new FrameProtocol(wire);
            double clock = 100;
            protocol.ElapsedMilliseconds = delegate { return ++clock; };
            protocol.SendRequest(ReadRequest.MainParameters, 7, CancellationToken.None);
            WireTraceRecord[] records = protocol.DrainWireTrace();
            Assert(records.Length == 11, "trace preserves four TX and seven RX bytes");
            Assert(records[0].Direction == "TX" && records[0].Value == 3 && records[1].Direction == "RX" && records[1].Value == 3 && records[2].Value == 252, "trace retains interleaved TX/echo/inverse ACK order");
            Assert(records[6].Direction == "TX" && records[6].Value == 0x12 && records[10].Value == 3, "command and final echo captured");
            for (int i = 0; i < records.Length; i++) {
                Assert(records[i].Utc != default(DateTimeOffset) && records[i].Stage.Length > 0, "every byte has UTC and stage");
                Assert(records[i].ElapsedMs == 101 + i, "per-byte monotonic capture precedes later disk drain");
            }
            Assert(protocol.DrainWireTrace().Length == 0, "drain cannot duplicate trace");

            wire = new FakeTransport(); wire.Input.Enqueue(3); wire.Input.Enqueue(252); wire.Input.Enqueue(0x11);
            protocol = new FrameProtocol(wire);
            MustFail(delegate { protocol.ReceiveFrame(CancellationToken.None); }, "partial frame timeout");
            records = protocol.DrainWireTrace();
            Assert(records.Length == 6 && records[3].Value == 0x11 && records[4].Direction == "TX" && records[4].Value == 0xEE, "partial frame and last ACK survive failure");
            Assert(records[5].Direction == "ERROR" && records[5].Detail.Contains("EE") && records[5].Detail.Contains("Эхо") && records[5].Stage.Contains("2/3"), "timeout identifies expected byte and stage");

            wire = new FakeTransport(); wire.Input.Enqueue(0x54); protocol = new FrameProtocol(wire);
            protocol.InitializationDelay = delegate(int ms, CancellationToken token) { };
            MustFail(delegate { protocol.Initialize(CancellationToken.None); }, "incorrect synchronization");
            records = protocol.DrainWireTrace();
            WireTraceRecord last = records[records.Length - 1];
            Assert(last.Direction == "ERROR" && last.Detail.Contains("55") && last.Detail.Contains("54"), "bad synchronization records actual and expected byte");
            Assert(records[records.Length - 2].Direction == "RX" && records[records.Length - 2].Value == 0x54, "bad sync byte retained");

            wire = new FakeTransport(); foreach (byte value in new byte[] { 0x55, 0xC7, 0x38 }) wire.Input.Enqueue(value);
            protocol = new FrameProtocol(wire); protocol.InitializationDelay = delegate(int ms, CancellationToken token) { };
            MustFail(delegate { protocol.Initialize(CancellationToken.None); }, "incomplete keyword initialization");
            records = protocol.DrainWireTrace(); last = records[records.Length - 1];
            Assert(last.Direction == "ERROR" && last.Stage.Contains("второй ключевой") && last.Detail.Contains("байт ЭБУ"), "incomplete initialization exposes missing keyword stage");
            Assert(wire.Output.Count == 1 && wire.Output[0] == 0x38, "failed initialization never emits an extra diagnostic request");

            protocol = new FrameProtocol(new WriteFailureTransport());
            MustFail(delegate { protocol.SendRequest(ReadRequest.MainParameters, 0, CancellationToken.None); }, "write timeout");
            records = protocol.DrainWireTrace();
            Assert(records.Length == 2 && records[0].Direction == "ERROR" && records[0].Value == 3 && records[0].Detail.Contains("байта 03"), "failed write identifies attempted HEX byte");
            Assert(!Array.Exists(records, delegate(WireTraceRecord item) { return item.Direction == "TX"; }), "failed driver write must not be labeled a completed TX");
        }
        public static void Run()
        {
            CancellationToken token = CancellationToken.None;
            CheckByteTrace();
            // The only command frames the program can construct. Exhaustive counter rollover coverage.
            foreach (ReadRequest request in new ReadRequest[] { ReadRequest.KeepAlive, ReadRequest.MainParameters, ReadRequest.InjectionAndPurge })
            for (int counter = 0; counter < 256; counter++)
            {
                byte[] frame = FrameProtocol.BuildRequest(request, (byte)counter);
                Assert(frame.Length == 4 && frame[0] == 3 && frame[1] == counter && frame[3] == 3, "request framing");
                Assert(frame[2] == 9 || frame[2] == 0x12 || frame[2] == 0x1C, "read allowlist");
            }
            MustFail(delegate { FrameProtocol.BuildRequest((ReadRequest)3, 0); }, "arbitrary request");
            MustFail(delegate { FrameProtocol.BuildRequest((ReadRequest)(-1), 0); }, "negative request");

            byte[] primary = new byte[17]; primary[0] = 16; primary[1] = 254; primary[2] = 0xF4;
            primary[3] = 128; primary[4] = 204; primary[5] = 100; primary[6] = 80; primary[10] = 64;
            primary[13] = 75; primary[15] = 10; primary[16] = 3;
            FakeTransport fake = new FakeTransport(); fake.FeedReceivedFrame(primary);
            FrameProtocol protocol = new FrameProtocol(fake);
            protocol.ElapsedMilliseconds = delegate { return 1234.5; };
            protocol.Trace = delegate(string direction, string kind, byte[] bytes) {
                if (kind == "frame") Assert(protocol.LastFrameElapsedMs == 1234.5 && protocol.LastFrameUtc != default(DateTimeOffset), "timestamp precedes trace/disk IO");
            };
            byte[] accepted = protocol.ReceiveFrame(token);
            Assert(BitConverter.ToString(accepted) == BitConverter.ToString(primary), "receive roundtrip");
            Assert(fake.Input.Count == 0 && fake.Output.Count == primary[0], "echo/ACK framing");
            for (int i = 0; i < fake.Output.Count; i++) Assert(fake.Output[i] == (byte)(primary[i] ^ 255), "inverted ACK");
            byte[] requestFrame = FrameProtocol.BuildRequest(ReadRequest.MainParameters, 255);
            fake = new FakeTransport(); fake.FeedSendAcknowledgements(requestFrame);
            new FrameProtocol(fake).SendRequest(ReadRequest.MainParameters, 255, token);
            Assert(BitConverter.ToString(fake.Output.ToArray()) == BitConverter.ToString(requestFrame), "transmit roundtrip");
            Assert(fake.Input.Count == 0, "no ACK expected after terminator");

            FakeTransport bad = new FakeTransport(); bad.Input.Enqueue(3); bad.Input.Enqueue(0);
            MustFail(delegate { new FrameProtocol(bad).ReceiveFrame(token); }, "bad own echo");
            bad = new FakeTransport(); bad.Input.Enqueue(2); bad.Input.Enqueue(253);
            MustFail(delegate { new FrameProtocol(bad).ReceiveFrame(token); }, "short length");
            bad = new FakeTransport(); bad.Input.Enqueue(65); bad.Input.Enqueue(190);
            MustFail(delegate { new FrameProtocol(bad).ReceiveFrame(token); }, "oversize length");
            bad = new FakeTransport(); bad.FeedReceivedFrame(new byte[] { 3, 0, 0x09, 0xFF });
            MustFail(delegate { new FrameProtocol(bad).ReceiveFrame(token); }, "bad terminator");
            bad = new FakeTransport(); bad.Input.Enqueue(3); bad.Input.Enqueue(3); bad.Input.Enqueue(0);
            MustFail(delegate { new FrameProtocol(bad).SendRequest(ReadRequest.MainParameters, 0, token); }, "bad ECU inverse ACK");
            bad = new FakeTransport();
            MustFail(delegate { new FrameProtocol(bad).ReceiveFrame(token); }, "missing bytes");
            CancellationTokenSource cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool cancellation = false;
            try { new FrameProtocol(bad).SendRequest(ReadRequest.MainParameters, 0, cancelled.Token); } catch (OperationCanceledException) { cancellation = true; }
            Assert(cancellation && bad.Output.Count == 0, "cancel does not transmit"); cancelled.Dispose();

            DateTimeOffset utc = DateTimeOffset.UtcNow;
            Sample sample = M15Decoder.Decode(primary, utc, 1234, 7);
            Near(sample.Values["rpm"], 3000, "native RPM quantization"); Near(sample.Values["voltage"], 14, "native voltage");
            Near(sample.Values["airflow"], 2.5, "native airflow"); Near(sample.Values["throttle"], 1.25, "native throttle");
            Near(sample.Values["air_temperature"], ProtocolTables.Temperature[100], "native air table");
            Near(sample.Values["coolant"], ProtocolTables.Temperature[80], "native coolant table");
            Near(sample.Values["ignition"], ProtocolTables.Ignition[10], "native ignition table");
            Assert(sample.Epoch == 7 && sample.ElapsedMs == 1234 && !sample.Values.ContainsKey("injection"), "sparse primary/timestamps");
            Near(sample.Values["engine_load"], 6.4, "original ECU load conversion");
            Assert(sample.Values["lambda_enabled"] == 0 && sample.Values.ContainsKey("co_potentiometer") && !sample.Values.ContainsKey("lambda_mv"), "non-lambda profile does not invent a lambda reading");
            for (int contact = 0; contact < 4; contact++) {
                primary[11] = (byte)(0xA4 | contact); primary[8] = 150;
                Sample configured = M15Decoder.Decode(primary, utc, 1240, 7);
                Near(configured.Values["throttle_switch"], contact, "contact bits remain independent of other configuration bits");
                Near(configured.Values["lambda_mv"], 730, "native x87 nearest-even lambda midpoint");
                Assert(configured.Values["lambda_enabled"] == 1 && !configured.Values.ContainsKey("co_potentiometer"), "shared byte has one interpreted sensor");
            }
            primary[8] = 50; Near(M15Decoder.Decode(primary, utc, 1240, 7).Values["lambda_mv"], 244, "lambda even rounding upward midpoint");
            primary[8] = 250; Near(M15Decoder.Decode(primary, utc, 1240, 7).Values["lambda_mv"], 1218, "lambda even rounding large midpoint");
            primary[8] = 255; Near(M15Decoder.Decode(primary, utc, 1240, 7).Values["lambda_mv"], 1242, "lambda full raw range");
            primary[8] = 128; primary[11] = 0x82;
            Sample withoutLambda = M15Decoder.Decode(primary, utc, 1240, 7);
            Near(withoutLambda.Values["co_potentiometer"], 2.5, "original CO conversion");
            Assert(!withoutLambda.Values.ContainsKey("lambda_mv") && withoutLambda.Values["throttle_switch"] == 2, "without lambda retains IDLE contact and CO only");
            byte[] secondary = new byte[11]; secondary[0] = 10; secondary[1] = 0; secondary[2] = 0xEE; secondary[4] = 0x84;
            secondary[5] = 0xE2; secondary[9] = 128; secondary[10] = 3;
            sample = M15Decoder.Decode(secondary, utc, 1250, 7);
            Near(sample.Values["injection"], 2.5, "native injection and bit mask"); Near(sample.Values["purge"], 50, "native purge");
            Assert(sample.Values.Count == 2 && !sample.Values.ContainsKey("rpm"), "no invented simultaneous RPM");
            MustFail(delegate { M15Decoder.Decode(new byte[] { 3, 0, 0xF4, 3 }, utc, 0, 0); }, "short F4");
            MustFail(delegate { M15Decoder.Decode(new byte[] { 3, 0, 0xEE, 3 }, utc, 0, 0); }, "short EE");
            Assert(M15Decoder.Decode(new byte[] { 3, 0, 0x09, 3 }, utc, 0, 0) == null, "unknown reply not interpreted");

            // Real recovered timing, but a fake wire: no COM port is opened.
            fake = new FakeTransport(); fake.Input.Enqueue(0x55); fake.Input.Enqueue(0x12); fake.Input.Enqueue(0xED); fake.Input.Enqueue(0x34); fake.Input.Enqueue(0xCB);
            new FrameProtocol(fake).Initialize(token);
            Assert(fake.Dtr && fake.Discards == 3 && fake.Input.Count == 0, "init DTR/purges/keyword handshake");
            Assert(BitConverter.ToString(fake.Output.ToArray()) == "ED-CB", "keywords individually complemented");
            Assert(fake.Breaks.Count == 4 && fake.Breaks[0] && !fake.Breaks[1] && fake.Breaks[2] && !fake.Breaks[3], "recovered BREAK sequence");
            Console.WriteLine("Protocol tests: passed (allowlist, framing, errors, cancellation, decoder, simulated initialization, timestamped interleaved bytes, partial-frame and keyword-error trace).");
        }
    }
}
