// Rig camera viewer: live view with flip, rotate, crop, brightness and contrast.
//
// Uses the OpenCV libraries that come with Bonsai (loaded from %LOCALAPPDATA%\Bonsai\Packages),
// and draws with plain Windows Forms, so it works without a GPU / OpenGL driver.
// Settings are remembered in camera_viewer.ini next to this program.
//
// Build: see build.cmd in this folder. Written for C# 5 (.NET Framework compiler).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using OpenCV.Net;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using Font = System.Drawing.Font;

static class Program
{
    static readonly string Packages = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bonsai", "Packages");

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

    [STAThread]
    static void Main()
    {
        // Only one viewer at a time (only one program can use the camera): focus the open one instead.
        bool first;
        var mutex = new Mutex(true, "RigCameraViewer_SingleInstance", out first);
        if (!first)
        {
            var me = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(p.MainWindowHandle, 9);   // restore if minimized
                SetForegroundWindow(p.MainWindowHandle);
            }
            return;
        }

        // Find OpenCV.Net and its native DLLs in the Bonsai install.
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var name = new AssemblyName(e.Name).Name;
            if (name != "OpenCV.Net") return null;
            var path = Path.Combine(Packages, @"OpenCV.Net.3.4.2\lib\net462\OpenCV.Net.dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        var native = Path.Combine(Packages, @"OpenCV.Net.runtime.win-x64.2.4.13\runtimes\win-x64\native\vc14\bin");
        Environment.SetEnvironmentVariable("PATH", native + ";" + Environment.GetEnvironmentVariable("PATH"));

        Application.EnableVisualStyles();
        Application.Run(new ViewerForm());
        GC.KeepAlive(mutex);   // hold the single-instance lock until the window closes
    }
}

class ViewerSettings
{
    public int CameraIndex = 0;
    public int Width = 1280, Height = 720;
    public bool Mjpg;                          // ask the camera for compressed (MJPG) frames: faster at high resolution
    public bool FlipH, FlipV;
    public int Rotation;                       // 0, 90, 180, 270 (clockwise)
    public double CropX, CropY, CropW = 1, CropH = 1;   // fractions of the flipped/rotated frame
    public int Brightness;                     // -100..100
    public int Contrast = 100;                 // percent, 50..300

    static string FilePath
    {
        get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "camera_viewer.ini"); }
    }

    public static ViewerSettings Load()
    {
        var s = new ViewerSettings();
        if (!File.Exists(FilePath)) return s;
        var ci = CultureInfo.InvariantCulture;
        foreach (var line in File.ReadAllLines(FilePath))
        {
            var kv = line.Split(new[] { '=' }, 2);
            if (kv.Length != 2) continue;
            double v;
            if (!double.TryParse(kv[1].Trim(), NumberStyles.Float, ci, out v)) continue;
            switch (kv[0].Trim())
            {
                case "CameraIndex": s.CameraIndex = (int)v; break;
                case "Width": s.Width = (int)v; break;
                case "Height": s.Height = (int)v; break;
                case "Mjpg": s.Mjpg = v != 0; break;
                case "FlipH": s.FlipH = v != 0; break;
                case "FlipV": s.FlipV = v != 0; break;
                case "Rotation": s.Rotation = ((int)v % 360 + 360) % 360 / 90 * 90; break;
                case "CropX": s.CropX = v; break;
                case "CropY": s.CropY = v; break;
                case "CropW": s.CropW = v; break;
                case "CropH": s.CropH = v; break;
                case "Brightness": s.Brightness = (int)v; break;
                case "Contrast": s.Contrast = (int)v; break;
            }
        }
        if (s.CropW <= 0.01 || s.CropH <= 0.01 || s.CropX < 0 || s.CropY < 0 || s.CropX + s.CropW > 1.0001 || s.CropY + s.CropH > 1.0001)
        { s.CropX = s.CropY = 0; s.CropW = s.CropH = 1; }
        return s;
    }

    public void Save()
    {
        var ci = CultureInfo.InvariantCulture;
        try
        {
            File.WriteAllLines(FilePath, new[]
            {
                "CameraIndex=" + CameraIndex.ToString(ci),
                "Width=" + Width.ToString(ci),
                "Height=" + Height.ToString(ci),
                "Mjpg=" + (Mjpg ? 1 : 0),
                "FlipH=" + (FlipH ? 1 : 0),
                "FlipV=" + (FlipV ? 1 : 0),
                "Rotation=" + Rotation.ToString(ci),
                "CropX=" + CropX.ToString("0.#####", ci),
                "CropY=" + CropY.ToString("0.#####", ci),
                "CropW=" + CropW.ToString("0.#####", ci),
                "CropH=" + CropH.ToString("0.#####", ci),
                "Brightness=" + Brightness.ToString(ci),
                "Contrast=" + Contrast.ToString(ci)
            });
        }
        catch (IOException) { }
    }
}

