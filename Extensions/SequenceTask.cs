// Two-port sequence lick task for the Harp Behavior board (see the Drive notes
// "sequence-task-design-notes.md", 11/22). Built from TwoAfcTask.cs; same hardware.
//
// Rule: lick the ports in the order the drops were presented. First port right -> a drop there;
// then the second port is basically a freebie (a drop as soon as the mouse switches to it).
//
// Trial (there is only one epoch with the spout in reach, the sample):
//   PreTrial : spout at RetractedPosition, wait PreTrialSeconds.
//   Cue      : still out of reach. Drop 1 on the first port (Drop1Ms), GapSeconds later drop 2 on
//              the other port (Drop2Ms; 0 = no drop, which is the familiar one-drop trial).
//              The spout extends LastDropToSpoutSeconds after drop 2.
//              CuePosition (0 = stay at RetractedPosition) lets the spout wait closer during the cue.
//   Sample   : spout extends. A port is "chosen" after LicksToChoose licks in a row on it
//              (a lick on the other port restarts the count).
//     first choice = first port  -> Reward1Ms there. Then the mouse has Item2WindowSeconds to
//                                   choose the second port -> Reward2Ms there, ConsumeSeconds,
//                                   next trial ("Correct"). Window runs out -> "FirstOnly", next trial.
//     first choice = second port -> no water, retract, TimeoutSeconds ("Wrong").
//     no choice in SampleWindowSeconds -> "Miss", same as wrong.
//   The first port is picked like the 2AFC sample side (Random with max repeats / Blocks /
//   Anti-bias, the anti-bias using first-choice correct/wrong; see TwoAfcTask.AntiBiasSide).
//
// Per-mouse settings: mice\<ID>_seq.txt (separate from the 2AFC file mice\<ID>.txt; a mouse
// with no _seq file starts from its 2AFC spout and Y positions). Weights share mice\<ID>_weights.csv.
// "Save video" box drives the same VideoGate nodes as the 2AFC task (TwoAfcTask.SaveVideoNow).
//
// Hardware mapping: Left = Harp Port 0 (DIPort0 / SupplyPort0), Right = Harp Port 1,
//   spout servo = DO2 (ServoMotor2), Y servo = DO3 (ServoMotor3).
//
// Written for C# 5 (Bonsai compiles this with the .NET Framework compiler).

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
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
[Description("Two-port sequence lick task with a control window. Input: timestamped digital input state. Output: commands for the Behavior board.")]
public class SequenceTask
{
    public SequenceTask()
    {
        RetractedPosition = 1650;
        ExtendedPosition = 1900;
        CuePosition = 0;
        YPosition = 1070;
        PreTrialSeconds = 1.6;
        Drop1Ms = 20;
        Drop2Ms = 0;
        GapSeconds = 1.0;
        LastDropToSpoutSeconds = 0.6;
        SampleWindowSeconds = 15;
        LicksToChoose = 2;
        Item2WindowSeconds = 2.0;
        Reward1Ms = 80;
        Reward2Ms = 80;
        ConsumeSeconds = 2.0;
        TimeoutSeconds = 1.0;
        MaxRepeats = 3;
        SideSequence = TwoAfcTask.SideSequenceMode.AntiBias;
        SaveVideo = true;
        TrialLogFile = "seq_trials.csv";
    }

    [Category("Spout")]
    [Description("Servo pulse (us) for the retracted / far spout position.")]
    public int RetractedPosition { get; set; }

    [Category("Spout")]
    [Description("Servo pulse (us) for the extended / close spout position.")]
    public int ExtendedPosition { get; set; }

    [Category("Spout")]
    [Description("Servo pulse (us) where the spout waits while the cue drops come out. 0 = stay at RetractedPosition.")]
    public int CuePosition { get; set; }

    [Category("Spout")]
    [Description("Servo pulse (us) for the Y (medial-lateral) motor on DO3. Fixed during trials.")]
    public int YPosition { get; set; }

    [Category("Timing")]
    [Description("Seconds at the retracted position before the first cue drop. Keep it longer than the spout travel time.")]
    public double PreTrialSeconds { get; set; }

    [Category("Cue")]
    [Description("Valve open time (ms) for cue drop 1 (first port).")]
    public int Drop1Ms { get; set; }

    [Category("Cue")]
    [Description("Valve open time (ms) for cue drop 2 (second port). 0 = no second drop.")]
    public int Drop2Ms { get; set; }

    [Category("Cue")]
    [Description("Seconds from cue drop 1 to cue drop 2.")]
    public double GapSeconds { get; set; }

    [Category("Cue")]
    [Description("Seconds from cue drop 2 to the spout extending.")]
    public double LastDropToSpoutSeconds { get; set; }

    [Category("Sample")]
    [Description("Seconds the mouse has to choose its first port once the spout extends.")]
    public double SampleWindowSeconds { get; set; }

    [Category("Sample")]
    [Description("Licks in a row on one port that count as choosing it.")]
    public int LicksToChoose { get; set; }

    [Category("Sample")]
    [Description("Seconds after the first reward to choose the second port.")]
    public double Item2WindowSeconds { get; set; }

    [Category("Reward")]
    [Description("Valve open time (ms) for the reward on the first port.")]
    public int Reward1Ms { get; set; }

    [Category("Reward")]
    [Description("Valve open time (ms) for the reward on the second port.")]
    public int Reward2Ms { get; set; }

    [Category("Timing")]
    [Description("Seconds to drink after the second reward.")]
    public double ConsumeSeconds { get; set; }

    [Category("Timing")]
    [Description("Seconds of timeout after a wrong first choice or a miss.")]
    public double TimeoutSeconds { get; set; }

    [Category("Trials")]
    [Description("Most trials in a row with the same first port.")]
    public int MaxRepeats { get; set; }

    [Category("Trials")]
    [Description("How the first port is picked: Random (max repeats), Blocks of MaxRepeats, or Anti-bias (Autocage).")]
    public TwoAfcTask.SideSequenceMode SideSequence { get; set; }

    [Category("Logging")]
    [Description("Starting state of the 'Save video' box (per rig). VideoGate nodes follow the box.")]
    public bool SaveVideo { get; set; }

    [Category("Logging")]
    [Description("Trial log file. Mouse ID and a timestamp are added to the name.")]
    public string TrialLogFile { get; set; }

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

    // One finished trial, for the plot.
    internal struct TrialResult
    {
        public int Trial;
        public int First;     // first port: 0 = L, 1 = R
        public int Outcome;   // 0 = correct (both), 3 = first only, 1 = wrong, 2 = miss
    }

