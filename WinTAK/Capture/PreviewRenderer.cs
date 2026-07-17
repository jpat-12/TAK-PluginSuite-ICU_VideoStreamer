using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ICUVideoStreamer.Capture
{
    /// <summary>
    /// Renders a raw <c>bgr0</c> video stream (from an FFmpeg <c>pipe:1</c> output) into a
    /// <see cref="WriteableBitmap"/> for WPF. Shared by the idle camera preview and the
    /// live-while-streaming preview: it always drains the pipe (so a busy UI can never stall
    /// the producing FFmpeg) and simply skips rendering frames the UI hasn't caught up on.
    /// </summary>
    public sealed class PreviewRenderer : IDisposable
    {
        private const int BytesPerPixel = 4; // bgr0

        private readonly int _width;
        private readonly int _height;

        private Thread     _thread;
        private volatile bool _running;
        private int        _pendingFrame;
        private bool       _firstFrameSeen;
        private Dispatcher _dispatcher;
        private System.IO.Stream _source;

        public WriteableBitmap Bitmap { get; }

        /// <summary>Raised (on the dispatcher) when the first frame has been rendered.</summary>
        public event Action FirstFrame;

        public PreviewRenderer(int width, int height)
        {
            _width  = width;
            _height = height;
            Bitmap  = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        }

        /// <summary>Begin reading <paramref name="source"/> and rendering frames.</summary>
        public void Start(System.IO.Stream source, Dispatcher dispatcher)
        {
            Stop();
            _source     = source;
            _dispatcher = dispatcher;
            _running    = true;
            _firstFrameSeen = false;
            _thread = new Thread(ReadLoop) { IsBackground = true, Name = "PreviewRenderer" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _thread?.Join(500); } catch { }
            _thread = null;
        }

        private void ReadLoop()
        {
            int frameSize = _width * _height * BytesPerPixel;
            byte[] buf = new byte[frameSize];
            var stream = _source;
            if (stream == null) return;

            while (_running)
            {
                // Read exactly one frame; always drain the pipe.
                int read = 0;
                while (read < frameSize && _running)
                {
                    int n;
                    try { n = stream.Read(buf, read, frameSize - read); }
                    catch { n = 0; }
                    if (n == 0) { _running = false; break; }
                    read += n;
                }
                if (read < frameSize) break;

                // Skip rendering if the UI thread hasn't finished the previous frame.
                if (Interlocked.CompareExchange(ref _pendingFrame, 1, 0) != 0) continue;

                byte[] frame = (byte[])buf.Clone();
                bool notifyFirst = !_firstFrameSeen;
                _firstFrameSeen = true;

                _dispatcher?.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    try
                    {
                        Bitmap.Lock();
                        Marshal.Copy(frame, 0, Bitmap.BackBuffer, frame.Length);
                        Bitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
                    }
                    finally
                    {
                        Bitmap.Unlock();
                        Interlocked.Exchange(ref _pendingFrame, 0);
                    }
                    if (notifyFirst) FirstFrame?.Invoke();
                }));
            }
        }

        public void Dispose() => Stop();
    }
}
