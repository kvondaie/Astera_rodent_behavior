// Water valve calibration for the Harp Behavior board (same hardware mapping as the tasks:
// Left = SupplyPort0, Right = SupplyPort1).
//
// Window: pick ports, pulse length (ms), drops per port and the interval between drops, press
// Start. With both ports ticked the valves alternate (L, R, L, R ...), each port still getting one
// drop per interval. When it finishes, weigh the water from each port (grams; 1 g = 1000 uL),
// type it in, and the window shows uL per drop. "Save result" appends one row per port to
// valve_calibration.csv (next to the workflow) and the history list shows the latest rows.
// "Flush" opens a valve once for the flush time to fill / purge the line before calibrating.
//
// Written for C# 5 (Bonsai compiles this with the .NET Framework compiler).

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Windows.Forms;
using Bonsai;
using Bonsai.Harp;
using Harp.Behavior;

[Combinator]
[WorkflowElementCategory(ElementCategory.Transform)]
[Description("Valve calibration window. Input: timestamped digital input state (board-ready signal). Output: commands for the Behavior board.")]
public class ValveCalibration
{
    public ValveCalibration()
    {
        PulseMs = 20;
        Drops = 100;
        IntervalSeconds = 0.5;
        FlushMs = 1000;
        LogFile = "valve_calibration.csv";
    }

    [Description("Valve open time (ms) per drop.")]
    public int PulseMs { get; set; }

    [Description("Drops per port.")]
    public int Drops { get; set; }

    [Description("Seconds between drops on the same port.")]
    public double IntervalSeconds { get; set; }

    [Description("Valve open time (ms) for the Flush buttons.")]
    public int FlushMs { get; set; }

    [Description("CSV that saved calibrations are appended to.")]
    public string LogFile { get; set; }

    public IObservable<HarpMessage> Process(IObservable<Timestamped<DigitalInputs>> source)
    {
        return Observable.Create<HarpMessage>(observer =>
        {
            var session = new Session(this, observer);
            var subscription = source.Subscribe(session.OnInput, observer.OnError);
            session.OpenWindow();
            return new CompositeDisposable(subscription, session);
        });
    }

    internal class Session : IDisposable
    {
        readonly ValveCalibration p;
        readonly IObserver<HarpMessage> output;
        readonly object gate = new object();
        readonly SerialDisposable timer = new SerialDisposable();
        CalibrationWindow window;
        bool boardReady, running, disposed;
        int token;

        // Current / last run
        int[] order = new int[0];
        int step;
        readonly int[] given = new int[2];
        int runMs, runDrops; double runInterval;
        bool[] runPorts = new bool[2];
        DateTime runStart;
        string state = "Waiting for board";

        public Session(ValveCalibration parameters, IObserver<HarpMessage> observer)
        {
            p = parameters;
            output = observer;
        }

        public ValveCalibration P { get { return p; } }

        public string LogPath
        {
            get { return Path.GetFullPath(string.IsNullOrEmpty(p.LogFile) ? "valve_calibration.csv" : p.LogFile); }
        }

        public void OnInput(Timestamped<DigitalInputs> input)
        {
            lock (gate)
            {
                if (disposed || boardReady) return;
                boardReady = true;
                Send(OutputPulseEnable.FromPayload(MessageType.Write, DigitalOutputs.SupplyPort0 | DigitalOutputs.SupplyPort1));
                state = "Ready";
                UpdateWindow();
            }
        }

        public void OpenWindow()
        {
            var w = CalibrationWindow.Open(this);
            lock (gate) { window = w; UpdateWindow(); }
        }

        public void Start(bool left, bool right, int ms, int drops, double interval)
        {
            lock (gate)
            {
                if (disposed || running) return;
                if (!boardReady) { state = "Board not connected yet"; UpdateWindow(); return; }
                if (!left && !right) { state = "Tick at least one port"; UpdateWindow(); return; }
                p.PulseMs = ms; p.Drops = drops; p.IntervalSeconds = interval;
                runMs = ms; runDrops = drops; runInterval = interval;
                runPorts = new[] { left, right };
                var list = new List<int>();
                for (int i = 0; i < drops; i++)
                {
                    if (left) list.Add(0);
                    if (right) list.Add(1);
                }
                order = list.ToArray();
                step = 0;
                given[0] = given[1] = 0;
                running = true;
                runStart = DateTime.Now;
                state = "Running";
                Next();
            }
        }

