// Passes items through only while "Save video" is ticked in the 2AFC task window.
// Put it in front of the VideoWriter and the video_frames CsvWriter (not the live preview).
// Bonsai's file writers only create their file when the first item arrives, so with the box
// unticked from the start no video file is made at all. Unticking mid-session leaves a gap;
// the video_frames log has the Harp time of every saved frame, so the gap can be found.
//
// Written for C# 5 (Bonsai compiles this with the .NET Framework compiler).

using System;
using System.ComponentModel;
using System.Reactive.Linq;
using Bonsai;

[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Passes items only while 'Save video' is ticked in the 2AFC task window.")]
public class VideoGate
{
    public IObservable<TSource> Process<TSource>(IObservable<TSource> source)
    {
        return source.Where(x => TwoAfcTask.SaveVideoNow);
    }
}
