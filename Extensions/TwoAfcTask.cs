// Simple 2AFC (two-alternative forced choice) lick task for the Harp Behavior board.
//
// Trial sequence:
//   PreTrial  : spout at RetractedPosition, wait PreTrialSeconds
//   Sample    : spout extends to ExtendedPosition, small drop on a random side (L or R)
//   Delay     : spout retracts, wait DelaySeconds
//   Response  : spout extends; first lick (L or R) is the choice
//     correct -> big drop on that side, ConsumeSeconds, retract, next trial
//     wrong   -> no reward, retract at once, TimeoutSeconds (spout stays away), next trial
//     no lick within ResponseWindowSeconds -> "Miss", same as wrong
//   TimeoutSeconds is set in the window ("Timeout after wrong") and saved per mouse.
//   MinSampleLicks (default 3; 0 = off): the sample does not end until the mouse has licked the
//     sampled (water) spout this many times, so it has to actually drink the drop. The sample
//     lasts at least SampleSeconds; if the licks are not done by then, it ends right at the last
//     needed lick. SampleActualSec in the log is how long it really took.
//     If the licks are still not done ResponseWindowSeconds after the minimum sample time, the
//     trial is a miss (Outcome "SampleMiss" in the log, counted with misses), timeout, next trial.
//     (Replaces RequireSampleLick = 1 lick, and the short-lived SampleAfterLickSeconds rule.)
//   PreSampleSeconds (default 0 = off): the sample drop is pushed out this long BEFORE the spout
//     extends, while it is still out of reach (so the mouse may smell it first). It comes out of
//     the end of the pre-trial wait, so it is capped at PreTrialSeconds (PreSampleSec in the log is
//     the value actually used). After a correct trial the spout only starts retracting when the
//     pre-trial begins, so set PreTrialSeconds >= PreSampleSeconds + spout travel time, or the
//     drop comes out while the spout is still moving back.
//     Sample timing (SampleSeconds, FirstSampleLickSec, SampleActualSec) still starts at the extend.
//
// A control window opens when the workflow starts: Start / Stop buttons, manual water
// buttons, running counts, and a plot of the last 50 trials
// (row = sample side, O = correct, X = wrong, dot = miss).
// Trials only run after Start is pressed. Stop aborts the current trial and retracts the spout.
// The window is drawn with plain Windows Forms (no OpenGL), so it works without a GPU driver.
//
// Hardware mapping: Left = Harp Port 0 (DIPort0 / SupplyPort0),
//                   Right = Harp Port 1 (DIPort1 / SupplyPort1),
//                   spout servo = DO2 (ServoMotor2), Y / medial-lateral servo = DO3 (ServoMotor3).
//   The Y motor sits at YPosition (per mouse); it moves when the board starts, when a mouse is
//   loaded, and whenever the Y box is changed (even mid-trial). The trial sequence never moves it.
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
[Description("Simple 2AFC lick task with a control window. Input: timestamped digital input state. Output: commands for the Behavior board.")]
public class TwoAfcTask
{
    public TwoAfcTask()
    {
        // Defaults = A4439's good stretch on 2026-11-14 (trials 54-84, 82% correct).
        RetractedPosition = 1650;
        ExtendedPosition = 1900;
        YPosition = 1070;   // near fully retracted = most stable; set 2026-11-21
        PreTrialSeconds = 1.0;
        SampleSeconds = 2.0;
        DelaySeconds = 0.3;
        ResponseWindowSeconds = 15;
        ConsumeSeconds = 2.0;
        TimeoutSeconds = 1.0;
        SmallDropMs = 20;
        BigDropMs = 80;
        MaxRepeats = 5;
        SideSequence = SideSequenceMode.Blocks;
        CorrectToSwitch = 0;
        RequireSampleLick = true;   // ignored; kept so older workflow files still load
        MinSampleLicks = 3;
        PreSampleSeconds = 0;
        TrialLogFile = "trials.csv";
    }

    [Category("Spout")]
    [Description("Servo pulse (us) for the retracted / far spout position.")]
    public int RetractedPosition { get; set; }

    [Category("Spout")]
    [Description("Servo pulse (us) for the extended / close spout position.")]
    public int ExtendedPosition { get; set; }

    [Category("Spout")]
    [Description("Servo pulse (us) for the Y (medial-lateral) motor on DO3. Fixed during trials.")]
    public int YPosition { get; set; }

    [Category("Timing")]
    [Description("Seconds at the retracted position before the sample.")]
    public double PreTrialSeconds { get; set; }

    [Category("Timing")]
    [Description("Seconds the spout stays close during the sample (includes travel time).")]
    public double SampleSeconds { get; set; }

    [Category("Timing")]
    [Description("Seconds the spout stays retracted between sample and response (includes travel time).")]
    public double DelaySeconds { get; set; }

    [Category("Timing")]
    [Description("Seconds the mouse has to make a choice once the response phase starts.")]
    public double ResponseWindowSeconds { get; set; }

    [Category("Timing")]
    [Description("Seconds to consume the big drop after a correct choice.")]
    public double ConsumeSeconds { get; set; }

    [Category("Timing")]
    [Description("Seconds of timeout after a wrong choice or a miss.")]
    public double TimeoutSeconds { get; set; }

    [Category("Reward")]
    [Description("Valve open time (ms) for the sample drop. Left valve: below ~20 ms almost no water.")]
    public int SmallDropMs { get; set; }

    [Category("Reward")]
    [Description("Valve open time (ms) for the reward after a correct choice. Left valve: 50 ms = 3.6 uL.")]
    public int BigDropMs { get; set; }

    [Category("Trials")]
    [Description("Most trials in a row that can have the same sample side (2AFC).")]
    public int MaxRepeats { get; set; }

    [Category("Trials")]
    [Description("Random: coin flip, but never more than MaxRepeats in a row. Blocks: LLL RRR ... in blocks of MaxRepeats.")]
    public SideSequenceMode SideSequence { get; set; }

    public enum SideSequenceMode { Random = 0, Blocks = 1 }

    [Category("Trials")]
    [Description("Blocks mode only. 0 = blocks of MaxRepeats trials. N > 0 = stay on a side until the mouse has N correct trials there, then switch (wrong trials and misses repeat the side).")]
    public int CorrectToSwitch { get; set; }

    [Browsable(false)]
    [Description("Obsolete, ignored (use MinSampleLicks). Kept so workflow files that set it still load.")]
    public bool RequireSampleLick { get; set; }

    [Category("Trials")]
    [Description("2AFC: the sample does not end until the sampled (water) spout has been licked this many times (and lasts at least SampleSeconds). 0 = no requirement.")]
    public int MinSampleLicks { get; set; }

    [Category("Trials")]
    [Description("2AFC: the sample drop is pushed out this long (s) before the spout extends, while it is out of reach. 0 = drop at the extend. Capped at PreTrialSeconds; keep PreTrialSeconds >= this + spout travel time.")]
    public double PreSampleSeconds { get; set; }

