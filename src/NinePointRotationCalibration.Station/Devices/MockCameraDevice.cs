using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using NinePointRotationCalibration.Station.Models;

namespace NinePointRotationCalibration.Station.Devices
{
    public sealed class MockCameraDevice : ICameraDevice
    {
        public const string DefaultDeviceId = "MOCK-PCB-01";

        private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);
        private readonly object _stateSync = new object();
        private readonly bool _simulateIntermittentFaults;
        private bool _disposeRequested;
        private bool _disposed;
        private bool _isConnected;
        private string _connectedDeviceId;
        private double _exposureMicroseconds = 10000.0;
        private long _sequence;

        public MockCameraDevice()
            : this(false)
        {
        }

        public MockCameraDevice(bool simulateIntermittentFaults)
        {
            _simulateIntermittentFaults = simulateIntermittentFaults;
        }

        public string ProviderName
        {
            get { return "Mock PCB Camera"; }
        }

        public bool IsConnected
        {
            get
            {
                lock (_stateSync)
                {
                    return _isConnected;
                }
            }
        }

        public string ConnectedDeviceId
        {
            get
            {
                lock (_stateSync)
                {
                    return _connectedDeviceId;
                }
            }
        }

        public async Task<IReadOnlyList<CameraDescriptor>> EnumerateAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();
                return new List<CameraDescriptor>
                {
                    new CameraDescriptor
                    {
                        Id = DefaultDeviceId,
                        DisplayName = "Mock PCB Camera (6 boards)"
                    }
                }.AsReadOnly();
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public async Task ConnectAsync(string deviceId, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();

                string requestedId = string.IsNullOrWhiteSpace(deviceId) ? DefaultDeviceId : deviceId.Trim();
                if (!string.Equals(requestedId, DefaultDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Unknown mock camera device: " + requestedId, nameof(deviceId));
                }

                lock (_stateSync)
                {
                    _connectedDeviceId = DefaultDeviceId;
                    _isConnected = true;
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public async Task DisconnectAsync()
        {
            ThrowIfDisposed();
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                lock (_stateSync)
                {
                    _isConnected = false;
                    _connectedDeviceId = null;
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public async Task SetExposureAsync(double exposureMicroseconds, CancellationToken cancellationToken)
        {
            if (double.IsNaN(exposureMicroseconds) || double.IsInfinity(exposureMicroseconds) || exposureMicroseconds <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(exposureMicroseconds));

            ThrowIfDisposed();
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                EnsureConnected();
                cancellationToken.ThrowIfCancellationRequested();
                _exposureMicroseconds = exposureMicroseconds;
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public async Task<CameraFrame> CaptureAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                EnsureConnected();

                int delayMilliseconds = Math.Max(12, Math.Min(120, (int)Math.Ceiling(_exposureMicroseconds / 1000.0)));
                await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);

                long sequence = ++_sequence;
                Bitmap image = CreatePcbScene(sequence, _exposureMicroseconds, _simulateIntermittentFaults);
                return new CameraFrame(image, sequence, DateTime.UtcNow);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public void Dispose()
        {
            lock (_stateSync)
            {
                if (_disposed || _disposeRequested) return;
                _disposeRequested = true;
            }

            _operationGate.Wait();
            try
            {
                lock (_stateSync)
                {
                    _isConnected = false;
                    _connectedDeviceId = null;
                    _disposed = true;
                }
            }
            finally
            {
                _operationGate.Release();
                _operationGate.Dispose();
            }
        }

        private void EnsureConnected()
        {
            lock (_stateSync)
            {
                if (!_isConnected)
                    throw new InvalidOperationException("The mock camera is not connected.");
            }
        }

        private void ThrowIfDisposed()
        {
            lock (_stateSync)
            {
                if (_disposed || _disposeRequested)
                    throw new ObjectDisposedException(nameof(MockCameraDevice));
            }
        }

        private static Bitmap CreatePcbScene(long sequence, double exposureMicroseconds, bool simulateFaults)
        {
            const int width = 1600;
            const int height = 1000;
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            double exposureScale = Math.Max(0.55, Math.Min(1.45, exposureMicroseconds / 10000.0));

            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.Clear(ScaleColor(Color.FromArgb(27, 31, 34), exposureScale));

                using (Pen railPen = new Pen(ScaleColor(Color.FromArgb(77, 83, 87), exposureScale), 5.0f))
                using (Pen guidePen = new Pen(ScaleColor(Color.FromArgb(49, 55, 59), exposureScale), 2.0f))
                {
                    graphics.DrawLine(railPen, 0, 55, width, 55);
                    graphics.DrawLine(railPen, 0, height - 55, width, height - 55);
                    graphics.DrawLine(guidePen, 0, height / 2, width, height / 2);
                }

                float[] centersX = { 270.0f, 800.0f, 1330.0f };
                float[] centersY = { 270.0f, 730.0f };
                float[] angles = { -2.2f, 0.8f, 2.0f, 1.5f, -1.0f, 2.7f };
                int boardIndex = 0;
                for (int row = 0; row < centersY.Length; row++)
                {
                    for (int column = 0; column < centersX.Length; column++)
                    {
                        bool omitBoard = simulateFaults && sequence % 8 == 0 && boardIndex == 5;
                        if (!omitBoard)
                            DrawBoard(graphics, centersX[column], centersY[row], angles[boardIndex], boardIndex, exposureScale);
                        boardIndex++;
                    }
                }
            }

            return bitmap;
        }

        private static void DrawBoard(
            Graphics graphics,
            float centerX,
            float centerY,
            float angleDegrees,
            int boardIndex,
            double exposureScale)
        {
            GraphicsState state = graphics.Save();
            try
            {
                graphics.TranslateTransform(centerX, centerY);
                graphics.RotateTransform(angleDegrees);

                RectangleF board = new RectangleF(-205.0f, -155.0f, 410.0f, 310.0f);
                Color boardColor = boardIndex % 2 == 0
                    ? ScaleColor(Color.FromArgb(13, 42, 48), exposureScale)
                    : ScaleColor(Color.FromArgb(17, 43, 83), exposureScale);

                using (GraphicsPath boardPath = CreateRoundedRectangle(board, 8.0f))
                using (Brush boardBrush = new SolidBrush(boardColor))
                using (Pen edgePen = new Pen(ScaleColor(Color.FromArgb(105, 126, 128), exposureScale), 3.0f))
                {
                    graphics.FillPath(boardBrush, boardPath);
                    graphics.DrawPath(edgePen, boardPath);
                }

                using (Pen tracePen = new Pen(ScaleColor(Color.FromArgb(74, 118, 97), exposureScale), 3.0f))
                using (Brush padBrush = new SolidBrush(ScaleColor(Color.FromArgb(161, 137, 70), exposureScale)))
                using (Brush componentBrush = new SolidBrush(ScaleColor(Color.FromArgb(91, 101, 104), exposureScale)))
                using (Pen componentPen = new Pen(ScaleColor(Color.FromArgb(125, 137, 139), exposureScale), 1.0f))
                {
                    graphics.DrawLine(tracePen, -165, -90, 160, -90);
                    graphics.DrawLine(tracePen, -165, 88, 160, 88);
                    graphics.DrawLine(tracePen, -140, -90, -140, 88);
                    graphics.DrawLine(tracePen, 135, -90, 135, 88);

                    for (int x = -150; x <= 150; x += 50)
                    {
                        graphics.FillEllipse(padBrush, x - 7, -98, 14, 14);
                        graphics.FillEllipse(padBrush, x - 7, 82, 14, 14);
                    }

                    for (int row = 0; row < 3; row++)
                    {
                        for (int column = 0; column < 5; column++)
                        {
                            float x = -138 + column * 68;
                            float y = -55 + row * 48;
                            RectangleF component = new RectangleF(x, y, 35, 20);
                            graphics.FillRectangle(componentBrush, component);
                            graphics.DrawRectangle(componentPen, component.X, component.Y, component.Width, component.Height);
                        }
                    }
                }

                using (Brush fiducialBrush = new SolidBrush(ScaleColor(Color.FromArgb(173, 150, 80), exposureScale)))
                using (Pen fiducialPen = new Pen(ScaleColor(Color.FromArgb(95, 78, 40), exposureScale), 2.0f))
                {
                    PointF[] fiducials =
                    {
                        new PointF(-177, -127), new PointF(177, -127),
                        new PointF(-177, 127), new PointF(177, 127)
                    };
                    foreach (PointF point in fiducials)
                    {
                        graphics.FillEllipse(fiducialBrush, point.X - 10, point.Y - 10, 20, 20);
                        graphics.DrawEllipse(fiducialPen, point.X - 10, point.Y - 10, 20, 20);
                    }
                }

                if (boardIndex == 1 || boardIndex == 5)
                    DrawRectangularLabel(graphics, boardIndex == 1 ? -52.0f : 42.0f, 34.0f);

                if (boardIndex == 0 || boardIndex == 4)
                    DrawManualCross(graphics, boardIndex == 0 ? 82.0f : -75.0f, boardIndex == 0 ? 32.0f : -28.0f);

                if (boardIndex == 2)
                    DrawManualDot(graphics, 67.0f, -10.0f);
            }
            finally
            {
                graphics.Restore(state);
            }
        }

        private static void DrawRectangularLabel(Graphics graphics, float x, float y)
        {
            RectangleF label = new RectangleF(x - 34.0f, y - 18.0f, 68.0f, 36.0f);
            using (Brush brush = new SolidBrush(Color.FromArgb(232, 234, 229)))
            using (Pen pen = new Pen(Color.FromArgb(248, 249, 246), 2.0f))
            {
                graphics.FillRectangle(brush, label);
                graphics.DrawRectangle(pen, label.X, label.Y, label.Width, label.Height);
            }
        }

        private static void DrawManualCross(Graphics graphics, float x, float y)
        {
            using (Pen underlay = new Pen(Color.FromArgb(225, 225, 218), 13.0f))
            using (Pen highlight = new Pen(Color.White, 7.0f))
            {
                underlay.StartCap = LineCap.Round;
                underlay.EndCap = LineCap.Round;
                highlight.StartCap = LineCap.Round;
                highlight.EndCap = LineCap.Round;
                graphics.DrawLine(underlay, x - 23, y - 23, x + 23, y + 23);
                graphics.DrawLine(underlay, x + 23, y - 23, x - 23, y + 23);
                graphics.DrawLine(highlight, x - 23, y - 23, x + 23, y + 23);
                graphics.DrawLine(highlight, x + 23, y - 23, x - 23, y + 23);
            }
        }

        private static void DrawManualDot(Graphics graphics, float x, float y)
        {
            using (Brush outer = new SolidBrush(Color.FromArgb(225, 225, 218)))
            using (Brush inner = new SolidBrush(Color.White))
            {
                graphics.FillEllipse(outer, x - 15, y - 15, 30, 30);
                graphics.FillEllipse(inner, x - 9, y - 9, 18, 18);
            }
        }

        private static GraphicsPath CreateRoundedRectangle(RectangleF rectangle, float radius)
        {
            float diameter = radius * 2.0f;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color ScaleColor(Color color, double scale)
        {
            int red = Math.Max(0, Math.Min(255, (int)Math.Round(color.R * scale)));
            int green = Math.Max(0, Math.Min(255, (int)Math.Round(color.G * scale)));
            int blue = Math.Max(0, Math.Min(255, (int)Math.Round(color.B * scale)));
            return Color.FromArgb(color.A, red, green, blue);
        }
    }
}
