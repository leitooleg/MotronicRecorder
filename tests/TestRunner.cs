using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace MotronicRecorder
{
    public static class TestRunner
    {
        [STAThread]
        public static int Main()
        {
            try
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                ProtocolTests.Run();
                StorageTests.Run();
                LiveSessionTests.Run();
                PollingTests.Run();
                MainFormTests.Run();
                TimeNavigationTests.Run();
                string root = Path.Combine(Path.GetTempPath(), "MotronicControllerTest_" + Guid.NewGuid().ToString("N"));
                using (SessionController controller = new SessionController())
                {
                    bool rejected = false;
                    try { controller.Start(new SessionOptions { Demo = false, PortName = "COM1", OutputDirectory = root, ExperimentalConfirmed = false }); }
                    catch (ArgumentException) { rejected = true; }
                    if (!rejected || controller.IsRunning) throw new Exception("Live must not start without explicit selection/acknowledgment.");
                    ManualResetEvent first = new ManualResetEvent(false);
                    controller.SampleReceived += delegate(Sample sample) { first.Set(); };
                    controller.Start(new SessionOptions { Demo = true, OutputDirectory = root, IncludeInjection = true, DurationMinutes = 1 });
                    if (!first.WaitOne(3000)) throw new Exception("Demo did not publish data.");
                    controller.Mark("Тестовая отметка; с кавычкой \" и переносом\nстроки");
                    controller.Stop();
                    Stopwatch wait = Stopwatch.StartNew();
                    while (controller.IsRunning && wait.ElapsedMilliseconds < 5000) Thread.Sleep(20);
                    if (controller.IsRunning) throw new Exception("Stop did not complete.");
                    Recording saved = RecordingReader.Load(controller.SessionDirectory);
                    if (saved.Mode != "DEMO" || saved.Samples.Count == 0 || !saved.Events.Exists(delegate(LogEvent e) { return e.Kind == "manual"; }))
                        throw new Exception("Demo lifecycle failed.");
                    first.Dispose();
                }
                Console.WriteLine("Controller tests: passed (live opt-in guard, DEMO lifecycle, mark, stop, reload). No COM ports opened.");
                Console.WriteLine("ALL TESTS PASSED");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
        }
    }
}
