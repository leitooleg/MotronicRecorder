using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace MotronicRecorder
{
    public sealed class TimelineView : UserControl
    {
        private Recording recording = new Recording();
        private readonly List<string> keys = new List<string>();
        private readonly ToolTip tip = new ToolTip();
        private readonly Color[] colors = { Color.FromArgb(25, 104, 184), Color.FromArgb(196, 100, 22), Color.FromArgb(15, 137, 116), Color.FromArgb(128, 81, 169), Color.FromArgb(190, 59, 94) };
        private double startMs, spanMs = 60000;
        private bool full = true, dragging, pressed, compact = true;
        private int dragX, dragY, mouseX = -1;
        private double dragStart;
        private double? selectedTime;
        private int PlotLeft { get { return compact ? (ClientSize.Width >= 1200 ? 212 : 180) : 155; } }
        private int LabelWidth { get { return PlotLeft - 52; } }
        private int RangeLabelX { get { return compact ? PlotLeft - 41 : 108; } }
        private const int PlotRight = 24;
        private int PlotTop { get { return compact ? 32 : 48; } }
        private int PlotBottom { get { return compact ? 22 : 39; } }
        public event Action ViewChanged;
        public event Action UserPanned;
        public new event Action CursorChanged;

        public TimelineView()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9f);
            MinimumSize = new Size(360, 120);
            SetStyle(ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, true); TabStop = true;
            keys.Add("rpm"); keys.Add("voltage"); keys.Add("airflow");
            tip.AutoPopDelay = 10000;
        }

        public double StartMs { get { return startMs; } }
        public bool IsFullRange { get { return full; } }
        public double? CursorMs { get { return selectedTime; } }
        public bool Compact { get { return compact; } set { if (compact != value) { compact = value; MinimumSize = new Size(360, compact ? 120 : 180); Invalidate(); Changed(); } } }
        public int SelectedSensorCount { get { return keys.Count; } }
        public double SpanMs { get { return full ? Math.Max(1000, EndMs) : spanMs; } }
        public double EndMs
        {
            get
            {
                double end = recording.Samples.Count == 0 ? 0 : recording.Samples[recording.Samples.Count - 1].ElapsedMs;
                if (recording.Events.Count > 0) end = Math.Max(end, recording.Events[recording.Events.Count - 1].ElapsedMs);
                return Math.Max(0, end);
            }
        }

        public void SetRecording(Recording value)
        {
            recording = value ?? new Recording();
            startMs = 0;
            ClearSelection();
            Invalidate(); Changed();
        }

        public void SelectTime(double elapsedMs)
        {
            if (double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs)) throw new ArgumentOutOfRangeException("elapsedMs");
            double value = Math.Max(0, Math.Min(EndMs, elapsedMs));
            if (!selectedTime.HasValue || selectedTime.Value != value)
            {
                selectedTime = value; Invalidate(); if (CursorChanged != null) CursorChanged();
            }
        }

        public void ClearSelection()
        {
            if (!selectedTime.HasValue) return;
            selectedTime = null; Invalidate(); if (CursorChanged != null) CursorChanged();
        }

        public void SelectSensors(IEnumerable<string> values)
        {
            keys.Clear(); keys.AddRange(values); Invalidate();
        }

        public void SetWindow(double milliseconds)
        {
            double center = startMs + SpanMs / 2;
            full = milliseconds <= 0;
            if (!full) spanMs = Math.Max(500, milliseconds);
            startMs = full ? 0 : ClampStart(center - spanMs / 2);
            Invalidate(); Changed();
        }

        public void PanTo(double milliseconds)
        {
            startMs = full ? 0 : ClampStart(milliseconds);
            Invalidate(); Changed();
        }

        public void CenterOn(double milliseconds)
        {
            if (full || spanMs > 10000) { full = false; spanMs = 5000; }
            startMs = ClampStart(milliseconds - spanMs / 2);
            Invalidate(); Changed();
        }

        public void FollowLatest()
        {
            startMs = full ? 0 : Math.Max(0, EndMs - spanMs);
        }

        private double ClampStart(double value) { return Math.Max(0, Math.Min(Math.Max(0, EndMs - spanMs), value)); }
        private void Changed() { if (ViewChanged != null) ViewChanged(); }
        private SensorDefinition Definition(string key)
        {
            foreach (SensorDefinition s in Sensors.All) if (s.Key == key) return s;
            return new SensorDefinition(key, key, "");
        }

        private int LowerBound(double value)
        {
            int lo = 0, hi = recording.Samples.Count;
            while (lo < hi) { int m = lo + (hi - lo) / 2; if (recording.Samples[m].ElapsedMs < value) lo = m + 1; else hi = m; }
            return lo;
        }

        private string ClockText(double elapsed)
        {
            DateTimeOffset? time = TimeNavigation.TimeAt(recording, elapsed);
            return time.HasValue ? time.Value.ToString(SpanMs <= 10000 ? "HH:mm:ss.fff" : "HH:mm:ss", CultureInfo.InvariantCulture) : "+" + (elapsed / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " с";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            int width = Math.Max(1, ClientSize.Width - PlotLeft - PlotRight);
            int height = Math.Max(1, ClientSize.Height - PlotTop - PlotBottom);
            Rectangle plot = new Rectangle(PlotLeft, PlotTop, width, height);
            using (SolidBrush muted = new SolidBrush(Color.FromArgb(97, 112, 130)))
            using (Pen grid = new Pen(Color.FromArgb(230, 235, 241)))
            {
                string source = (recording.Mode ?? "").IndexOf("demo", StringComparison.OrdinalIgnoreCase) >= 0 ? "DEMO · модельные данные. " : "";
                g.DrawString(source + (compact ? "Клик — курсор · ← → — шаг · колесо — время · Ctrl+колесо — масштаб" : "Время общее · разрыв > 1 с / новая связь не соединяется"), Font, muted, 12, compact ? 0 : 9);
                if (keys.Count == 0)
                {
                    g.DrawString("Выберите датчики слева.", Font, muted, 20, 65); return;
                }
                for (int tick = 0; tick <= 4; tick++)
                {
                    double t = startMs + SpanMs * tick / 4;
                    int x = PlotLeft + width * tick / 4;
                    g.DrawLine(grid, x, PlotTop, x, PlotTop + height);
                    string text = ClockText(t);
                    SizeF size = g.MeasureString(text, Font);
                    float labelX = Math.Min(ClientSize.Width - size.Width - 3, Math.Max(PlotLeft, x - size.Width / 2));
                    g.DrawString(text, Font, muted, labelX, PlotTop - (compact ? 16 : 22));
                    g.DrawString(text, Font, muted, labelX, PlotTop + height + 8);
                }
                int begin = Math.Max(0, LowerBound(startMs - 1000) - 1);
                int end = LowerBound(startMs + SpanMs + 1);
                List<double> boundaries = new List<double>();
                foreach (LogEvent item in recording.Events)
                    if (item.Kind == "link_lost" || item.Kind == "link_restored" || item.Kind == "connect_attempt") boundaries.Add(item.ElapsedMs);
                boundaries.Sort();
                for (int row = 0; row < keys.Count; row++)
                {
                    int y = PlotTop + height * row / keys.Count;
                    int h = height * (row + 1) / keys.Count - height * row / keys.Count;
                    Rectangle rowRect = new Rectangle(PlotLeft, y + (compact ? 3 : 5), width, Math.Max(1, h - (compact ? 6 : 10)));
                    if (row % 2 == 0) using (SolidBrush stripe = new SolidBrush(Color.FromArgb(248, 250, 253))) g.FillRectangle(stripe, rowRect);
                    g.DrawLine(grid, 8, y + h, PlotLeft + width, y + h);
                    SensorDefinition def = Definition(keys[row]);
                    double min = double.MaxValue, max = double.MinValue;
                    for (int i = begin; i < end; i++)
                    {
                        Sample sample = recording.Samples[i]; double v;
                        if (sample.ElapsedMs < startMs || sample.ElapsedMs > startMs + SpanMs || !sample.Values.TryGetValue(def.Key, out v) || double.IsNaN(v) || double.IsInfinity(v)) continue;
                        min = Math.Min(min, v); max = Math.Max(max, v);
                    }
                    Color color = colors[row % colors.Length];
                    using (SolidBrush label = new SolidBrush(color))
                    using (StringFormat format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter })
                    {
                        if (compact)
                        {
                            g.DrawString(def.Name, Font, label, new RectangleF(10, y + 2, LabelWidth, Math.Max(1, Math.Min(18, h - 3))), format);
                            if (h >= 29)
                            {
                                NearestReading reading = selectedTime.HasValue ? TimeNavigation.FindNearest(recording, def.Key, selectedTime.Value) : null;
                                string valueText = selectedTime.HasValue ? (reading == null ? "—" : Sensors.FormatValue(def, reading.Value)) : def.Unit;
                                g.DrawString(valueText, Font, muted, new RectangleF(10, y + h - 17, LabelWidth, 17), format);
                            }
                        }
                        else g.DrawString(def.Name + "\n" + def.Unit, Font, label, new RectangleF(10, y + 10, 97, Math.Max(1, h - 12)), format);
                    }
                    if (min == double.MaxValue)
                    {
                        g.DrawString("нет свежих значений в этом интервале", Font, muted, PlotLeft + 12, y + Math.Max(0, (h - Font.Height) / 2)); continue;
                    }
                    double margin = Math.Max((max - min) * .12, Math.Max(Math.Abs(max) * .015, .01));
                    double low = min - margin, high = max + margin;
                    if (compact && h < 29)
                        g.DrawString(max.ToString("0.###", CultureInfo.InvariantCulture), Font, muted, RangeLabelX, y + Math.Max(0, (h - Font.Height) / 2));
                    else
                    {
                        g.DrawString(max.ToString("0.###", CultureInfo.InvariantCulture), Font, muted, RangeLabelX, y + (compact ? 1 : 5));
                        g.DrawString(min.ToString("0.###", CultureInfo.InvariantCulture), Font, muted, RangeLabelX, y + h - (compact ? 16 : 22));
                    }
                    GraphicsState state = g.Save();
                    g.SetClip(rowRect);
                    using (Pen pen = new Pen(color, 1.6f)) DrawSeries(g, pen, def.Key, rowRect, begin, end, low, high, boundaries);
                    g.Restore(state);
                }
                GraphicsState markers = g.Save(); g.SetClip(plot);
                using (Pen eventPen = new Pen(Color.FromArgb(125, 191, 72, 62)))
                {
                    eventPen.DashStyle = DashStyle.Dash;
                    int lastX = -100;
                    foreach (LogEvent item in recording.Events)
                    {
                        if (item.ElapsedMs < startMs || item.ElapsedMs > startMs + SpanMs) continue;
                        int x = PlotLeft + (int)((item.ElapsedMs - startMs) / SpanMs * width);
                        if (x == lastX) continue;
                        lastX = x; g.DrawLine(eventPen, x, PlotTop, x, PlotTop + height);
                        g.FillPolygon(Brushes.IndianRed, new Point[] { new Point(x - 4, PlotTop), new Point(x + 4, PlotTop), new Point(x, PlotTop + 6) });
                    }
                }
                g.Restore(markers);
                if (mouseX >= PlotLeft && mouseX <= PlotLeft + width)
                    using (Pen cursor = new Pen(Color.FromArgb(125, 95, 115, 140))) { cursor.DashStyle = DashStyle.Dot; g.DrawLine(cursor, mouseX, PlotTop, mouseX, PlotTop + height); }
                if (selectedTime.HasValue && selectedTime.Value >= startMs && selectedTime.Value <= startMs + SpanMs)
                {
                    int x = PlotLeft + (int)((selectedTime.Value - startMs) / SpanMs * width);
                    using (Pen cursor = new Pen(Color.FromArgb(31, 88, 175), 1.8f)) g.DrawLine(cursor, x, PlotTop, x, PlotTop + height);
                    g.FillPolygon(Brushes.RoyalBlue, new Point[] { new Point(x - 5, PlotTop), new Point(x + 5, PlotTop), new Point(x, PlotTop + 7) });
                }
            }
        }

        // Each pixel retains the first, last, minimum and maximum measurements.
        // Epoch boundaries and missing periods flush a bucket before any connector is drawn.
        private void DrawSeries(Graphics g, Pen pen, string key, Rectangle rect, int begin, int end, double low, double high, List<double> boundaries)
        {
            if (Definition(key).IsDiscrete)
            {
                // Contacts/configuration codes are states, never a diagonal
                // interpolated value between two received states.
                using (SolidBrush dots = new SolidBrush(pen.Color))
                    for (int i = begin; i < end; i++)
                    {
                        Sample sample = recording.Samples[i]; double value;
                        if (sample.ElapsedMs < startMs || sample.ElapsedMs > startMs + SpanMs || !sample.Values.TryGetValue(key, out value) || double.IsNaN(value) || double.IsInfinity(value)) continue;
                        float x = rect.Left + (float)((sample.ElapsedMs - startMs) / SpanMs * rect.Width);
                        float y = rect.Bottom - (float)((value - low) / (high - low) * rect.Height);
                        g.FillEllipse(dots, x - 2, y - 2, 4, 4);
                    }
                return;
            }

            bool active = false, connector = false, havePrevious = false;
            int bx = 0, previousEpoch = 0;
            float firstY = 0, lastY = 0, minY = 0, maxY = 0, previousBucketY = 0;
            int previousBucketX = 0;
            int boundaryIndex = 0;
            double previousTime = 0;
            for (int i = begin; i < end; i++)
            {
                Sample s = recording.Samples[i]; double value;
                if (!s.Values.TryGetValue(key, out value) || double.IsNaN(value) || double.IsInfinity(value)) continue;
                int x = rect.Left + (int)((s.ElapsedMs - startMs) / SpanMs * rect.Width);
                float y = rect.Bottom - (float)((value - low) / (high - low) * rect.Height);
                while (boundaryIndex < boundaries.Count && boundaries[boundaryIndex] <= previousTime) boundaryIndex++;
                bool crossedBoundary = havePrevious && boundaryIndex < boundaries.Count && boundaries[boundaryIndex] <= s.ElapsedMs;
                bool continuous = havePrevious && !crossedBoundary && s.Epoch == previousEpoch && s.ElapsedMs >= previousTime && s.ElapsedMs - previousTime <= 1000;
                if (active && (x != bx || !continuous))
                {
                    g.DrawLine(pen, bx, minY, bx, maxY);
                    g.DrawLine(pen, bx - .8f, lastY, bx + .8f, lastY);
                    previousBucketX = bx; previousBucketY = lastY; connector = continuous; active = false;
                }
                if (!active)
                {
                    bx = x; firstY = lastY = minY = maxY = y;
                    if (connector && continuous) g.DrawLine(pen, previousBucketX, previousBucketY, bx, firstY);
                    active = true; connector = false;
                }
                else { lastY = y; minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
                previousTime = s.ElapsedMs; previousEpoch = s.Epoch; havePrevious = true;
            }
            if (active) { g.DrawLine(pen, bx, minY, bx, maxY); g.DrawLine(pen, bx - .8f, lastY, bx + .8f, lastY); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && e.X >= PlotLeft && e.X <= ClientSize.Width - PlotRight)
            {
                Focus(); pressed = true; dragging = false; dragX = e.X; dragY = e.Y; dragStart = startMs; Capture = true;
            }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && pressed)
            {
                if (!dragging && e.X >= PlotLeft && e.X <= ClientSize.Width - PlotRight)
                {
                    if (UserPanned != null) UserPanned();
                    SelectTime(startMs + (e.X - PlotLeft) * SpanMs / Math.Max(1, ClientSize.Width - PlotLeft - PlotRight));
                }
                pressed = false; dragging = false; Capture = false; Cursor = Cursors.Default;
            }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); mouseX = -1; tip.Hide(this); Invalidate(); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); mouseX = e.X;
            int width = Math.Max(1, ClientSize.Width - PlotLeft - PlotRight);
            if (pressed && !dragging && (Math.Abs(e.X - dragX) >= 4 || Math.Abs(e.Y - dragY) >= 4))
            {
                dragging = true; Cursor = Cursors.SizeWE; if (UserPanned != null) UserPanned();
            }
            if (dragging && !full)
            {
                PanTo(dragStart - (e.X - dragX) * SpanMs / width); return;
            }
            if (e.X >= PlotLeft && recording.Samples.Count > 0)
            {
                double t = startMs + (e.X - PlotLeft) * SpanMs / width;
                int at = Math.Min(recording.Samples.Count - 1, LowerBound(t));
                if (at > 0 && Math.Abs(recording.Samples[at - 1].ElapsedMs - t) < Math.Abs(recording.Samples[at].ElapsedMs - t)) at--;
                Sample s = recording.Samples[at];
                string text = "Курсор: " + ClockText(t) + " · +" + (t / 1000).ToString("0.000", CultureInfo.InvariantCulture) + " с";
                int plotHeight = Math.Max(1, ClientSize.Height - PlotTop - PlotBottom);
                int row = (e.Y - PlotTop) * keys.Count / plotHeight;
                if (e.Y >= PlotTop && e.Y < PlotTop + plotHeight && row >= 0 && row < keys.Count)
                {
                    SensorDefinition d = Definition(keys[row]);
                    NearestReading nearest = TimeNavigation.FindNearest(recording, d.Key, t);
                    text += "\n" + d.Description;
                    if (nearest == null) text += "\n" + d.Name + ": нет показаний в пределах ±1 с либо разрыв связи.";
                    else
                    {
                        text += "\n" + d.Name + ": " + Sensors.FormatValue(d, nearest.Value);
                        text += "\nИзмерение: " + TimeNavigation.DisplayTime(recording, nearest.Sample.Utc).ToString("HH:mm:ss.fff") + " · " + nearest.Sample.Reply.ToString("X2") + " · связь " + nearest.Sample.Epoch;
                        text += "\nОт курсора: " + nearest.OffsetMs.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " мс. Значение не интерполировано.";
                    }
                }
                else text += "\nБлижайший ответ: " + TimeNavigation.DisplayTime(recording, s.Utc).ToString("HH:mm:ss.fff") + " · " + s.Reply.ToString("X2") + " · связь " + s.Epoch;
                if (tip.GetToolTip(this) != text) tip.SetToolTip(this, text);
            }
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || key == Keys.Home || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown || base.IsInputKey(keyData);
        }

        private void MoveSelection(double delta)
        {
            if (UserPanned != null) UserPanned();
            SelectTime((selectedTime ?? (startMs + SpanMs / 2)) + delta);
            if (selectedTime.Value < startMs) PanTo(selectedTime.Value - SpanMs * .1);
            else if (selectedTime.Value > startMs + SpanMs) PanTo(selectedTime.Value - SpanMs * .9);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            double step = e.Control ? 10 : e.Shift ? 1000 : 100;
            if (e.KeyCode == Keys.Left) MoveSelection(-step);
            else if (e.KeyCode == Keys.Right) MoveSelection(step);
            else if (e.KeyCode == Keys.Home) { SelectTime(0); PanTo(0); }
            else if (e.KeyCode == Keys.End) { SelectTime(EndMs); PanTo(EndMs - SpanMs); }
            else if (e.KeyCode == Keys.Escape) ClearSelection();
            else if (e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown)
            {
                if (UserPanned != null) UserPanned();
                PanTo(startMs + (e.KeyCode == Keys.PageUp ? -1 : 1) * SpanMs * .8);
            }
            else return;
            e.Handled = true; e.SuppressKeyPress = true;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (e.Delta == 0) return;
            Focus();
            if ((ModifierKeys & Keys.Control) != 0)
            {
                if (UserPanned != null) UserPanned();
                double fraction = Math.Max(0, Math.Min(1, (double)(e.X - PlotLeft) / Math.Max(1, ClientSize.Width - PlotLeft - PlotRight)));
                double anchor = startMs + SpanMs * fraction;
                double nextSpan = Math.Max(500, Math.Min(Math.Max(1000, EndMs), SpanMs * Math.Pow(.75, e.Delta / 120.0)));
                full = false; spanMs = nextSpan; startMs = ClampStart(anchor - spanMs * fraction); Invalidate(); Changed();
            }
            else MoveSelection((e.Delta / 120.0) * Math.Max(10, Math.Min(1000, SpanMs / 100)));
        }

        protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
    }
}
