using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ICUVideoStreamer.Capture
{
    public sealed class CameraPreviewService : IDisposable
    {
        private const int PreviewWidth  = 640;
        private const int PreviewHeight = 360;
        private const int BytesPerPixel = 4; // bgr0 = 4 bytes, no stride issues

        private Process    _process;
        private Thread     _readThread;
        private volatile bool _running;
        private int        _pendingFrame;
        private Dispatcher _dispatcher;

        public WriteableBitmap Bitmap { get; private set; }

        public event Action<string> StatusChanged;

        private readonly System.Collections.Generic.Queue<string> _lastStderr =
            new System.Collections.Generic.Queue<string>(3);

        public void Start(string deviceName, string ffmpegPath, Dispatcher dispatcher)
        {
            Stop();
            _dispatcher = dispatcher;

            Bitmap = new WriteableBitmap(
                PreviewWidth, PreviewHeight, 96, 96, PixelFormats.Bgr32, null);

            string safeDevice = (deviceName ?? "").Replace("\"", "").Replace("\r", "").Replace("\n", "");
            // -flush_packets 1 forces FFmpeg to flush the pipe after every packet so frames
            // are not held in an internal buffer waiting to fill a large write.
            string args = string.Format(
                "-f dshow -i \"video={0}\" -vf scale={1}:{2} -f rawvideo -pix_fmt bgr0 -an -flush_packets 1 pipe:1",
                safeDevice, PreviewWidth, PreviewHeight);

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = ffmpegPath,
                    Arguments              = args,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                },
                EnableRaisingEvents = true,
            };

            try
            {
                _process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        lock (_lastStderr)
                        {
                            if (_lastStderr.Count >= 3) _lastStderr.Dequeue();
                            _lastStderr.Enqueue(e.Data.Trim());
                        }
                    }
                };
                _process.Exited += (s, e) =>
                {
                    if (!_running) return; // intentional Stop() — already handled
                    _running = false;
                    // Give stderr reader a moment to capture the last lines
                    System.Threading.Tasks.Task.Delay(200).ContinueWith(_ =>
                    {
                        string err;
                        lock (_lastStderr)
                            err = _lastStderr.Count > 0 ? string.Join(" | ", _lastStderr) : "no output";
                        _dispatcher.BeginInvoke(DispatcherPriority.Normal,
                            new Action(() => StatusChanged?.Invoke("Preview stopped: " + err)));
                    });
                };
                _process.Start();
                _process.BeginErrorReadLine();
                _running = true;
                _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "CameraPreview" };
                _readThread.Start();
            }
            catch (Exception ex)
            {
                _process?.Dispose();
                _process = null;
                StatusChanged?.Invoke("Preview failed: " + ex.Message);
            }
        }

        private void ReadLoop()
        {
            int frameSize = PreviewWidth * PreviewHeight * BytesPerPixel;
            byte[] buf    = new byte[frameSize];
            var    stream = _process.StandardOutput.BaseStream;

            while (_running)
            {
                // Read exactly one frame
                int read = 0;
                while (read < frameSize && _running)
                {
                    int n;
                    try { n = stream.Read(buf, read, frameSize - read); }
                    catch { n = 0; }
                    if (n == 0) { _running = false; break; }
                    read += n;
                }

                if (read < frameSize)
                {
                    // Wait for process to exit so all async stderr is captured
                    try { _process?.WaitForExit(500); } catch { }
                    string err;
                    lock (_lastStderr)
                        err = _lastStderr.Count > 0 ? string.Join(" | ", _lastStderr) : "no output";
                    if (_running) // only report if not a deliberate Stop()
                        _dispatcher.BeginInvoke(DispatcherPriority.Normal,
                            new Action(() => StatusChanged?.Invoke("Preview stopped: " + err)));
                    break;
                }

                // Skip this frame if the UI thread hasn't finished the last one yet
                if (Interlocked.CompareExchange(ref _pendingFrame, 1, 0) != 0) continue;

                byte[] frame = (byte[])buf.Clone();
                _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    try
                    {
                        Bitmap.Lock();
                        Marshal.Copy(frame, 0, Bitmap.BackBuffer, frame.Length);
                        Bitmap.AddDirtyRect(new Int32Rect(0, 0, PreviewWidth, PreviewHeight));
                    }
                    finally
                    {
                        Bitmap.Unlock();
                        Interlocked.Exchange(ref _pendingFrame, 0);
                    }
                }));
            }
        }

        public void Stop()
        {
            _running = false;
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(2000);
                }
            }
            catch { }
            _process?.Dispose();
            _process = null;
        }

        public void Dispose() => Stop();
    }
}
