using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace MotronicRecorder
{
    public static class MainFormTests
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception("MainForm regression: " + message);
        }

        private static T Field<T>(MainForm form, string name) where T : class
        {
            FieldInfo field = typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "missing test target " + name);
            T result = field.GetValue(form) as T;
            Assert(result != null, "unexpected target type " + name);
            return result;
        }

        private static int SensorIndex(string key)
        {
            for (int i = 0; i < Sensors.All.Length; i++) if (Sensors.All[i].Key == key) return i;
            throw new Exception("Missing sensor " + key);
        }

        private static int GraphIndex(string key)
        {
            int index = 0; foreach (SensorDefinition def in Sensors.All) { if (!def.Graphable) continue; if (def.Key == key) return index; index++; }
            throw new Exception("Missing graph " + key);
        }
        private static int GraphCount() { int count = 0; foreach (SensorDefinition def in Sensors.All) if (def.Graphable) count++; return count; }

        private static void FullyReachable(Control control, string context)
        {
            Assert(control.Visible && control.Enabled, context + " is not visible/enabled");
            Assert(control.Parent != null && control.Width >= 20 && control.Height >= 15, context + " has no usable bounds");
            Rectangle bounds = control.Parent.RectangleToScreen(control.Bounds);
            Rectangle visible = bounds;
            for (Control parent = control.Parent; parent != null; parent = parent.Parent)
                visible = Rectangle.Intersect(visible, parent.RectangleToScreen(parent.ClientRectangle));
            // A small allowance covers integer rounding when 125% produces fractional pixels.
            Assert(visible.Width >= bounds.Width - 2 && visible.Height >= bounds.Height - 2,
                context + " is clipped: control=" + bounds + ", visible=" + visible);
            Assert(control.CanSelect, context + " cannot be reached by keyboard");
        }

        private static void NoAcknowledgmentGate(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                CheckBox check = child as CheckBox;
                if (check != null && check.Visible)
                    Assert(check.Text.IndexOf("Понимаю", StringComparison.OrdinalIgnoreCase) < 0 &&
                           check.Text.IndexOf("разрешаю", StringComparison.OrdinalIgnoreCase) < 0,
                           "obsolete live acknowledgment checkbox is still displayed");
                NoAcknowledgmentGate(child);
            }
        }

        private static void RunCase(float factor, Size viewport)
        {
            string context = (factor * 100).ToString("0") + "% / " + viewport.Width + "x" + viewport.Height;
            using (MainForm form = new MainForm())
            using (Font scaledFont = new Font(form.Font.FontFamily, form.Font.Size * factor, form.Font.Style))
            {
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                // This deliberately stresses layout in fixed physical viewports. It is a
                // deterministic approximation of DPI/font scaling, not a claim of OS DPI emulation.
                form.AutoScaleMode = AutoScaleMode.None;
                if (factor != 1f) form.Scale(new SizeF(factor, factor));
                form.Font = scaledFont;
                form.MinimumSize = Size.Empty;
                // WinForms otherwise applies the local 1280px desktop max-track limit
                // to offscreen 1920px layout simulations. No live session is opened.
                form.MaximumSize = new Size(4096, 2160);
                form.Size = viewport;
                form.Show();
                // Show() normalizes initial bounds to the physical desktop. Apply
                // the simulated offscreen viewport again after the native handle exists.
                form.Size = viewport;
                Application.DoEvents();

                ComboBox mode = Field<ComboBox>(form, "mode");
                ComboBox port = Field<ComboBox>(form, "port");
                Button start = Field<Button>(form, "start");
                Button refresh = Field<Button>(form, "refresh");
                Button choose = Field<Button>(form, "choose");
                Button stop = Field<Button>(form, "stop");
                SessionController controller = Field<SessionController>(form, "controller");
                Assert(form.Text == "Motronic Recorder by leitoauto.com · 0.2.4", context + ": window author/version branding is missing");
                Assert(mode.SelectedIndex == 1, context + ": default source must be Vehicle");
                Assert(!controller.IsRunning && !stop.Enabled, context + ": constructing/showing the form started work");

                // Both cases are UI state only. Do not click Start or call controller.Start:
                // COM123 is just a ComboBox string and no serial port is opened by these tests.
                port.Items.Clear();
                port.Text = "";
                mode.SelectedIndex = 0;
                mode.SelectedIndex = 1;
                Application.DoEvents();
                Assert(string.IsNullOrWhiteSpace(port.Text), context + ": missing-port case is not empty");
                Assert(start.Enabled, context + ": live Start is disabled when a port is missing; click must explain it");
                Assert(port.Enabled && refresh.Enabled, context + ": live port controls are disabled");
                NoAcknowledgmentGate(form);
                FullyReachable(start, context + " Start without COM");
                FullyReachable(port, context + " COM selector");
                FullyReachable(refresh, context + " Refresh COM");
                FullyReachable(choose, context + " output folder chooser");
                TableLayoutPanel setup = Field<TableLayoutPanel>(form, "settings");
                Assert(setup.RowCount == 3, context + ": setup layout is unavailable");
                int[] rowHeights = setup.GetRowHeights();
                Assert(rowHeights[2] <= Math.Ceiling(setup.RowStyles[2].Height) + 2,
                    context + ": setup warning row consumed blank space needed by the graphs (actual " +
                    rowHeights[2] + ", intended " + setup.RowStyles[2].Height + ")");

                CheckedListBox selected = Field<CheckedListBox>(form, "selected");
                CheckBox compact = Field<CheckBox>(form, "compact");
                TimelineView timeline = Field<TimelineView>(form, "timeline");
                Panel chartHost = Field<Panel>(form, "chartHost");
                Assert(compact.Checked && timeline.Compact, context + ": compact layout must be the default");
                Assert(selected.CheckedItems.Count == 7 && timeline.SelectedSensorCount == 7, context + ": all seven primary F4 graphs must be selected");
                ComboBox preset = Field<ComboBox>(form, "viewPreset");
                bool injectionBefore = Field<CheckBox>(form, "injection").Checked;
                preset.SelectedIndex = 1; Application.DoEvents();
                Assert(!compact.Checked && selected.CheckedItems.Count == 3 && timeline.SelectedSensorCount == 3, context + ": view 0.1.1 preset did not restore three taller graphs");
                Assert(Field<CheckBox>(form, "injection").Checked == injectionBefore, context + ": display preset changed ECU polling options");
                preset.SelectedIndex = 0; Application.DoEvents();
                Assert(compact.Checked && selected.CheckedItems.Count == 7 && timeline.SelectedSensorCount == 7, context + ": compact preset did not restore seven graphs");
                ToolTip hints = Field<ToolTip>(form, "hints");
                Assert(hints.GetToolTip(Field<CheckBox>(form, "injection")).Contains("1C") && hints.GetToolTip(selected).Contains("отображение"), context + ": ECU-impact tooltips are missing");
                if (factor == 1f && viewport.Width >= 1280)
                {
                    Assert(!chartHost.VerticalScroll.Visible && timeline.Height <= chartHost.ClientSize.Height + 1,
                        context + ": compact seven graphs require vertical scrolling");
                    selected.SetItemChecked(GraphIndex("injection"), true); selected.SetItemChecked(GraphIndex("purge"), true); Application.DoEvents();
                    Assert(timeline.SelectedSensorCount == 9 && !chartHost.VerticalScroll.Visible && timeline.Height <= chartHost.ClientSize.Height + 1,
                        context + ": compact nine graphs require vertical scrolling");
                    selected.SetItemChecked(GraphIndex("injection"), false); selected.SetItemChecked(GraphIndex("purge"), false); Application.DoEvents();
                }
                if (viewport.Width >= 1900)
                {
                    preset.SelectedIndex = 2; Application.DoEvents();
                    Assert(compact.Checked && selected.CheckedItems.Count == GraphCount() && timeline.SelectedSensorCount == GraphCount(),
                        context + ": Full HD did not display all current sensor definitions");
                    Assert(!chartHost.VerticalScroll.Visible && timeline.Height <= chartHost.ClientSize.Height + 1,
                        context + ": Full HD graphs require vertical scrolling: actual form=" + form.Size + ", timeline=" + timeline.Size + ", host=" + chartHost.ClientSize + ", location=" + timeline.Location + ", display=" + chartHost.DisplayRectangle + ", vertical=" + chartHost.VerticalScroll.Visible + ", horizontal=" + chartHost.HorizontalScroll.Visible);
                    Assert(Field<CheckBox>(form, "injection").Checked == injectionBefore, context + ": Full HD display changed ECU polling");
                    Assert(Field<DataGridView>(form, "current").Rows.Count == Sensors.All.Length, context + ": new sensor values are missing from the table");
                    preset.SelectedIndex = 0; Application.DoEvents();
                }
                Control[] brand = form.Controls.Find("siteMonogram", true);
                Control[] website = form.Controls.Find("websiteLink", true);
                Assert(brand.Length == 1 && brand[0].Text == "LA", context + ": native LA badge is missing");
                Assert(website.Length == 1 && website[0].Text == "leitoauto.com", context + ": website link changed");
                FullyReachable(brand[0], context + " LA badge"); FullyReachable(website[0], context + " website link");
                FullyReachable(Field<Button>(form, "go"), context + " time navigation");
                FullyReachable(Field<TextBox>(form, "clockInput"), context + " clock input");
                FullyReachable(Field<Button>(form, "about"), context + " About");
                Button expand = Field<Button>(form, "expand");
                expand.PerformClick(); Application.DoEvents();
                Assert(Field<SplitContainer>(form, "mainSplit").Panel1Collapsed && Field<SplitContainer>(form, "historySplit").Panel2Collapsed && !setup.Visible,
                    context + ": expanded graphs did not collapse auxiliary panels");
                FullyReachable(start, context + " Start with expanded graphs");
                expand.PerformClick(); Application.DoEvents();
                Assert(setup.Visible && !Field<SplitContainer>(form, "mainSplit").Panel1Collapsed, context + ": panels did not return");
                port.Items.Add("COM123"); port.SelectedItem = "COM123";
                Application.DoEvents();
                Assert(start.Enabled && port.Text == "COM123", context + ": selecting a port does not leave Start usable");
                refresh.PerformClick(); Application.DoEvents();
                Assert(port.Text == "COM123", context + ": refreshing the inventory discarded a manually entered COM port");
                mode.SelectedIndex = 0;
                Application.DoEvents();
                Assert(start.Enabled && !port.Enabled && !refresh.Enabled, context + ": DEMO control state");
                mode.SelectedIndex = 1;
                Application.DoEvents();
                Assert(start.Enabled && port.Enabled && refresh.Enabled, context + ": returning to Vehicle control state");
                FullyReachable(start, context + " Start after mode round trip");
                Assert(!controller.IsRunning && controller.SessionDirectory == null,
                    context + ": UI regression test created a recording or opened a session");
                form.Close();
            }
        }

        private static void Invoke(MainForm form, string name, params object[] args)
        {
            MethodInfo method = typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(method != null, "missing method " + name); method.Invoke(form, args);
        }

        private static Sample Primary(DateTimeOffset origin, double elapsed, int epoch, double rpm)
        {
            Sample value = new Sample { Utc = origin.AddMilliseconds(elapsed).ToUniversalTime(), ElapsedMs = elapsed, Epoch = epoch, Reply = 0xF4 };
            value.Values["rpm"] = rpm; value.Values["voltage"] = 13.2; value.Values["airflow"] = 1.25;
            value.Values["throttle"] = .625; value.Values["ignition"] = 10; value.Values["coolant"] = 85; value.Values["air_temperature"] = 25; return value;
        }

        private static void OfflineNavigation()
        {
            using (MainForm form = new MainForm())
            {
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.Show(); Application.DoEvents();
                DateTimeOffset origin = new DateTimeOffset(2026, 10, 14, 18, 39, 59, TimeSpan.FromHours(3));
                Recording recording = new Recording { Mode = "LIVE", UtcOffsetMinutes = 180 };
                recording.Samples.Add(Primary(origin, 0, 1, 800)); recording.Samples.Add(Primary(origin, 1000, 1, 400));
                Sample secondary = new Sample { Utc = origin.AddMilliseconds(1250).ToUniversalTime(), ElapsedMs = 1250, Epoch = 1, Reply = 0xEE };
                secondary.Values["injection"] = 2.5; secondary.Values["purge"] = 10; recording.Samples.Add(secondary);
                recording.Samples.Add(Primary(origin, 1300, 1, 720)); recording.Samples.Add(Primary(origin, 1900, 1, 800)); recording.Samples.Add(Primary(origin, 4000, 2, 840));
                recording.Events.Add(new LogEvent { Utc = origin.AddMilliseconds(2000).ToUniversalTime(), ElapsedMs = 2000, Epoch = 1, Kind = "link_lost", Message = "Offline fixture" });
                recording.Events.Add(new LogEvent { Utc = origin.AddMilliseconds(4000).ToUniversalTime(), ElapsedMs = 4000, Epoch = 2, Kind = "link_restored", Message = "Offline fixture" });
                form.LoadRecording(recording); Application.DoEvents();
                TimelineView timeline = Field<TimelineView>(form, "timeline"); DataGridView current = Field<DataGridView>(form, "current");
                Assert(form.GoToClock("18:40", "") && timeline.CursorMs.HasValue && Math.Abs(timeline.CursorMs.Value - 1000) < .01,
                    "HH:mm navigation did not use the recorded UTC offset");
                Assert(current.Rows[SensorIndex("rpm")].Cells[1].Value.ToString().StartsWith("400 ") && current.Rows[SensorIndex("rpm")].Cells[2].Value.ToString() == "18:40:00.000",
                    "cursor did not show a real RPM value with its measurement time");
                Assert(form.GoToClock("18:40:00", "14.10.2026"), "HH:mm:ss / date navigation failed");
                form.FocusTime(1200); Application.DoEvents();
                Assert(current.Rows[SensorIndex("rpm")].Cells[1].Value.ToString().StartsWith("720 ") && current.Rows[SensorIndex("rpm")].Cells[2].Value.ToString() == "18:40:00.300" && current.Rows[SensorIndex("rpm")].Cells[3].Value.ToString() == "+100",
                    "cursor interpolated RPM or hid the sample time / signed offset");
                Assert(current.Rows[SensorIndex("injection")].Cells[1].Value.ToString().StartsWith("2.5 ") && current.Rows[SensorIndex("injection")].Cells[2].Value.ToString() == "18:40:00.250" && current.Rows[SensorIndex("injection")].Cells[3].Value.ToString() == "+50",
                    "EE reading was assigned the F4 timestamp");
                form.FocusTime(1000); Invoke(form, "StepSample", 1);
                Assert(Math.Abs(timeline.CursorMs.Value - 1250) < .01, "next-sample navigation did not land on a real reply");
                Invoke(form, "StepSample", -1); Assert(Math.Abs(timeline.CursorMs.Value - 1000) < .01, "previous-sample navigation failed");
                Invoke(form, "MoveCursor", 1000.0); Assert(Math.Abs(timeline.CursorMs.Value - 2000) < .01, "+1 second navigation failed");
                form.FocusTime(3000); Assert(current.Rows[SensorIndex("rpm")].Cells[1].Value.ToString() == "—", "cursor carried RPM through a lost-link gap");
                form.FocusTime(1000); TrackBar zoom = Field<TrackBar>(form, "zoom"); zoom.Value = 1;
                Assert(Math.Abs(timeline.SpanMs - 3600000) < .01, "moving a selected cursor silently replaced the selected hour scale");
                CheckBox follow = Field<CheckBox>(form, "follow"); follow.Checked = true;
                Assert(follow.Checked && !timeline.CursorMs.HasValue, "clearing a cursor switched follow off again");
                Assert(!form.GoToClock("99:99", ""), "invalid clock text was accepted");
                SessionController controller = Field<SessionController>(form, "controller");
                Assert(!controller.IsRunning && controller.SessionDirectory == null, "offline navigation opened a recording session");
                form.Close();
            }
        }

        private static void OfflineUiBatch()
        {
            using (MainForm form = new MainForm())
            {
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.Show(); Application.DoEvents();
                System.Collections.Generic.Queue<Sample> queue = Field<System.Collections.Generic.Queue<Sample>>(form, "pendingSamples");
                DateTimeOffset origin = DateTimeOffset.UtcNow;
                for (int i = 0; i < 100; i++) queue.Enqueue(Primary(origin, i * 250, 1, 800));
                Invoke(form, "Tick", null, EventArgs.Empty);
                Recording recording = Field<Recording>(form, "recording");
                Assert(recording.Samples.Count == 100 && queue.Count == 0, "UI batching lost a received reply");
                recording.Samples[99].Utc = DateTimeOffset.UtcNow.AddSeconds(-5);
                Invoke(form, "Tick", null, EventArgs.Empty);
                Assert(Field<DataGridView>(form, "current").Rows[SensorIndex("rpm")].Cells[1].Value.ToString().Contains("устар."), "freshness froze when the controller was stopped and the UI was not dirty");
                Sample withLambda = Primary(origin, 26000, 1, 800); withLambda.Values["lambda_enabled"] = 1; withLambda.Values["lambda_mv"] = 250; queue.Enqueue(withLambda);
                Sample withCo = Primary(origin, 27000, 1, 800); withCo.Values["lambda_enabled"] = 0; withCo.Values["co_potentiometer"] = 1.25; withCo.Values["throttle_switch"] = 2; queue.Enqueue(withCo);
                Invoke(form, "Tick", null, EventArgs.Empty);
                DataGridView current = Field<DataGridView>(form, "current");
                Assert(current.Rows[SensorIndex("lambda_mv")].Cells[1].Value.ToString() == "—", "latest lambda survived a new F4 where the field is unavailable");
                Assert(current.Rows[SensorIndex("lambda_enabled")].Cells[1].Value.ToString() == "Нет (ЭБУ)", "configuration flag was formatted as a physical sensor test");
                Assert(current.Rows[SensorIndex("throttle_switch")].Cells[1].Value.ToString() == "Холостой ход", "contact code was not formatted as its actual state");
                form.FocusTime(27000);
                Assert(current.Rows[SensorIndex("lambda_mv")].Cells[1].Value.ToString() == "—", "cursor carried lambda from the opposite CO configuration");
                SessionController controller = Field<SessionController>(form, "controller");
                Assert(!controller.IsRunning && controller.SessionDirectory == null, "offline UI batch test started a session");
                form.Close();
            }
        }

        private static void VirtualFullHdLayout(float factor)
        {
            using (MainForm form = new MainForm())
            using (Font scaled = new Font(form.Font.FontFamily, form.Font.Size * factor, form.Font.Style))
            {
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000);
                form.AutoScaleMode = AutoScaleMode.None; if (factor != 1f) form.Scale(new SizeF(factor, factor)); form.Font = scaled;
                form.Show(); Application.DoEvents();
                // Test the actual WinForms content layout at Full HD, not a top-level
                // offscreen window silently capped to the local desktop's max-track size.
                TableLayoutPanel surface = form.Controls[0] as TableLayoutPanel;
                Assert(surface != null, "missing Full HD content surface");
                surface.Dock = DockStyle.None; surface.Size = new Size(1900, 1000); surface.PerformLayout(); Application.DoEvents();
                ComboBox preset = Field<ComboBox>(form, "viewPreset"); preset.SelectedIndex = 2; Application.DoEvents();
                TimelineView timeline = Field<TimelineView>(form, "timeline"); Panel host = Field<Panel>(form, "chartHost");
                Assert(surface.ClientSize.Width >= 1900 && host.ClientSize.Width >= 1200, "Full HD virtual layout retained the small desktop width");
                Assert(timeline.SelectedSensorCount == GraphCount() && Field<CheckedListBox>(form, "selected").CheckedItems.Count == GraphCount(), "Full HD omitted a graphable sensor");
                Assert(!host.VerticalScroll.Visible && timeline.Height <= host.ClientSize.Height + 1, "Full HD needs graph scrolling (" + factor + "): surface=" + surface.Size + ", timeline=" + timeline.Size + ", host=" + host.ClientSize + ", display=" + host.DisplayRectangle + ", V=" + host.VerticalScroll.Visible + ", H=" + host.HorizontalScroll.Visible);
                Assert((timeline.ClientSize.Height - 54) / GraphCount() >= 29, "Full HD rows cannot show a real value under each graph name");
                Assert(Field<DataGridView>(form, "current").Rows.Count == Sensors.All.Length, "Full HD table omitted a status/value");
                Assert(!Field<CheckBox>(form, "injection").Checked && !Field<SessionController>(form, "controller").IsRunning, "displaying all graphs changed polling or started a session");
                if (factor == 1f)
                {
                    Recording fixture = new Recording { Mode = "DEMO", UtcOffsetMinutes = 180, Note = "Искусственные данные для проверки Full HD; это не показания автомобиля." };
                    for (double ms = 0; ms <= 30000; ms += 100)
                    {
                        fixture.Samples.Add(DemoSource.Sample(ms, 1, false));
                        if (ms % 2000 == 0) fixture.Samples.Add(DemoSource.Sample(ms, 1, true));
                    }
                    fixture.Events.Add(new LogEvent { Utc = fixture.Samples[0].Utc.AddMilliseconds(15000), ElapsedMs = 15000, Epoch = 1, Kind = "demo_dip", Message = "Искусственный провал 15,0–15,4 с. Не данные автомобиля." });
                    form.LoadRecording(fixture); form.FocusTime(15000); Application.DoEvents();
                }
                using (Bitmap rendered = new Bitmap(surface.Width, surface.Height))
                {
                    surface.DrawToBitmap(rendered, new Rectangle(Point.Empty, surface.Size));
                    if (factor == 1f) rendered.Save(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "preview-fullhd.png")), System.Drawing.Imaging.ImageFormat.Png);
                }
                form.Close();
            }
        }

        public static void Run()
        {
            Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "UI regression runner must be STA");
            using (MainForm form = new MainForm())
            using (Form dialog = form.CreateAboutDialog())
            {
                Control[] aboutText = dialog.Controls.Find("aboutText", true);
                Control[] guide = dialog.Controls.Find("connectionGuide", true);
                Assert(aboutText.Length == 1 && aboutText[0].Text.Contains("AS IS") && aboutText[0].Text.Contains(ProductInfo.Author), "About/disclaimer text unavailable");
                Assert(guide.Length == 1 && guide[0].Text.Contains("Latency Timer") && guide[0].Text.Contains("9600"), "embedded connection guide unavailable");
                Assert(!Field<SessionController>(form, "controller").IsRunning, "reading guide started diagnostics");
            }
            foreach (float factor in new float[] { 1f, 1.25f, 1.5f })
                foreach (Size viewport in new Size[] { new Size(1280, 680), new Size(1280, 720), new Size(1366, 768), new Size(1060, 760) })
                    RunCase(factor, viewport);
            foreach (Rectangle work in new Rectangle[] { new Rectangle(0, 0, 1280, 680), new Rectangle(1920, 0, 1920, 1040), new Rectangle(0, 0, 1024, 560) })
            {
                Size fit = MainForm.WindowSizeFor(work);
                Assert(fit.Width <= work.Width && fit.Height <= work.Height && fit.Width > 0 && fit.Height > 0, "default window extends beyond WorkingArea");
            }
            foreach (float factor in new float[] { 1f, 1.25f, 1.5f }) VirtualFullHdLayout(factor);
            OfflineNavigation();
            OfflineUiBatch();
            Console.WriteLine("MainForm tests: passed (live/default mode, empty/mock COM, no acknowledgment gate, reachable controls, compact 7/9 graphs, cursor values/timestamps/gaps and clock navigation at simulated 100/125/150% in four native viewports plus a verified 1900x1000 virtual Full HD content surface, working-area bounds and native LA badge). Start was never invoked; no COM ports opened.");
        }
    }
}