        // One drop, then schedule the next. Spacing = interval / ports, so each port gets one per interval.
        void Next()
        {
            if (step >= order.Length)
            {
                running = false;
                state = string.Format(CultureInfo.InvariantCulture,
                    "Done: {0} drops of {1} ms per port. Weigh the water and enter it below.", runDrops, runMs);
                UpdateWindow();
                return;
            }
            int port = order[step++];
            Valve(port, runMs);
            given[port]++;
            UpdateWindow();
            int nPorts = (runPorts[0] ? 1 : 0) + (runPorts[1] ? 1 : 0);
            var t = ++token;
            timer.Disposable = Scheduler.Default.Schedule(TimeSpan.FromSeconds(runInterval / nPorts), () =>
            {
                lock (gate) { if (!disposed && running && t == token) Next(); }
            });
        }

        public void Stop()
        {
            lock (gate)
            {
                if (!running) return;
                running = false;
                token++;
                timer.Disposable = null;
                state = string.Format("Stopped early: L {0}, R {1} drops given (uL/drop uses these counts).", given[0], given[1]);
                UpdateWindow();
            }
        }

        public void Flush(int port)
        {
            lock (gate)
            {
                if (disposed || running || !boardReady) return;
                Valve(port, p.FlushMs);
                state = string.Format("Flushed {0} for {1} ms", port == 0 ? "L" : "R", p.FlushMs);
                UpdateWindow();
            }
        }

        public int Given(int port) { lock (gate) return given[port]; }
        public int RunMs { get { lock (gate) return runMs; } }
        public double RunInterval { get { lock (gate) return runInterval; } }

        // Appends one row per port that has a weight. Returns a message for the window.
        public string Save(double waterL, double waterR, string note)
        {
            lock (gate)
            {
                if (running) return "Wait for the run to finish.";
                if (runMs == 0) return "Run a calibration first.";
                var ci = CultureInfo.InvariantCulture;
                var rows = new List<string>();
                var water = new[] { waterL, waterR };
                for (int port = 0; port < 2; port++)
                {
                    if (double.IsNaN(water[port]) || given[port] == 0) continue;
                    double ul = water[port] * 1000.0 / given[port];
                    rows.Add(string.Join(",", new[]
                    {
                        DateTime.Now.ToString("yyyy-MM-dd", ci), DateTime.Now.ToString("HH:mm:ss", ci),
                        port == 0 ? "L" : "R", runMs.ToString(ci), given[port].ToString(ci),
                        runInterval.ToString("0.###", ci), water[port].ToString("0.####", ci),
                        ul.ToString("0.###", ci), Csv(note)
                    }));
                }
                if (rows.Count == 0) return "Enter the water weight for at least one port.";
                var path = LogPath;
                bool header = !File.Exists(path);
                using (var w = new StreamWriter(path, true))
                {
                    if (header) w.WriteLine("Date,Time,Port,PulseMs,Drops,IntervalSec,WaterG,uLPerDrop,Note");
                    foreach (var r in rows) w.WriteLine(r);
                }
                return "Saved " + rows.Count + (rows.Count == 1 ? " row" : " rows") + " to " + path;
            }
        }

        static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        void UpdateWindow()
        {
            if (window == null) return;
            string progress = "";
            if (order.Length > 0)
            {
                var parts = new List<string>();
                if (runPorts[0]) parts.Add(string.Format("L {0}/{1}", given[0], runDrops));
                if (runPorts[1]) parts.Add(string.Format("R {0}/{1}", given[1], runDrops));
                progress = string.Join("   ", parts.ToArray());
                if (running)
                {
                    int nPorts = parts.Count;
                    double left = (order.Length - step) * runInterval / nPorts;
                    progress += string.Format(CultureInfo.InvariantCulture, "   |   {0:0} s left", left);
                }
            }
            window.Post(state, progress, running, boardReady);
        }

        void Valve(int port, int ms)
        {
            if (ms <= 0) return;
            if (port == 0)
            {
                Send(PulseSupplyPort0.FromPayload(MessageType.Write, (ushort)ms));
                Send(OutputSet.FromPayload(MessageType.Write, DigitalOutputs.SupplyPort0));
            }
            else
            {
                Send(PulseSupplyPort1.FromPayload(MessageType.Write, (ushort)ms));
                Send(OutputSet.FromPayload(MessageType.Write, DigitalOutputs.SupplyPort1));
            }
        }

