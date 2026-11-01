using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace CamTel;

/// <summary>
/// Background worker: decodes the newest JPEG from the phone, applies rotation / mirror / fit,
/// pushes it to the virtual camera and raises a preview copy. Shows a placeholder when idle.
/// </summary>
public sealed class FramePipeline : IDisposable
{
    private readonly object _lock = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private volatile bool _running = true;

    private byte[]? _pending;
    private int _width;
    private int _height;
    private volatile bool _sizeChanged = true;

    private VirtualCamera? _camera;
    private Bitmap? _output;
    private long _lastCameraAttempt = -10_000;
    private long _lastFrameAt = -10_000;
    private long _lastPreviewAt;
    private int _framesThisSecond;
    private long _fpsWindowStart;

    public volatile int Rotation;
    public volatile bool Mirror;
    public volatile bool Fill;
    public volatile string Placeholder = "En attente du téléphone…";

    public int Fps { get; private set; }
    public int SourceWidth { get; private set; }
    public int SourceHeight { get; private set; }
    public bool CameraActive => _camera != null;
    public bool CameraInUse => _camera?.IsConnected ?? false;

    /// <summary>Raised on the worker thread with a bitmap the handler must dispose.</summary>
    public event Action<Bitmap>? Preview;

    public FramePipeline(int width, int height)
    {
        _width = width;
        _height = height;
        _thread = new Thread(Run) { IsBackground = true, Name = "CamTel pipeline" };
        _thread.Start();
    }

    public void SetOutputSize(int width, int height)
    {
        lock (_lock)
        {
            if (width == _width && height == _height) return;
            _width = width;
            _height = height;
            _sizeChanged = true;
        }
        _signal.Set();
    }

    /// <summary>Drops the virtual camera so it gets recreated (e.g. after a driver install).</summary>
    public void ResetCamera()
    {
        _sizeChanged = true;
        _lastCameraAttempt = -10_000;
        _signal.Set();
    }

    public void SubmitJpeg(byte[] jpeg)
    {
        lock (_lock) _pending = jpeg;
        _signal.Set();
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        while (_running)
        {
            if (_sizeChanged)
            {
                int w, h;
                lock (_lock)
                {
                    w = _width;
                    h = _height;
                    _sizeChanged = false;
                }
                _camera?.Dispose();
                _camera = null;
                _output?.Dispose();
                _output = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                _lastCameraAttempt = -10_000;
            }

            var now = clock.ElapsedMilliseconds;
            if (_camera == null && now - _lastCameraAttempt > 2000)
            {
                _lastCameraAttempt = now;
                _camera = VirtualCamera.Create(_output!.Width, _output.Height, 30f);
            }

            _signal.WaitOne(100);
            now = clock.ElapsedMilliseconds;

            byte[]? jpeg;
            lock (_lock)
            {
                jpeg = _pending;
                _pending = null;
            }

            var output = _output!;
            if (jpeg != null)
            {
                if (!RenderJpeg(jpeg, output)) continue;
                _lastFrameAt = now;
                CountFps(now);
            }
            else if (now - _lastFrameAt > 1000)
            {
                Fps = 0;
                RenderPlaceholder(output);
            }
            else
            {
                continue;
            }

            Send(output);

            // Placeholder previews are throttled; live frames go through at full rate.
            if (jpeg != null || now - _lastPreviewAt > 500)
            {
                _lastPreviewAt = now;
                var handler = Preview;
                if (handler != null) handler((Bitmap)output.Clone());
            }
        }
        _camera?.Dispose();
        _output?.Dispose();
    }

    private void CountFps(long now)
    {
        _framesThisSecond++;
        if (now - _fpsWindowStart >= 1000)
        {
            Fps = _framesThisSecond;
            _framesThisSecond = 0;
            _fpsWindowStart = now;
        }
    }

    private bool RenderJpeg(byte[] jpeg, Bitmap output)
    {
        try
        {
            using var ms = new MemoryStream(jpeg, writable: false);
            using var src = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
            SourceWidth = src.Width;
            SourceHeight = src.Height;
            using var g = Graphics.FromImage(output);
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighSpeed;

            var rot = ((Rotation % 360) + 360) % 360;
            var quarter = rot == 90 || rot == 270;
            float sw = quarter ? src.Height : src.Width;
            float sh = quarter ? src.Width : src.Height;
            var scale = Fill
                ? Math.Max(output.Width / sw, output.Height / sh)
                : Math.Min(output.Width / sw, output.Height / sh);

            g.TranslateTransform(output.Width / 2f, output.Height / 2f);
            if (rot != 0) g.RotateTransform(rot);
            g.ScaleTransform(Mirror ? -scale : scale, scale);
            g.DrawImage(src, -src.Width / 2f, -src.Height / 2f, src.Width, src.Height);
            return true;
        }
        catch (Exception)
        {
            // Corrupt or truncated JPEG: skip it.
            return false;
        }
    }

    private void RenderPlaceholder(Bitmap output)
    {
        using var g = Graphics.FromImage(output);
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, output.Width, output.Height),
                   Color.FromArgb(18, 24, 38), Color.FromArgb(8, 10, 16), 90f))
        {
            g.FillRectangle(bg, 0, 0, output.Width, output.Height);
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var unit = output.Height / 720f;
        var cx = output.Width / 2f;
        var cy = output.Height / 2f - 40 * unit;
        using (var blue = new SolidBrush(Color.FromArgb(47, 128, 237)))
        using (var white = new SolidBrush(Color.White))
        {
            // Camera glyph.
            var body = new RectangleF(cx - 70 * unit, cy - 45 * unit, 100 * unit, 90 * unit);
            using (var path = RoundedRect(body, 14 * unit)) g.FillPath(blue, path);
            g.FillPolygon(blue, new[]
            {
                new PointF(cx + 35 * unit, cy - 15 * unit),
                new PointF(cx + 75 * unit, cy - 40 * unit),
                new PointF(cx + 75 * unit, cy + 40 * unit),
                new PointF(cx + 35 * unit, cy + 15 * unit),
            });
            g.FillEllipse(white, cx - 38 * unit, cy - 18 * unit, 36 * unit, 36 * unit);
        }

        using var title = new Font("Segoe UI Semibold", 34 * unit, GraphicsUnit.Pixel);
        using var sub = new Font("Segoe UI", 22 * unit, GraphicsUnit.Pixel);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString("CamTel", title, Brushes.White, cx, cy + 70 * unit, fmt);
        using var grey = new SolidBrush(Color.FromArgb(170, 180, 200));
        g.DrawString(Placeholder, sub, grey, cx, cy + 120 * unit, fmt);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void Send(Bitmap output)
    {
        var cam = _camera;
        if (cam == null) return;
        var data = output.LockBits(new Rectangle(0, 0, output.Width, output.Height), ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);
        try
        {
            // Output widths are multiples of 4, so GDI+ rows are tightly packed like softcam expects.
            cam.Send(data.Scan0);
        }
        finally
        {
            output.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        _running = false;
        _signal.Set();
        _thread.Join(2000);
    }
}
