using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Threading;

namespace MotronicRecorder
{
    // This transport is deliberately narrow. No arbitrary diagnostic command API exists.
    public enum ReadRequest { KeepAlive, MainParameters, InjectionAndPurge }

    public interface IByteTransport : IDisposable
    {
        int ReadByte();
        void WriteByte(byte value);
        void DiscardBuffers();
        bool BreakState { set; }
        bool DtrEnable { set; }
    }

    internal sealed class SerialByteTransport : IByteTransport
    {
        private readonly SerialPort port;
        public SerialByteTransport(string name)
        {
            port = new SerialPort(name, 9600, Parity.None, 8, StopBits.One);
            port.Handshake = Handshake.None;
            port.DtrEnable = false;
            port.RtsEnable = false;
            port.ReadTimeout = 500;
            // Original COMMTIMEOUTS at VA 54F07C: constant 1000 ms +
            // multiplier 100 ms per byte. This transport always writes one byte.
            port.WriteTimeout = 1100;
            try { port.Open(); } catch { port.Dispose(); throw; }
        }
        public int ReadByte() { return port.ReadByte(); }
        public void WriteByte(byte value) { port.Write(new byte[] { value }, 0, 1); }
        public void DiscardBuffers() { port.DiscardInBuffer(); port.DiscardOutBuffer(); }
        public bool BreakState { set { port.BreakState = value; } }
        public bool DtrEnable { set { port.DtrEnable = value; } }
        public void Dispose()
        {
            try { if (port.IsOpen) port.BreakState = false; } catch { }
            port.Dispose();
        }
    }

    public sealed class FrameProtocol
    {
        private readonly IByteTransport transport;
        private readonly List<WireTraceRecord> pendingWireTrace = new List<WireTraceRecord>();
        public Action<string, string, byte[]> Trace;
        public Action<string> ControlTrace;
        public Func<double> ElapsedMilliseconds;
        public DateTimeOffset LastFrameUtc { get; private set; }
        public double LastFrameElapsedMs { get; private set; }
        internal Action<int, CancellationToken> InitializationDelay;
        public string Stage { get; private set; }
        public FrameProtocol(IByteTransport transport) { this.transport = transport; }

        public static byte[] BuildRequest(ReadRequest kind, byte counter)
        {
            byte command;
            switch (kind)
            {
                case ReadRequest.KeepAlive: command = 0x09; break;
                case ReadRequest.MainParameters: command = 0x12; break;
                case ReadRequest.InjectionAndPurge: command = 0x1C; break;
                default: throw new ArgumentOutOfRangeException("kind", "Only verified read requests are permitted.");
            }
            return new byte[] { 3, counter, command, 3 };
        }

        private void Wire(string direction, byte? value, string detail)
        {
            // No callbacks, formatting or file access in the per-byte path.
            pendingWireTrace.Add(new WireTraceRecord { Utc = DateTimeOffset.UtcNow,
                ElapsedMs = ElapsedMilliseconds == null ? 0 : ElapsedMilliseconds(),
                Direction = direction, Stage = Stage ?? "Обмен M1.5", Value = value, Detail = detail });
        }

        internal WireTraceRecord[] DrainWireTrace()
        {
            WireTraceRecord[] result = pendingWireTrace.ToArray();
            pendingWireTrace.Clear();
            return result;
        }

        private int Read(CancellationToken token, List<byte> received, string expected)
        {
            token.ThrowIfCancellationRequested();
            int value;
            try { value = transport.ReadByte(); }
            catch (TimeoutException ex) { throw new TimeoutException((Stage ?? "Обмен M1.5") + ": ожидалось " + expected + "; байт не получен. " + ex.Message, ex); }
            catch (IOException ex) { throw new IOException((Stage ?? "Обмен M1.5") + ": ожидалось " + expected + "; чтение не завершено. " + ex.Message, ex); }
            if (value < 0 || value > 255) throw new EndOfStreamException((Stage ?? "Обмен M1.5") + ": ожидалось " + expected + "; нет байта от адаптера.");
            received.Add((byte)value);
            Wire("RX", (byte)value, expected);
            return value;
        }

        private void ReadExpected(CancellationToken token, List<byte> received, byte expected, string purpose)
        {
            int actual = Read(token, received, purpose + " " + expected.ToString("X2"));
            if (actual != expected) throw new InvalidDataException((Stage ?? "Обмен M1.5") + ": " + purpose + "; ожидался " + expected.ToString("X2") + ", получен " + actual.ToString("X2") + ". Обмен остановлен.");
        }