class ViewerForm : Form
{
    readonly ViewerSettings s = ViewerSettings.Load();
    readonly object settingsLock = new object();
    readonly PictureBox picture;
    readonly Label info;
    readonly CheckBox flipH, flipV;
    readonly Button rotate;
    readonly TrackBar brightness, contrast;
    Thread captureThread;
    volatile bool stopping;
    Point dragStart;
    Rectangle dragRect;
    bool dragging;
    int frames;
    DateTime fpsStart = DateTime.Now;
    string fpsText = "";

    public ViewerForm()
    {
        Text = "Rig camera";
        ClientSize = new Size(900, 620);
        Font = new Font("Segoe UI", 9.5f);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(6, 6, 6, 0) };
        flipH = new CheckBox { Text = "Flip ↔", AutoSize = true, Checked = s.FlipH, Margin = new Padding(4, 6, 8, 0) };
        flipV = new CheckBox { Text = "Flip ↕", AutoSize = true, Checked = s.FlipV, Margin = new Padding(4, 6, 8, 0) };
        rotate = new Button { Width = 110, Height = 28 };
        var resetCrop = new Button { Text = "Reset crop", Width = 90, Height = 28 };
        brightness = new TrackBar { Minimum = -100, Maximum = 100, Value = Clamp(s.Brightness, -100, 100), Width = 130, TickFrequency = 50, AutoSize = false, Height = 30 };
        contrast = new TrackBar { Minimum = 50, Maximum = 300, Value = Clamp(s.Contrast, 50, 300), Width = 130, TickFrequency = 50, AutoSize = false, Height = 30 };
        var resetLevels = new Button { Text = "Reset", Width = 60, Height = 28 };

        flipH.CheckedChanged += delegate { lock (settingsLock) { s.FlipH = flipH.Checked; ResetCropLocked(); } s.Save(); };
        flipV.CheckedChanged += delegate { lock (settingsLock) { s.FlipV = flipV.Checked; ResetCropLocked(); } s.Save(); };
        rotate.Click += delegate { lock (settingsLock) { s.Rotation = (s.Rotation + 90) % 360; ResetCropLocked(); } UpdateRotateText(); s.Save(); };
        resetCrop.Click += delegate { lock (settingsLock) ResetCropLocked(); s.Save(); };
        brightness.ValueChanged += delegate { lock (settingsLock) s.Brightness = brightness.Value; s.Save(); };
        contrast.ValueChanged += delegate { lock (settingsLock) s.Contrast = contrast.Value; s.Save(); };
        resetLevels.Click += delegate { brightness.Value = 0; contrast.Value = 100; };
        UpdateRotateText();

        bar.Controls.AddRange(new Control[]
        {
            flipH, flipV, rotate, resetCrop,
            Lbl("   Brightness"), brightness, Lbl("Contrast"), contrast, resetLevels
        });

        info = new Label { Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(6, 3, 0, 0), ForeColor = Color.DimGray,
                           Text = "Drag a rectangle on the image to crop.  Settings are remembered." };
        picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        picture.MouseDown += OnMouseDown;
        picture.MouseMove += OnMouseMove;
        picture.MouseUp += OnMouseUp;
        picture.Paint += OnPicturePaint;

