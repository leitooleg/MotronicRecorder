using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Windows.Forms;

namespace MotronicRecorder
{
    public sealed class MainForm : Form
    {
        private readonly SessionController controller = new SessionController();
        private Recording recording = new Recording { Mode = "LIVE_EXPERIMENTAL" };
        private readonly ComboBox mode = new ComboBox(), port = new ComboBox(), duration = new ComboBox(), viewPreset = new ComboBox();
        private readonly TextBox output = new TextBox(), clockInput = new TextBox(), dateInput = new TextBox();
        private readonly CheckBox injection = new CheckBox(), follow = new CheckBox(), compact = new CheckBox();
        private readonly Button start = new Button(), stop = new Button(), mark = new Button(), open = new Button(), folder = new Button(), choose = new Button(), refresh = new Button();
        private readonly Button expand = new Button(), setupToggle = new Button(), about = new Button(), go = new Button();
        private readonly Label status = new Label(), warning = new Label(), viewLabel = new Label(), counters = new Label(), currentTitle = new Label(), cursorLabel = new Label();
        private readonly CheckedListBox selected = new CheckedListBox();
        private readonly List<SensorDefinition> graphSensors = new List<SensorDefinition>();
        private readonly DataGridView current = new DataGridView(), events = new DataGridView();
        private readonly TimelineView timeline = new TimelineView();
        private readonly TrackBar zoom = new TrackBar();
        private readonly HScrollBar scroll = new HScrollBar();
        private readonly ToolTip hints = new ToolTip();
        private Panel chartHost;
        private TableLayoutPanel settings, sensorPanel;
        private SplitContainer mainSplit, historySplit;
        private readonly Timer timer = new Timer();
        private readonly Dictionary<string, Sample> latest = new Dictionary<string, Sample>();
        private readonly object pendingGate = new object();
        private readonly Queue<Sample> pendingSamples = new Queue<Sample>();
        private readonly Queue<LogEvent> pendingEvents = new Queue<LogEvent>();
        private string pendingState;
        private readonly double[] windows = { 0, 3600000, 1800000, 900000, 300000, 60000, 30000, 10000, 5000, 2000, 1000, 500 };
        private readonly string[] windowNames = { "Весь сеанс", "60 минут", "30 минут", "15 минут", "5 минут", "1 минута", "30 секунд", "10 секунд", "5 секунд", "2 секунды", "1 секунда", "0,5 секунды" };
        private bool closing, closeReady, refreshingScroll, dirty, loadedFromDisk, expanded, fittingTimeline;
        private string stateText = "Готово. Запись начинается только кнопкой «Подключиться и записывать».";