    [Category("Logging")]
    [Description("Trial log file. A timestamp is added to the name so runs never overwrite each other.")]
    public string TrialLogFile { get; set; }

    public IObservable<HarpMessage> Process(IObservable<Timestamped<DigitalInputs>> source)
    {
        return Observable.Create<HarpMessage>(observer =>
        {
            var session = new Session(this, observer);
            var subscription = source.Subscribe(session.OnInput, observer.OnError);
            session.OpenWindow();   // after subscribing, so the board's first message is not missed
            return new CompositeDisposable(subscription, session);
        });
    }

    // One finished trial, for the plot.
    internal struct TrialResult
    {
        public int Trial;
        public int Side;      // 0 = L, 1 = R
        public int Outcome;   // 0 = correct, 1 = wrong, 2 = miss
    }

    internal class Session : IDisposable
    {
        const int Left = 0;
        const int Right = 1;
        static readonly string[] SideNames = { "L", "R" };

        readonly TwoAfcTask p;
        readonly IObserver<HarpMessage> output;
        readonly object gate = new object();
        readonly SerialDisposable timer = new SerialDisposable();
        readonly Random random = new Random();
        StreamWriter log;
        string logMouse;
        string mouseId = "";
        readonly List<TrialResult> results = new List<TrialResult>();
        TaskWindow window;

        string state = "Waiting for board";
        bool boardReady;
        bool running;
        int token;
        bool disposed;
        int trial;
        int sampleSide;
        DateTime responseStart;
        DigitalInputs previous;
        int nCorrect, nWrong, nMiss;
        bool sampleTimeUp;                  // sample: minimum time over
        int sampledLicks;                   // licks on the sampled (water) spout this sample
        bool sampleDropGiven;               // drop already pushed out in the pre-sample
        double preSampleActual;             // pre-sample used this trial (s), for the log
        double sampleActual = double.NaN;   // how long this trial's sample really lasted
        int licksL, licksR;
        bool spoutExtended;
        int lastSide = -1, runLength;   // current run of same-side trials
        int blockCorrect;               // correct trials on the current side (CorrectToSwitch)
        // Licks in this trial's sample and delay phases, per side; latency of the first sample lick.
        int sampleLicksL, sampleLicksR, delayLicksL, delayLicksR;
        DateTime sampleStart;
        double firstSampleLick = double.NaN;
        string lastTrialLicks = "";

        public const int Mode2Afc = 0;
        public const int ModeLickForWater = 1;
        public static readonly string[] ModeNames = { "2AFC", "LickForWater" };
        int mode = Mode2Afc;        // selected in the window
        int trialMode = Mode2Afc;   // mode of the trial in progress

        public int Mode { get { lock (gate) return mode; } }

        public void SetMode(int m)
        {
            lock (gate)
            {
                if (disposed || m < 0 || m >= ModeNames.Length) return;
                mode = m;
                SaveSettings();
            }
        }

        public int RetractedPosition { get { return p.RetractedPosition; } }
        public int ExtendedPosition { get { return p.ExtendedPosition; } }
        public int YPosition { get { return p.YPosition; } }
        public int SmallDropMs { get { return p.SmallDropMs; } }
        public int BigDropMs { get { return p.BigDropMs; } }
        public bool IsRunning { get { lock (gate) return running; } }

        // Workflow values at startup = defaults for new mice and for settings a mouse file lacks.
        readonly int dRetracted, dExtended, dY, dSmall, dBig;
        readonly double dSample, dDelay, dResponse, dTimeout, dPreTrial, dConsume, dPreSample;
        readonly int dMaxRepeats, dCorrectToSwitch;
        readonly SideSequenceMode dSideSequence;
        readonly int dMinSampleLicks;

        public Session(TwoAfcTask parameters, IObserver<HarpMessage> observer)
        {
            p = parameters;
            output = observer;
            dRetracted = p.RetractedPosition; dExtended = p.ExtendedPosition; dY = p.YPosition;
            dSmall = p.SmallDropMs; dBig = p.BigDropMs;
            dSample = p.SampleSeconds; dDelay = p.DelaySeconds; dResponse = p.ResponseWindowSeconds;
            dTimeout = p.TimeoutSeconds; dPreTrial = p.PreTrialSeconds; dConsume = p.ConsumeSeconds;
            dMaxRepeats = p.MaxRepeats; dSideSequence = p.SideSequence; dCorrectToSwitch = p.CorrectToSwitch;
            dMinSampleLicks = p.MinSampleLicks;
            dPreSample = p.PreSampleSeconds;
        }

        void ApplyDefaults()
        {
            p.RetractedPosition = dRetracted; p.ExtendedPosition = dExtended; p.YPosition = dY;
            p.SmallDropMs = dSmall; p.BigDropMs = dBig;
            p.SampleSeconds = dSample; p.DelaySeconds = dDelay; p.ResponseWindowSeconds = dResponse;
            p.TimeoutSeconds = dTimeout; p.PreTrialSeconds = dPreTrial; p.ConsumeSeconds = dConsume;
            p.MaxRepeats = dMaxRepeats; p.SideSequence = dSideSequence; p.CorrectToSwitch = dCorrectToSwitch;
            p.MinSampleLicks = dMinSampleLicks;
            p.PreSampleSeconds = dPreSample;
            mode = Mode2Afc;
        }