    internal const int OutcomeCorrect = 0, OutcomeWrong = 1, OutcomeMiss = 2, OutcomeFirstOnly = 3;

    internal class Session : IDisposable
    {
        const int Left = 0, Right = 1;
        static readonly string[] PortNames = { "L", "R" };

        readonly SequenceTask p;
        readonly IObserver<HarpMessage> output;
        readonly object gate = new object();
        readonly SerialDisposable timer = new SerialDisposable();
        readonly Random random = new Random();
        StreamWriter log;
        string logMouse;
        string mouseId = "";
        readonly List<TrialResult> results = new List<TrialResult>();
        readonly List<int> histSides = new List<int>(), histOutcomes = new List<int>();
        double sidePLeft = double.NaN;
        string sideReason = "";
        TaskWindow window;

        string state = "Waiting for board";
        bool boardReady, yEnabled;
        bool running;
        int token;
        bool disposed;
        int trial;
        DigitalInputs previous;
        int licksL, licksR;
        int nCorrect, nFirstOnly, nWrong, nMiss;
        int lastSide = -1, runLength;
        bool spoutExtended;

        // This trial
        int first, second;                      // ports in presentation order
        int drop1Given, drop2Given;             // ms actually given (0 = none)
        double gapUsed, leadUsed;
        DateTime trialStart, sampleStart, item1Time;
        int runPort = -1, runCount;             // current run of licks on one port
        int firstLick = -1; double firstLickSec = double.NaN;
        int item1 = -1, item2 = -1;
        double item1Sec = double.NaN, item2Sec = double.NaN, item1Harp = double.NaN, item2Harp = double.NaN;
        int cueLicksL, cueLicksR;               // licks before the spout extends (cue + lead)
        int sampleLicksL, sampleLicksR;         // licks after it extends
        string lastTrialText = "";

        const int PhaseOther = 0, PhaseCue = 1, PhaseItem1 = 2, PhaseItem2 = 3;
        int phase = PhaseOther;

        // Workflow values = defaults for new mice and for settings a mouse file lacks.
        readonly SequenceTask d;

        public Session(SequenceTask parameters, IObserver<HarpMessage> observer)
        {
            p = parameters;
            output = observer;
            d = (SequenceTask)p.MemberwiseClone();
            TwoAfcTask.SaveVideoNow = p.SaveVideo;
        }

        public SequenceTask P { get { return p; } }
        public bool IsRunning { get { lock (gate) return running; } }
        public bool SaveVideo { get { return TwoAfcTask.SaveVideoNow; } }

        public void SetSaveVideo(bool on)
        {
            lock (gate) { if (!disposed) TwoAfcTask.SaveVideoNow = on; }
        }

        void ApplyDefaults()
        {
            p.RetractedPosition = d.RetractedPosition; p.ExtendedPosition = d.ExtendedPosition;
            p.CuePosition = d.CuePosition; p.YPosition = d.YPosition;
            p.PreTrialSeconds = d.PreTrialSeconds; p.Drop1Ms = d.Drop1Ms; p.Drop2Ms = d.Drop2Ms;
            p.GapSeconds = d.GapSeconds; p.LastDropToSpoutSeconds = d.LastDropToSpoutSeconds;
            p.SampleWindowSeconds = d.SampleWindowSeconds; p.LicksToChoose = d.LicksToChoose;
            p.Item2WindowSeconds = d.Item2WindowSeconds; p.Reward1Ms = d.Reward1Ms; p.Reward2Ms = d.Reward2Ms;
            p.ConsumeSeconds = d.ConsumeSeconds; p.TimeoutSeconds = d.TimeoutSeconds;
            p.MaxRepeats = d.MaxRepeats; p.SideSequence = d.SideSequence;
        }

        // Applies a window change under the lock and saves it for the mouse.
        public void Change(Action<SequenceTask> edit)
        {
            lock (gate)
            {
                if (disposed) return;
                int r = p.RetractedPosition, e = p.ExtendedPosition, c = p.CuePosition, y = p.YPosition;
                edit(p);
                p.MaxRepeats = Math.Max(1, p.MaxRepeats);
                p.LicksToChoose = Math.Max(1, p.LicksToChoose);
                SaveSettings();
                if (y != p.YPosition) MoveYOnly();
                if (boardReady)
                {
                    // re-send the position the spout is parked at if it changed
                    if (spoutExtended && e != p.ExtendedPosition) Extend();
                    else if (!spoutExtended && phase == PhaseCue && c != p.CuePosition) Servo(CueServo);
                    else if (!spoutExtended && phase != PhaseCue && r != p.RetractedPosition) Retract();
                }
                UpdateWindow();
            }
        }

        // ---- Log ----

