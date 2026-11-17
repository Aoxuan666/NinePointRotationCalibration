using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace NinePointRotationCalibration.Station.Controls
{
    /// <summary>视口中的十字点位叠加层配置。</summary>
    public sealed class ViewportPointOverlay
    {
        public ViewportPointOverlay()
        {
            Color = Color.LimeGreen;
            Radius = 5.0f;
            Label = string.Empty;
        }

        public PointF ImagePoint { get; set; }
        public string Label { get; set; }
        public Color Color { get; set; }
        public float Radius { get; set; }
    }

    /// <summary>视口中的可旋转矩形 ROI 叠加层配置。</summary>
    public sealed class ViewportRoiOverlay
    {
        public ViewportRoiOverlay()
        {
            Color = Color.DeepSkyBlue;
            Label = string.Empty;
            LineWidth = 2.0f;
        }

        public RectangleF ImageBounds { get; set; }
        public float AngleDegrees { get; set; }
        public string Label { get; set; }
        public Color Color { get; set; }
        public float LineWidth { get; set; }
    }

    /// <summary>视口中的轮廓多段线叠加层配置。</summary>
    public sealed class ViewportContourOverlay
    {
        public ViewportContourOverlay()
        {
            Points = new List<PointF>();
            Color = Color.LimeGreen;
            Label = string.Empty;
            LineWidth = 1.5f;
        }

        public List<PointF> Points { get; set; }
        public string Label { get; set; }
        public Color Color { get; set; }
        public float LineWidth { get; set; }
    }

    /// <summary>鼠标在图像区域内移动时的像素坐标事件参数。</summary>
    public sealed class ImagePositionEventArgs : EventArgs
    {
        public ImagePositionEventArgs(PointF imagePoint, bool isInsideImage)
        {
            ImagePoint = imagePoint;
            IsInsideImage = isInsideImage;
        }

        public PointF ImagePoint { get; private set; }
        public bool IsInsideImage { get; private set; }
    }

    /// <summary>支持双缓冲、帧合并显示和图像叠加层的图像视口控件。</summary>
    [DesignerCategory("Code")]
    public sealed class ImageViewport : Control
    {
        private const float MinimumZoom = 0.05f;
        private const float MaximumZoom = 32.0f;

        private readonly object _frameSync = new object();
        private readonly object _overlaySync = new object();
        private readonly List<ViewportPointOverlay> _points = new List<ViewportPointOverlay>();
        private readonly List<ViewportRoiOverlay> _rois = new List<ViewportRoiOverlay>();
        private readonly List<ViewportContourOverlay> _contours = new List<ViewportContourOverlay>();

        private Bitmap _displayedImage;
        private Bitmap _pendingImage;
        private bool _presentScheduled;
        private bool _previewCopyReserved;
        private bool _disposed;
        private bool _lastImageAvailability;
        private bool _fitMode = true;
        private float _zoom = 1.0f;
        private PointF _pan = PointF.Empty;
        private bool _isPanning;
        private Point _lastMousePoint;

        public ImageViewport()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable,
                true);

            BackColor = Color.Black;
            ForeColor = Color.FromArgb(220, 225, 230);
            TabStop = true;
            MinimumSize = new Size(320, 240);
        }

        public event EventHandler ViewChanged;
        public event EventHandler ImageAvailabilityChanged;
        public event EventHandler<ImagePositionEventArgs> ImagePositionChanged;

        [Browsable(false)]
        public bool HasImage
        {
            get { return _displayedImage != null; }
        }

        [Browsable(false)]
        public Size ImageSize
        {
            get { return _displayedImage == null ? Size.Empty : _displayedImage.Size; }
        }

        [Browsable(false)]
        public bool IsFitMode
        {
            get { return _fitMode; }
        }

        [Browsable(false)]
        public float EffectiveScale
        {
            get
            {
                float scale;
                PointF origin;
                return TryGetTransform(out scale, out origin) ? scale : 1.0f;
            }
        }

        /// <summary>
        /// 显示输入 Bitmap 的副本，输入对象仍由调用方负责释放。
        /// 待显示帧会合并，避免界面较慢时形成无限增长的帧队列。
        /// </summary>
        public void SetImage(Bitmap image)
        {
            if (image == null)
            {
                ClearImage();
                return;
            }

            Bitmap copy = CloneToDisplayFormat(image);
            QueueOwnedImage(copy);
        }

        /// <summary>
        /// 仅在界面没有等待显示的帧时排队预览图像。
        /// 在耗时的全分辨率转换之前先占用刷新名额，避免并发复制。
        /// </summary>
        public bool TrySetPreviewImage(Bitmap image)
        {
            if (image == null || _disposed) return false;

            lock (_frameSync)
            {
                if (_disposed || _previewCopyReserved || _pendingImage != null || _presentScheduled)
                    return false;
                _previewCopyReserved = true;
            }

            Bitmap copy = null;
            try
            {
                copy = CloneToDisplayFormat(image);
            }
            finally
            {
                lock (_frameSync) _previewCopyReserved = false;
            }

            QueueOwnedImage(copy);
            return true;
        }

        /// <summary>
        /// 不复制 Bitmap，直接交给控件显示；调用方不再拥有该对象。
        /// </summary>
        public void SetImageOwned(Bitmap image)
        {
            if (image == null)
            {
                ClearImage();
                return;
            }

            QueueOwnedImage(image);
        }

        /// <summary>获取当前显示图像的线程安全克隆副本（无图时返回 null）。</summary>
        public Bitmap Snapshot()
        {
            if (InvokeRequired)
            {
                return (Bitmap)Invoke(new Func<Bitmap>(Snapshot));
            }

            return _displayedImage == null ? null : new Bitmap(_displayedImage);
        }

        /// <summary>清空当前显示图像与待渲染帧缓存，并重置缩放平移状态。</summary>
        public void ClearImage()
        {
            if (_disposed) return;

            if (IsHandleCreated && InvokeRequired)
            {
                try
                {
                    BeginInvoke(new MethodInvoker(ClearImage));
                }
                catch (InvalidOperationException)
                {
                }
                return;
            }

            Bitmap pending;
            lock (_frameSync)
            {
                pending = _pendingImage;
                _pendingImage = null;
            }
            if (pending != null) pending.Dispose();

            Bitmap previous = Interlocked.Exchange(ref _displayedImage, null);
            if (previous != null) previous.Dispose();
            RaiseImageAvailabilityChanged(false);
            _fitMode = true;
            _zoom = 1.0f;
            _pan = PointF.Empty;
            RaiseViewChanged();
            Invalidate();
        }

        public void FitImage()
        {
            _fitMode = true;
            _zoom = 1.0f;
            _pan = PointF.Empty;
            RaiseViewChanged();
            Invalidate();
        }

        public void ShowActualPixels()
        {
            _fitMode = false;
            _zoom = 1.0f;
            _pan = PointF.Empty;
            RaiseViewChanged();
            Invalidate();
        }

        public void SetPointOverlays(IEnumerable<ViewportPointOverlay> points)
        {
            lock (_overlaySync)
            {
                _points.Clear();
                if (points != null)
                {
                    _points.AddRange(points.Where(item => item != null).Select(ClonePoint));
                }
            }
            InvalidateSafe();
        }

        public void SetRoiOverlays(IEnumerable<ViewportRoiOverlay> rois)
        {
            lock (_overlaySync)
            {
                _rois.Clear();
                if (rois != null)
                {
                    _rois.AddRange(rois.Where(item => item != null).Select(CloneRoi));
                }
            }
            InvalidateSafe();
        }

        public void SetContourOverlays(IEnumerable<ViewportContourOverlay> contours)
        {
            lock (_overlaySync)
            {
                _contours.Clear();
                if (contours != null)
                {
                    _contours.AddRange(contours
                        .Where(item => item != null)
                        .Select(CloneContour));
                }
            }
            InvalidateSafe();
        }

        public void ClearOverlays()
        {
            lock (_overlaySync)
            {
                _points.Clear();
                _rois.Clear();
                _contours.Clear();
            }
            InvalidateSafe();
        }

        /// <summary>将控件客户区坐标换算为图像像素坐标。</summary>
        public PointF ClientToImage(Point clientPoint)
        {
            float scale;
            PointF origin;
            if (!TryGetTransform(out scale, out origin)) return PointF.Empty;
            return new PointF((clientPoint.X - origin.X) / scale, (clientPoint.Y - origin.Y) / scale);
        }

        /// <summary>将图像像素坐标换算为控件客户区坐标。</summary>
        public PointF ImageToClient(PointF imagePoint)
        {
            float scale;
            PointF origin;
            if (!TryGetTransform(out scale, out origin)) return PointF.Empty;
            return new PointF(origin.X + imagePoint.X * scale, origin.Y + imagePoint.Y * scale);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SchedulePendingFrame();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(BackColor);

            Bitmap image = _displayedImage;
            if (image == null)
            {
                return;
            }

            float scale;
            PointF origin;
            if (!TryGetTransform(out scale, out origin)) return;

            RectangleF destination = new RectangleF(
                origin.X,
                origin.Y,
                image.Width * scale,
                image.Height * scale);

            e.Graphics.InterpolationMode = scale >= 1.0f
                ? InterpolationMode.NearestNeighbor
                : InterpolationMode.Bilinear;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(image, destination);

            DrawOverlays(e.Graphics, scale, origin);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Middle)
            {
                _isPanning = true;
                _lastMousePoint = e.Location;
                Capture = true;
                Cursor = Cursors.SizeAll;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_isPanning)
            {
                _pan = new PointF(
                    _pan.X + e.X - _lastMousePoint.X,
                    _pan.Y + e.Y - _lastMousePoint.Y);
                _lastMousePoint = e.Location;
                RaiseViewChanged();
                Invalidate();
            }

            PointF imagePoint = ClientToImage(e.Location);
            bool inside = _displayedImage != null &&
                          imagePoint.X >= 0.0f && imagePoint.Y >= 0.0f &&
                          imagePoint.X < _displayedImage.Width && imagePoint.Y < _displayedImage.Height;
            EventHandler<ImagePositionEventArgs> handler = ImagePositionChanged;
            if (handler != null) handler(this, new ImagePositionEventArgs(imagePoint, inside));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Middle)
            {
                _isPanning = false;
                Capture = false;
                Cursor = Cursors.Default;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_displayedImage == null || e.Delta == 0) return;

            float oldScale;
            PointF oldOrigin;
            if (!TryGetTransform(out oldScale, out oldOrigin)) return;

            // 以鼠标指针所在的图像像素点为缩放锚点，调整平移补偿以保持指针下图像位置不变
            PointF anchor = new PointF(
                (e.X - oldOrigin.X) / oldScale,
                (e.Y - oldOrigin.Y) / oldScale);

            float factor = e.Delta > 0 ? 1.15f : 1.0f / 1.15f;
            _zoom = Math.Max(MinimumZoom, Math.Min(MaximumZoom, _zoom * factor));

            float newScale = GetBaseScale() * _zoom;
            PointF centeredOrigin = GetCenteredOrigin(newScale);
            _pan = new PointF(
                e.X - anchor.X * newScale - centeredOrigin.X,
                e.Y - anchor.Y * newScale - centeredOrigin.Y);

            RaiseViewChanged();
            Invalidate();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture)
            {
                _isPanning = false;
                Cursor = Cursors.Default;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                Bitmap pending;
                lock (_frameSync)
                {
                    pending = _pendingImage;
                    _pendingImage = null;
                    _presentScheduled = false;
                }
                if (pending != null) pending.Dispose();

                Bitmap displayed = Interlocked.Exchange(ref _displayedImage, null);
                if (displayed != null) displayed.Dispose();
            }

            base.Dispose(disposing);
        }

        private static Bitmap CloneToDisplayFormat(Bitmap source)
        {
            Bitmap copy = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
            copy.SetResolution(
                source.HorizontalResolution > 0.0f ? source.HorizontalResolution : 96.0f,
                source.VerticalResolution > 0.0f ? source.VerticalResolution : 96.0f);
            using (Graphics graphics = Graphics.FromImage(copy))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(source, 0, 0);
            }
            return copy;
        }

        private void QueueOwnedImage(Bitmap image)
        {
            if (image == null) return;
            Bitmap replaced = null;
            bool shouldSchedule = false;

            lock (_frameSync)
            {
                if (_disposed)
                {
                    replaced = image;
                }
                else
                {
                    // 帧合并策略：丢弃尚未绘制的旧待显示帧并立即释放，仅保留最新帧
                    replaced = _pendingImage;
                    _pendingImage = image;
                    if (!_presentScheduled && IsHandleCreated)
                    {
                        _presentScheduled = true;
                        shouldSchedule = true;
                    }
                }
            }

            if (replaced != null) replaced.Dispose();
            if (shouldSchedule) PostPresentPendingFrame();
        }

        private void SchedulePendingFrame()
        {
            bool shouldSchedule = false;
            lock (_frameSync)
            {
                if (!_disposed && _pendingImage != null && !_presentScheduled && IsHandleCreated)
                {
                    _presentScheduled = true;
                    shouldSchedule = true;
                }
            }
            if (shouldSchedule) PostPresentPendingFrame();
        }

        private void PostPresentPendingFrame()
        {
            try
            {
                BeginInvoke(new MethodInvoker(PresentPendingFrame));
            }
            catch (ObjectDisposedException)
            {
                ReleaseScheduledFrame();
            }
            catch (InvalidOperationException)
            {
                ReleaseScheduledFrame();
            }
        }

        private void ReleaseScheduledFrame()
        {
            Bitmap pending;
            lock (_frameSync)
            {
                _presentScheduled = false;
                pending = _pendingImage;
                _pendingImage = null;
            }
            if (pending != null) pending.Dispose();
        }

        private void PresentPendingFrame()
        {
            Bitmap next;
            lock (_frameSync)
            {
                next = _pendingImage;
                _pendingImage = null;
                _presentScheduled = false;
            }

            if (_disposed)
            {
                if (next != null) next.Dispose();
                return;
            }

            if (next != null)
            {
                Size previousSize = _displayedImage == null ? Size.Empty : _displayedImage.Size;
                Bitmap previous = Interlocked.Exchange(ref _displayedImage, next);
                if (previous != null) previous.Dispose();
                RaiseImageAvailabilityChanged(true);
                if (previousSize != next.Size) FitImage();
                else Invalidate();
            }

            SchedulePendingFrame();
        }

        private bool TryGetTransform(out float scale, out PointF origin)
        {
            scale = 1.0f;
            origin = PointF.Empty;
            if (_displayedImage == null || ClientSize.Width <= 0 || ClientSize.Height <= 0) return false;

            scale = GetBaseScale() * _zoom;
            PointF centered = GetCenteredOrigin(scale);
            origin = new PointF(centered.X + _pan.X, centered.Y + _pan.Y);
            return true;
        }

        private float GetBaseScale()
        {
            if (_displayedImage == null || !_fitMode) return 1.0f;
            float widthScale = ClientSize.Width / (float)_displayedImage.Width;
            float heightScale = ClientSize.Height / (float)_displayedImage.Height;
            return Math.Max(MinimumZoom, Math.Min(widthScale, heightScale));
        }

        private PointF GetCenteredOrigin(float scale)
        {
            if (_displayedImage == null) return PointF.Empty;
            return new PointF(
                (ClientSize.Width - _displayedImage.Width * scale) / 2.0f,
                (ClientSize.Height - _displayedImage.Height * scale) / 2.0f);
        }

        private void DrawOverlays(Graphics graphics, float scale, PointF origin)
        {
            List<ViewportPointOverlay> points;
            List<ViewportRoiOverlay> rois;
            List<ViewportContourOverlay> contours;
            lock (_overlaySync)
            {
                // Set*Overlays 已经保存了防御性副本；这里仅复制列表引用，
                // 避免每次重绘都复制全部轮廓点。
                points = new List<ViewportPointOverlay>(_points);
                rois = new List<ViewportRoiOverlay>(_rois);
                contours = new List<ViewportContourOverlay>(_contours);
            }

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (ViewportRoiOverlay roi in rois)
            {
                DrawRoi(graphics, roi, scale, origin);
            }

            foreach (ViewportContourOverlay contour in contours)
            {
                DrawContour(graphics, contour, scale, origin);
            }

            foreach (ViewportPointOverlay point in points)
            {
                PointF center = Transform(point.ImagePoint, scale, origin);
                float radius = Math.Max(3.0f, point.Radius);
                using (Pen pen = new Pen(point.Color, 1.5f))
                using (SolidBrush brush = new SolidBrush(point.Color))
                {
                    graphics.DrawEllipse(pen, center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);
                    graphics.DrawLine(pen, center.X - radius - 4.0f, center.Y, center.X + radius + 4.0f, center.Y);
                    graphics.DrawLine(pen, center.X, center.Y - radius - 4.0f, center.X, center.Y + radius + 4.0f);
                    if (!string.IsNullOrWhiteSpace(point.Label))
                    {
                        graphics.DrawString(point.Label, Font, brush, center.X + radius + 5.0f, center.Y + 3.0f);
                    }
                }
            }
        }

        private void DrawRoi(Graphics graphics, ViewportRoiOverlay roi, float scale, PointF origin)
        {
            RectangleF bounds = roi.ImageBounds;
            PointF center = new PointF(bounds.Left + bounds.Width / 2.0f, bounds.Top + bounds.Height / 2.0f);
            PointF[] corners =
            {
                new PointF(bounds.Left, bounds.Top),
                new PointF(bounds.Right, bounds.Top),
                new PointF(bounds.Right, bounds.Bottom),
                new PointF(bounds.Left, bounds.Bottom)
            };

            // 绕 ROI 中心按角度旋转四角并变换至客户区坐标，支持旋转矩形绘制
            double radians = roi.AngleDegrees * Math.PI / 180.0;
            double cosine = Math.Cos(radians);
            double sine = Math.Sin(radians);
            for (int index = 0; index < corners.Length; index++)
            {
                float dx = corners[index].X - center.X;
                float dy = corners[index].Y - center.Y;
                PointF rotated = new PointF(
                    center.X + (float)(dx * cosine - dy * sine),
                    center.Y + (float)(dx * sine + dy * cosine));
                corners[index] = Transform(rotated, scale, origin);
            }

            using (Pen pen = new Pen(roi.Color, Math.Max(1.0f, roi.LineWidth)))
            using (SolidBrush brush = new SolidBrush(roi.Color))
            {
                graphics.DrawPolygon(pen, corners);
                if (!string.IsNullOrWhiteSpace(roi.Label))
                {
                    graphics.DrawString(roi.Label, Font, brush, corners[0].X + 4.0f, corners[0].Y + 4.0f);
                }
            }
        }

        private void DrawContour(
            Graphics graphics,
            ViewportContourOverlay contour,
            float scale,
            PointF origin)
        {
            if (contour == null || contour.Points == null || contour.Points.Count == 0) return;

            // 对超长轮廓按步长抽稀并剔除屏幕距离过近(小于 0.8px)的冗余点，限制单轮廓最多绘制约 5000 点
            List<PointF> display = new List<PointF>(Math.Min(contour.Points.Count, 5000));
            PointF previous = PointF.Empty;
            bool hasPrevious = false;
            int stride = Math.Max(1, (int)Math.Ceiling(contour.Points.Count / 5000.0));
            for (int index = 0; index < contour.Points.Count; index += stride)
            {
                PointF next = Transform(contour.Points[index], scale, origin);
                if (hasPrevious)
                {
                    float dx = next.X - previous.X;
                    float dy = next.Y - previous.Y;
                    if ((dx * dx) + (dy * dy) < 0.64f) continue;
                }
                display.Add(next);
                previous = next;
                hasPrevious = true;
            }

            if (display.Count == 1)
            {
                using (SolidBrush brush = new SolidBrush(contour.Color))
                    graphics.FillEllipse(brush, display[0].X - 1.5f, display[0].Y - 1.5f, 3.0f, 3.0f);
                return;
            }
            if (display.Count < 2) return;

            using (Pen pen = new Pen(contour.Color, Math.Max(1.0f, contour.LineWidth)))
            using (SolidBrush brush = new SolidBrush(contour.Color))
            {
                graphics.DrawLines(pen, display.ToArray());
                if (!string.IsNullOrWhiteSpace(contour.Label))
                    graphics.DrawString(contour.Label, Font, brush, display[0].X + 4.0f, display[0].Y + 4.0f);
            }
        }

        private static PointF Transform(PointF imagePoint, float scale, PointF origin)
        {
            return new PointF(origin.X + imagePoint.X * scale, origin.Y + imagePoint.Y * scale);
        }

        private static ViewportPointOverlay ClonePoint(ViewportPointOverlay source)
        {
            return new ViewportPointOverlay
            {
                ImagePoint = source.ImagePoint,
                Label = source.Label,
                Color = source.Color,
                Radius = source.Radius
            };
        }

        private static ViewportRoiOverlay CloneRoi(ViewportRoiOverlay source)
        {
            return new ViewportRoiOverlay
            {
                ImageBounds = source.ImageBounds,
                AngleDegrees = source.AngleDegrees,
                Label = source.Label,
                Color = source.Color,
                LineWidth = source.LineWidth
            };
        }

        private static ViewportContourOverlay CloneContour(ViewportContourOverlay source)
        {
            return new ViewportContourOverlay
            {
                Points = new List<PointF>(source.Points ?? new List<PointF>()),
                Label = source.Label,
                Color = source.Color,
                LineWidth = source.LineWidth
            };
        }

        private void InvalidateSafe()
        {
            if (_disposed) return;
            if (IsHandleCreated && InvokeRequired)
            {
                try
                {
                    BeginInvoke(new MethodInvoker(Invalidate));
                }
                catch (InvalidOperationException)
                {
                }
            }
            else
            {
                Invalidate();
            }
        }

        private void RaiseViewChanged()
        {
            EventHandler handler = ViewChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseImageAvailabilityChanged(bool available)
        {
            if (_lastImageAvailability == available) return;
            _lastImageAvailability = available;
            EventHandler handler = ImageAvailabilityChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }
}