        // The trial log is opened on Start, one file per mouse per session.
        void EnsureLog()
        {
            var id = mouseId.Length > 0 ? mouseId : "noID";
            if (log != null && logMouse == id) return;
            if (log != null) log.Dispose();
            var name = string.IsNullOrEmpty(p.TrialLogFile) ? "trials.csv" : p.TrialLogFile;
            var path = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(name)),
                Path.GetFileNameWithoutExtension(name) + "_" + id + DateTime.Now.ToString("_yyyy-MM-dd_HH-mm-ss") + Path.GetExtension(name));
            log = new StreamWriter(path);
            logMouse = id;
            log.WriteLine("Trial,MouseID,Mode,SampleSide,Choice,Outcome,ResponseTimeSec,ChoiceHarpSeconds,TrialStart,RetractedPos,ExtendedPos,SmallDropMs,BigDropMs,SampleSec,DelaySec,ResponseSec,SideSequence,MaxRepeats,CorrectToSwitch,SampleLicksL,SampleLicksR,FirstSampleLickSec,DelayLicksL,DelayLicksR,TimeoutSec,PreTrialSec,ConsumeSec,RequireSampleLick,SampleActualSec,YPos,MinSampleLicks,PreSampleSec");
            log.Flush();
        }

        // ---- Per-mouse settings (mice\<id>.txt, key=value lines) ----

        static string SettingsPath(string id)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
            return Path.Combine(Path.GetFullPath("mice"), id + ".txt");
        }

        // Selects a mouse. Loads its saved settings if it has any, otherwise saves the current ones for it.
        // Returns false (and changes nothing) while trials are running.
        public bool SetMouse(string id)
        {
            lock (gate)
            {
                if (disposed || running) return false;
                id = (id ?? "").Trim();
                if (id == mouseId) return true;
                mouseId = id;
                // new mouse: fresh counts and plot
                trial = 0; nCorrect = nWrong = nMiss = 0; licksL = licksR = 0;
                lastSide = -1; runLength = 0; blockCorrect = 0; lastTrialLicks = "";
                results.Clear();
                if (id.Length > 0)
                {
                    // Start from defaults, then overlay whatever this mouse has saved.
                    // A new mouse gets the defaults, saved as its own file.
                    ApplyDefaults();
                    var path = SettingsPath(id);
                    if (File.Exists(path)) LoadSettings(path);
                    SaveSettings();
                }
                LoadWeights();
                if (boardReady) { Retract(); MoveY(); }
                UpdateWindow();
                return true;
            }
        }

        void LoadSettings(string path)
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var hash = line.IndexOf('#');                 // '#' starts a comment
                var text = hash >= 0 ? line.Substring(0, hash) : line;
                var kv = text.Split(new[] { '=' }, 2);
                double d;
                if (kv.Length != 2 || !double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) continue;
                int v = (int)Math.Round(d);
                switch (kv[0].Trim())
                {
                    case "RetractedPosition": p.RetractedPosition = v; break;
                    case "ExtendedPosition": p.ExtendedPosition = v; break;
                    case "YPosition": if (v >= 1000 && v <= 2000) p.YPosition = v; break;
                    case "SmallDropMs": p.SmallDropMs = v; break;
                    case "BigDropMs": p.BigDropMs = v; break;
                    case "Mode": if (v >= 0 && v < ModeNames.Length) mode = v; break;
                    case "SampleSeconds": p.SampleSeconds = d; break;
                    case "DelaySeconds": p.DelaySeconds = d; break;
                    case "ResponseWindowSeconds": p.ResponseWindowSeconds = d; break;
                    case "TimeoutSeconds": if (d >= 0) p.TimeoutSeconds = d; break;
                    case "PreTrialSeconds": if (d >= 0) p.PreTrialSeconds = d; break;
                    case "ConsumeSeconds": if (d >= 0) p.ConsumeSeconds = d; break;
                    case "MaxRepeats": if (v >= 1) p.MaxRepeats = v; break;
                    case "SideSequence": if (v == 0 || v == 1) p.SideSequence = (SideSequenceMode)v; break;
                    case "CorrectToSwitch": if (v >= 0) p.CorrectToSwitch = v; break;
                    case "RequireSampleLick": if (v == 0) p.MinSampleLicks = 0; break;   // old files; MinSampleLicks wins if both
                    case "MinSampleLicks": if (v >= 0) p.MinSampleLicks = v; break;
                    case "PreSampleSeconds": if (d >= 0) p.PreSampleSeconds = d; break;
                }
            }
        }

        void SaveSettings()
        {
            if (mouseId.Length == 0) return;
            try
            {
                var path = SettingsPath(mouseId);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, new[]
                {
                    "RetractedPosition=" + p.RetractedPosition.ToString(CultureInfo.InvariantCulture),
                    "ExtendedPosition=" + p.ExtendedPosition.ToString(CultureInfo.InvariantCulture),
                    "YPosition=" + p.YPosition.ToString(CultureInfo.InvariantCulture) + "   # medial-lateral motor (DO3)",
                    "SmallDropMs=" + p.SmallDropMs.ToString(CultureInfo.InvariantCulture),
                    "BigDropMs=" + p.BigDropMs.ToString(CultureInfo.InvariantCulture),
                    "Mode=" + mode.ToString(CultureInfo.InvariantCulture),
                    "SampleSeconds=" + p.SampleSeconds.ToString(CultureInfo.InvariantCulture),
                    "DelaySeconds=" + p.DelaySeconds.ToString(CultureInfo.InvariantCulture),
                    "ResponseWindowSeconds=" + p.ResponseWindowSeconds.ToString(CultureInfo.InvariantCulture),
                    "TimeoutSeconds=" + p.TimeoutSeconds.ToString(CultureInfo.InvariantCulture) + "   # after wrong / miss",
                    "PreTrialSeconds=" + p.PreTrialSeconds.ToString(CultureInfo.InvariantCulture) + "   # spout away before the sample",
                    "ConsumeSeconds=" + p.ConsumeSeconds.ToString(CultureInfo.InvariantCulture) + "   # drink time after correct",
                    "MaxRepeats=" + p.MaxRepeats.ToString(CultureInfo.InvariantCulture),
                    "SideSequence=" + ((int)p.SideSequence).ToString(CultureInfo.InvariantCulture) + "   # 0 = random, 1 = blocks",
                    "CorrectToSwitch=" + p.CorrectToSwitch.ToString(CultureInfo.InvariantCulture) + "   # blocks only; 0 = off",
                    "MinSampleLicks=" + p.MinSampleLicks.ToString(CultureInfo.InvariantCulture) + "   # sample waits for this many licks on the water spout; 0 = off",
                    "PreSampleSeconds=" + p.PreSampleSeconds.ToString(CultureInfo.InvariantCulture) + "   # sample drop this long before the spout extends; 0 = off"
                });
            }
            catch (IOException) { }
        }

        // ---- Body weights (mice\<id>_weights.csv, one row per day: Date,BeforeG,AfterG,ChangeG,Updated) ----

        double weightBefore = double.NaN, weightAfter = double.NaN;
        string lastWeightText = "";

        public double WeightBefore { get { lock (gate) return weightBefore; } }
        public double WeightAfter { get { lock (gate) return weightAfter; } }
        public string LastWeightText { get { lock (gate) return lastWeightText; } }

        static string WeightsPath(string id)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
            return Path.Combine(Path.GetFullPath("mice"), id + "_weights.csv");
        }

        const string WeightsHeader = "Date,BeforeG,AfterG,ChangeG,Updated";

        // Today's weights (to refill the boxes after a restart) and the most recent earlier day.
        void LoadWeights()
        {
            weightBefore = weightAfter = double.NaN;
            lastWeightText = "";
            if (mouseId.Length == 0) return;
            var path = WeightsPath(mouseId);
            if (!File.Exists(path)) { lastWeightText = "no earlier weights"; return; }
            var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var line in File.ReadAllLines(path))
            {
                var f = line.Split(',');
                if (f.Length < 3 || f[0] == "Date") continue;
                if (f[0] == today)
                {
                    weightBefore = ParseWeight(f[1]);
                    weightAfter = ParseWeight(f[2]);
                }
                else if (string.CompareOrdinal(f[0], today) < 0)
                {
                    // file is in date order, so the last earlier row wins
                    lastWeightText = "last: " + f[0] + "  before " + (f[1].Length > 0 ? f[1] : "-") + " g, after " + (f[2].Length > 0 ? f[2] : "-") + " g";
                }
            }
            if (lastWeightText.Length == 0) lastWeightText = "no earlier weights";
        }

        static double ParseWeight(string s)
        {
            double d;
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d > 0 ? d : double.NaN;
        }

        // From the window. NaN = box left empty. Writes/updates today's row right away.
        public void SetWeights(double before, double after)
        {
            lock (gate)
            {
                if (disposed || mouseId.Length == 0) return;
                weightBefore = before;
                weightAfter = after;
                try
                {
                    var ci = CultureInfo.InvariantCulture;
                    var path = WeightsPath(mouseId);
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

        public double SampleSeconds { get { return p.SampleSeconds; } }
        public double DelaySeconds { get { return p.DelaySeconds; } }
        public double ResponseSeconds { get { return p.ResponseWindowSeconds; } }
        public double TimeoutSeconds { get { return p.TimeoutSeconds; } }
        public double PreTrialSeconds { get { return p.PreTrialSeconds; } }
        public double ConsumeSeconds { get { return p.ConsumeSeconds; } }

        // Epoch durations; each is read when its phase starts, so changes apply from the next phase.
        public void SetEpochs(double preTrial, double sample, double delay, double response, double consume, double timeout)
        {
            lock (gate)
            {
                if (disposed) return;
                p.PreTrialSeconds = preTrial;
                p.SampleSeconds = sample;
                p.DelaySeconds = delay;
                p.ResponseWindowSeconds = response;
                p.ConsumeSeconds = consume;
                p.TimeoutSeconds = timeout;
                SaveSettings();
            }
        }

        public int MaxRepeats { get { return p.MaxRepeats; } }
        public int SideSequenceIndex { get { return (int)p.SideSequence; } }
        public int CorrectToSwitch { get { return p.CorrectToSwitch; } }

        // Side rules; apply from the next trial.
        public void SetSideRules(int sequence, int maxRepeats, int correctToSwitch)
        {
            lock (gate)
            {
                if (disposed) return;
                p.SideSequence = sequence == 1 ? SideSequenceMode.Blocks : SideSequenceMode.Random;
                p.MaxRepeats = Math.Max(1, maxRepeats);
                p.CorrectToSwitch = Math.Max(0, correctToSwitch);
                SaveSettings();
                UpdateWindow();
            }
        }

        public int MinSampleLicks { get { return p.MinSampleLicks; } }

        // Applies from the next sample.
        public void SetMinSampleLicks(int n)
        {
            lock (gate)
            {
                if (disposed) return;
                p.MinSampleLicks = Math.Max(0, n);
                SaveSettings();
            }
        }

        public double PreSampleSeconds { get { return p.PreSampleSeconds; } }

        // Applies from the next trial.
        public void SetPreSample(double seconds)
        {
            lock (gate)
            {
                if (disposed) return;
                p.PreSampleSeconds = Math.Max(0, seconds);
                SaveSettings();
            }
        }

        public void SetDrops(int small, int big)
        {
            lock (gate)
            {
                if (disposed) return;
                p.SmallDropMs = small;
                p.BigDropMs = big;
                SaveSettings();
            }
        }

        public void OpenWindow()
        {
            var w = TaskWindow.Open(this);
            lock (gate)
            {
                window = w;
                UpdateWindow();
            }
        }

        // Sends the one-time board configuration and moves the spout home.
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

        // New Y position from the window: saved, and the motor moves there now (also during trials).
        public void SetY(int y)
        {
            lock (gate)
            {
                if (disposed) return;
                p.YPosition = y;
                SaveSettings();
                MoveYOnly();
            }
        }

        // Y "Go" button: sends the current Y position.
        public void GoY()
        {
            lock (gate)
            {
                if (disposed) return;
                MoveYOnly();
            }
        }

        // Moves only the Y motor. If the board has not been set up yet, enables just the Y servo
        // output (EnableServos only switches the given outputs on) - the spout is not touched.
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

        bool yEnabled;   // Y servo output switched on before the full board setup

        // ---- Called from the control window buttons ----

        public void StartTrials()
        {
            lock (gate)
            {
                if (disposed || running) return;
                running = true;
                EnsureLog();
                // By the time anyone presses Start the board is open, even if we missed its first message.
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
                token++;               // cancels any pending timer
                state = "Stopped";
                if (boardReady) Retract();
                UpdateWindow();
            }
        }

        // New spout positions from the window. Applies to the next movement; if the spout is
        // currently parked at the changed position, it moves there now.
        public void SetPositions(int retracted, int extended)
        {
            lock (gate)
            {
                if (disposed) return;
                bool rChanged = retracted != p.RetractedPosition;
                bool eChanged = extended != p.ExtendedPosition;
                p.RetractedPosition = retracted;
                p.ExtendedPosition = extended;
                SaveSettings();
                if (!boardReady) return;
                if (spoutExtended && eChanged) Extend();
                else if (!spoutExtended && rChanged) Retract();
            }
        }

        // "Go" buttons: only while trials are stopped.
        public void MoveSpout(bool extended)
        {
            lock (gate)
            {
                if (disposed || running) return;
                if (!boardReady) InitBoard();
                if (extended) Extend(); else Retract();
            }
        }

        public void ManualWater(int side)
        {
            lock (gate)
            {
                if (disposed) return;
                if (!boardReady) InitBoard();
                Valve(side, p.BigDropMs);
            }
        }

        // ---- Board input ----

        // Called for every change of the digital inputs (licks), with Harp hardware time.
        // The first message arrives when the board starts (DumpRegisters), so we know it is listening.
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
                if (lickL) licksL++;
                if (lickR) licksR++;
                if (running && phase == PhaseSample)
                {
                    if (lickL) sampleLicksL++;
                    if (lickR) sampleLicksR++;
                    if ((lickL || lickR) && double.IsNaN(firstSampleLick))
                        firstSampleLick = (DateTime.Now - sampleStart).TotalSeconds;
                    if (sampleSide == Left ? lickL : lickR)
                    {
                        sampledLicks++;
                        if (sampleTimeUp)
                        {
                            // Minimum time is over and we were waiting for licks: end at the last needed one.
                            if (sampledLicks >= p.MinSampleLicks) { GoDelay(); return; }
                            state = WaitingText();
                        }
                    }
                }
                else if (running && phase == PhaseDelay)
                {
                    if (lickL) delayLicksL++;
                    if (lickR) delayLicksR++;
                }
                if (lickL || lickR) UpdateWindow();
                if (!running || state != "Response") return;

                int choice = -1;
                if ((onsets & DigitalInputs.DIPort0) != 0) choice = Left;
                else if ((onsets & DigitalInputs.DIPort1) != 0) choice = Right;
                if (choice >= 0) EndResponse(choice, input.Seconds);
            }
        }

        // ---- State machine ----

        // Phase of the trial, for counting licks (the state string is only for display).
        const int PhaseOther = 0, PhaseSample = 1, PhaseDelay = 2;
        int phase = PhaseOther;

        void GoPreTrial()
        {
            trial++;
            phase = PhaseOther;
            sampleLicksL = sampleLicksR = delayLicksL = delayLicksR = 0;
            firstSampleLick = double.NaN;
            sampleActual = double.NaN;
            trialMode = mode;   // a mode change applies from the next trial
            sampleSide = trialMode == ModeLickForWater ? -1 : ChooseSide();
            sampleDropGiven = false;
            // The pre-sample is the last part of the pre-trial; it can never be longer than the pre-trial.
            preSampleActual = trialMode == Mode2Afc ? Math.Max(0, Math.Min(p.PreSampleSeconds, p.PreTrialSeconds)) : 0;
            state = "PreTrial";
            Retract();
            UpdateWindow();
            // Lick for water: no sample, no delay - straight to the response phase.
            if (trialMode == ModeLickForWater) After(p.PreTrialSeconds, GoResponse);
            else if (preSampleActual > 0) After(p.PreTrialSeconds - preSampleActual, GoPreSample);
            else After(p.PreTrialSeconds, GoSample);
        }

        // Pre-sample: the drop comes out while the spout is still retracted; the spout extends later.
        void GoPreSample()
        {
            state = "Pre-sample (" + SideNames[sampleSide] + ") - drop out, spout away";
            Valve(sampleSide, p.SmallDropMs);
            sampleDropGiven = true;
            UpdateWindow();
            After(preSampleActual, GoSample);
        }

        // Picks the next sample side, never more than MaxRepeats of the same side in a row.
        // Blocks with CorrectToSwitch > 0: stay on the side until it has that many correct trials.
        int ChooseSide()
        {
            int max = Math.Max(1, p.MaxRepeats);
            int side;
            if (p.SideSequence == SideSequenceMode.Blocks && p.CorrectToSwitch > 0)
                side = lastSide < 0 ? random.Next(2) : blockCorrect >= p.CorrectToSwitch ? 1 - lastSide : lastSide;
            else if (runLength >= max) side = 1 - lastSide;                  // run is full: switch
            else if (p.SideSequence == SideSequenceMode.Blocks && runLength > 0) side = lastSide;  // fill the block
            else side = random.Next(2);
            if (side == lastSide) runLength++;
            else { lastSide = side; runLength = 1; blockCorrect = 0; }
            return side;
        }

        void GoSample()
        {
            state = "Sample (" + SideNames[sampleSide] + ")";
            phase = PhaseSample;
            sampleStart = DateTime.Now;
            sampleTimeUp = false;
            sampledLicks = 0;
            Extend();
            if (!sampleDropGiven) Valve(sampleSide, p.SmallDropMs);
            sampleDropGiven = true;
            UpdateWindow();
            After(p.SampleSeconds, SampleTimeUp);
        }

        // Minimum sample time is over: go on, or keep the spout there until the water spout has
        // been licked MinSampleLicks times.
        void SampleTimeUp()
        {
            sampleTimeUp = true;
            if (sampledLicks >= p.MinSampleLicks) GoDelay();
            else
            {
                // Wait at most the response window for the licks, then it is a miss.
                state = WaitingText();
                UpdateWindow();
                After(p.ResponseWindowSeconds, SampleMiss);
            }
        }

        string WaitingText()
        {
            return string.Format("Sample ({0}) - waiting for licks on the water spout ({1}/{2})",
                SideNames[sampleSide], sampledLicks, p.MinSampleLicks);
        }

        // The sampled spout was never licked, even after waiting: counted as a miss
        // (logged as "SampleMiss"), timeout, next trial.
        void SampleMiss()
        {
            sampleActual = (DateTime.Now - sampleStart).TotalSeconds;
            phase = PhaseOther;
            nMiss++;
            state = "Timeout (sample not licked)";
            Retract();
            results.Add(new TrialResult { Trial = trial, Side = sampleSide, Outcome = 2 });
            WriteTrial(-1, "SampleMiss", double.NaN, double.NaN);
            lastTrialLicks = string.Format(CultureInfo.InvariantCulture,
                "Last trial: water spout licked {3}/{4} times in {0:0.0} s - sample miss (sample licks L {1} R {2})",
                sampleActual, sampleLicksL, sampleLicksR, sampledLicks, p.MinSampleLicks);
            UpdateWindow();
            After(p.TimeoutSeconds, GoPreTrial);
        }

        void GoDelay()
        {
            sampleActual = (DateTime.Now - sampleStart).TotalSeconds;
            state = "Delay";
            phase = PhaseDelay;
            Retract();
            UpdateWindow();
            After(p.DelaySeconds, GoResponse);
        }

        void GoResponse()
        {
            state = "Response";
            phase = PhaseOther;
            responseStart = DateTime.Now;
            Extend();
            UpdateWindow();
            After(p.ResponseWindowSeconds, () => EndResponse(-1, double.NaN));
        }

        void EndResponse(int choice, double harpSeconds)
        {
            var rt = (DateTime.Now - responseStart).TotalSeconds;
            int outcome;
            bool rewarded = trialMode == ModeLickForWater ? choice >= 0 : choice == sampleSide;
            if (rewarded)
            {
                outcome = 0;
                nCorrect++;
                if (trialMode == Mode2Afc) blockCorrect++;
                state = "Consume";
                Valve(choice, p.BigDropMs);
                After(p.ConsumeSeconds, GoPreTrial);   // GoPreTrial retracts the spout
            }
            else
            {
                outcome = choice < 0 ? 2 : 1;
                if (choice < 0) nMiss++; else nWrong++;
                state = "Timeout";
                Retract();
                After(p.TimeoutSeconds, GoPreTrial);
            }
            // Plot row: sample side in 2AFC; licked side in lick-for-water (-1 = middle, for a miss).
            int plotSide = trialMode == ModeLickForWater ? choice : sampleSide;
            results.Add(new TrialResult { Trial = trial, Side = plotSide, Outcome = outcome });
            string outcomeName = outcome == 0 ? (trialMode == ModeLickForWater ? "Rewarded" : "Correct") : outcome == 1 ? "Wrong" : "Miss";
            WriteTrial(choice, outcomeName, rt, harpSeconds);
            if (trialMode == Mode2Afc)
                lastTrialLicks = string.Format(CultureInfo.InvariantCulture, "Last trial licks: sample L {0} R {1}, delay L {2} R {3}   |   sample took {4:0.0} s",
                    sampleLicksL, sampleLicksR, delayLicksL, delayLicksR, sampleActual);
            UpdateWindow();
        }

        void WriteTrial(int choice, string outcome, double rt, double harpSeconds)
        {
            var ci = CultureInfo.InvariantCulture;
            if (log == null) return;
            log.WriteLine(string.Join(",", new[]
            {
                trial.ToString(ci),
                logMouse,
                ModeNames[trialMode],
                sampleSide < 0 ? "" : SideNames[sampleSide],
                choice < 0 ? "None" : SideNames[choice],
                outcome,
                choice < 0 ? "" : rt.ToString("0.000", ci),
                double.IsNaN(harpSeconds) ? "" : harpSeconds.ToString("0.000000", ci),
                DateTime.Now.ToString("HH:mm:ss.fff", ci),
                p.RetractedPosition.ToString(ci),
                p.ExtendedPosition.ToString(ci),
                p.SmallDropMs.ToString(ci),
                p.BigDropMs.ToString(ci),
                p.SampleSeconds.ToString("0.###", ci),
                p.DelaySeconds.ToString("0.###", ci),
                p.ResponseWindowSeconds.ToString("0.###", ci),
                p.SideSequence.ToString(),
                p.MaxRepeats.ToString(ci),
                p.CorrectToSwitch.ToString(ci),
                // lick counts only mean something for 2AFC trials (lick for water has no sample/delay)
                trialMode == Mode2Afc ? sampleLicksL.ToString(ci) : "",
                trialMode == Mode2Afc ? sampleLicksR.ToString(ci) : "",
                double.IsNaN(firstSampleLick) ? "" : firstSampleLick.ToString("0.000", ci),
                trialMode == Mode2Afc ? delayLicksL.ToString(ci) : "",
                trialMode == Mode2Afc ? delayLicksR.ToString(ci) : "",
                p.TimeoutSeconds.ToString("0.###", ci),
                p.PreTrialSeconds.ToString("0.###", ci),
                p.ConsumeSeconds.ToString("0.###", ci),
                p.MinSampleLicks > 0 ? "1" : "0",
                double.IsNaN(sampleActual) ? "" : sampleActual.ToString("0.000", ci),
                p.YPosition.ToString(ci),
                p.MinSampleLicks.ToString(ci),
                preSampleActual.ToString("0.###", ci)
            }));
            log.Flush();
        }

        void UpdateWindow()
        {
            if (window == null) return;
            int done = nCorrect + nWrong + nMiss;
            int answered = nCorrect + nWrong;
            string pct = answered > 0 ? (100.0 * nCorrect / answered).ToString("0") + "%" : "-";
            int recentN = 0, recentC = 0;
            for (int i = results.Count - 1; i >= 0 && recentN < 20; i--)
            {
                if (results[i].Outcome == 2) continue;
                recentN++;
                if (results[i].Outcome == 0) recentC++;
            }
            string recent = recentN > 0 ? (100.0 * recentC / recentN).ToString("0") + "%" : "-";
            string block = "";
            if (p.SideSequence == SideSequenceMode.Blocks && p.CorrectToSwitch > 0 && lastSide >= 0)
                block = string.Format("   |   Block {0}: {1}/{2} correct", SideNames[lastSide], blockCorrect, p.CorrectToSwitch);
            string status = string.Format(
                "Mouse {9}   |   Trial {0}   |   {1}{10}\nCorrect {2}   Wrong {3}   Miss {4}   |   % correct {5} (last 20: {6})   |   Licks  L {7}  R {8}\n{11}",
                trial, running ? state : (boardReady ? (done > 0 ? "Stopped" : "Ready - press Start") : "Ready - press Start"),
                nCorrect, nWrong, nMiss, pct, recent, licksL, licksR, mouseId.Length > 0 ? mouseId : "(none)", block, lastTrialLicks);
            window.Post(status, running, results.ToArray());
        }

        // Runs action after the given delay, unless the state machine has moved on in the meantime.
        void After(double seconds, Action action)
        {
            var t = ++token;
            timer.Disposable = Scheduler.Default.Schedule(TimeSpan.FromSeconds(seconds), () =>
            {
                lock (gate)
                {
                    if (!disposed && running && t == token) action();
                }
            });
        }

        void Retract() { spoutExtended = false; Servo(p.RetractedPosition); }
        void Extend() { spoutExtended = true; Servo(p.ExtendedPosition); }

        void Servo(int pulse)
        {
            Send(ServoMotor2Pulse.FromPayload(MessageType.Write, (ushort)pulse));
        }

        void MoveY()
        {
            Send(ServoMotor3Pulse.FromPayload(MessageType.Write, (ushort)p.YPosition));
        }

        void Valve(int side, int ms)
        {
            if (side == Left)
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

        void Send(HarpMessage message)
        {
            if (!disposed) output.OnNext(message);
        }

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

    // ---- Control window (runs on its own UI thread) ----

    internal class TaskWindow : Form
    {
        readonly Session session;
        readonly Label status;
        readonly Button startButton, stopButton;
        readonly NumericUpDown retractedBox, extendedBox, yBox;
        readonly Button goRetracted, goExtended, goY;
        readonly TextBox mouseBox;
        readonly TextBox weightBeforeBox, weightAfterBox;
        readonly Label weightInfo;
        readonly NumericUpDown smallBox, bigBox;
        readonly ComboBox modeBox;
        readonly NumericUpDown preTrialBox, sampleBox, delayBox, responseBox, consumeBox, timeoutBox;
        readonly NumericUpDown minLicksBox, preSampleBox;
        readonly ComboBox sideBox;
        readonly NumericUpDown repeatsBox, switchBox;
        readonly TrialPlot plot;
        bool closingFromTask;
        bool loading;          // true while boxes are filled from saved settings
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

        TaskWindow(Session session)
        {
            this.session = session;
            Text = "2AFC task";
            ClientSize = new Size(900, 660);
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
            modeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Margin = new Padding(4, 6, 4, 0) };
            modeBox.Items.AddRange(new object[] { "2AFC", "Lick for water" });
            modeBox.SelectedIndex = session.Mode;
            modeBox.SelectedIndexChanged += delegate { if (!loading) session.SetMode(modeBox.SelectedIndex); };
            top.Controls.AddRange(new Control[] { startButton, stopButton, waterL, waterR, MakeLabel("   Mode"), modeBox });

            // Spout positions (servo pulse, us). Editable any time; applied on the next movement.
            var pos = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(8, 6, 8, 0) };
            retractedBox = MakePositionBox(session.RetractedPosition);
            extendedBox = MakePositionBox(session.ExtendedPosition);
            EventHandler apply = delegate { if (!loading) session.SetPositions((int)retractedBox.Value, (int)extendedBox.Value); };
            retractedBox.ValueChanged += apply;
            extendedBox.ValueChanged += apply;
            goRetracted = MakeSmallButton("Go");
            goExtended = MakeSmallButton("Go");
            goRetracted.Click += delegate { session.MoveSpout(false); };
            goExtended.Click += delegate { session.MoveSpout(true); };
            // Y (medial-lateral) motor: moves as soon as the value changes.
            yBox = MakePositionBox(session.YPosition);
            yBox.Increment = 5;
            yBox.ValueChanged += delegate { if (!loading) session.SetY((int)yBox.Value); };
            goY = MakeSmallButton("Go");
            goY.Click += delegate { session.GoY(); };
            // -5 / +5 us clickers for fine Y adjustment.
            var yDown = new Button { Text = "-5", Width = 38, Height = 28, Margin = new Padding(2, 2, 0, 0) };
            var yUp = new Button { Text = "+5", Width = 38, Height = 28, Margin = new Padding(2, 2, 4, 0) };
            yDown.Click += delegate { yBox.Value = Math.Max(yBox.Minimum, yBox.Value - 5); };
            yUp.Click += delegate { yBox.Value = Math.Min(yBox.Maximum, yBox.Value + 5); };
            pos.Controls.AddRange(new Control[]
            {
                MakeLabel("Retracted"), retractedBox, goRetracted,
                MakeLabel("   Extended"), extendedBox, goExtended,
                MakeLabel("   Y (ML)"), yDown, yBox, yUp, goY
            });

            // Mouse ID (loads/saves per-mouse settings) and drop sizes.
            var mouseRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 6, 8, 0) };
            mouseBox = new TextBox { Width = 110, Margin = new Padding(4, 4, 4, 0) };
            mouseBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitMouse(); } };
            mouseBox.Leave += delegate { CommitMouse(); };
            smallBox = MakeDropBox(session.SmallDropMs);
            bigBox = MakeDropBox(session.BigDropMs);
            EventHandler applyDrops = delegate { if (!loading) session.SetDrops((int)smallBox.Value, (int)bigBox.Value); };
            smallBox.ValueChanged += applyDrops;
            bigBox.ValueChanged += applyDrops;
            mouseRow.Controls.AddRange(new Control[]
            {
                MakeLabel("Mouse ID"), mouseBox,
                MakeLabel("     Sample drop (ms)"), smallBox,
                MakeLabel("     Reward drop (ms)"), bigBox
            });

            // Body weight before / after the session, saved per mouse per day (mice\<ID>_weights.csv).
            var weightRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 6, 8, 0) };
            weightBeforeBox = new TextBox { Width = 60, Margin = new Padding(4, 4, 4, 0), Enabled = false };
            weightAfterBox = new TextBox { Width = 60, Margin = new Padding(4, 4, 4, 0), Enabled = false };
            weightInfo = new Label { Text = "(enter a mouse ID first)", AutoSize = true, Margin = new Padding(12, 7, 0, 0), ForeColor = Color.Gray };
            foreach (var tb in new[] { weightBeforeBox, weightAfterBox })
            {
                tb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitWeights(); } };
                tb.Leave += delegate { CommitWeights(); };
            }
            weightRow.Controls.AddRange(new Control[]
            {
                MakeLabel("Weight before (g)"), weightBeforeBox,
                MakeLabel("     after (g)"), weightAfterBox,
                weightInfo
            });

            // Epoch durations (s). Sample and delay are only used in 2AFC.
            var epochRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 6, 8, 0) };
            preTrialBox = MakeSecondsBox(session.PreTrialSeconds);
            sampleBox = MakeSecondsBox(session.SampleSeconds);
            delayBox = MakeSecondsBox(session.DelaySeconds);
            responseBox = MakeSecondsBox(session.ResponseSeconds);
            consumeBox = MakeSecondsBox(session.ConsumeSeconds);
            timeoutBox = MakeSecondsBox(session.TimeoutSeconds);
            EventHandler applyEpochs = delegate
            {
                if (!loading) session.SetEpochs((double)preTrialBox.Value, (double)sampleBox.Value, (double)delayBox.Value,
                                                (double)responseBox.Value, (double)consumeBox.Value, (double)timeoutBox.Value);
            };
            foreach (var b in new[] { preTrialBox, sampleBox, delayBox, responseBox, consumeBox, timeoutBox })
                b.ValueChanged += applyEpochs;
            epochRow.Controls.AddRange(new Control[]
            {
                MakeLabel("Pre-trial (s)"), preTrialBox,
                MakeLabel("     Sample (s)"), sampleBox,
                MakeLabel("     Delay (s)"), delayBox,
                MakeLabel("     Response window (s)"), responseBox
            });

            // What happens after the choice.
            var outcomeRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 6, 8, 0) };
            outcomeRow.Controls.AddRange(new Control[]
            {
                MakeLabel("Drink time after correct (s)"), consumeBox,
                MakeLabel("     Timeout after wrong / miss (s)"), timeoutBox
            });

            // Sample-phase rules (2AFC).
            var sampleRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 6, 8, 0) };
            minLicksBox = new NumericUpDown
            {
                Minimum = 0, Maximum = 50, Increment = 1, DecimalPlaces = 0, Width = 60,
                Value = Math.Max(0, Math.Min(50, session.MinSampleLicks)),
                Margin = new Padding(4, 4, 4, 0)
            };
            minLicksBox.ValueChanged += delegate { if (!loading) session.SetMinSampleLicks((int)minLicksBox.Value); };
            preSampleBox = MakeSecondsBox(session.PreSampleSeconds);
            preSampleBox.ValueChanged += delegate { if (!loading) session.SetPreSample((double)preSampleBox.Value); };
            sampleRow.Controls.AddRange(new Control[]
            {
                MakeLabel("Sample waits for licks on water spout (0 = off)"), minLicksBox,
                MakeLabel("     Drop before spout comes (s)"), preSampleBox
            });

            // Sample-side rules (2AFC).
            var sideRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 6, 8, 0) };
            sideBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, Margin = new Padding(4, 4, 4, 0) };
            sideBox.Items.AddRange(new object[] { "Random (max repeats)", "Blocks (LLL RRR ...)" });
            sideBox.SelectedIndex = session.SideSequenceIndex;
            repeatsBox = new NumericUpDown { Minimum = 1, Maximum = 50, Width = 55, Margin = new Padding(4, 4, 4, 0), Value = Math.Max(1, Math.Min(50, session.MaxRepeats)) };
            switchBox = new NumericUpDown { Minimum = 0, Maximum = 50, Width = 55, Margin = new Padding(4, 4, 4, 0), Value = Math.Max(0, Math.Min(50, session.CorrectToSwitch)) };
            EventHandler applySides = delegate { if (!loading) session.SetSideRules(sideBox.SelectedIndex, (int)repeatsBox.Value, (int)switchBox.Value); };
            sideBox.SelectedIndexChanged += applySides;
            repeatsBox.ValueChanged += applySides;
            switchBox.ValueChanged += applySides;
            sideRow.Controls.AddRange(new Control[]
            {
                MakeLabel("Sides"), sideBox,
                MakeLabel("   Max repeats"), repeatsBox,
                MakeLabel("   Blocks: correct to switch (0=off)"), switchBox
            });

            status = new Label { Dock = DockStyle.Top, Height = 70, Padding = new Padding(12, 4, 8, 0), Font = new Font("Segoe UI", 11f) };
            plot = new TrialPlot { Dock = DockStyle.Fill };

            Controls.Add(plot);
            Controls.Add(status);
            Controls.Add(sideRow);
            Controls.Add(outcomeRow);
            Controls.Add(sampleRow);
            Controls.Add(epochRow);
            Controls.Add(pos);
            Controls.Add(weightRow);
            Controls.Add(mouseRow);
            Controls.Add(top);
            SetRunning(false);
        }

        // Selects the typed mouse and shows its settings.
        void CommitMouse()
        {
            var id = mouseBox.Text.Trim();
            if (id == currentMouse) return;
            if (!session.SetMouse(id))
            {
                mouseBox.Text = currentMouse;
                MessageBox.Show(this, "Stop trials before changing the mouse.", "2AFC task");
                return;
            }
            currentMouse = id;
            loading = true;
            retractedBox.Value = Math.Max(1000, Math.Min(2000, session.RetractedPosition));
            extendedBox.Value = Math.Max(1000, Math.Min(2000, session.ExtendedPosition));
            yBox.Value = Math.Max(1000, Math.Min(2000, session.YPosition));
            smallBox.Value = Math.Max(0, Math.Min(1000, session.SmallDropMs));
            bigBox.Value = Math.Max(0, Math.Min(1000, session.BigDropMs));
            modeBox.SelectedIndex = session.Mode;
            sampleBox.Value = (decimal)Math.Max(0, Math.Min(120, session.SampleSeconds));
            delayBox.Value = (decimal)Math.Max(0, Math.Min(120, session.DelaySeconds));
            responseBox.Value = (decimal)Math.Max(0, Math.Min(120, session.ResponseSeconds));
            preTrialBox.Value = (decimal)Math.Max(0, Math.Min(120, session.PreTrialSeconds));
            consumeBox.Value = (decimal)Math.Max(0, Math.Min(120, session.ConsumeSeconds));
            minLicksBox.Value = Math.Max(0, Math.Min(50, session.MinSampleLicks));
            preSampleBox.Value = (decimal)Math.Max(0, Math.Min(120, session.PreSampleSeconds));
            timeoutBox.Value = (decimal)Math.Max(0, Math.Min(120, session.TimeoutSeconds));
            sideBox.SelectedIndex = session.SideSequenceIndex;
            repeatsBox.Value = Math.Max(1, Math.Min(50, session.MaxRepeats));
            switchBox.Value = Math.Max(0, Math.Min(50, session.CorrectToSwitch));
            loading = false;
            ShowWeights();
        }

        // Fills the weight boxes with today's saved values for this mouse (if any).
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

        // Saves the two weight boxes as today's row. Empty box = not weighed yet.
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
            if (Same(before, session.WeightBefore) && Same(after, session.WeightAfter)) return;   // unchanged
            session.SetWeights(before, after);
            weightInfo.ForeColor = Color.FromArgb(46, 160, 67);
            string change = !double.IsNaN(before) && !double.IsNaN(after)
                ? string.Format(CultureInfo.InvariantCulture, "   change {0:+0.0#;-0.0#;0} g", after - before) : "";
            weightInfo.Text = "saved " + DateTime.Now.ToString("HH:mm") + change + "   |   " + session.LastWeightText;
        }

        static bool Same(double a, double b)
        {
            return double.IsNaN(a) ? double.IsNaN(b) : a == b;
        }

        // Empty -> NaN (ok). Accepts "24.3" or "24,3".
        static bool TryWeight(string text, out double value)
        {
            value = double.NaN;
            text = text.Trim().Replace(',', '.');
            if (text.Length == 0) return true;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0 && value < 100;
        }

        static NumericUpDown MakeSecondsBox(double value)
        {
            return new NumericUpDown
            {
                Minimum = 0, Maximum = 120, Increment = 0.1m, DecimalPlaces = 2, Width = 70,
                Value = (decimal)Math.Max(0, Math.Min(120, value)),
                Margin = new Padding(4, 4, 4, 0)
            };
        }

        static NumericUpDown MakeDropBox(int value)
        {
            return new NumericUpDown
            {
                Minimum = 0, Maximum = 1000, Increment = 1, Width = 65,
                Value = Math.Max(0, Math.Min(1000, value)),
                Margin = new Padding(4, 4, 4, 0)
            };
        }

        static NumericUpDown MakePositionBox(int value)
        {
            return new NumericUpDown
            {
                Minimum = 1000, Maximum = 2000, Increment = 10, Width = 80,
                Value = Math.Max(1000, Math.Min(2000, value)),
                Margin = new Padding(4, 4, 4, 0)
            };
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
            goRetracted.Enabled = !running;
            goExtended.Enabled = !running;
            startButton.BackColor = running ? Color.Silver : Color.FromArgb(46, 160, 67);
            stopButton.BackColor = running ? Color.FromArgb(207, 34, 46) : Color.Silver;
        }

        // Thread-safe update from the task.
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
            try
            {
                BeginInvoke((MethodInvoker)delegate { closingFromTask = true; Close(); });
            }
            catch (InvalidOperationException) { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing the window by hand only stops trials; the workflow keeps running.
            if (!closingFromTask) session.StopTrials();
            base.OnFormClosing(e);
        }
    }

    // Last 50 trials: row = sample side, O = correct (green), X = wrong (red), dot = miss (grey).
    internal class TrialPlot : Control
    {
        const int Window = 50;
        TrialResult[] results = new TrialResult[0];

        public TrialPlot()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.White;
        }

        public void SetResults(TrialResult[] r)
        {
            results = r;
            Invalidate();
        }

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
                g.DrawString("R", label, text, 22, yR - 9);
                g.DrawString("L", label, text, 22, yL - 9);
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
                g.DrawString("O correct     X wrong     • miss          row = sample side (2AFC) or licked side (lick for water)", small, text, area.Left, 6);
            }

            using (var green = new Pen(Color.FromArgb(46, 160, 67), 2.5f))
            using (var red = new Pen(Color.FromArgb(207, 34, 46), 2.5f))
            using (var grey = new SolidBrush(Color.FromArgb(150, 150, 150)))
            {
                float r = Math.Min(7f, area.Width / (float)Window * 0.4f);
                foreach (var res in results)
                {
                    if (res.Trial < xMin) continue;
                    float x = X(res.Trial);
                    float y = res.Side == 1 ? yR : res.Side == 0 ? yL : (yL + yR) / 2;
                    if (res.Outcome == 0) g.DrawEllipse(green, x - r, y - r, 2 * r, 2 * r);
                    else if (res.Outcome == 1)
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