        private void Write(byte value, CancellationToken token, List<byte> sent)
        {
            token.ThrowIfCancellationRequested();
            try { transport.WriteByte(value); }
            catch (Exception ex) {
                Wire("ERROR", value, "Попытка передачи байта " + value.ToString("X2") + ": драйвер не подтвердил завершение записи; состояние линии неизвестно. " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }
            sent.Add(value);
            Wire("TX", value, "Передано драйверу адаптера");
        }

        private byte ReceiveAcknowledged(CancellationToken token, List<byte> received, List<byte> sent)
        {
            byte value = (byte)Read(token, received, "байт ЭБУ с последующим инверсным подтверждением");
            byte inverted = (byte)(value ^ 0xFF);
            Write(inverted, token, sent);
            ReadExpected(token, received, inverted, "Эхо подтверждения KKL");
            return value;
        }

        private void Emit(string direction, string kind, IList<byte> bytes)
        {
            if (Trace == null || bytes.Count == 0) return;
            byte[] copy = new byte[bytes.Count];
            for (int i = 0; i < bytes.Count; i++) copy[i] = bytes[i];
            Trace(direction, kind, copy);
        }

        public byte[] ReceiveFrame(CancellationToken token)
        {
            List<byte> rx = new List<byte>(), tx = new List<byte>();
            bool valid = false;
            try
            {
                Stage = "Приём длины кадра";
                int length = ReceiveAcknowledged(token, rx, tx);
                if (length < 3 || length > 64) throw new InvalidDataException("Недопустимая длина пакета: " + length);
                byte[] frame = new byte[length + 1];
                frame[0] = (byte)length;
                for (int i = 1; i < length; i++) { Stage = "Приём байта кадра " + (i + 1) + "/" + length; frame[i] = ReceiveAcknowledged(token, rx, tx); }
                Stage = "Приём конца кадра";
                ReadExpected(token, rx, 0x03, "Конец пакета");
                frame[length] = 0x03;
                // Stamp reception before any logging, decoding, or UI work can delay it.
                LastFrameElapsedMs = ElapsedMilliseconds == null ? 0 : ElapsedMilliseconds();
                LastFrameUtc = DateTimeOffset.UtcNow;
                valid = true;
                Emit("RX", "frame", frame);
                return frame;
            }
            catch (Exception ex) { Wire("ERROR", null, ex.GetType().Name + ": " + ex.Message); throw; }
            finally
            {
                Emit("RX", valid ? "receive_wire" : "receive_failed_wire", rx);
                Emit("TX", "receive_acknowledgements", tx);
            }
        }

        public void SendRequest(ReadRequest kind, byte counter, CancellationToken token)
        {
            byte[] frame = BuildRequest(kind, counter);
            List<byte> rx = new List<byte>(), tx = new List<byte>();
            bool valid = false;
            try
            {
                for (int i = 0; i < frame.Length - 1; i++)
                {
                    Stage = "Запрос " + frame[2].ToString("X2") + ", байт " + (i + 1) + ": эхо и подтверждение";
                    byte value = frame[i];
                    Write(value, token, tx);
                    ReadExpected(token, rx, value, "Эхо запроса KKL");
                    ReadExpected(token, rx, (byte)(value ^ 0xFF), "Инверсное подтверждение ЭБУ");
                }
                Stage = "Эхо конца запроса " + frame[2].ToString("X2");
                Write(0x03, token, tx);
                ReadExpected(token, rx, 0x03, "Эхо конца запроса");
                valid = true;
                Emit("TX", "frame", frame);
            }
            catch (Exception ex) { Wire("ERROR", null, ex.GetType().Name + ": " + ex.Message); throw; }
            finally
            {
                Emit("TX", valid ? "send_wire" : "send_failed_wire", tx);
                Emit("RX", "send_echo_and_acknowledgements", rx);
            }
        }

        private void Wait(int ms, CancellationToken token)
        {
            if (InitializationDelay != null) { InitializationDelay(ms, token); return; }
            if (token.WaitHandle.WaitOne(ms)) token.ThrowIfCancellationRequested();
        }
        private void Break(bool state, int milliseconds, CancellationToken token)
        {
            transport.BreakState = state;
            Wire("CONTROL", null, "BREAK=" + state + "; " + milliseconds + " ms");
            if (ControlTrace != null) ControlTrace("BREAK=" + state + "; " + milliseconds + " ms");
            if (milliseconds > 0) Wait(milliseconds, token);
        }

        public void Initialize(CancellationToken token)
        {
            // Exact M1.5 wake sequence recovered from original VA 54F07C / 54F100 / 54F1C0.
            // Still requires validation on the user's ECU and USB adapter.
            List<byte> rx = new List<byte>(), tx = new List<byte>();
            try
            {
                token.ThrowIfCancellationRequested();
                Stage = "Настройка COM-порта";
                transport.DiscardBuffers();
                Wire("CONTROL", null, "Очистка входного и выходного буфера адаптера");
                transport.DtrEnable = true;
                Wire("CONTROL", null, "9600,8N1; DTR=True; RTS=False; ReadTimeout=500 ms; WriteTimeout=1100 ms (пределы ожидания, не паузы опроса)");
                if (ControlTrace != null) ControlTrace("9600,8N1; DTR=True; RTS=False");
                transport.DiscardBuffers();
                Wire("CONTROL", null, "Очистка буферов; ожидание 1000 ms перед BREAK");
                Wait(1000, token);
                Break(true, 1000, token);
                Break(false, 200, token);
                Break(true, 600, token);
                Break(false, 0, token);
                transport.DiscardBuffers();
                Wire("CONTROL", null, "Очистка буферов; ожидание 100 ms перед синхронизацией");
                Wait(100, token);
                Stage = "Инициализация: ожидание синхронизации 55";
                if (ControlTrace != null) ControlTrace(Stage);
                ReadExpected(token, rx, 0x55, "Синхронизация M1.5");
                // The original acknowledges EACH keyword separately, including local echo.
                Stage = "Инициализация: первый ключевой байт и эхо";
                ReceiveAcknowledged(token, rx, tx);
                Stage = "Инициализация: второй ключевой байт и эхо";
                ReceiveAcknowledged(token, rx, tx);
                if (ControlTrace != null) ControlTrace("Ключевые байты приняты; ожидание первого кадра ЭБУ");
            }
            catch (Exception ex) { Wire("ERROR", null, ex.GetType().Name + ": " + ex.Message); throw; }
            finally
            {
                Emit("RX", "initialization_wire", rx);
                Emit("TX", "initialization_acknowledgements", tx);
            }
        }
    }