        public MainForm()
        {
            Text = ProductInfo.Name + " · " + ProductInfo.Version;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.FromArgb(244, 247, 251);
            Rectangle workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            MinimumSize = new Size(Math.Min(1000, workingArea.Width), Math.Min(600, workingArea.Height));
            Size = WindowSizeFor(workingArea);
            StartPosition = FormStartPosition.CenterScreen; KeyPreview = true;
            BuildLayout();
            // Worker callbacks only enqueue small references. The UI consumes batches;
            // disk recording is owned by SessionController and never waits for this timer.
            controller.SampleReceived += delegate(Sample sample) { lock (pendingGate) pendingSamples.Enqueue(sample); };
            controller.EventReceived += delegate(LogEvent item) { lock (pendingGate) pendingEvents.Enqueue(item); };
            controller.StateChanged += delegate(string value) { lock (pendingGate) pendingState = value; };
            timeline.ViewChanged += delegate { UpdateScroll(); UpdateScaleLabel(); };
            timeline.UserPanned += delegate { follow.Checked = false; };
            timeline.CursorChanged += delegate { if (timeline.CursorMs.HasValue) follow.Checked = false; RefreshCurrent(); UpdateCursorLabel(); };
            timer.Interval = 150; timer.Tick += Tick; timer.Start();
            FormClosing += HandleFormClosing;
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.F8) { Mark(); e.Handled = true; e.SuppressKeyPress = true; } };
            RefreshPorts(); UpdateMode(); UpdateSelection(); UpdateButtons(); UpdateScroll(); FitResponsivePanels(); dirty = true;
        }

        internal static Size WindowSizeFor(Rectangle workingArea)
        {
            int preferredWidth = workingArea.Width >= 1700 ? 1840 : 1280;
            int preferredHeight = workingArea.Height >= 900 ? 1000 : 720;
            return new Size(Math.Max(1, Math.Min(preferredWidth, workingArea.Width)), Math.Max(1, Math.Min(preferredHeight, workingArea.Height)));
        }

        private static bool IsCorePrimary(SensorDefinition def)
        {
            if (def.SourceReply != 0xF4) return false;
            switch (def.Key)
            {
                case "rpm": case "voltage": case "airflow": case "throttle": case "ignition": case "coolant": case "air_temperature": return true;
                default: return false;
            }
        }

        private static bool IsLegacyCurve(string key) { return key == "rpm" || key == "voltage" || key == "airflow"; }
        private static string SensorCaption()
        {
            int primary = 0, secondary = 0;
            foreach (SensorDefinition def in Sensors.All) { if (!def.Graphable) continue; if (def.SourceReply == 0xF4) primary++; else if (def.SourceReply == 0xEE) secondary++; }
            return "Графики · " + primary + " основных + " + secondary + " дополнительных";
        }

        private static Label Label(string text, int width) { return new Label { Text = text, Width = width, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) }; }
        private static void SetupCombo(ComboBox c, int width) { c.DropDownStyle = ComboBoxStyle.DropDownList; c.Width = width; c.Height = 28; c.Margin = new Padding(0, 2, 10, 0); }

        private void BuildLayout()
        {
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(7), BackColor = BackColor };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); Controls.Add(root);
            TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            FlowLayoutPanel titleBlock = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
            titleBlock.Controls.Add(new Label { Text = "Motronic Recorder", Font = new Font("Segoe UI", 14f, FontStyle.Bold), AutoSize = true, ForeColor = Color.FromArgb(29, 52, 80), Margin = new Padding(0, 0, 0, 0) });
            titleBlock.Controls.Add(new Label { Text = "by leitoauto.com", Font = new Font("Segoe UI", 8.5f), AutoSize = true, ForeColor = Color.FromArgb(103, 119, 138), Margin = new Padding(8, 9, 0, 0) });
            header.Controls.Add(titleBlock, 0, 0);
            CopyButton(setupToggle, "Настройки ▾", 112); setupToggle.Click += delegate { settings.Visible = !settings.Visible; setupToggle.Text = settings.Visible ? "Настройки ▾" : "Настройки ▸"; FitHistory(); };
            CopyButton(expand, "Развернуть графики", 164); expand.Click += delegate { SetExpanded(!expanded); };
            CopyButton(about, "О программе", 108); about.Click += delegate { ShowAbout(); };
            header.Controls.Add(setupToggle, 1, 0); header.Controls.Add(expand, 2, 0); header.Controls.Add(about, 3, 0); root.Controls.Add(header, 0, 0);

            FlowLayoutPanel buttons = FlowRow();
            CopyButton(start, "Подключиться и записывать", 215); start.Click += delegate { StartRecording(); };
            CopyButton(stop, "■ Стоп", 76); stop.Click += delegate { StopRecording(); };
            CopyButton(mark, "Отметка · F8", 112); mark.Click += delegate { Mark(); };
            CopyButton(open, "Открыть запись…", 140); open.Click += delegate { OpenRecording(); };
            CopyButton(folder, "Папка сеанса", 117); folder.Click += delegate { OpenFolder(); };
            buttons.Controls.Add(start); buttons.Controls.Add(stop); buttons.Controls.Add(mark); buttons.Controls.Add(open); buttons.Controls.Add(folder);
            counters.Width = 280; counters.Height = 28; counters.TextAlign = ContentAlignment.MiddleLeft; counters.ForeColor = Color.FromArgb(91, 110, 130); buttons.Controls.Add(counters); root.Controls.Add(buttons, 0, 1);

            settings = new TableLayoutPanel { Dock = DockStyle.Top, RowCount = 3, ColumnCount = 1, BackColor = Color.White, Padding = new Padding(6, 2, 6, 2), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 5) };
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1200));
            settings.ClientSizeChanged += delegate { float width = Math.Max(1, settings.ClientSize.Width - settings.Padding.Horizontal); if (Math.Abs(settings.ColumnStyles[0].Width - width) > .5f) settings.ColumnStyles[0].Width = width; };
            settings.RowStyles.Add(new RowStyle(SizeType.AutoSize)); settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 29)); settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); root.Controls.Add(settings, 0, 2);
            FlowLayoutPanel first = FlowRow();
            SetupCombo(mode, 210); mode.Items.AddRange(new object[] { "Пример · искусственные данные", "Автомобиль · только чтение" }); mode.SelectedIndex = 1; mode.SelectedIndexChanged += delegate { UpdateMode(); };
            SetupCombo(port, 88); port.DropDownStyle = ComboBoxStyle.DropDown; port.AutoCompleteMode = AutoCompleteMode.SuggestAppend; port.AutoCompleteSource = AutoCompleteSource.ListItems;
            port.TextChanged += delegate { port.BackColor = SystemColors.Window; UpdateButtons(); };
            SetupCombo(duration, 78); duration.Items.AddRange(new object[] { "30 мин", "60 мин", "120 мин" }); duration.SelectedIndex = 1;
            CopyButton(refresh, "Обновить COM", 110); refresh.Click += delegate { RefreshPorts(); };
            injection.Text = "Впрыск и адсорбер · реже"; injection.Width = 210; injection.Height = 27;
            hints.SetToolTip(injection, "Добавляет отдельный запрос 1C не чаще раза в 2 с; может снижать частоту основных ответов. Только читает показания, без управления форсунками.");
            hints.SetToolTip(duration, "Длительность записи журнала на ПК. Не меняет частоту запросов или скорость порта ЭБУ.");
            first.Controls.Add(Label("Источник", 63)); first.Controls.Add(mode); first.Controls.Add(Label("Порт", 36)); first.Controls.Add(port); first.Controls.Add(refresh); first.Controls.Add(Label("Запись", 52)); first.Controls.Add(duration); first.Controls.Add(injection); settings.Controls.Add(first, 0, 0);
            TableLayoutPanel path = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            path.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 63)); path.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); path.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 102));
            output.Dock = DockStyle.Fill; output.Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recordings"); output.Margin = new Padding(0, 2, 7, 0);
            CopyButton(choose, "Выбрать…", 95); choose.Click += delegate { using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "Папка для новых записей", SelectedPath = Directory.Exists(output.Text) ? output.Text : "" }) if (dialog.ShowDialog(this) == DialogResult.OK) output.Text = dialog.SelectedPath; };
            path.Controls.Add(Label("Записи", 63), 0, 0); path.Controls.Add(output, 1, 0); path.Controls.Add(choose, 2, 0); settings.Controls.Add(path, 0, 1);
            warning.Dock = DockStyle.Fill; warning.AutoEllipsis = true; warning.TextAlign = ContentAlignment.MiddleLeft; warning.ForeColor = Color.FromArgb(116, 80, 32); settings.Controls.Add(warning, 0, 2);

            mainSplit = new SplitContainer { Size = new Size(1250, 490), Dock = DockStyle.Fill, SplitterDistance = 312, FixedPanel = FixedPanel.Panel1, SplitterWidth = 6, BackColor = BackColor, Margin = Padding.Empty };
            root.Controls.Add(mainSplit, 0, 3); mainSplit.SizeChanged += delegate { FitResponsivePanels(); };
            TableLayoutPanel sensors = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = Color.White, Padding = new Padding(5) };
            sensors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sensors.RowStyles.Add(new RowStyle(SizeType.Absolute, 23)); sensors.RowStyles.Add(new RowStyle(SizeType.Absolute, 153)); sensors.RowStyles.Add(new RowStyle(SizeType.Absolute, 25)); sensors.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); mainSplit.Panel1.Controls.Add(sensors); sensorPanel = sensors;
            sensors.Controls.Add(new Label { Text = SensorCaption(), Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            selected.Dock = DockStyle.Fill; selected.CheckOnClick = true; selected.BorderStyle = BorderStyle.None; selected.IntegralHeight = false; selected.MultiColumn = false;
            foreach (SensorDefinition def in Sensors.All) if (def.Graphable) { graphSensors.Add(def); selected.Items.Add(def.Name, IsCorePrimary(def)); }
            hints.SetToolTip(selected, "Выбор кривых меняет только отображение. Все основные поля уже приходят одним ответом F4; дополнительные поля EE доступны при включённом чтении впрыска. Выбор кривых не добавляет запросов к ЭБУ.");
            selected.MouseMove += delegate(object sender, MouseEventArgs e) { int index = selected.IndexFromPoint(e.Location); if (index >= 0 && index < graphSensors.Count) { string text = graphSensors[index].Description + "\nВыбор кривой меняет только отображение."; if (hints.GetToolTip(selected) != text) hints.SetToolTip(selected, text); } };
            selected.ItemCheck += delegate { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)UpdateSelection); }; sensors.Controls.Add(selected, 0, 1);
            currentTitle.Text = "Последние ответы · свежесть 1 с"; currentTitle.Dock = DockStyle.Fill; currentTitle.TextAlign = ContentAlignment.MiddleLeft; currentTitle.Font = new Font(Font, FontStyle.Bold); sensors.Controls.Add(currentTitle, 0, 2);
            SetupGrid(current); current.Columns.Add("sensor", "Датчик"); current.Columns.Add("value", "Значение"); current.Columns.Add("measured", "Время ответа"); current.Columns.Add("offset", "Возраст, мс");
            current.Columns[0].FillWeight = 30; current.Columns[1].FillWeight = 25; current.Columns[2].FillWeight = 29; current.Columns[3].FillWeight = 16;
            foreach (SensorDefinition def in Sensors.All) { int row = current.Rows.Add(ShortSensorName(def.Key), "—", "—", "—"); current.Rows[row].Cells[0].ToolTipText = def.Name + "\n" + def.Description; } sensors.Controls.Add(current, 0, 3);

            historySplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5, BackColor = BackColor, Size = new Size(920, 490), Panel1MinSize = 140, Panel2MinSize = 55, SplitterDistance = 400 }; mainSplit.Panel2.Controls.Add(historySplit);
            historySplit.SizeChanged += delegate { FitHistory(); };
            TableLayoutPanel chart = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = Color.White };
            chart.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); chart.RowStyles.Add(new RowStyle(SizeType.AutoSize)); chart.RowStyles.Add(new RowStyle(SizeType.AutoSize)); chart.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); chart.RowStyles.Add(new RowStyle(SizeType.Absolute, 17)); historySplit.Panel1.Controls.Add(chart);
            FlowLayoutPanel toolbar = FlowRow();
            toolbar.Controls.Add(Label("Масштаб", 65)); zoom.Minimum = 0; zoom.Maximum = windows.Length - 1; zoom.Width = 145; zoom.Height = 27; zoom.TickStyle = TickStyle.None; zoom.SmallChange = 1; zoom.LargeChange = 1;
            zoom.ValueChanged += delegate { timeline.SetWindow(windows[zoom.Value]); viewLabel.Text = windowNames[zoom.Value]; if (timeline.CursorMs.HasValue) timeline.PanTo(timeline.CursorMs.Value - timeline.SpanMs / 2); }; toolbar.Controls.Add(zoom);
            viewLabel.Text = windowNames[0]; viewLabel.Width = 95; viewLabel.Height = 27; viewLabel.TextAlign = ContentAlignment.MiddleLeft; toolbar.Controls.Add(viewLabel);
            follow.Text = "Следить за концом"; follow.Checked = true; follow.Width = 150; follow.Height = 27; follow.CheckedChanged += delegate { if (follow.Checked) { timeline.ClearSelection(); timeline.FollowLatest(); } }; toolbar.Controls.Add(follow);
            compact.Checked = true; compact.CheckedChanged += delegate { timeline.Compact = compact.Checked; FitTimeline(); };
            SetupCombo(viewPreset, 160); viewPreset.Items.AddRange(new object[] { "Плотный вид", "Вид 0.1.1", "Full HD · все графики" }); viewPreset.SelectedIndex = 0; viewPreset.SelectedIndexChanged += delegate { ApplyViewPreset(); }; toolbar.Controls.Add(viewPreset);
            hints.SetToolTip(viewPreset, "Только отображение: плотный вид — 7 привычных кривых; вид 0.1.1 — 3 кривые и более высокие строки; Full HD — все доступные графики. Переключение не добавляет запросов и не меняет протокол или частоту ЭБУ.");
            Button clear = new Button(); CopyButton(clear, "Без курсора", 103); clear.Click += delegate { timeline.ClearSelection(); RefreshCurrent(); UpdateCursorLabel(); }; toolbar.Controls.Add(clear);
            chart.Controls.Add(toolbar, 0, 0);
            FlowLayoutPanel navigation = FlowRow();
            navigation.Controls.Add(Label("К времени", 71)); clockInput.Width = 105; clockInput.Height = 25; clockInput.Margin = new Padding(0, 2, 5, 0); navigation.Controls.Add(clockInput);
            hints.SetToolTip(clockInput, "Часы записи: 18:40 или 18:40:00. Enter — перейти. Это позиция курсора, показания не интерполируются.");
            navigation.Controls.Add(Label("дата", 32)); dateInput.Width = 83; dateInput.Margin = new Padding(0, 2, 5, 0); navigation.Controls.Add(dateInput); hints.SetToolTip(dateInput, "Необязательная дата: дд.ММ.гггг. Нужна, если время встречается в записи в разные дни.");
            CopyButton(go, "Перейти", 78); go.Click += delegate { GoToClock(clockInput.Text, dateInput.Text); }; navigation.Controls.Add(go);
            clockInput.KeyDown += ClockEnter; dateInput.KeyDown += ClockEnter;
            AddNavigationButton(navigation, "−1 с", 49, delegate { MoveCursor(-1000); }); AddNavigationButton(navigation, "+1 с", 49, delegate { MoveCursor(1000); });
            AddNavigationButton(navigation, "‹ ответ", 72, delegate { StepSample(-1); }); AddNavigationButton(navigation, "ответ ›", 72, delegate { StepSample(1); });
            cursorLabel.Width = 235; cursorLabel.Height = 27; cursorLabel.TextAlign = ContentAlignment.MiddleLeft; cursorLabel.ForeColor = Color.FromArgb(51, 75, 99); navigation.Controls.Add(cursorLabel); chart.Controls.Add(navigation, 0, 1);
            chartHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, Margin = Padding.Empty };
            timeline.Dock = DockStyle.None; timeline.Location = Point.Empty; timeline.Compact = true; chartHost.Controls.Add(timeline); chart.Controls.Add(chartHost, 0, 2); chartHost.SizeChanged += delegate { FitTimeline(); };
            scroll.Dock = DockStyle.Fill; scroll.Minimum = 0; scroll.Maximum = 1000000; scroll.LargeChange = 1; scroll.SmallChange = 1000; scroll.ValueChanged += delegate { if (!refreshingScroll) { follow.Checked = false; timeline.PanTo(Math.Max(0, timeline.EndMs - timeline.SpanMs) * scroll.Value / 1000000); } }; chart.Controls.Add(scroll, 0, 3);
            TableLayoutPanel eventPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.White };
            eventPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); eventPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); eventPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); historySplit.Panel2.Controls.Add(eventPanel);
            eventPanel.Controls.Add(new Label { Text = "  События · двойной щелчок: курсор и интервал 5 с", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            SetupGrid(events); events.Columns.Add("time", "Время"); events.Columns.Add("elapsed", "+ сек"); events.Columns.Add("kind", "Событие"); events.Columns.Add("message", "Описание"); events.Columns[0].FillWeight = 16; events.Columns[1].FillWeight = 11; events.Columns[2].FillWeight = 20; events.Columns[3].FillWeight = 53;
            events.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) { LogEvent item = events.Rows[e.RowIndex].Tag as LogEvent; if (item != null) FocusTime(item.ElapsedMs); } }; eventPanel.Controls.Add(events, 0, 1);
            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 157));
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.TextAlign = ContentAlignment.MiddleLeft; status.ForeColor = Color.FromArgb(60, 83, 108); footer.Controls.Add(status, 0, 0);
            FlowLayoutPanel brand = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
            LinkLabel website = new LinkLabel { Name = "websiteLink", Text = "leitoauto.com", AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(0, 5, 0, 0) };
            website.LinkClicked += delegate { OpenAuthorWebsite(); }; hints.SetToolTip(website, ProductInfo.Website + " — открыть сайт в браузере"); brand.Controls.Add(website);
            SiteMonogram monogram = new SiteMonogram { Name = "siteMonogram", Size = new Size(26, 26), Margin = new Padding(0, 0, 7, 0) };
            monogram.Click += delegate { OpenAuthorWebsite(); }; hints.SetToolTip(monogram, "LA · " + ProductInfo.Website); brand.Controls.Add(monogram); footer.Controls.Add(brand, 1, 0); root.Controls.Add(footer, 0, 4);
            timeline.SetRecording(recording); UpdateCursorLabel();
            Shown += delegate { FitHistory(); FitTimeline(); };
        }

        private static string ShortSensorName(string key)
        {
            switch (key)
            {
                case "rpm": return "Обороты";
                case "voltage": return "Напр. ЭБУ";
                case "airflow": return "Расходомер";
                case "throttle": return "Дроссель";
                case "ignition": return "Зажигание";
                case "coolant": return "Темп. ОЖ";
                case "air_temperature": return "Темп. возд.";
                case "injection": return "Впрыск";
                case "purge": return "Адсорбер";
                case "co_potentiometer": return "Потенц. CO";
                case "lambda_mv": return "Лямбда";
                case "engine_load": return "Нагрузка";
                case "throttle_switch": return "Контакт ДЗ";
                case "lambda_enabled": return "Конф. λ";
                default: foreach (SensorDefinition def in Sensors.All) if (def.Key == key) return def.Name; return key;
            }
        }

        private static FlowLayoutPanel FlowRow() { return new FlowLayoutPanel { Dock = DockStyle.Top, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 30), Margin = Padding.Empty, Padding = new Padding(0, 1, 0, 1) }; }
        private void AddNavigationButton(FlowLayoutPanel row, string text, int width, Action action) { Button button = new Button(); CopyButton(button, text, width); button.Click += delegate { action(); }; row.Controls.Add(button); }

        private void CopyButton(Button target, string text, int width) { target.Text = text; target.Width = width; target.Height = 28; target.FlatStyle = FlatStyle.System; target.Margin = new Padding(0, 0, 7, 0); }
        private void SetupGrid(DataGridView grid)
        {
            grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false; grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false; grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; grid.GridColor = Color.FromArgb(232, 236, 242); grid.RowTemplate.Height = 21; grid.ColumnHeadersHeight = 24; grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(235, 241, 248); grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(57, 82, 111); grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(218, 232, 247); grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(29, 52, 80);
        }

        private void UpdateSelection()
        {
            if (IsDisposed) return;
            List<string> keys = new List<string>(); for (int i = 0; i < selected.Items.Count; i++) if (selected.GetItemChecked(i)) keys.Add(graphSensors[i].Key);
            timeline.SelectSensors(keys);
            FitTimeline();
        }

        private void ApplyViewPreset()
        {
            bool dense = viewPreset.SelectedIndex != 1;
            compact.Checked = dense;
            for (int i = 0; i < selected.Items.Count; i++)
            {
                SensorDefinition def = graphSensors[i];
                bool show = viewPreset.SelectedIndex == 2 || (dense ? IsCorePrimary(def) : IsLegacyCurve(def.Key));
                selected.SetItemChecked(i, show);
            }
            UpdateSelection();
        }

        private void UpdateScaleLabel()
        {
            double span = timeline.SpanMs;
            if (timeline.IsFullRange) { viewLabel.Text = windowNames[0]; return; }
            for (int i = 1; i < windows.Length; i++) if (Math.Abs(span - windows[i]) < 1) { viewLabel.Text = windowNames[i]; return; }
            viewLabel.Text = (span / 1000).ToString("0.###", CultureInfo.InvariantCulture) + " с";
        }

        private void FitTimeline()
        {
            if (chartHost == null || fittingTimeline) return;
            fittingTimeline = true;
            timeline.Margin = Padding.Empty;
            chartHost.AutoScroll = !compact.Checked;
            chartHost.AutoScrollMinSize = Size.Empty;
            if (compact.Checked)
            {
                // Fill the actual viewport; stale AutoScroll display bounds from a
                // taller legacy view must not leave phantom H/V scrollbars.
                timeline.Dock = DockStyle.Fill;
            }
            else
            {
                timeline.Dock = DockStyle.None; timeline.Location = Point.Empty;
                int required = Math.Max(180, timeline.SelectedSensorCount * 64 + 87);
                timeline.Height = Math.Max(chartHost.ClientSize.Height, required);
                int scrollbar = timeline.Height > chartHost.ClientSize.Height && !chartHost.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
                timeline.Width = Math.Max(360, chartHost.ClientSize.Width - scrollbar - 1);
            }
            fittingTimeline = false;
        }

        private void FitResponsivePanels()
        {
            if (mainSplit == null || sensorPanel == null || mainSplit.ClientSize.Width < 600) return;
            int desired = mainSplit.ClientSize.Width >= 1500 ? 370 : 312;
            desired = Math.Min(desired, mainSplit.ClientSize.Width - 400);
            if (desired > 0 && mainSplit.SplitterDistance != desired) mainSplit.SplitterDistance = desired;
            int available = mainSplit.Panel1.ClientSize.Height;
            int choices = Math.Max(153, selected.Items.Count * Math.Max(16, selected.ItemHeight));
            int choiceHeight = Math.Min(choices, Math.Max(120, (int)(available * .35)));
            if (Math.Abs(sensorPanel.RowStyles[1].Height - choiceHeight) > 1) sensorPanel.RowStyles[1].Height = choiceHeight;
        }

        private void FitHistory()
        {
            if (historySplit == null) return;
            if (!historySplit.Panel2Collapsed)
            {
                int available = historySplit.ClientSize.Height - historySplit.SplitterWidth;
                if (available >= historySplit.Panel1MinSize + historySplit.Panel2MinSize)
                    historySplit.SplitterDistance = Math.Max(historySplit.Panel1MinSize, available - (available >= 650 ? 112 : 82));
            }
            FitTimeline();
        }

        private void SetExpanded(bool value)
        {
            expanded = value;
            mainSplit.Panel1Collapsed = value;
            historySplit.Panel2Collapsed = value;
            settings.Visible = !value;
            setupToggle.Text = settings.Visible ? "Настройки ▾" : "Настройки ▸";
            expand.Text = value ? "Вернуть панели" : "Развернуть графики";
            FitHistory();
        }

        private void OpenAuthorWebsite()
        {
            try { Process.Start(new ProcessStartInfo(ProductInfo.Website) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось открыть сайт", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }

        private void ShowAbout()
        {
            using (Form dialog = CreateAboutDialog()) dialog.ShowDialog(this);
        }

        internal Form CreateAboutDialog()
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            Form dialog = new Form { Text = "О программе · " + ProductInfo.Name + " · " + ProductInfo.Version, Font = Font,
                ClientSize = new Size(Math.Min(730, Math.Max(300, area.Width - 60)), Math.Min(530, Math.Max(280, area.Height - 100))),
                StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label title = new Label { Text = ProductInfo.Name + " · " + ProductInfo.Version, Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
            layout.Controls.Add(title, 0, 0);
            TabControl tabs = new TabControl { Dock = DockStyle.Fill };
            string[] headings = { "О программе / AS IS", "Подключение и журнал" };
            string[] texts = { ProductInfo.AboutText, ProductInfo.ConnectionGuide() };
            for (int i = 0; i < headings.Length; i++)
            {
                TabPage page = new TabPage(headings[i]) { Padding = new Padding(8) };
                Control text;
                if (i == 0)
                    text = new TextBox { Name = "aboutText", Dock = DockStyle.Fill,
                        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                        BackColor = SystemColors.Window, Text = texts[i] };
                else
                    text = CreateGuideView(texts[i]);
                page.Controls.Add(text); tabs.TabPages.Add(page);
            }
            layout.Controls.Add(tabs, 0, 1);
            FlowLayoutPanel bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 10, 0, 0), WrapContents = true };
            SiteMonogram logo = new SiteMonogram { Name = "aboutMonogram", Size = new Size(28, 28), Margin = new Padding(0, 0, 8, 0) };
            logo.Click += delegate { OpenAuthorWebsite(); }; bottom.Controls.Add(logo);
            LinkLabel link = new LinkLabel { Text = ProductInfo.Website, AutoSize = true, Margin = new Padding(0, 5, 20, 0) };
            link.LinkClicked += delegate { OpenAuthorWebsite(); }; bottom.Controls.Add(link);
            Button close = new Button { Text = "Закрыть", DialogResult = DialogResult.OK, Size = new Size(103, 28) };
            bottom.Controls.Add(close); layout.Controls.Add(bottom, 0, 2);
            dialog.Controls.Add(layout); dialog.AcceptButton = close; dialog.CancelButton = close;
            return dialog;
        }

        private RichTextBox CreateGuideView(string content)
        {
            RichTextBox text = new RichTextBox { Name = "connectionGuide", Dock = DockStyle.Fill, ReadOnly = true,
                BorderStyle = BorderStyle.None, BackColor = SystemColors.Window, Font = Font,
                ScrollBars = RichTextBoxScrollBars.Vertical, WordWrap = true, DetectUrls = false, Text = content };
            text.SelectAll(); text.SelectionIndent = 12; text.SelectionRightIndent = 12;
            // Format only the reader. The embedded text keeps its original facts,
            // paragraphs and list entries, with wrapping handled by the control.
            int offset = 0;
            using (Font heading = new Font(Font, FontStyle.Bold))
                foreach (string line in text.Text.Split('\n'))
                {
                    string caption = line.Trim();
                    if (caption.StartsWith("Motronic Recorder", StringComparison.Ordinal) ||
                        caption.StartsWith("ПОДКЛЮЧЕНИЕ:", StringComparison.Ordinal) ||
                        caption.StartsWith("ИСТОЧНИКИ", StringComparison.Ordinal) ||
                        (caption.Length > 2 && Char.IsDigit(caption[0]) && caption[1] == '.'))
                    {
                        text.Select(offset, line.Length); text.SelectionFont = heading;
                        text.SelectionIndent = 0; text.SelectionColor = Color.FromArgb(29, 52, 80);
                    }
                    offset += line.Length + 1;
                }
            text.Select(0, 0); text.ScrollToCaret();
            return text;
        }

        private void ClockEnter(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { GoToClock(clockInput.Text, dateInput.Text); e.Handled = true; e.SuppressKeyPress = true; } }

        public bool GoToClock(string time, string date)
        {
            double elapsed; string error;
            if (!TimeNavigation.TryResolve(recording, time, date, out elapsed, out error)) { stateText = error; status.Text = error; return false; }
            FocusTime(elapsed); return true;
        }

        private void MoveCursor(double delta)
        {
            double time = timeline.CursorMs ?? (timeline.StartMs + timeline.SpanMs / 2);
            follow.Checked = false; timeline.SelectTime(Math.Max(0, Math.Min(timeline.EndMs, time + delta))); timeline.PanTo(timeline.CursorMs.Value - timeline.SpanMs / 2);
        }

        private void StepSample(int direction)
        {
            if (recording.Samples.Count == 0) return;
            double time = timeline.CursorMs ?? (timeline.StartMs + timeline.SpanMs / 2);
            int low = 0, high = recording.Samples.Count;
            while (low < high) { int middle = low + (high - low) / 2; if (recording.Samples[middle].ElapsedMs < time) low = middle + 1; else high = middle; }
            int index;
            if (direction < 0) { index = low - 1; }
            else { index = low; while (index < recording.Samples.Count && recording.Samples[index].ElapsedMs <= time) index++; }
            index = Math.Max(0, Math.Min(recording.Samples.Count - 1, index));
            follow.Checked = false; timeline.SelectTime(recording.Samples[index].ElapsedMs); timeline.PanTo(timeline.CursorMs.Value - timeline.SpanMs / 2);
        }

        private void UpdateCursorLabel()
        {
            if (!timeline.CursorMs.HasValue) { cursorLabel.Text = "Щелчок по графику — курсор"; return; }
            double elapsed = timeline.CursorMs.Value;
            DateTimeOffset? time = TimeNavigation.TimeAt(recording, elapsed);
            cursorLabel.Text = (time.HasValue ? time.Value.ToString("HH:mm:ss.fff") : "Курсор") + " · +" + (elapsed / 1000).ToString("0.000", CultureInfo.InvariantCulture) + " с";
            hints.SetToolTip(cursorLabel, "Позиция курсора. В таблице — только настоящие ответы в пределах ±1 с, их собственное время и смещение относительно курсора.");
        }

        private void UpdateMode()
        {
            bool demo = mode.SelectedIndex == 0;
            warning.Text = demo ? "ПРИМЕР: искусственные данные. COM-порт не открывается. Это не диагностика автомобиля." : "M1.5 · только чтение. AS IS — без гарантий. Условия использования и инструкция — в «О программе».";
            warning.ForeColor = demo ? Color.FromArgb(26, 106, 164) : Color.FromArgb(159, 77, 18);
            port.Enabled = refresh.Enabled = !demo && !controller.IsRunning;
            start.Text = demo ? "Записать пример" : "Подключиться и записывать";
            start.Width = Math.Max(demo ? 160 : 215, TextRenderer.MeasureText(start.Text, start.Font).Width + 24);
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool running = controller.IsRunning;
            // Do not silently disable the only connection action. Validate missing inputs on click.
            start.Enabled = !closing && !running;
            stop.Enabled = running && !closing; mark.Enabled = running && !closing; open.Enabled = !running && !closing;
            mode.Enabled = duration.Enabled = output.Enabled = choose.Enabled = injection.Enabled = !running && !closing;
            port.Enabled = refresh.Enabled = !running && !closing && mode.SelectedIndex == 1;
            folder.Enabled = !string.IsNullOrEmpty(recording.Directory) && Directory.Exists(recording.Directory);
        }

        private void RefreshPorts()
        {
            string previous = port.Text; port.Items.Clear();
            try { string[] names = SerialPort.GetPortNames(); Array.Sort(names, StringComparer.OrdinalIgnoreCase); port.Items.AddRange(names); if (!string.IsNullOrWhiteSpace(previous)) { port.Text = previous; if (port.Items.Contains(previous)) port.SelectedItem = previous; } else if (names.Length > 0) port.SelectedIndex = 0; }
            catch (Exception ex) { stateText = "Не удалось прочитать список COM: " + ex.Message; }
            UpdateButtons();
        }

        private void StartRecording()
        {
            if (controller.IsRunning) return;
            try
            {
                SessionOptions options = new SessionOptions { Demo = mode.SelectedIndex == 0, PortName = port.Text.Trim().ToUpperInvariant(), OutputDirectory = output.Text.Trim(), DurationMinutes = duration.SelectedIndex == 0 ? 30 : duration.SelectedIndex == 2 ? 120 : 60, IncludeInjection = injection.Checked, ExperimentalConfirmed = mode.SelectedIndex == 1 };
                if (!options.Demo && String.IsNullOrWhiteSpace(options.PortName))
                {
                    stateText = "COM-порт не выбран. Выберите порт адаптера или введите его номер из Motronic Diagnost (например COM3).";
                    warning.Text = stateText; status.Text = stateText; port.BackColor = Color.MistyRose; port.Focus(); return;
                }
                if (options.OutputDirectory.Length == 0) throw new InvalidOperationException("Выберите папку для записи.");
                ClearPending(); loadedFromDisk = false; latest.Clear(); recording = new Recording { Mode = options.Demo ? "DEMO" : "LIVE", UtcOffsetMinutes = (int)DateTimeOffset.Now.Offset.TotalMinutes }; events.Rows.Clear(); timeline.SetRecording(recording); follow.Checked = true;
                controller.Start(options); recording.Directory = controller.SessionDirectory; stateText = options.Demo ? "DEMO: идет запись модельных данных." : "Запись с автомобиля: ожидание ответов."; dirty = true; UpdateButtons();
            }
            catch (Exception ex) { stateText = "Старт не выполнен: " + ex.Message; MessageBox.Show(this, ex.Message, "Не удалось начать запись", MessageBoxButtons.OK, MessageBoxIcon.Error); UpdateButtons(); }
        }

        private void StopRecording() { controller.Stop(); stateText = "Остановка и сохранение записи…"; stop.Enabled = false; }
        private void Mark() { if (controller.IsRunning && !closing) controller.Mark("Ручная отметка F8"); }
        private void OpenRecording()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "Выберите папку конкретного сеанса (с CSV и manifest)", SelectedPath = Directory.Exists(output.Text) ? output.Text : "" })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    try { LoadRecording(RecordingReader.Load(dialog.SelectedPath)); }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "Запись не открыта", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        public void LoadRecording(Recording value)
        {
            if (controller.IsRunning) throw new InvalidOperationException("Сначала остановите текущую запись.");
            ClearPending(); recording = value ?? new Recording(); loadedFromDisk = true; latest.Clear(); events.Rows.Clear();
            foreach (Sample sample in recording.Samples) UpdateLatest(sample);
            foreach (LogEvent item in recording.Events) AddEventRow(item);
            timeline.SetRecording(recording); zoom.Value = 0; follow.Checked = false;
            bool demo = (recording.Mode ?? "").IndexOf("demo", StringComparison.OrdinalIgnoreCase) >= 0;
            string note = string.IsNullOrWhiteSpace(recording.Note) ? "" : " " + recording.Note;
            warning.Text = (demo ? "ОТКРЫТА DEMO-ЗАПИСЬ — модельные данные, включая искусственный провал." : "Открыта запись: " + (recording.Mode ?? "неизвестный источник") + ".") + note;
            warning.ForeColor = demo ? Color.FromArgb(26, 106, 164) : Color.FromArgb(116, 80, 32);
            stateText = "Открыта запись: " + (recording.Directory ?? "предпросмотр") + note; dirty = true; UpdateButtons(); RefreshCurrent();
        }

        public void FocusTime(double elapsedMs)
        {
            follow.Checked = false; zoom.Value = 8; timeline.SelectTime(elapsedMs); timeline.CenterOn(elapsedMs); RefreshCurrent(); UpdateCursorLabel();
        }

        private void OpenFolder()
        {
            if (string.IsNullOrEmpty(recording.Directory)) return;
            try { Process.Start(new ProcessStartInfo(recording.Directory) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Папка не открыта", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void AddEventRow(LogEvent item)
        {
            int row = events.Rows.Add(TimeNavigation.DisplayTime(recording, item.Utc).ToString("HH:mm:ss.fff"), (item.ElapsedMs / 1000).ToString("0.000", CultureInfo.InvariantCulture), EventName(item.Kind), item.Message); events.Rows[row].Tag = item;
            if (controller.IsRunning && follow.Checked && row > 0) events.FirstDisplayedScrollingRowIndex = row;
        }

        private static string EventName(string kind)
        {
            switch (kind)
            {
                case "session_start": return "Начало записи";
                case "session_end": return "Конец записи";
                case "demo_start": return "Начало DEMO";
                case "demo_dip": return "Модельный провал";
                case "manual": return "Отметка F8";
                case "rpm_drop": return "Падение оборотов";
                case "link_lost": return "Потеря связи";
                case "link_restored": return "Связь восстановлена";
                case "connect_attempt": return "Подключение";
                case "link_initialized": return "Синхронизация";
                case "fatal_error": return "Ошибка записи";
                case "serial_control": return "Состояние порта";
                case "identification_frame": return "Идентификация";
                case "unknown_reply": return "Неизвестный ответ";
                default: return kind ?? "Событие";
            }
        }

        private void ClearPending()
        {
            lock (pendingGate) { pendingSamples.Clear(); pendingEvents.Clear(); pendingState = null; }
        }

        private void UpdateLatest(Sample sample)
        {
            // A field absent from the next reply of its own source is unavailable,
            // rather than an old measurement (notably alternative lambda/CO fields).
            foreach (SensorDefinition def in Sensors.All)
                if (def.SourceReply == sample.Reply && !sample.Values.ContainsKey(def.Key)) latest.Remove(def.Key);
            foreach (string key in sample.Values.Keys) latest[key] = sample;
        }

        private void DrainPending()
        {
            Sample[] samples; LogEvent[] items; string state;
            lock (pendingGate) { samples = pendingSamples.ToArray(); pendingSamples.Clear(); items = pendingEvents.ToArray(); pendingEvents.Clear(); state = pendingState; pendingState = null; }
            if (!loadedFromDisk)
            {
                foreach (Sample sample in samples) { recording.Samples.Add(sample); UpdateLatest(sample); }
                foreach (LogEvent item in items) { recording.Events.Add(item); AddEventRow(item); }
                if (state != null) stateText = state;
                if (samples.Length > 0 || items.Length > 0 || state != null) dirty = true;
            }
        }

        private void Tick(object sender, EventArgs e)
        {
            DrainPending();
            if (closing && !controller.IsRunning) { closeReady = true; Close(); return; }
            if (controller.IsRunning && follow.Checked) timeline.FollowLatest();
            if (!loadedFromDisk && latest.Count > 0 && !timeline.CursorMs.HasValue) RefreshCurrent();
            if (dirty || controller.IsRunning)
            {
                timeline.Invalidate(); RefreshCurrent(); UpdateCursorLabel(); UpdateScroll();
                counters.Text = string.Format(CultureInfo.InvariantCulture, "{0:N0} ответов · {1} событий · {2:0.0} мин", recording.Samples.Count, recording.Events.Count, timeline.EndMs / 60000);
                status.Text = stateText; dirty = false; UpdateButtons();
            }
        }

        private void RefreshCurrent()
        {
            bool cursor = timeline.CursorMs.HasValue;
            currentTitle.Text = cursor ? "Ответы рядом с курсором · ±1 с" : loadedFromDisk ? "Ответы в конце записи" : "Последние ответы · свежесть 1 с";
            current.Columns[3].HeaderText = cursor ? "Δ, мс" : "Возраст, мс";
            for (int i = 0; i < Sensors.All.Length; i++)
            {
                SensorDefinition def = Sensors.All[i]; Sample sample = null; double value = 0, offset = 0;
                if (cursor)
                {
                    NearestReading nearest = TimeNavigation.FindNearest(recording, def.Key, timeline.CursorMs.Value, 1000);
                    if (nearest != null) { sample = nearest.Sample; value = nearest.Value; offset = nearest.OffsetMs; }
                }
                else if (latest.TryGetValue(def.Key, out sample)) { value = sample.Values[def.Key]; offset = loadedFromDisk ? Math.Max(0, timeline.EndMs - sample.ElapsedMs) : Math.Max(0, (DateTimeOffset.UtcNow - sample.Utc).TotalMilliseconds); }
                if (sample == null)
                {
                    current.Rows[i].Cells[1].Value = "—"; current.Rows[i].Cells[2].Value = "—"; current.Rows[i].Cells[3].Value = "—";
                    current.Rows[i].Cells[1].ToolTipText = (cursor ? "Рядом с курсором нет настоящего ответа. Значение не интерполируется." : "Ответ ещё не получен или это поле не доступно в данном ответе.") + "\n" + def.Description;
                    current.Rows[i].DefaultCellStyle.ForeColor = Color.Gray; continue;
                }
                bool stale = !cursor && !loadedFromDisk && offset > 1000;
                string text = Sensors.FormatValue(def, value) + (stale ? " · устар." : "");
                string clockText = TimeNavigation.DisplayTime(recording, sample.Utc).ToString("HH:mm:ss.fff");
                current.Rows[i].Cells[1].Value = text;
                current.Rows[i].Cells[2].Value = clockText;
                current.Rows[i].Cells[3].Value = cursor ? offset.ToString("+0;-0;0", CultureInfo.InvariantCulture) : offset.ToString("0", CultureInfo.InvariantCulture);
                current.Rows[i].Cells[1].ToolTipText = text + "\nНастоящий ответ " + sample.Reply.ToString("X2") + "; " + clockText + "; +" + (sample.ElapsedMs / 1000).ToString("0.000", CultureInfo.InvariantCulture) + " с; связь " + sample.Epoch + "\n" + def.Description;
                current.Rows[i].DefaultCellStyle.ForeColor = stale ? Color.FromArgb(159, 91, 24) : Color.FromArgb(35, 54, 75);
            }
        }

        private void UpdateScroll()
        {
            refreshingScroll = true;
            double range = Math.Max(0, timeline.EndMs - timeline.SpanMs);
            scroll.Enabled = range > 0; scroll.Value = range <= 0 ? 0 : Math.Max(0, Math.Min(1000000, (int)(timeline.StartMs / range * 1000000)));
            refreshingScroll = false;
        }

        private void HandleFormClosing(object sender, FormClosingEventArgs e)
        {
            if (closeReady || !controller.IsRunning) return;
            e.Cancel = true; closing = true; controller.Stop(); stateText = "Завершение: закрываю журнал и порт…"; status.Text = stateText; UpdateButtons();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Stop(); timer.Dispose(); controller.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // Native vector counterpart of the site's CSS logo-mark: LA, #38bdf8,
    // #06202f, radius 12 at 38 px. Scaling preserves the same proportions.
    internal sealed class SiteMonogram : Control
    {
        public SiteMonogram()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent; Cursor = Cursors.Hand; TabStop = true;
            Font = new Font("Segoe UI", 10f, FontStyle.Bold); Text = "LA";
            AccessibleName = "LA — leitoauto.com"; AccessibleRole = AccessibleRole.PushButton;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF rect = new RectangleF(0, 0, Math.Max(1, ClientSize.Width - 1), Math.Max(1, ClientSize.Height - 1));
            float diameter = Math.Max(1f, Math.Min(rect.Width, rect.Height) * 24f / 38f);
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
                path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
                path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure();
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(56, 189, 248))) e.Graphics.FillPath(brush, path);
            }
            TextRenderer.DrawText(e.Graphics, "LA", Font, ClientRectangle, Color.FromArgb(6, 32, 47), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2), Color.FromArgb(6, 32, 47), Color.FromArgb(56, 189, 248));
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e); if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; e.SuppressKeyPress = true; }
        }
    }

}
