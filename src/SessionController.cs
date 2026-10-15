using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace MotronicRecorder
{
    internal sealed class RecentFrameRate
    {
        private readonly Queue<double> received = new Queue<double>();
        private readonly int capacity;
        private double last;
        public RecentFrameRate(int capacity)
        {
            if (capacity < 2) throw new ArgumentOutOfRangeException("capacity");
            this.capacity = capacity;
        }
        public int Count { get { return received.Count; } }
        public double Hertz
        {
            get { return received.Count < 2 || last <= received.Peek() ? 0 : (received.Count - 1) * 1000.0 / (last - received.Peek()); }
        }
        public void Add(double elapsedMs)
        {
            if (Double.IsNaN(elapsedMs) || Double.IsInfinity(elapsedMs) || elapsedMs < 0) throw new ArgumentOutOfRangeException("elapsedMs");
            if (received.Count > 0 && elapsedMs <= last) received.Clear();
            received.Enqueue(elapsedMs); last = elapsedMs;
            while (received.Count > capacity) received.Dequeue();
        }
    }

    internal sealed class RecordingFailureException : Exception
    {
        public RecordingFailureException(Exception inner) : base("Ошибка сохранения журнала: " + inner.Message, inner) { }
    }

    internal sealed class SecondaryRequestGate
    {
        private double lastAttemptCompleted = Double.NegativeInfinity;
        public bool IsDue(double elapsedMs) { return elapsedMs - lastAttemptCompleted >= 2000; }
        // A failed attempt may already have reached the ECU. Conservatively apply
        // the same guard after both success and failure within the session.
        public void AttemptCompleted(double elapsedMs) { lastAttemptCompleted = elapsedMs; }
    }

    public sealed class SessionController : IDisposable
    {
        private readonly object gate = new object();
        private volatile bool running;
        private volatile bool manualFlushRequested;
        private CancellationTokenSource cancellation;
        private Thread worker;
        private SessionStorage storage;
        private readonly Stopwatch clock = new Stopwatch();
        private int epoch;
        private Sample previous;
        private double lastAnomaly = -10000;
        private readonly Func<string, IByteTransport> transportFactory;
        private readonly Action<int, CancellationToken> initializationDelay;
        private readonly List<PacketRecord> pendingPackets = new List<PacketRecord>();
        private readonly List<LogEvent> pendingControls = new List<LogEvent>();
        internal Action BeforeStorageFlush { get; set; }
        public bool IsRunning { get { return running; } }
        public string SessionDirectory { get; private set; }
        public event Action<Sample> SampleReceived;
        public event Action<LogEvent> EventReceived;
        public event Action<string> StateChanged;

        public SessionController() : this(delegate(string name) { return new SerialByteTransport(name); }, null) { }
        // Test seam: the integration tests use an in-memory wire, never an OS serial port.
        internal SessionController(Func<string, IByteTransport> factory, Action<int, CancellationToken> delay)
        { transportFactory = factory; initializationDelay = delay; }

        public void Start(SessionOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            // Freeze UI inputs before starting a worker or opening a port.
            options = new SessionOptions { Demo = options.Demo, PortName = options.PortName,
                OutputDirectory = options.OutputDirectory, DurationMinutes = options.DurationMinutes,
                IncludeInjection = options.IncludeInjection, ExperimentalConfirmed = options.ExperimentalConfirmed };
            if (options.DurationMinutes < 1 || options.DurationMinutes > 240) throw new ArgumentException("Длительность: от 1 до 240 минут.");
            if (!options.Demo && !options.ExperimentalConfirmed) throw new ArgumentException("Режим соединения с автомобилем должен быть выбран явно.");
            if (!options.Demo && !Regex.IsMatch(options.PortName ?? "", @"^COM[1-9][0-9]{0,3}$", RegexOptions.IgnoreCase))
                throw new ArgumentException("Укажите номер COM-порта адаптера, например COM3.");
            lock (gate)
            {
                if (running) throw new InvalidOperationException("Запись уже идёт.");
                storage = new SessionStorage(options.OutputDirectory, options.Demo ? "DEMO" : "LIVE_EXPERIMENTAL", options.PortName);
                SessionDirectory = storage.DirectoryPath;
                cancellation = new CancellationTokenSource();
                cancellation.CancelAfter(TimeSpan.FromMinutes(options.DurationMinutes));
                epoch = 0; previous = null; lastAnomaly = -10000; manualFlushRequested = false;
                pendingPackets.Clear(); pendingControls.Clear();
                clock.Restart();
                running = true;
                worker = new Thread(delegate() { Run(options, cancellation.Token); });
                worker.Name = "Motronic recorder";
                worker.IsBackground = true;
                worker.Start();
            }
        }

        public void Stop() { lock (gate) { if (running && cancellation != null) cancellation.Cancel(); } }
        private void State(string message) { Action<string> handler = StateChanged; if (handler != null) handler(message); }

        private void Store(Action action)
        {
            try { action(); }
            catch (IOException ex) { throw new RecordingFailureException(ex); }
            catch (UnauthorizedAccessException ex) { throw new RecordingFailureException(ex); }
        }
        private void FlushStorage() { Store(delegate { if (BeforeStorageFlush != null) BeforeStorageFlush(); storage.Flush(); }); }

        private void Event(string kind, string message)
        {
            LogEvent item = new LogEvent { Utc = DateTimeOffset.UtcNow, ElapsedMs = clock.Elapsed.TotalMilliseconds, Epoch = epoch, Kind = kind, Message = message };
            PublishEvent(item);
        }

        private void PublishEvent(LogEvent item)
        {
            SessionStorage target = storage;
            if (target != null) Store(delegate { target.WriteEvent(item); });
            Action<LogEvent> handler = EventReceived; if (handler != null) handler(item);
        }

        public void Mark(string message)
        {
            lock (gate)
            {
                if (!running) return;
                try
                {
                    Event("manual", String.IsNullOrWhiteSpace(message) ? "Отметка водителя / наблюдателя" : message);
                    // The worker flushes between complete protocol exchanges. A UI
                    // marker must not hold the disk lock while an ECU byte needs ACK.
                    manualFlushRequested = true;
                }
                catch (RecordingFailureException ex)
                {
                    cancellation.Cancel();
                    State(ex.Message + ". Запись останавливается.");
                }
            }
        }

        private void Packet(string direction, string kind, byte[] data)
        {
            // A request can cause an immediate ECU reply. Retain even the old
            // JSONL packet trace in memory until that reply has been received.
            pendingPackets.Add(new PacketRecord { Utc = DateTimeOffset.UtcNow, ElapsedMs = clock.Elapsed.TotalMilliseconds,
                Epoch = epoch, Direction = direction, Kind = kind, Hex = BitConverter.ToString(data).Replace('-', ' ') });
        }

        private void DrainExchangeTrace(FrameProtocol protocol)
        {
            WireTraceRecord[] wire = protocol.DrainWireTrace();
            Store(delegate { storage.WriteWireTrace(wire, epoch); });
            foreach (PacketRecord packet in pendingPackets) Store(delegate { storage.WritePacket(packet); });
            pendingPackets.Clear();
            foreach (LogEvent entry in pendingControls) PublishEvent(entry);
            pendingControls.Clear();
        }

        private void Publish(Sample sample)
        {
            Store(delegate { storage.WriteSample(sample); });
            Action<Sample> handler = SampleReceived; if (handler != null) handler(sample);
            if (sample.Values.ContainsKey("rpm"))
            {
                if (previous != null && previous.Epoch == sample.Epoch && sample.ElapsedMs - previous.ElapsedMs < 1200 && sample.ElapsedMs - lastAnomaly > 2000)
                {
                    double drop = previous.Values["rpm"] - sample.Values["rpm"];
                    if (drop >= 400)
                    {
                        lastAnomaly = sample.ElapsedMs;
                        Event("rpm_drop", "Снижение показаний оборотов на " + Sensors.Number(drop) + " об/мин. Отметка отклонения, не диагноз.");
                    }
                }
                previous = sample;
            }
        }

        private void Run(SessionOptions options, CancellationToken token)
        {
            string reason = "stopped";
            string fatalMessage = "";
            try
            {
                Event("session_start", options.Demo ? "ДЕМОНСТРАЦИЯ. Все показания искусственные; COM-порт не открывается." : "M1.5: разрешены только 09,12,1C и служебные подтверждения. Версия 0.1.1 подтверждена реальными логами владельца; версия " + ProductInfo.Version + " требует проверки на автомобиле.");
                State(options.Demo ? "ДЕМО: искусственные показания, автомобиль не подключается" : "Подключение M1.5: только чтение; проверка версии " + ProductInfo.Version);
                if (options.Demo) RunDemo(options, token); else reason = RunLive(options, token);
            }
            catch (OperationCanceledException) { reason = clock.Elapsed.TotalMinutes >= options.DurationMinutes - 0.01 ? "duration_reached" : "stopped"; }
            catch (Exception ex)
            {
                reason = "error";
                fatalMessage = ex.Message;
                try { Event("fatal_error", ex.GetType().Name + ": " + ex.Message); } catch { }
                State("Ошибка записи: " + ex.Message);
            }
            finally
            {
                lock (gate)
                {
                    try { Event("session_end", reason); storage.Complete(reason); }
                    catch (Exception ex) { State("Не удалось завершить журнал: " + ex.Message); }
                    finally
                    {
                        try { if (storage != null) storage.Dispose(); }
                        catch (Exception ex) { State("Ошибка закрытия файлов журнала: " + ex.Message); }
                        finally { storage = null; clock.Stop(); running = false; }
                    }
                }
                State((reason == "error" ? "Запись остановлена: " + fatalMessage : reason == "link_lost" ? "Опрос остановлен из-за потери связи. Проверьте подключение; повторный запуск — кнопкой «Подключиться и записывать»" : "Запись завершена: " + reason) + ". " + SessionDirectory);
            }
        }

        private void RunDemo(SessionOptions options, CancellationToken token)
        {
            epoch = 1;
            bool lost = false, resumed = false;
            double nextMain = 0, nextSecondary = 0, lastFlush = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                double ms = clock.Elapsed.TotalMilliseconds;
                if (ms >= 30000 && !lost) { lost = true; Event("link_lost", "ДЕМО: искусственный обрыв связи на 1,5 секунды."); State("ДЕМО: модель разрыва связи"); }
                if (lost && ms >= 31500 && !resumed) { resumed = true; epoch++; Event("link_restored", "ДЕМО: искусственное восстановление связи."); State("ДЕМО: связь в модели восстановлена"); }
                if (!(lost && !resumed))
                {
                    if (ms >= nextMain) { Sample item = DemoSource.Sample(ms, epoch, false); item.Utc = DateTimeOffset.UtcNow; Publish(item); nextMain = ms + 200; }
                    if (options.IncludeInjection && ms >= nextSecondary) { Sample item = DemoSource.Sample(ms, epoch, true); item.Utc = DateTimeOffset.UtcNow; Publish(item); nextSecondary = ms + 1000; }
                }
                if (ms - lastFlush >= 1000 || manualFlushRequested) { manualFlushRequested = false; FlushStorage(); lastFlush = ms; }
                if (token.WaitHandle.WaitOne(20)) token.ThrowIfCancellationRequested();
            }
        }

        private string RunLive(SessionOptions options, CancellationToken token)
        {
            SecondaryRequestGate secondaryGate = new SecondaryRequestGate();
            token.ThrowIfCancellationRequested();
            epoch++;
            previous = null;
            string failure = "Нет ответа";
            FrameProtocol protocol = null;
            try
            {
                State("M1.5: установление связи, попытка " + epoch);
                Event("connect_attempt", options.PortName + "; epoch=" + epoch);
                try
                {
                    using (IByteTransport serial = transportFactory(options.PortName))
                    {
                        protocol = new FrameProtocol(serial);
                        protocol.InitializationDelay = initializationDelay;
                        protocol.ElapsedMilliseconds = delegate { return clock.Elapsed.TotalMilliseconds; };
                        protocol.Trace = Packet;
                        protocol.ControlTrace = delegate(string text) { pendingControls.Add(new LogEvent { Utc = DateTimeOffset.UtcNow,
                            ElapsedMs = clock.Elapsed.TotalMilliseconds, Epoch = epoch, Kind = "serial_control", Message = text }); };
                        protocol.Initialize(token);
                        // The ECU can send its first frame immediately after the
                        // keywords. Do not touch disk before receiving that frame.
                        pendingControls.Add(new LogEvent { Utc = DateTimeOffset.UtcNow, ElapsedMs = clock.Elapsed.TotalMilliseconds,
                            Epoch = epoch, Kind = "link_initialized", Message = "Получены синхронизация и ключевые байты; идентификация профиля не гарантирована." });
                        int unknown = 0, identifying = 0;
                        double lastFlush = 0, lastStatus = 0;
                        RecentFrameRate mainRate = new RecentFrameRate(20);
                        byte? lastSent = null;
                        bool gotMain = false;
                        while (true)
                        {
                            token.ThrowIfCancellationRequested();
                            byte[] frame = protocol.ReceiveFrame(token);
                            DrainExchangeTrace(protocol);
                            double now = protocol.LastFrameElapsedMs;
                            if (lastSent.HasValue && frame[1] != unchecked((byte)(lastSent.Value + 1)))
                                throw new InvalidDataException("Нарушена последовательность счётчиков; кадр не используется.");
                            Sample sample = M15Decoder.Decode(frame, protocol.LastFrameUtc, now, epoch);
                            if (sample != null)
                            {
                                Publish(sample);
                                unknown = 0;
                                if (frame[2] == 0xF4)
                                {
                                    mainRate.Add(now);
                                    if (!gotMain) { Event("link_restored", "Получен первый корректный F4 в этом подключении."); gotMain = true; }
                                    if (now - lastStatus > 1000)
                                    {
                                        string rate = mainRate.Count < 2 ? "измеряется" : mainRate.Hertz.ToString("0.00", CultureInfo.InvariantCulture) + " Гц по " + mainRate.Count + " свежим ответам";
                                        State("M1.5: идёт запись; основной пакет: " + rate);
                                        lastStatus = now;
                                    }
                                }
                            }
                            ReadRequest request;
                            if (frame[2] == 0xF6)
                            {
                                if (++identifying > 32) throw new InvalidDataException("Слишком много кадров идентификации; профиль не подтверждён.");
                                Event("identification_frame", BitConverter.ToString(frame));
                                request = ReadRequest.KeepAlive;
                            }
                            else if (frame[2] == 0x09 || frame[2] == 0xF4 || frame[2] == 0xEE)
                            {
                                request = ReadRequest.MainParameters;
                            }
                            else
                            {
                                Event("unknown_reply", "Ответ " + frame[2].ToString("X2") + "; показания не расшифровываются.");
                                if (++unknown >= 8) throw new InvalidDataException("Ответы не соответствуют профилю M1.5.");
                                request = ReadRequest.KeepAlive;
                            }
                            // Finish durable disk work before asking for another response.
                            // After SendRequest the ECU can already be waiting for byte ACKs;
                            // a slow disk must not delay entering ReceiveFrame at that point.
                            if (now - lastFlush >= 1000 || manualFlushRequested) { manualFlushRequested = false; FlushStorage(); lastFlush = clock.Elapsed.TotalMilliseconds; }
                            // Evaluate only after all disk work, using the current
                            // monotonic time, never the older F4 receive timestamp.
                            if (frame[2] == 0xF4 && options.IncludeInjection && secondaryGate.IsDue(clock.Elapsed.TotalMilliseconds))
                                request = ReadRequest.InjectionAndPurge;
                            byte counter = unchecked((byte)(frame[1] + 1));
                            try { protocol.SendRequest(request, counter, token); }
                            finally { if (request == ReadRequest.InjectionAndPurge) secondaryGate.AttemptCompleted(clock.Elapsed.TotalMilliseconds); }
                            lastSent = counter;
                        }
                    }
                    // The port closes before a failed/aborted exchange is written
                    // to disk. No automatic BREAK/reinitialization follows failure.
                }
                finally { if (protocol != null) DrainExchangeTrace(protocol); }
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidDataException ex)
            {
                failure = "Пакет отклонён: " + ex.Message;
                Event("link_lost", failure);
            }
            catch (IOException ex)
            {
                failure = ex.GetType().Name + ": " + ex.Message;
                Event("link_lost", failure);
            }
            catch (TimeoutException ex) { failure = "Таймаут: " + ex.Message; Event("link_lost", failure); }
            catch (UnauthorizedAccessException ex) { failure = "COM-порт занят или недоступен: " + ex.Message; Event("link_lost", failure); }
            catch (InvalidOperationException ex) { failure = "COM-порт: " + ex.Message; Event("link_lost", failure); }
            FlushStorage();
            Event("polling_stopped", failure + ". Опрос остановлен. Проверьте подключение; повторный запуск — кнопкой «Подключиться и записывать». Автоматической повторной инициализации нет.");
            return "link_lost";
        }

        public void Dispose()
        {
            Stop();
            Thread target = worker;
            if (target != null && target != Thread.CurrentThread) target.Join(3000);
            lock (gate) { if (!running && cancellation != null) { cancellation.Dispose(); cancellation = null; } }
        }
    }
}