        void EnsureLog()
        {
            var id = mouseId.Length > 0 ? mouseId : "noID";
            if (log != null && logMouse == id) return;
            if (log != null) log.Dispose();
            var name = string.IsNullOrEmpty(p.TrialLogFile) ? "seq_trials.csv" : p.TrialLogFile;
            var path = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(name)),
                Path.GetFileNameWithoutExtension(name) + "_" + id + DateTime.Now.ToString("_yyyy-MM-dd_HH-mm-ss") + Path.GetExtension(name));
            log = new StreamWriter(path);
            logMouse = id;
            log.WriteLine("Trial,MouseID,Sequence,FirstPort,SecondPort,Outcome,FirstLick,FirstLickSec,Item1,Item1Sec,Item1HarpSeconds,Item2,Item2Sec,Item2HarpSeconds,TrialStart,SampleStart,Drop1Ms,Drop2Ms,GapSec,LastDropToSpoutSec,Reward1Ms,Reward2Ms,LicksToChoose,Item2WindowSec,SampleWindowSec,PreTrialSec,ConsumeSec,TimeoutSec,CueLicksL,CueLicksR,SampleLicksL,SampleLicksR,RetractedPos,ExtendedPos,CuePos,YPos,SideSequence,MaxRepeats,AntiBiasPLeft,SideRule,SaveVideo,ValveScaleL,ValveScaleR");
            log.Flush();
        }

        // ---- Per-mouse settings ----

        static string MiceFile(string id, string suffix)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
            return Path.Combine(Path.GetFullPath("mice"), id + suffix);
        }

        public bool SetMouse(string id)
        {
            lock (gate)
            {
                if (disposed || running) return false;
                id = (id ?? "").Trim();
                if (id == mouseId) return true;
                mouseId = id;
                trial = 0; nCorrect = nFirstOnly = nWrong = nMiss = 0; licksL = licksR = 0;
                lastSide = -1; runLength = 0; lastTrialText = "";
                results.Clear(); histSides.Clear(); histOutcomes.Clear();
                if (id.Length > 0)
                {
                    ApplyDefaults();
                    var path = MiceFile(id, "_seq.txt");
                    if (File.Exists(path)) LoadSettings(path, false);
                    else if (File.Exists(MiceFile(id, ".txt"))) LoadSettings(MiceFile(id, ".txt"), true);   // 2AFC file: positions only
                    SaveSettings();
                }
                LoadWeights();
                if (boardReady) { Retract(); MoveY(); }
                UpdateWindow();
                return true;
            }
        }

        void LoadSettings(string path, bool positionsOnly)
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var hash = line.IndexOf('#');
                var text = hash >= 0 ? line.Substring(0, hash) : line;
                var kv = text.Split(new[] { '=' }, 2);
                double v;
                if (kv.Length != 2 || !double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                int i = (int)Math.Round(v);
                var key = kv[0].Trim();
                bool pos = key == "RetractedPosition" || key == "ExtendedPosition" || key == "YPosition";
                if (positionsOnly && !pos) continue;
                switch (key)
                {
                    case "RetractedPosition": if (i >= 1000 && i <= 2000) p.RetractedPosition = i; break;
                    case "ExtendedPosition": if (i >= 1000 && i <= 2000) p.ExtendedPosition = i; break;
                    case "CuePosition": if (i == 0 || (i >= 1000 && i <= 2000)) p.CuePosition = i; break;
                    case "YPosition": if (i >= 1000 && i <= 2000) p.YPosition = i; break;
                    case "PreTrialSeconds": if (v >= 0) p.PreTrialSeconds = v; break;
                    case "Drop1Ms": if (i >= 0) p.Drop1Ms = i; break;
                    case "Drop2Ms": if (i >= 0) p.Drop2Ms = i; break;
                    case "GapSeconds": if (v >= 0) p.GapSeconds = v; break;
                    case "LastDropToSpoutSeconds": if (v >= 0) p.LastDropToSpoutSeconds = v; break;
                    case "SampleWindowSeconds": if (v > 0) p.SampleWindowSeconds = v; break;
                    case "LicksToChoose": if (i >= 1) p.LicksToChoose = i; break;
                    case "Item2WindowSeconds": if (v >= 0) p.Item2WindowSeconds = v; break;
                    case "Reward1Ms": if (i >= 0) p.Reward1Ms = i; break;
                    case "Reward2Ms": if (i >= 0) p.Reward2Ms = i; break;
                    case "ConsumeSeconds": if (v >= 0) p.ConsumeSeconds = v; break;
                    case "TimeoutSeconds": if (v >= 0) p.TimeoutSeconds = v; break;
                    case "MaxRepeats": if (i >= 1) p.MaxRepeats = i; break;
                    case "SideSequence": if (i >= 0 && i <= 2) p.SideSequence = (TwoAfcTask.SideSequenceMode)i; break;
                }
            }
        }

        void SaveSettings()
        {
            if (mouseId.Length == 0) return;
            var ci = CultureInfo.InvariantCulture;
            try
            {
                var path = MiceFile(mouseId, "_seq.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, new[]
                {
                    "RetractedPosition=" + p.RetractedPosition.ToString(ci),
                    "ExtendedPosition=" + p.ExtendedPosition.ToString(ci),
                    "CuePosition=" + p.CuePosition.ToString(ci) + "   # spout position while the cue drops come out; 0 = retracted",
                    "YPosition=" + p.YPosition.ToString(ci) + "   # medial-lateral motor (DO3)",
                    "PreTrialSeconds=" + p.PreTrialSeconds.ToString(ci) + "   # spout away before cue drop 1",
                    "Drop1Ms=" + p.Drop1Ms.ToString(ci) + "   # cue drop on the first port",
                    "Drop2Ms=" + p.Drop2Ms.ToString(ci) + "   # cue drop on the second port; 0 = none",
                    "GapSeconds=" + p.GapSeconds.ToString(ci) + "   # drop 1 -> drop 2",
                    "LastDropToSpoutSeconds=" + p.LastDropToSpoutSeconds.ToString(ci) + "   # drop 2 -> spout extends",
                    "SampleWindowSeconds=" + p.SampleWindowSeconds.ToString(ci) + "   # time to choose the first port",
                    "LicksToChoose=" + p.LicksToChoose.ToString(ci) + "   # licks in a row that choose a port",
                    "Item2WindowSeconds=" + p.Item2WindowSeconds.ToString(ci) + "   # time after reward 1 to choose the second port",
                    "Reward1Ms=" + p.Reward1Ms.ToString(ci),
                    "Reward2Ms=" + p.Reward2Ms.ToString(ci),
                    "ConsumeSeconds=" + p.ConsumeSeconds.ToString(ci) + "   # drink time after reward 2",
                    "TimeoutSeconds=" + p.TimeoutSeconds.ToString(ci) + "   # after wrong / miss",
                    "MaxRepeats=" + p.MaxRepeats.ToString(ci),
                    "SideSequence=" + ((int)p.SideSequence).ToString(ci) + "   # first port: 0 = random, 1 = blocks, 2 = anti-bias"
                });
            }
            catch (IOException) { }
        }

        // ---- Body weights (shared with the 2AFC task: mice\<id>_weights.csv) ----

        double weightBefore = double.NaN, weightAfter = double.NaN;
        string lastWeightText = "";
        const string WeightsHeader = "Date,BeforeG,AfterG,ChangeG,Updated";

        public double WeightBefore { get { lock (gate) return weightBefore; } }
        public double WeightAfter { get { lock (gate) return weightAfter; } }
        public string LastWeightText { get { lock (gate) return lastWeightText; } }

        void LoadWeights()
        {
            weightBefore = weightAfter = double.NaN;
            lastWeightText = "";
            if (mouseId.Length == 0) return;
            var path = MiceFile(mouseId, "_weights.csv");
            if (!File.Exists(path)) { lastWeightText = "no earlier weights"; return; }
            var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var line in File.ReadAllLines(path))
            {
                var f = line.Split(',');
                if (f.Length < 3 || f[0] == "Date") continue;
                if (f[0] == today) { weightBefore = ParseWeight(f[1]); weightAfter = ParseWeight(f[2]); }
                else if (string.CompareOrdinal(f[0], today) < 0)
                    lastWeightText = "last: " + f[0] + "  before " + (f[1].Length > 0 ? f[1] : "-") + " g, after " + (f[2].Length > 0 ? f[2] : "-") + " g";
            }
            if (lastWeightText.Length == 0) lastWeightText = "no earlier weights";
        }

        static double ParseWeight(string s)
        {
            double v;
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0 ? v : double.NaN;
        }

        public void SetWeights(double before, double after)
        {
            lock (gate)
            {
                if (disposed || mouseId.Length == 0) return;
                weightBefore = before; weightAfter = after;
                try
                {
                    var ci = CultureInfo.InvariantCulture;
                    var path = MiceFile(mouseId, "_weights.csv");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var lines = new List<string>(File.Exists(path) ? File.ReadAllLines(path) : new string[0]);
                    if (lines.Count == 0 || lines[0] != WeightsHeader) lines.Insert(0, WeightsHeader);
                    var today = DateTime.Now.ToString("yyyy-MM-dd", ci);
                    var row = string.Join(",", new[]
                    {
                        today,
                        double.IsNaN(before) ? "" : before.ToString("0.0#", ci),
                        double.IsNaN(after) ? "" : after.ToString("0.0#", ci),
                        double.IsNaN(before) || double.IsNaN(after) ? "" : (after - before).ToString("0.0#", ci),
                        DateTime.Now.ToString("HH:mm", ci)
                    });
                    int i = lines.FindIndex(l => l.StartsWith(today + ","));
                    if (i >= 0) lines[i] = row; else lines.Add(row);
                    File.WriteAllLines(path, lines);
                }
                catch (IOException) { }
            }
        }

        // ---- Board ----

        public void OpenWindow()
        {
            var w = TaskWindow.Open(this);
            lock (gate) { window = w; UpdateWindow(); }
        }

        void InitBoard()
        {
            boardReady = true;
            Send(EnableServos.FromPayload(MessageType.Write, ServoOutputs.ServoOutput2 | ServoOutputs.ServoOutput3));
            Send(ServoMotor2Period.FromPayload(MessageType.Write, 20000));
            Send(ServoMotor3Period.FromPayload(MessageType.Write, 20000));
            Send(OutputPulseEnable.FromPayload(MessageType.Write, DigitalOutputs.SupplyPort0 | DigitalOutputs.SupplyPort1));
            Retract();
            MoveY();
        }

        void MoveYOnly()
        {
            if (!boardReady && !yEnabled)
            {
                Send(EnableServos.FromPayload(MessageType.Write, ServoOutputs.ServoOutput3));
                Send(ServoMotor3Period.FromPayload(MessageType.Write, 20000));
                yEnabled = true;
            }
            MoveY();
        }

        public void GoY() { lock (gate) { if (!disposed) MoveYOnly(); } }

        public void StartTrials()
        {
            lock (gate)
            {
                if (disposed || running) return;
                running = true;
                EnsureLog();
                if (!boardReady) InitBoard();
                GoPreTrial();
            }
        }

        public void StopTrials()
        {
            lock (gate)
            {
                if (disposed || !running) return;
                running = false;
                token++;
                phase = PhaseOther;
                state = "Stopped";
                if (boardReady) Retract();
                UpdateWindow();
            }
        }

        // "Go" buttons, only while stopped. which: 0 = retracted, 1 = extended, 2 = cue position.
        public void MoveSpout(int which)
        {
            lock (gate)
            {
                if (disposed || running) return;
                if (!boardReady) InitBoard();
                if (which == 1) Extend();
                else if (which == 2) { spoutExtended = false; Servo(CueServo); }
                else Retract();
            }
        }

        public void ManualWater(int port)
        {
            lock (gate)
            {
                if (disposed) return;
                if (!boardReady) InitBoard();
                Valve(port, p.Reward1Ms);
            }
        }

        public void OnInput(Timestamped<DigitalInputs> input)
        {
            lock (gate)
            {
                if (disposed) return;
                var current = input.Value;
                if (!boardReady)
                {
                    previous = current;
                    InitBoard();
                    UpdateWindow();
                    return;
                }
                var onsets = current & ~previous;
                previous = current;
                bool lickL = (onsets & DigitalInputs.DIPort0) != 0;
                bool lickR = (onsets & DigitalInputs.DIPort1) != 0;
                if (!lickL && !lickR) return;
                if (lickL) licksL++;
                if (lickR) licksR++;
                if (running)
                {
                    if (phase == PhaseCue) { if (lickL) cueLicksL++; if (lickR) cueLicksR++; }
                    else if (phase == PhaseItem1 || phase == PhaseItem2)
                    {
                        if (lickL) sampleLicksL++;
                        if (lickR) sampleLicksR++;
                        // both at once (rare): count the left one first, like the 2AFC choice
                        if (lickL) Lick(Left, input.Seconds);
                        if (lickR && (phase == PhaseItem1 || phase == PhaseItem2)) Lick(Right, input.Seconds);
                    }
                }
                UpdateWindow();
            }
        }

        // One lick onset during the sample.
        void Lick(int port, double harpSeconds)
        {
            if (firstLick < 0) { firstLick = port; firstLickSec = (DateTime.Now - sampleStart).TotalSeconds; }
            if (port == runPort) runCount++;
            else { runPort = port; runCount = 1; }
            if (runCount < Math.Max(1, p.LicksToChoose)) return;

            if (phase == PhaseItem1)
            {
                item1 = port;
                item1Sec = (DateTime.Now - sampleStart).TotalSeconds;
                item1Harp = harpSeconds;
                if (port == first)
                {
                    Valve(first, p.Reward1Ms);
                    item1Time = DateTime.Now;
                    phase = PhaseItem2;
                    runPort = -1; runCount = 0;   // the second port needs its own run
                    state = "Sample - first port right, waiting for " + PortNames[second];
                    After(p.Item2WindowSeconds, Item2TimeUp);
                }
                else
                {
                    phase = PhaseOther;
                    nWrong++;
                    state = "Timeout (wrong first port)";
                    Retract();
                    EndTrial(OutcomeWrong, "Wrong");
                    After(p.TimeoutSeconds, GoPreTrial);
                }
            }
            else if (phase == PhaseItem2 && port == second)
            {
                item2 = port;
                item2Sec = (DateTime.Now - item1Time).TotalSeconds;
                item2Harp = harpSeconds;
                Valve(second, p.Reward2Ms);
                phase = PhaseOther;
                nCorrect++;
                state = "Consume";
                EndTrial(OutcomeCorrect, "Correct");
                After(p.ConsumeSeconds, GoPreTrial);
            }
        }

        // ---- State machine ----

        void GoPreTrial()
        {
            trial++;
            phase = PhaseOther;
            trialStart = DateTime.Now;
            first = ChooseFirst();
            second = 1 - first;
            drop1Given = drop2Given = 0;
            gapUsed = leadUsed = double.NaN;
            runPort = -1; runCount = 0;
            firstLick = item1 = item2 = -1;
            firstLickSec = item1Sec = item2Sec = item1Harp = item2Harp = double.NaN;
            cueLicksL = cueLicksR = sampleLicksL = sampleLicksR = 0;
            state = "PreTrial (next " + PortNames[first] + PortNames[second] + ")";
            Retract();
            UpdateWindow();
            After(p.PreTrialSeconds, GoDrop1);
        }

        int ChooseFirst()
        {
            int max = Math.Max(1, p.MaxRepeats);
            int side;
            sidePLeft = double.NaN; sideReason = "";
            if (p.SideSequence == TwoAfcTask.SideSequenceMode.AntiBias)
            {
                double pl; string why;
                side = TwoAfcTask.AntiBiasSide(histSides, histOutcomes, lastSide, runLength, max, random.NextDouble(), out pl, out why);
                sidePLeft = pl; sideReason = why;
            }
            else if (runLength >= max) side = 1 - lastSide;
            else if (p.SideSequence == TwoAfcTask.SideSequenceMode.Blocks && runLength > 0) side = lastSide;
            else side = random.Next(2);
            if (side == lastSide) runLength++;
            else { lastSide = side; runLength = 1; }
            return side;
        }

        int CueServo { get { return p.CuePosition > 0 ? p.CuePosition : p.RetractedPosition; } }

        void GoDrop1()
        {
            phase = PhaseCue;
            if (p.CuePosition > 0) { spoutExtended = false; Servo(p.CuePosition); }
            if (p.Drop1Ms > 0) { Valve(first, p.Drop1Ms); drop1Given = p.Drop1Ms; }
            gapUsed = p.GapSeconds;
            state = "Cue: drop 1 on " + PortNames[first];
            UpdateWindow();
            After(p.GapSeconds, GoDrop2);
        }

        void GoDrop2()
        {
            if (p.Drop2Ms > 0) { Valve(second, p.Drop2Ms); drop2Given = p.Drop2Ms; }
            leadUsed = p.LastDropToSpoutSeconds;
            state = p.Drop2Ms > 0 ? "Cue: drop 2 on " + PortNames[second] : "Cue: (no drop 2)";
            UpdateWindow();
            After(p.LastDropToSpoutSeconds, GoSample);
        }

        void GoSample()
        {
            phase = PhaseItem1;
            sampleStart = DateTime.Now;
            Extend();
            state = "Sample - waiting for the first port";
            UpdateWindow();
            After(p.SampleWindowSeconds, SampleMiss);
        }

        void SampleMiss()
        {
            phase = PhaseOther;
            nMiss++;
            state = "Timeout (miss)";
            Retract();
            EndTrial(OutcomeMiss, "Miss");
            After(p.TimeoutSeconds, GoPreTrial);
        }

        // First port was right but the second was not chosen in time.
        void Item2TimeUp()
        {
            phase = PhaseOther;
            nFirstOnly++;
            EndTrial(OutcomeFirstOnly, "FirstOnly");
            GoPreTrial();   // retracts
        }

        void EndTrial(int outcome, string name)
        {
            results.Add(new TrialResult { Trial = trial, First = first, Outcome = outcome });
            // anti-bias history: was the FIRST choice right?
            histSides.Add(first);
            histOutcomes.Add(outcome == OutcomeMiss ? 2 : outcome == OutcomeWrong ? 1 : 0);
            WriteTrial(name);
            var ci = CultureInfo.InvariantCulture;
            lastTrialText = string.Format(ci, "Last trial {0}{1}: {2}   |   first lick {3}   |   licks before spout L {4} R {5}, after L {6} R {7}",
                PortNames[first], PortNames[second], name, firstLick < 0 ? "-" : PortNames[firstLick],
                cueLicksL, cueLicksR, sampleLicksL, sampleLicksR);
            UpdateWindow();
        }

        static string Num(double v, string fmt)
        {
            return double.IsNaN(v) ? "" : v.ToString(fmt, CultureInfo.InvariantCulture);
        }

        void WriteTrial(string outcome)
        {
            if (log == null) return;
            var ci = CultureInfo.InvariantCulture;
            log.WriteLine(string.Join(",", new[]
            {
                trial.ToString(ci),
                logMouse,
                PortNames[first] + PortNames[second],
                PortNames[first],
                PortNames[second],
                outcome,
                firstLick < 0 ? "None" : PortNames[firstLick],
                Num(firstLickSec, "0.000"),
                item1 < 0 ? "None" : PortNames[item1],
                Num(item1Sec, "0.000"),
                Num(item1Harp, "0.000000"),
                item2 < 0 ? "None" : PortNames[item2],
                Num(item2Sec, "0.000"),
                Num(item2Harp, "0.000000"),
                trialStart.ToString("HH:mm:ss.fff", ci),
                sampleStart >= trialStart ? sampleStart.ToString("HH:mm:ss.fff", ci) : "",
                drop1Given.ToString(ci),
                drop2Given.ToString(ci),
                Num(gapUsed, "0.###"),
                Num(leadUsed, "0.###"),
                p.Reward1Ms.ToString(ci),
                p.Reward2Ms.ToString(ci),
                p.LicksToChoose.ToString(ci),
                p.Item2WindowSeconds.ToString("0.###", ci),
                p.SampleWindowSeconds.ToString("0.###", ci),
                p.PreTrialSeconds.ToString("0.###", ci),
                p.ConsumeSeconds.ToString("0.###", ci),
                p.TimeoutSeconds.ToString("0.###", ci),
                cueLicksL.ToString(ci),
                cueLicksR.ToString(ci),
                sampleLicksL.ToString(ci),
                sampleLicksR.ToString(ci),
                p.RetractedPosition.ToString(ci),
                p.ExtendedPosition.ToString(ci),
                CueServo.ToString(ci),
                p.YPosition.ToString(ci),
                p.SideSequence.ToString(),
                p.MaxRepeats.ToString(ci),
                Num(sidePLeft, "0.000"),
                sideReason,
                TwoAfcTask.SaveVideoNow ? "1" : "0",
                RigValves.Scale(0).ToString("0.###", ci),
                RigValves.Scale(1).ToString("0.###", ci)
            }));
            log.Flush();
        }

        void UpdateWindow()
        {
            if (window == null) return;
            int answered = nCorrect + nFirstOnly + nWrong;
            string pFirst = answered > 0 ? (100.0 * (nCorrect + nFirstOnly) / answered).ToString("0") + "%" : "-";
            string pBoth = answered > 0 ? (100.0 * nCorrect / answered).ToString("0") + "%" : "-";
            int recentN = 0, recentC = 0;
            for (int i = results.Count - 1; i >= 0 && recentN < 20; i--)
            {
                if (results[i].Outcome == OutcomeMiss) continue;
                recentN++;
                if (results[i].Outcome != OutcomeWrong) recentC++;
            }
            string recent = recentN > 0 ? (100.0 * recentC / recentN).ToString("0") + "%" : "-";
            string ab = p.SideSequence == TwoAfcTask.SideSequenceMode.AntiBias && !double.IsNaN(sidePLeft)
                ? string.Format(CultureInfo.InvariantCulture, "   |   Anti-bias P(L first) {0:0.00}, by {1}", sidePLeft, sideReason) : "";
            string status = string.Format(
                "Mouse {0}   |   Trial {1}   |   {2}{3}\nBoth {4}   First only {5}   Wrong {6}   Miss {7}   |   first port right {8} (last 20: {9}), both {10}   |   Licks L {11} R {12}\n{13}",
                mouseId.Length > 0 ? mouseId : "(none)", trial,
                running ? state : (trial > 0 ? "Stopped" : "Ready - press Start"), ab,
                nCorrect, nFirstOnly, nWrong, nMiss, pFirst, recent, pBoth, licksL, licksR, lastTrialText);
            window.Post(status, running, results.ToArray());
        }

        void After(double seconds, Action action)
        {
            var t = ++token;
            timer.Disposable = Scheduler.Default.Schedule(TimeSpan.FromSeconds(Math.Max(0, seconds)), () =>
            {
                lock (gate)
                {
                    if (!disposed && running && t == token) action();
                }
            });
        }

        void Retract() { spoutExtended = false; Servo(p.RetractedPosition); }
        void Extend() { spoutExtended = true; Servo(p.ExtendedPosition); }

        void Servo(int pulse) { Send(ServoMotor2Pulse.FromPayload(MessageType.Write, (ushort)pulse)); }
        void MoveY() { Send(ServoMotor3Pulse.FromPayload(MessageType.Write, (ushort)p.YPosition)); }

        void Valve(int port, int ms)
        {
            ms = RigValves.Ms(port, ms);   // per-rig L/R valve scale
            if (ms <= 0) return;
            if (port == Left)
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
                timer.Dispose();
                if (log != null) log.Dispose();
            }
            if (window != null) window.CloseFromTask();
        }
    }

    // ---- Control window (own UI thread) ----

    internal class TaskWindow : Form
    {
        readonly Session session;
        readonly Label status;
        readonly Button startButton, stopButton;
        readonly TextBox mouseBox, weightBeforeBox, weightAfterBox;
        readonly Label weightInfo;
        readonly TrialPlot plot;
        readonly List<Button> stoppedOnly = new List<Button>();
        readonly List<Action> refreshers = new List<Action>();   // refill boxes from settings
        bool closingFromTask;
        bool loading;
        string currentMouse = "";

        public static TaskWindow Open(Session session)
        {
            TaskWindow form = null;
            var ready = new ManualResetEvent(false);
            var thread = new Thread(() =>
            {
                Application.EnableVisualStyles();
                form = new TaskWindow(session);
                form.Load += delegate { ready.Set(); };
                Application.Run(form);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            ready.WaitOne(5000);
            return form;
        }

        // A number box bound to one setting: shows it, and writes it back (and saves) on change.
        NumericUpDown Box(decimal min, decimal max, decimal step, int decimals, int width,
            Func<SequenceTask, double> get, Action<SequenceTask, double> set)
        {
            var b = new NumericUpDown
            {
                Minimum = min, Maximum = max, Increment = step, DecimalPlaces = decimals, Width = width,
                Margin = new Padding(4, 4, 4, 0)
            };
            Action refresh = () => b.Value = Math.Max(min, Math.Min(max, (decimal)get(session.P)));
            refresh();
            refreshers.Add(refresh);
            b.ValueChanged += delegate
            {
                if (loading) return;
                double v = (double)b.Value;
                session.Change(t => set(t, v));
            };
            return b;
        }

        NumericUpDown Seconds(Func<SequenceTask, double> get, Action<SequenceTask, double> set)
        {
            return Box(0, 120, 0.1m, 2, 70, get, set);
        }

        NumericUpDown Ms(Func<SequenceTask, double> get, Action<SequenceTask, double> set)
        {
            return Box(0, 1000, 1, 0, 65, get, set);
        }

        static FlowLayoutPanel Row(int height)
        {
            return new FlowLayoutPanel { Dock = DockStyle.Top, Height = height, Padding = new Padding(8, 6, 8, 0) };
        }

        TaskWindow(Session session)
        {
            this.session = session;
            Text = "Sequence task";
            ClientSize = new Size(940, 720);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(80, 80);
            Font = new Font("Segoe UI", 10f);
            BackColor = Color.White;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8, 8, 8, 0) };
            startButton = MakeButton("Start", Color.FromArgb(46, 160, 67));
            stopButton = MakeButton("Stop", Color.FromArgb(207, 34, 46));
            var waterL = MakeButton("Water L", Color.FromArgb(9, 105, 218));
            var waterR = MakeButton("Water R", Color.FromArgb(9, 105, 218));
            startButton.Click += delegate { session.StartTrials(); };
            stopButton.Click += delegate { session.StopTrials(); };
            waterL.Click += delegate { session.ManualWater(0); };
            waterR.Click += delegate { session.ManualWater(1); };
            var saveVideoBox = new CheckBox { Text = "Save video", AutoSize = true, Checked = session.SaveVideo, Margin = new Padding(16, 9, 4, 0) };
            saveVideoBox.CheckedChanged += delegate { session.SetSaveVideo(saveVideoBox.Checked); };
            top.Controls.AddRange(new Control[] { startButton, stopButton, waterL, waterR, saveVideoBox });

            // Mouse ID
            var mouseRow = Row(40);
            mouseBox = new TextBox { Width = 110, Margin = new Padding(4, 4, 4, 0) };
            mouseBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitMouse(); } };
            mouseBox.Leave += delegate { CommitMouse(); };
            weightBeforeBox = new TextBox { Width = 60, Margin = new Padding(4, 4, 4, 0), Enabled = false };
            weightAfterBox = new TextBox { Width = 60, Margin = new Padding(4, 4, 4, 0), Enabled = false };
            weightInfo = new Label { Text = "(enter a mouse ID first)", AutoSize = true, Margin = new Padding(12, 7, 0, 0), ForeColor = Color.Gray };
            foreach (var tb in new[] { weightBeforeBox, weightAfterBox })
            {
                tb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitWeights(); } };
                tb.Leave += delegate { CommitWeights(); };
            }
            mouseRow.Controls.AddRange(new Control[]
            {
                MakeLabel("Mouse ID"), mouseBox,
                MakeLabel("     Weight before (g)"), weightBeforeBox, MakeLabel("after (g)"), weightAfterBox, weightInfo
            });

            // Spout positions
            var pos = Row(76);
            var goR = GoButton(0); var goE = GoButton(1); var goC = GoButton(2);
            var yBox = Box(1000, 2000, 5, 0, 80, t => t.YPosition, (t, v) => t.YPosition = (int)v);
            var goY = MakeSmallButton("Go");
            goY.Click += delegate { session.GoY(); };
            var yDown = new Button { Text = "-5", Width = 38, Height = 28, Margin = new Padding(2, 2, 0, 0) };
            var yUp = new Button { Text = "+5", Width = 38, Height = 28, Margin = new Padding(2, 2, 4, 0) };
            yDown.Click += delegate { yBox.Value = Math.Max(yBox.Minimum, yBox.Value - 5); };
            yUp.Click += delegate { yBox.Value = Math.Min(yBox.Maximum, yBox.Value + 5); };
            pos.Controls.AddRange(new Control[]
            {
                MakeLabel("Retracted"), Box(1000, 2000, 10, 0, 80, t => t.RetractedPosition, (t, v) => t.RetractedPosition = (int)v), goR,
                MakeLabel("   Extended"), Box(1000, 2000, 10, 0, 80, t => t.ExtendedPosition, (t, v) => t.ExtendedPosition = (int)v), goE,
                MakeLabel("   Cue pos (0 = retracted)"), Box(0, 2000, 10, 0, 80, t => t.CuePosition, (t, v) => t.CuePosition = v > 0 && v < 1000 ? 1000 : (int)v), goC,
                MakeLabel("Y (ML)"), yDown, yBox, yUp, goY
            });
            pos.SetFlowBreak(goC, true);

            // Cue: the ordered drops while out of reach
            var cueRow = Row(40);
            cueRow.Controls.AddRange(new Control[]
            {
                MakeLabel("Cue drop 1 (ms)"), Ms(t => t.Drop1Ms, (t, v) => t.Drop1Ms = (int)v),
                MakeLabel("   drop 2 (ms, 0 = none)"), Ms(t => t.Drop2Ms, (t, v) => t.Drop2Ms = (int)v),
                MakeLabel("   Gap 1->2 (s)"), Seconds(t => t.GapSeconds, (t, v) => t.GapSeconds = v),
                MakeLabel("   Drop 2 -> spout (s)"), Seconds(t => t.LastDropToSpoutSeconds, (t, v) => t.LastDropToSpoutSeconds = v)
            });

            // Sample: choosing the ports
            var sampleRow = Row(40);
            sampleRow.Controls.AddRange(new Control[]
            {
                MakeLabel("Licks in a row to choose"), Box(1, 20, 1, 0, 55, t => t.LicksToChoose, (t, v) => t.LicksToChoose = (int)v),
                MakeLabel("   Sample window (s)"), Seconds(t => t.SampleWindowSeconds, (t, v) => t.SampleWindowSeconds = v),
                MakeLabel("   2nd port window (s)"), Seconds(t => t.Item2WindowSeconds, (t, v) => t.Item2WindowSeconds = v)
            });

            var rewardRow = Row(40);
            rewardRow.Controls.AddRange(new Control[]
            {
                MakeLabel("Reward 1st port (ms)"), Ms(t => t.Reward1Ms, (t, v) => t.Reward1Ms = (int)v),
                MakeLabel("   2nd port (ms)"), Ms(t => t.Reward2Ms, (t, v) => t.Reward2Ms = (int)v),
                MakeLabel("   Drink time (s)"), Seconds(t => t.ConsumeSeconds, (t, v) => t.ConsumeSeconds = v)
            });

            var timeRow = Row(40);
            timeRow.Controls.AddRange(new Control[]
            {
                MakeLabel("Pre-trial (s)"), Seconds(t => t.PreTrialSeconds, (t, v) => t.PreTrialSeconds = v),
                MakeLabel("   Timeout after wrong / miss (s)"), Seconds(t => t.TimeoutSeconds, (t, v) => t.TimeoutSeconds = v)
            });

            // First-port rules
            var sideRow = Row(40);
            var sideBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, Margin = new Padding(4, 4, 4, 0) };
            sideBox.Items.AddRange(new object[] { "Random (max repeats)", "Blocks (LLL RRR ...)", "Anti-bias (Autocage)" });
            Action refreshSide = () => sideBox.SelectedIndex = (int)session.P.SideSequence;
            refreshSide();
            refreshers.Add(refreshSide);
            sideBox.SelectedIndexChanged += delegate
            {
                if (loading) return;
                int i = sideBox.SelectedIndex;
                session.Change(t => t.SideSequence = (TwoAfcTask.SideSequenceMode)i);
            };
            sideRow.Controls.AddRange(new Control[]
            {
                MakeLabel("First port"), sideBox,
                MakeLabel("   Max repeats"), Box(1, 50, 1, 0, 55, t => t.MaxRepeats, (t, v) => t.MaxRepeats = (int)v)
            });

            status = new Label { Dock = DockStyle.Top, Height = 70, Padding = new Padding(12, 4, 8, 0), Font = new Font("Segoe UI", 11f) };
            plot = new TrialPlot { Dock = DockStyle.Fill };

            Controls.Add(plot);
            Controls.Add(status);
            Controls.Add(sideRow);
            Controls.Add(timeRow);
            Controls.Add(rewardRow);
            Controls.Add(sampleRow);
            Controls.Add(cueRow);
            Controls.Add(pos);
            Controls.Add(mouseRow);
            Controls.Add(top);
            SetRunning(false);
        }

        Button GoButton(int which)
        {
            var b = MakeSmallButton("Go");
            b.Click += delegate { session.MoveSpout(which); };
            stoppedOnly.Add(b);
            return b;
        }

        void CommitMouse()
        {
            var id = mouseBox.Text.Trim();
            if (id == currentMouse) return;
            if (!session.SetMouse(id))
            {
                mouseBox.Text = currentMouse;
                MessageBox.Show(this, "Stop trials before changing the mouse.", "Sequence task");
                return;
            }
            currentMouse = id;
            loading = true;
            foreach (var r in refreshers) r();
            loading = false;
            ShowWeights();
        }

        void ShowWeights()
        {
            bool hasMouse = currentMouse.Length > 0;
            weightBeforeBox.Enabled = weightAfterBox.Enabled = hasMouse;
            var b = session.WeightBefore;
            var a = session.WeightAfter;
            weightBeforeBox.Text = hasMouse && !double.IsNaN(b) ? b.ToString("0.0#", CultureInfo.InvariantCulture) : "";
            weightAfterBox.Text = hasMouse && !double.IsNaN(a) ? a.ToString("0.0#", CultureInfo.InvariantCulture) : "";
            weightInfo.ForeColor = Color.Gray;
            weightInfo.Text = hasMouse ? session.LastWeightText : "(enter a mouse ID first)";
        }

        void CommitWeights()
        {
            if (currentMouse.Length == 0) return;
            double before, after;
            if (!TryWeight(weightBeforeBox.Text, out before) || !TryWeight(weightAfterBox.Text, out after))
            {
                weightInfo.ForeColor = Color.FromArgb(207, 34, 46);
                weightInfo.Text = "weight must be a number in grams, e.g. 24.3";
                return;
            }
            if (Same(before, session.WeightBefore) && Same(after, session.WeightAfter)) return;
            session.SetWeights(before, after);
            weightInfo.ForeColor = Color.FromArgb(46, 160, 67);
            string change = !double.IsNaN(before) && !double.IsNaN(after)
                ? string.Format(CultureInfo.InvariantCulture, "   change {0:+0.0#;-0.0#;0} g", after - before) : "";
            weightInfo.Text = "saved " + DateTime.Now.ToString("HH:mm") + change + "   |   " + session.LastWeightText;
        }

        static bool Same(double a, double b) { return double.IsNaN(a) ? double.IsNaN(b) : a == b; }

        static bool TryWeight(string text, out double value)
        {
            value = double.NaN;
            text = text.Trim().Replace(',', '.');
            if (text.Length == 0) return true;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0 && value < 100;
        }

        static Label MakeLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(4, 7, 0, 0) };
        }

        static Button MakeSmallButton(string text)
        {
            return new Button { Text = text, Width = 50, Height = 28, Margin = new Padding(2, 2, 4, 0) };
        }

        static Button MakeButton(string text, Color color)
        {
            var b = new Button { Text = text, Width = 110, Height = 34, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = color };
            b.FlatAppearance.BorderSize = 0;
            b.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            return b;
        }

        void SetRunning(bool running)
        {
            startButton.Enabled = !running;
            stopButton.Enabled = running;
            mouseBox.Enabled = !running;
            foreach (var b in stoppedOnly) b.Enabled = !running;
            startButton.BackColor = running ? Color.Silver : Color.FromArgb(46, 160, 67);
            stopButton.BackColor = running ? Color.FromArgb(207, 34, 46) : Color.Silver;
        }

        public void Post(string text, bool running, TrialResult[] results)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    status.Text = text;
                    SetRunning(running);
                    plot.SetResults(results);
                });
            }
            catch (InvalidOperationException) { }
        }

        public void CloseFromTask()
        {
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke((MethodInvoker)delegate { closingFromTask = true; Close(); }); }
            catch (InvalidOperationException) { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!closingFromTask) session.StopTrials();
            base.OnFormClosing(e);
        }
    }

    // Last 50 trials: row = first port (LR on the L row, RL on the R row).
    // Filled green = both right, open green = first only, red X = wrong first port, grey dot = miss.
    internal class TrialPlot : Control
    {
        const int Window = 50;
        TrialResult[] results = new TrialResult[0];

        public TrialPlot() { DoubleBuffered = true; ResizeRedraw = true; BackColor = Color.White; }

        public void SetResults(TrialResult[] r) { results = r; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            int left = 50, right = 20, top = 30, bottom = 40;
            var area = new Rectangle(left, top, Width - left - right, Height - top - bottom);
            if (area.Width < 50 || area.Height < 30) return;

            int lastTrial = results.Length > 0 ? results[results.Length - 1].Trial : 0;
            int xMax = Math.Max(Window, lastTrial);
            int xMin = xMax - Window + 1;
            Func<int, float> X = t => area.Left + (t - xMin + 0.5f) * area.Width / Window;
            float yR = area.Top + area.Height * 0.3f;
            float yL = area.Top + area.Height * 0.7f;

            using (var axis = new Pen(Color.FromArgb(200, 200, 200)))
            using (var text = new SolidBrush(Color.FromArgb(90, 90, 90)))
            using (var small = new Font("Segoe UI", 8.5f))
            using (var label = new Font("Segoe UI", 10f, FontStyle.Bold))
            {
                g.DrawRectangle(axis, area);
                g.DrawLine(axis, area.Left, yR, area.Right, yR);
                g.DrawLine(axis, area.Left, yL, area.Right, yL);
                g.DrawString("RL", label, text, 16, yR - 9);
                g.DrawString("LR", label, text, 16, yL - 9);
                for (int t = ((xMin + 9) / 10) * 10; t <= xMax; t += 10)
                {
                    if (t < 1) continue;
                    float x = X(t);
                    g.DrawLine(axis, x, area.Bottom, x, area.Bottom + 4);
                    var s = t.ToString(CultureInfo.InvariantCulture);
                    var sz = g.MeasureString(s, small);
                    g.DrawString(s, small, text, x - sz.Width / 2, area.Bottom + 6);
                }
                g.DrawString("Trial", small, text, area.Left + area.Width / 2 - 14, area.Bottom + 20);
                g.DrawString("● both right     O first port only     X wrong first port     • miss          row = presented order", small, text, area.Left, 6);
            }

            using (var green = new Pen(Color.FromArgb(46, 160, 67), 2.5f))
            using (var greenFill = new SolidBrush(Color.FromArgb(46, 160, 67)))
            using (var red = new Pen(Color.FromArgb(207, 34, 46), 2.5f))
            using (var grey = new SolidBrush(Color.FromArgb(150, 150, 150)))
            {
                float r = Math.Min(7f, area.Width / (float)Window * 0.4f);
                foreach (var res in results)
                {
                    if (res.Trial < xMin) continue;
                    float x = X(res.Trial);
                    float y = res.First == 1 ? yR : yL;
                    if (res.Outcome == OutcomeCorrect) g.FillEllipse(greenFill, x - r, y - r, 2 * r, 2 * r);
                    else if (res.Outcome == OutcomeFirstOnly) g.DrawEllipse(green, x - r, y - r, 2 * r, 2 * r);
                    else if (res.Outcome == OutcomeWrong)
                    {
                        g.DrawLine(red, x - r, y - r, x + r, y + r);
                        g.DrawLine(red, x - r, y + r, x + r, y - r);
                    }
                    else g.FillEllipse(grey, x - 3, y - 3, 6, 6);
                }
            }
        }
    }
}