        void Send(HarpMessage message) { if (!disposed) output.OnNext(message); }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                running = false;
                timer.Dispose();
            }
            if (window != null) window.CloseFromTask();
        }
    }

    // ---- Window (own UI thread) ----

    internal class CalibrationWindow : Form
    {
        readonly Session session;
        readonly Button startButton, stopButton, flushL, flushR, saveButton;
        readonly CheckBox leftBox, rightBox;
        readonly NumericUpDown msBox, dropsBox, intervalBox;
        readonly TextBox waterLBox, waterRBox, noteBox, history;
        readonly Label stateLabel, progressLabel, resultL, resultR, saveLabel;
        bool closingFromTask;

        public static CalibrationWindow Open(Session session)
        {
            CalibrationWindow form = null;
            var ready = new ManualResetEvent(false);
            var thread = new Thread(() =>
            {
                Application.EnableVisualStyles();
                form = new CalibrationWindow(session);
                form.Load += delegate { ready.Set(); };
                Application.Run(form);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            ready.WaitOne(5000);
            return form;
        }

        static FlowLayoutPanel Row(int height)
        {
            return new FlowLayoutPanel { Dock = DockStyle.Top, Height = height, Padding = new Padding(8, 6, 8, 0) };
        }

        static Label MakeLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(4, 7, 0, 0) };
        }

        static Label WideLabel(string text)
        {
            return new Label { Text = text, AutoSize = false, Width = 110, Height = 24, Margin = new Padding(4, 7, 0, 0) };
        }

        static Button MakeButton(string text, Color color)
        {
            return new Button
            {
                Text = text, Width = 100, Height = 32, BackColor = color, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Margin = new Padding(4, 0, 4, 0)
            };
        }

        static NumericUpDown Num(decimal min, decimal max, decimal step, int decimals, decimal value, int width)
        {
            return new NumericUpDown
            {
                Minimum = min, Maximum = max, Increment = step, DecimalPlaces = decimals, Width = width,
                Value = Math.Max(min, Math.Min(max, value)), Margin = new Padding(4, 4, 4, 0)
            };
        }

        CalibrationWindow(Session session)
        {
            this.session = session;
            var p = session.P;
            Text = "Valve calibration";
            ClientSize = new Size(760, 560);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(100, 100);
            Font = new Font("Segoe UI", 10f);
            BackColor = Color.White;

            var top = Row(48);
            top.Padding = new Padding(8, 8, 8, 0);
            startButton = MakeButton("Start", Color.FromArgb(46, 160, 67));
            stopButton = MakeButton("Stop", Color.FromArgb(207, 34, 46));
            flushL = MakeButton("Flush L", Color.FromArgb(9, 105, 218));
            flushR = MakeButton("Flush R", Color.FromArgb(9, 105, 218));
            top.Controls.AddRange(new Control[] { startButton, stopButton, flushL, flushR,
                MakeLabel(string.Format("   (flush = {0} ms)", p.FlushMs)) });

            var settings = Row(40);
            leftBox = new CheckBox { Text = "Left", Checked = true, AutoSize = true, Margin = new Padding(4, 6, 4, 0) };
            rightBox = new CheckBox { Text = "Right", Checked = true, AutoSize = true, Margin = new Padding(4, 6, 12, 0) };
            msBox = Num(1, 2000, 1, 0, p.PulseMs, 70);
            dropsBox = Num(1, 5000, 10, 0, p.Drops, 75);
            intervalBox = Num(0.05m, 30, 0.1m, 2, (decimal)p.IntervalSeconds, 70);
            settings.Controls.AddRange(new Control[]
            {
                leftBox, rightBox, MakeLabel("Pulse (ms)"), msBox, MakeLabel("   Drops per port"), dropsBox,
                MakeLabel("   Interval (s)"), intervalBox
            });

            var status = new Panel { Dock = DockStyle.Top, Height = 64, Padding = new Padding(12, 6, 8, 0) };
            stateLabel = new Label { Dock = DockStyle.Top, Height = 26, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
            progressLabel = new Label { Dock = DockStyle.Top, Height = 26 };
            status.Controls.Add(progressLabel);
            status.Controls.Add(stateLabel);

            var weighL = Row(38);
            waterLBox = new TextBox { Width = 80, Margin = new Padding(4, 4, 4, 0) };
            resultL = new Label { AutoSize = true, Margin = new Padding(8, 7, 0, 0), Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
            weighL.Controls.AddRange(new Control[] { WideLabel("Left water (g)"), waterLBox, resultL });
            var weighR = Row(38);
            waterRBox = new TextBox { Width = 80, Margin = new Padding(4, 4, 4, 0) };
            resultR = new Label { AutoSize = true, Margin = new Padding(8, 7, 0, 0), Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
            weighR.Controls.AddRange(new Control[] { WideLabel("Right water (g)"), waterRBox, resultR });

            var saveRow = Row(44);
            noteBox = new TextBox { Width = 300, Margin = new Padding(4, 4, 4, 0) };
            saveButton = MakeButton("Save result", Color.FromArgb(110, 84, 148));
            saveButton.Width = 120;
            saveLabel = new Label { AutoSize = true, Margin = new Padding(8, 7, 0, 0), ForeColor = Color.Gray };
            saveRow.Controls.AddRange(new Control[] { MakeLabel("Note"), noteBox, saveButton, saveLabel });

            var histTitle = new Label
            {
                Dock = DockStyle.Top, Height = 28, Padding = new Padding(12, 8, 0, 0), ForeColor = Color.Gray,
                Text = "History (" + session.LogPath + ")"
            };
            history = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, TabStop = false,
                Font = new Font("Consolas", 9.5f), BackColor = Color.FromArgb(246, 248, 250), BorderStyle = BorderStyle.None
            };
            var histPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 12) };
            histPanel.Controls.Add(history);

            Controls.Add(histPanel);
            Controls.Add(histTitle);
            Controls.Add(saveRow);
            Controls.Add(weighR);
            Controls.Add(weighL);
            Controls.Add(status);
            Controls.Add(settings);
            Controls.Add(top);

            startButton.Click += delegate
            {
                waterLBox.Text = ""; waterRBox.Text = ""; saveLabel.Text = "";
                session.Start(leftBox.Checked, rightBox.Checked, (int)msBox.Value, (int)dropsBox.Value, (double)intervalBox.Value);
            };
            stopButton.Click += delegate { session.Stop(); };
            flushL.Click += delegate { session.Flush(0); };
            flushR.Click += delegate { session.Flush(1); };
            waterLBox.TextChanged += delegate { ShowResults(); };
            waterRBox.TextChanged += delegate { ShowResults(); };
            saveButton.Click += delegate
            {
                saveLabel.Text = session.Save(Parse(waterLBox.Text), Parse(waterRBox.Text), noteBox.Text);
                LoadHistory();
            };
            LoadHistory();
        }

        static double Parse(string s)
        {
            double v;
            s = (s ?? "").Trim().Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0 ? v : double.NaN;
        }

        void ShowResults()
        {
            ShowResult(0, waterLBox, resultL);
            ShowResult(1, waterRBox, resultR);
        }

        void ShowResult(int port, TextBox box, Label label)
        {
            double g = Parse(box.Text);
            int n = session.Given(port);
            if (double.IsNaN(g) || n == 0) { label.Text = n == 0 ? "" : string.Format("({0} drops)", n); return; }
            label.Text = string.Format(CultureInfo.InvariantCulture, "= {0:0.00} uL per drop  ({1} drops of {2} ms)",
                g * 1000.0 / n, n, session.RunMs);
        }

        void LoadHistory()
        {
            try
            {
                var path = session.LogPath;
                if (!File.Exists(path)) { history.Text = "(no saved calibrations yet)"; return; }
                var lines = File.ReadAllLines(path);
                var rows = lines.Skip(1).Reverse().Take(30).ToArray();
                var text = new List<string> { "Date        Time      Port  ms    Drops  Int(s)  Water(g)  uL/drop  Note" };
                foreach (var line in rows)
                {
                    var c = line.Split(new[] { ',' }, 9);
                    if (c.Length < 8) continue;
                    text.Add(string.Format("{0,-11} {1,-9} {2,-5} {3,-5} {4,-6} {5,-7} {6,-9} {7,-8} {8}",
                        c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c.Length > 8 ? c[8].Trim('"') : ""));
                }
                history.Text = string.Join("\r\n", text.ToArray());
            }
            catch (Exception ex) { history.Text = "Could not read history: " + ex.Message; }
        }

        public void Post(string state, string progress, bool running, bool boardReady)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    stateLabel.Text = state;
                    progressLabel.Text = progress;
                    startButton.Enabled = !running && boardReady;
                    stopButton.Enabled = running;
                    flushL.Enabled = flushR.Enabled = !running && boardReady;
                    foreach (Control c in new Control[] { leftBox, rightBox, msBox, dropsBox, intervalBox })
                        c.Enabled = !running;
                    ShowResults();
                }));
            }
            catch (InvalidOperationException) { }
        }

        public void CloseFromTask()
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke((Action)(() => { closingFromTask = true; Close(); }));
            }
            catch (InvalidOperationException) { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!closingFromTask) session.Stop();
            base.OnFormClosing(e);
        }
    }
}
