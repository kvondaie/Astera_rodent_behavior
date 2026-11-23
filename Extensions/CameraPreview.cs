// Live camera preview window that does not need OpenGL / the GPU driver (plain Windows Forms,
// same drawing method as harp\tools\CameraViewer). Frames pass through unchanged, so put it in
// front of the VideoWriter: what you see is what is recorded.
//
// - Opens when the workflow starts, closes when it stops.
// - Closing the window only stops the preview; the recording carries on.
// - If drawing falls behind, frames are skipped in the preview only (never in the recording).
//
// Must stay C# 5 (Bonsai's script compiler).

using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Bonsai;
using OpenCV.Net;

[Combinator]
[Description("Shows frames in a simple live window (no OpenGL needed) and passes them on unchanged.")]
[WorkflowElementCategory(ElementCategory.Sink)]
public class CameraPreview
{
    public CameraPreview()
    {
        Title = "Rig camera (live)";
    }

    [Description("Window title.")]
    public string Title { get; set; }

    public IObservable<IplImage> Process(IObservable<IplImage> source)
    {
        return Observable.Create<IplImage>(observer =>
        {
            var window = new PreviewWindow(Title);
            var sub = source.Subscribe(frame =>
            {
                window.Post(frame);
                observer.OnNext(frame);
            }, observer.OnError, observer.OnCompleted);
            return new CompositeDisposable(sub, window);
        });
    }

    class PreviewWindow : IDisposable
    {
        readonly string title;
        Form form;
        PictureBox picture;
        volatile bool ready, disposed;
        int busy;                       // 1 while a frame is waiting to be drawn
        int frames;
        DateTime fpsStart = DateTime.Now;

        public PreviewWindow(string title)
        {
            this.title = title;
            var t = new Thread(Run) { IsBackground = true, Name = "CameraPreview" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        void Run()
        {
            form = new Form { Text = title, ClientSize = new System.Drawing.Size(660, 500), ShowIcon = false };
            picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
            form.Controls.Add(picture);
            form.Shown += delegate { ready = !disposed; if (disposed) form.Close(); };
            form.FormClosed += delegate { ready = false; };
            Application.Run(form);
        }

        public void Post(IplImage frame)
        {
            if (!ready || frame == null) return;
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;   // previous frame not drawn yet: skip
            Bitmap bmp;
            try { bmp = ToBitmap(frame); }
            catch { busy = 0; return; }

            frames++;
            var now = DateTime.Now;
            string fps = null;
            if ((now - fpsStart).TotalSeconds >= 1)
            {
                fps = string.Format("{0}   ({1} x {2}, preview {3:0} fps)", title, frame.Width, frame.Height,
                                    frames / (now - fpsStart).TotalSeconds);
                frames = 0; fpsStart = now;
            }
            try
            {
                form.BeginInvoke((MethodInvoker)delegate
                {
                    var old = picture.Image;
                    picture.Image = bmp;
                    if (old != null) old.Dispose();
                    if (fps != null) form.Text = fps;
                    busy = 0;
                });
            }
            catch (Exception) { bmp.Dispose(); busy = 0; }   // window closed in the meantime
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

        public void Dispose()
        {
            disposed = true;
            if (!ready) return;          // not shown yet: Shown handler closes it
            ready = false;
            try { form.BeginInvoke((MethodInvoker)delegate { form.Close(); }); }
            catch (Exception) { }
        }
    }
}
