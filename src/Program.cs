using System;
using System.IO;
using System.Windows.Forms;

namespace MotronicRecorder
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                // Offline review only. This branch loads files and never starts a
                // controller, opens a serial port, or contacts an ECU.
                if (args.Length == 4 && args[0] == "--render-time")
                {
                    Recording recording = RecordingReader.Load(Path.GetFullPath(args[1]));
                    double elapsed; string error;
                    if (!TimeNavigation.TryResolve(recording, args[2], "", out elapsed, out error)) throw new ArgumentException(error);
                    using (MainForm form = new MainForm())
                    {
                        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                        form.Location = new System.Drawing.Point(-30000, -30000);
                        form.MinimumSize = System.Drawing.Size.Empty;
                        form.Size = new System.Drawing.Size(1280, 680);
                        form.LoadRecording(recording); form.FocusTime(elapsed);
                        form.Show(); Application.DoEvents();
                        using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
                        { form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.GetFullPath(args[3]), System.Drawing.Imaging.ImageFormat.Png); }
                        form.Close();
                    }
                    return 0;
                }
                if (args.Length == 2 && args[0] == "--render-live")
                {
                    using (MainForm form = new MainForm())
                    {
                        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                        form.Location = new System.Drawing.Point(-30000, -30000); form.Show(); Application.DoEvents();
                        using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
                        { form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.GetFullPath(args[1]), System.Drawing.Imaging.ImageFormat.Png); }
                        form.Close();
                    }
                    return 0;
                }
                if (args.Length == 2 && args[0] == "--make-demo")
                {
                    string directory = DemoSource.WriteDemoRecording(Path.GetFullPath(args[1]), 3600, 200);
                    File.WriteAllText(Path.Combine(Path.GetFullPath(args[1]), "latest-demo.txt"), directory);
                    return 0;
                }
                if (args.Length == 3 && (args[0] == "--render-demo" || args[0] == "--render-event"))
                {
                    using (MainForm form = new MainForm())
                    {
                        form.ShowInTaskbar = false;
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new System.Drawing.Point(-30000, -30000);
                        form.LoadRecording(RecordingReader.Load(Path.GetFullPath(args[1])));
                        if (args[0] == "--render-event") form.FocusTime(15000);
                        form.Show();
                        Application.DoEvents();
                        using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                            bitmap.Save(Path.GetFullPath(args[2]), System.Drawing.Imaging.ImageFormat.Png);
                        }
                        form.Close();
                    }
                    return 0;
                }
                Application.Run(new MainForm());
                return 0;
            }
            catch (Exception ex)
            {
                if (args.Length > 0) { Console.Error.WriteLine(ex.ToString()); return 1; }
                MessageBox.Show(ex.Message, "Motronic Recorder — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