        Controls.Add(picture);
        Controls.Add(info);
        Controls.Add(bar);
    }

    static Label Lbl(string t) { return new Label { Text = t, AutoSize = true, Margin = new Padding(4, 8, 0, 0) }; }
    static int Clamp(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }

    void UpdateRotateText() { rotate.Text = "Rotate (" + s.Rotation + "°)"; }

    void ResetCropLocked() { s.CropX = s.CropY = 0; s.CropW = s.CropH = 1; }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        captureThread = new Thread(CaptureLoop) { IsBackground = true };
        captureThread.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        stopping = true;
        if (captureThread != null) captureThread.Join(2000);
        s.Save();
        base.OnFormClosing(e);
    }

    // ---- Capture and processing (background thread) ----

    void CaptureLoop()
    {
        OpenCV.Net.Capture capture = null;
        try
        {
            capture = OpenCV.Net.Capture.CreateCameraCapture(s.CameraIndex);
            if (capture == null) throw new InvalidOperationException("No camera at index " + s.CameraIndex + ".");
            if (s.Mjpg)
                capture.SetProperty(CaptureProperty.FourCC, 'M' | ('J' << 8) | ('P' << 16) | ('G' << 24));
            capture.SetProperty(CaptureProperty.FrameWidth, s.Width);
            capture.SetProperty(CaptureProperty.FrameHeight, s.Height);

            while (!stopping)
            {
                var frame = capture.QueryFrame();          // owned by the capture; do not dispose
                if (frame == null) { Thread.Sleep(10); continue; }
                Bitmap bmp;
                using (var processed = Process(frame)) bmp = ToBitmap(processed);
                ShowFrame(bmp, frame.Width, frame.Height);
            }
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (!stopping && IsHandleCreated)
                BeginInvoke((MethodInvoker)delegate { info.Text = "Camera error: " + msg + "  (Is another program, e.g. the Camera app, using it?)"; });
        }
        finally
        {
            if (capture != null) capture.Close();
        }
    }

    // Flip / rotate, then crop, then brightness / contrast. Returns a new image the caller disposes.
    IplImage Process(IplImage frame)
    {
        bool fh, fv; int rot; double cx, cy, cw, ch; int br, ct;
        lock (settingsLock)
        {
            fh = s.FlipH; fv = s.FlipV; rot = s.Rotation;
            cx = s.CropX; cy = s.CropY; cw = s.CropW; ch = s.CropH;
            br = s.Brightness; ct = s.Contrast;
        }

        // Rotation clockwise: 90 = transpose + mirror, 180 = both flips, 270 = transpose + upside-down.
        IplImage img;
        if (rot == 90 || rot == 270)
        {
            img = new IplImage(new OpenCV.Net.Size(frame.Height, frame.Width), frame.Depth, frame.Channels);
            CV.Transpose(frame, img);
            CV.Flip(img, img, rot == 90 ? FlipMode.Horizontal : FlipMode.Vertical);
        }
        else
        {
            img = frame.Clone();
            if (rot == 180) CV.Flip(img, img, FlipMode.Both);
        }
        if (fh && fv) CV.Flip(img, img, FlipMode.Both);
        else if (fh) CV.Flip(img, img, FlipMode.Horizontal);
        else if (fv) CV.Flip(img, img, FlipMode.Vertical);

        // Crop (fractions of the transformed frame).
        var rect = new Rect(
            (int)(cx * img.Width), (int)(cy * img.Height),
            Math.Max(8, (int)(cw * img.Width)), Math.Max(8, (int)(ch * img.Height)));
        rect.Width = Math.Min(rect.Width, img.Width - rect.X);
        rect.Height = Math.Min(rect.Height, img.Height - rect.Y);
        var sub = img.GetSubRect(rect);

        var output = new IplImage(new OpenCV.Net.Size(rect.Width, rect.Height), img.Depth, img.Channels);
        CV.ConvertScale(sub, output, ct / 100.0, br * 1.28);
        sub.Dispose();
        img.Dispose();
        return output;
    }

    static Bitmap ToBitmap(IplImage img)
    {
        var fmt = img.Channels == 1 ? PixelFormat.Format8bppIndexed : PixelFormat.Format24bppRgb;
        var bmp = new Bitmap(img.Width, img.Height, fmt);
        if (img.Channels == 1)
        {
            var pal = bmp.Palette;
            for (int i = 0; i < 256; i++) pal.Entries[i] = Color.FromArgb(i, i, i);
            bmp.Palette = pal;
        }
        var data = bmp.LockBits(new Rectangle(0, 0, img.Width, img.Height), ImageLockMode.WriteOnly, fmt);
        int rowBytes = img.Width * img.Channels;
        var row = new byte[rowBytes];
        for (int y = 0; y < img.Height; y++)
        {
            Marshal.Copy(img.ImageData + y * img.WidthStep, row, 0, rowBytes);
            Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, rowBytes);
        }
        bmp.UnlockBits(data);
        return bmp;
    }

    void ShowFrame(Bitmap bmp, int w, int h)
    {
        frames++;
        var now = DateTime.Now;
        if ((now - fpsStart).TotalSeconds >= 1)
        {
            fpsText = string.Format("{0} x {1} camera, {2:0} fps", w, h, frames / (now - fpsStart).TotalSeconds);
            frames = 0; fpsStart = now;
        }
        if (stopping || !IsHandleCreated) { bmp.Dispose(); return; }
        try
        {
            BeginInvoke((MethodInvoker)delegate
            {
                var old = picture.Image;
                picture.Image = bmp;
                if (old != null) old.Dispose();
                if (!info.Text.StartsWith("Camera error"))
                    info.Text = fpsText + "    |    Drag a rectangle on the image to crop.  Settings are remembered.";
            });
        }
        catch (InvalidOperationException) { bmp.Dispose(); }
    }

    // ---- Crop by dragging ----

    // Where the (zoomed, letterboxed) image sits inside the PictureBox.
    RectangleF ImageArea()
    {
        var img = picture.Image;
        if (img == null) return RectangleF.Empty;
        float scale = Math.Min((float)picture.Width / img.Width, (float)picture.Height / img.Height);
        float w = img.Width * scale, h = img.Height * scale;
        return new RectangleF((picture.Width - w) / 2, (picture.Height - h) / 2, w, h);
    }

    void OnMouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || picture.Image == null) return;
        dragging = true;
        dragStart = e.Location;
        dragRect = Rectangle.Empty;
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!dragging) return;
        dragRect = Rectangle.FromLTRB(Math.Min(dragStart.X, e.X), Math.Min(dragStart.Y, e.Y),
                                      Math.Max(dragStart.X, e.X), Math.Max(dragStart.Y, e.Y));
        picture.Invalidate();
    }

    void OnMouseUp(object sender, MouseEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        var area = ImageArea();
        var r = RectangleF.Intersect(dragRect, area);
        dragRect = Rectangle.Empty;
        picture.Invalidate();
        if (r.Width < 10 || r.Height < 10 || area.Width <= 0) return;   // ignore clicks / tiny drags

        // Selection as fractions of what is shown now, composed with the current crop.
        double fx = (r.X - area.X) / area.Width, fy = (r.Y - area.Y) / area.Height;
        double fw = r.Width / area.Width, fh = r.Height / area.Height;
        lock (settingsLock)
        {
            s.CropX += fx * s.CropW;
            s.CropY += fy * s.CropH;
            s.CropW *= fw;
            s.CropH *= fh;
        }
        s.Save();
    }

    void OnPicturePaint(object sender, PaintEventArgs e)
    {
        if (!dragging || dragRect.Width == 0) return;
        using (var pen = new Pen(Color.Yellow, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
            e.Graphics.DrawRectangle(pen, dragRect);
    }
}
