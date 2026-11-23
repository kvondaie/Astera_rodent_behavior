// Stamps camera frames with Harp time, so video can be lined up with trials and licks.
//
// A UVC webcam cannot be triggered by the Harp board, so each frame gets a software estimate:
//   HarpSeconds = timestamp of the latest Harp message + PC time elapsed since that message arrived.
// The Harp board sends a heartbeat every second (Heartbeat = Enabled on the Device node), so the
// extrapolation never runs more than ~1 s. Accuracy is limited by the camera's own delay (exposure,
// processing, USB transfer: unknown, a few to tens of ms), not by this clock.
//
// Inputs: Source1 = frames (any type), Source2 = Harp messages (the "Behavior Events" subject).
// Output: one FrameStamp per frame. Frames that arrive before any Harp message get HarpSeconds = NaN.
//
// Must stay C# 5 (Bonsai's script compiler).

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.Harp;

[Combinator]
[Description("Stamps each frame with Harp time: latest Harp message timestamp + PC time elapsed since it arrived.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class HarpFrameClock
{
    public IObservable<FrameStamp<TFrame>> Process<TFrame>(IObservable<TFrame> frames, IObservable<HarpMessage> harp)
    {
        return Observable.Create<FrameStamp<TFrame>>(observer =>
        {
            var gate = new object();
            var clock = Stopwatch.StartNew();
            double lastHarp = double.NaN;
            long lastTicks = 0;
            long frameNumber = 0;

            var harpSub = harp.Subscribe(message =>
            {
                double t;
                if (!message.TryGetTimestamp(out t)) return;
                long now = clock.ElapsedTicks;
                lock (gate) { lastHarp = t; lastTicks = now; }
            }, observer.OnError);

            var frameSub = frames.Subscribe(frame =>
            {
                long now = clock.ElapsedTicks;
                DateTime pcTime = DateTime.Now;
                double harpTime, age;
                lock (gate)
                {
                    age = double.IsNaN(lastHarp) ? double.NaN : (now - lastTicks) / (double)Stopwatch.Frequency;
                    harpTime = lastHarp + age;
                }
                observer.OnNext(new FrameStamp<TFrame>(frame, frameNumber++, harpTime, age, pcTime));
            }, observer.OnError, observer.OnCompleted);

            return new CompositeDisposable(harpSub, frameSub);
        });
    }
}

public class FrameStamp<TFrame>
{
    public FrameStamp(TFrame frame, long frameNumber, double harpSeconds, double sinceHarpMessage, DateTime pcTime)
    {
        Frame = frame;
        FrameNumber = frameNumber;
        HarpSeconds = harpSeconds;
        SinceHarpMessage = sinceHarpMessage;
        PcTime = pcTime;
    }

    public TFrame Frame { get; private set; }

    // 0-based index; equals the frame's position in the video file.
    public long FrameNumber { get; private set; }

    // Estimated Harp time (s) when the frame reached the PC.
    public double HarpSeconds { get; private set; }

    // Seconds since the Harp message used as the anchor (should stay below ~1 s with heartbeat on).
    public double SinceHarpMessage { get; private set; }

    public DateTime PcTime { get; private set; }
}