    public static class M15Decoder
    {
        public static Sample Decode(byte[] frame, DateTimeOffset utc, double elapsedMs, int epoch)
        {
            if (frame == null || frame.Length < 4 || frame[0] != frame.Length - 1 || frame[frame.Length - 1] != 3)
                throw new InvalidDataException("Повреждённый кадр.");
            if (frame[2] != 0xF4 && frame[2] != 0xEE) return null;
            Sample sample = new Sample { Utc = utc, ElapsedMs = elapsedMs, Epoch = epoch, Counter = frame[1], Reply = frame[2] };
            if (frame[2] == 0xF4)
            {
                if (frame.Length < 17) throw new InvalidDataException("Короткий ответ F4; показания отклонены.");
                sample.Values["rpm"] = frame[13] * 40.0;
                sample.Values["voltage"] = frame[4] / 15.0 + 0.4;
                sample.Values["airflow"] = frame[3] * 5.0 / 256.0;
                sample.Values["throttle"] = frame[10] * 5.0 / 256.0;
                sample.Values["air_temperature"] = ProtocolTables.Temperature[frame[5]];
                sample.Values["coolant"] = ProtocolTables.Temperature[frame[6]];
                sample.Values["ignition"] = ProtocolTables.Ignition[frame[15]];
                sample.Values["engine_load"] = frame[3] * 0.05;
                sample.Values["throttle_switch"] = frame[11] & 3;
                bool lambdaConfigured = (frame[11] & 0x20) != 0;
                sample.Values["lambda_enabled"] = lambdaConfigured ? 1 : 0;
                // Original CO and lambda conversions share F4[8]. Interpret it
                // according to the reported ECU configuration, not as two sensors.
                // The flag cannot prove that a physical sensor is connected/healthy.
                if (lambdaConfigured) sample.Values["lambda_mv"] = Math.Round(frame[8] * 4.87, MidpointRounding.ToEven);
                else sample.Values["co_potentiometer"] = frame[8] * 5.0 / 256.0;
            }
            else
            {
                if (frame.Length < 11) throw new InvalidDataException("Короткий ответ EE; показания отклонены.");
                sample.Values["injection"] = (((frame[4] & 0x7F) << 8) | frame[5]) / 500.0;
                sample.Values["purge"] = frame[9] * 100.0 / 256.0;
            }
            return sample;
        }
    }
}
