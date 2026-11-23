// Applies the rig camera viewer's flip / rotate / crop to recorded frames, so the video matches
// what you set up in harp\tools\CameraViewer (drag a rectangle there to crop; it saves camera_viewer.ini).
//
// The ini is read once when the workflow starts, so every frame of a recording has the same size.
// Brightness / contrast are NOT applied: they are viewing aids, the recording stays raw.
// Crop width / height are rounded down to even numbers (the video codec needs them).
// For the crop to match what you saw, the viewer must use the same resolution as the recording
// (Width=640, Height=480 in camera_viewer.ini).
//
// Must stay C# 5 (Bonsai's script compiler).

using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reactive.Linq;
using Bonsai;
using OpenCV.Net;

[Combinator]
[Description("Flips / rotates / crops frames using the settings saved by the rig camera viewer.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class RigCameraTransform
{
    public RigCameraTransform()
    {
        SettingsFile = @"tools\CameraViewer\camera_viewer.ini";
    }

    [Description("Path to camera_viewer.ini (relative to the workflow folder).")]
    public string SettingsFile { get; set; }

    public IObservable<IplImage> Process(IObservable<IplImage> source)
    {
        return Observable.Defer(() =>
        {
            var s = Settings.Load(SettingsFile);
            Console.WriteLine("RigCameraTransform: " + s);
            return source.Select(frame => Apply(frame, s));
        });
    }

    static IplImage Apply(IplImage frame, Settings s)
    {
        // Rotation clockwise: 90 = transpose + mirror, 180 = both flips, 270 = transpose + upside-down
        // (same order as the viewer: rotate, then flip, then crop).
        IplImage img;
        if (s.Rotation == 90 || s.Rotation == 270)
        {
            img = new IplImage(new Size(frame.Height, frame.Width), frame.Depth, frame.Channels);
            CV.Transpose(frame, img);
            CV.Flip(img, img, s.Rotation == 90 ? FlipMode.Horizontal : FlipMode.Vertical);
        }
        else
        {
            img = frame.Clone();
            if (s.Rotation == 180) CV.Flip(img, img, FlipMode.Both);
        }
        if (s.FlipH && s.FlipV) CV.Flip(img, img, FlipMode.Both);
        else if (s.FlipH) CV.Flip(img, img, FlipMode.Horizontal);
        else if (s.FlipV) CV.Flip(img, img, FlipMode.Vertical);

        int x = (int)(s.CropX * img.Width), y = (int)(s.CropY * img.Height);
        int w = Math.Min(Math.Max(8, (int)(s.CropW * img.Width)), img.Width - x) & ~1;
        int h = Math.Min(Math.Max(8, (int)(s.CropH * img.Height)), img.Height - y) & ~1;
        if (x == 0 && y == 0 && w == img.Width && h == img.Height) return img;

        var output = new IplImage(new Size(w, h), img.Depth, img.Channels);
        using (var sub = img.GetSubRect(new Rect(x, y, w, h))) CV.Copy(sub, output);
        img.Dispose();
        return output;
    }

    class Settings
    {
        public bool FlipH, FlipV;
        public int Rotation;
        public double CropX, CropY, CropW = 1, CropH = 1;
        public int Width, Height;
        public string Source = "(no ini found: no flip, no crop)";

        public static Settings Load(string path)
        {
            var s = new Settings();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return s;
            s.Source = Path.GetFullPath(path);
            var ci = CultureInfo.InvariantCulture;
            foreach (var line in File.ReadAllLines(path))
            {
                var kv = line.Split(new[] { '=' }, 2);
                if (kv.Length != 2) continue;
                double v;
                if (!double.TryParse(kv[1].Trim(), NumberStyles.Float, ci, out v)) continue;
                switch (kv[0].Trim())
                {
                    case "Width": s.Width = (int)v; break;
                    case "Height": s.Height = (int)v; break;
                    case "FlipH": s.FlipH = v != 0; break;
                    case "FlipV": s.FlipV = v != 0; break;
                    case "Rotation": s.Rotation = ((int)v % 360 + 360) % 360 / 90 * 90; break;
                    case "CropX": s.CropX = v; break;
                    case "CropY": s.CropY = v; break;
                    case "CropW": s.CropW = v; break;
                    case "CropH": s.CropH = v; break;
                }
            }
            if (s.CropW <= 0.01 || s.CropH <= 0.01 || s.CropX < 0 || s.CropY < 0 || s.CropX + s.CropW > 1.0001 || s.CropY + s.CropH > 1.0001)
            { s.CropX = s.CropY = 0; s.CropW = s.CropH = 1; }
            return s;
        }

        public override string ToString()
        {
            var ci = CultureInfo.InvariantCulture;
            return string.Format(ci, "flipH={0} flipV={1} rot={2} crop x={3:0.###} y={4:0.###} w={5:0.###} h={6:0.###} (viewer res {7}x{8}) from {9}",
                FlipH, FlipV, Rotation, CropX, CropY, CropW, CropH, Width, Height, Source);
        }
    }
}
