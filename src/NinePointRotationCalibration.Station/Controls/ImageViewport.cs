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
            LineWidth = 1.5f;
            CrossSize = 0.0f;
            LabelOffsetY = 0.0f;
            Label = string.Empty;
        }

        public PointF ImagePoint { get; set; }
        public string Label { get; set; }
        public Color Color { get; set; }
        public float Radius { get; set; }
        public float LineWidth { get; set; }
        public float CrossSize { get; set; }
        public float LabelOffsetY { get; set; }
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
        private readonly PointF[] _scratchRoiCorners = new PointF[4];
        private readonly List<PointF> _scratchContourPoints = new List<PointF>(256);

        private Bitmap _displayedImage;
        private Bitmap _displayedPreview;
        private bool _displayedIsPooled;
        private Bitmap _pendingImage;
        private Bitmap _pendingPreview;
        private bool _pendingIsPooled;
        private Bitmap _reusablePreviewBuffer;
        private Bitmap _reusableScaledPreviewBuffer;
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

            BackColor = Color.FromArgb(11, 15, 23);
            ForeColor = Color.FromArgb(226, 232, 240);
            TabStop = true;
            MinimumSize = new Size(160, 120);
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
        public bool IsPreviewFrameBusy
        {
            get
            {
                lock (_frameSync)
                {
                    return _disposed || _previewCopyReserved || _pendingImage != null || _presentScheduled;
                }
            }
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

            Bitmap copy = CloneToPArgbFast(image);
            Bitmap preview = CreateFastPreview(copy);
            QueueOwnedImage(copy, preview, false);
        }

        /// <summary>
        /// 仅在界面没有等待显示的帧时排队预览图像。
        /// 复用双缓冲 32bppPArgb 离屏位图及最长边 1600px 快速降采样预览帧（均在后台线程完成），
        /// 消除高像素实时流在 UI 线程直接缩放大图导致的卡顿与 LOH GC 抖动。
        /// </summary>
        public bool TrySetPreviewImage(Bitmap image)
        {
            if (image == null || _disposed) return false;

            Bitmap reusable = null;
            Bitmap reusableScaled = null;
            lock (_frameSync)
            {
                if (_disposed || _previewCopyReserved || _pendingImage != null || _presentScheduled)
                    return false;
                _previewCopyReserved = true;
                reusable = _reusablePreviewBuffer;
                _reusablePreviewBuffer = null;
                reusableScaled = _reusableScaledPreviewBuffer;
                _reusableScaledPreviewBuffer = null;
            }

            Bitmap target = null;
            Bitmap targetScaled = null;
            try
            {
                int width = image.Width;
                int height = image.Height;
                if (width <= 0 || height <= 0)
                {
                    if (reusable != null) reusable.Dispose();
                    if (reusableScaled != null) reusableScaled.Dispose();
                    return false;
                }

                if (reusable != null &&
                    reusable.Width == width &&
                    reusable.Height == height &&
                    reusable.PixelFormat == PixelFormat.Format32bppPArgb)
                {
                    target = reusable;
                    reusable = null;
                }
                else
                {
                    if (reusable != null)
                    {
                        reusable.Dispose();
                        reusable = null;
                    }
                    target = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                }

                CopyToPArgb(image, target);

                // 对超过 1600px 的高分辨率实时流在后台线程同步生成池化降采样缓冲，UI 线程绘制耗时 < 1.5ms
                int maxDim = Math.Max(width, height);
                if (maxDim > 1600)
                {
                    double ratio = 1600.0 / maxDim;
                    int pw = Math.Max(1, (int)Math.Round(width * ratio));
                    int ph = Math.Max(1, (int)Math.Round(height * ratio));
                    if (reusableScaled != null &&
                        reusableScaled.Width == pw &&
                        reusableScaled.Height == ph &&
                        reusableScaled.PixelFormat == PixelFormat.Format32bppPArgb)
                    {
                        targetScaled = reusableScaled;
                        reusableScaled = null;
                    }
                    else
                    {
                        if (reusableScaled != null)
                        {
                            reusableScaled.Dispose();
                            reusableScaled = null;
                        }
                        targetScaled = new Bitmap(pw, ph, PixelFormat.Format32bppPArgb);
                    }
                    DownsamplePArgbFast(target, targetScaled);
                }
                else if (reusableScaled != null)
                {
                    reusableScaled.Dispose();
                    reusableScaled = null;
                }
            }
            catch
            {
                if (reusable != null) reusable.Dispose();
                if (reusableScaled != null) reusableScaled.Dispose();
                if (target != null) target.Dispose();
                if (targetScaled != null) targetScaled.Dispose();
                lock (_frameSync) _previewCopyReserved = false;
                return false;
            }
            finally
            {
                lock (_frameSync) _previewCopyReserved = false;
            }

            QueueOwnedImage(target, targetScaled, true);
            return true;
        }

        /// <summary>
        /// 不复制 Bitmap，直接交给控件显示；调用方不再拥有该对象。
        /// </summary>
        public void SetImageOwned(Bitmap image)
        {
            SetImageOwned(image, null);
        }

        /// <summary>
        /// 直接接管主图及后台预生成的 1600px 预览图所有权，彻底消除大分辨率静态图首次绘制时的 UI 卡顿。
        /// </summary>
        public void SetImageOwned(Bitmap image, Bitmap prebuiltPreview)
        {
            if (image == null)
            {
                if (prebuiltPreview != null) prebuiltPreview.Dispose();
                ClearImage();
                return;
            }

            QueueOwnedImage(image, prebuiltPreview, false);
        }

        /// <summary>通过 LockBits 快速克隆任意常见格式位图为 DWM 加速的 32bppPArgb 位图。</summary>
        public static Bitmap CloneToPArgbFast(Bitmap source)
        {
            if (source == null) return null;
            Bitmap copy = new Bitmap(
                Math.Max(1, source.Width),
                Math.Max(1, source.Height),
                PixelFormat.Format32bppPArgb);
            copy.SetResolution(
                source.HorizontalResolution > 0.0f ? source.HorizontalResolution : 96.0f,
                source.VerticalResolution > 0.0f ? source.VerticalResolution : 96.0f);
            CopyToPArgb(source, copy);
            return copy;
        }

        /// <summary>在调用线程通过 unsafe LockBits 极速生成最长边不超过指定像素的 32bppPArgb 预览图（小图返回 null）。</summary>
        public static Bitmap CreateFastPreview(Bitmap source, int maximumDimension = 1600)
        {
            if (source == null) return null;
            int maxDim = Math.Max(source.Width, source.Height);
            if (maxDim <= maximumDimension) return null;
            double ratio = (double)maximumDimension / maxDim;
            int w = Math.Max(1, (int)Math.Round(source.Width * ratio));
            int h = Math.Max(1, (int)Math.Round(source.Height * ratio));
            Bitmap preview = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            try
            {
                DownsampleAnyToPArgbFast(source, preview);
                return preview;
            }
            catch
            {
                preview.Dispose();
                throw;
            }
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
            Bitmap pendingPreview;
            Bitmap reusable;
            Bitmap reusableScaled;
            Bitmap previous;
            Bitmap previousPreview;
            lock (_frameSync)
            {
                pending = _pendingImage;
                pendingPreview = _pendingPreview;
                _pendingImage = null;
                _pendingPreview = null;
                _pendingIsPooled = false;
                reusable = _reusablePreviewBuffer;
                _reusablePreviewBuffer = null;
                reusableScaled = _reusableScaledPreviewBuffer;
                _reusableScaledPreviewBuffer = null;
                previous = _displayedImage;
                previousPreview = _displayedPreview;
                _displayedImage = null;
                _displayedPreview = null;
                _displayedIsPooled = false;
            }
            if (pending != null) pending.Dispose();
            if (pendingPreview != null) pendingPreview.Dispose();
            if (reusable != null) reusable.Dispose();
            if (reusableScaled != null) reusableScaled.Dispose();
            if (previous != null) previous.Dispose();
            if (previousPreview != null) previousPreview.Dispose();
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

        /// <summary>同步对齐另一视口的缩放倍率与画布中心位置（不触发 ViewChanged 避免双向循环）。</summary>
        public void SyncViewFrom(ImageViewport source)
        {
            if (source == null || ReferenceEquals(source, this) || _disposed) return;

            float sourceScale;
            PointF sourceOrigin;
            if (!source.TryGetTransform(out sourceScale, out sourceOrigin)) return;
            if (_displayedImage == null || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

            PointF sourceCenterImage = new PointF(
                ((source.ClientSize.Width * 0.5f) - sourceOrigin.X) / sourceScale,
                ((source.ClientSize.Height * 0.5f) - sourceOrigin.Y) / sourceScale);

            _fitMode = source._fitMode;
            float baseScale = GetBaseScale();
            float targetScale = source._fitMode ? baseScale * source._zoom : sourceScale;
            float nextZoom = baseScale > 0.0f ? targetScale / baseScale : source._zoom;
            _zoom = Math.Max(MinimumZoom, Math.Min(MaximumZoom, nextZoom));

            float appliedScale = baseScale * _zoom;
            PointF centeredOrigin = GetCenteredOrigin(appliedScale);
            _pan = new PointF(
                (ClientSize.Width * 0.5f) - (sourceCenterImage.X * appliedScale) - centeredOrigin.X,
                (ClientSize.Height * 0.5f) - (sourceCenterImage.Y * appliedScale) - centeredOrigin.Y);
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
            try
            {
                e.Graphics.Clear(BackColor);

                Bitmap image = _displayedImage;
                if (image == null || _disposed)
                {
                    return;
                }

                float scale;
                PointF origin;
                if (!TryGetTransform(out scale, out origin)) return;

                // 无论实时采集流还是静态检测/调试图，在缩小时统一复用最长边 1600px 的 32bppPArgb 预降采样缓存，
                // 彻底消除高像素实时预览与静态图在 UI 线程缩放重绘引起的卡顿。
                Bitmap drawSource = image;
                if (Math.Max(image.Width, image.Height) > 1600)
                {
                    if (_displayedPreview == null && !_displayedIsPooled)
                    {
                        _displayedPreview = CreateFastPreview(image);
                    }
                    if (_displayedPreview != null)
                    {
                        float previewRatio = _displayedPreview.Width / (float)image.Width;
                        if (scale <= previewRatio * 1.02f)
                        {
                            drawSource = _displayedPreview;
                        }
                    }
                }

                float scaleX = drawSource.Width / (float)image.Width;
                float scaleY = drawSource.Height / (float)image.Height;
                float effScaleX = scale / scaleX;
                float effScaleY = scale / scaleY;

                // 仅裁取当前视口可见的源图子区域并保持稳定的双线性/最近邻插值与 Half 像素偏移，
                // 彻底消除缩放、平移以及高倍率放大时 GDI+ 坐标舍入与 Low 插值引起的图像抖动。
                int srcLeft = Math.Max(0, (int)Math.Floor((-origin.X) / effScaleX) - 1);
                int srcTop = Math.Max(0, (int)Math.Floor((-origin.Y) / effScaleY) - 1);
                int srcRight = Math.Min(drawSource.Width, (int)Math.Ceiling((ClientSize.Width - origin.X) / effScaleX) + 1);
                int srcBottom = Math.Min(drawSource.Height, (int)Math.Ceiling((ClientSize.Height - origin.Y) / effScaleY) + 1);
                if (srcRight > srcLeft && srcBottom > srcTop)
                {
                    RectangleF srcRect = new RectangleF(srcLeft, srcTop, srcRight - srcLeft, srcBottom - srcTop);
                    RectangleF dstRect = new RectangleF(
                        origin.X + (srcLeft * effScaleX),
                        origin.Y + (srcTop * effScaleY),
                        (srcRight - srcLeft) * effScaleX,
                        (srcBottom - srcTop) * effScaleY);

                    e.Graphics.InterpolationMode = scale >= 2.0f
                        ? InterpolationMode.NearestNeighbor
                        : InterpolationMode.Bilinear;
                    e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                    e.Graphics.CompositingMode = CompositingMode.SourceCopy;
                    e.Graphics.DrawImage(drawSource, dstRect, srcRect, GraphicsUnit.Pixel);
                    e.Graphics.CompositingMode = CompositingMode.SourceOver;
                }

                DrawOverlays(e.Graphics, scale, origin);
            }
            catch (ArgumentException)
            {
                // 窗体尺寸瞬变或图像释放交错时跳过单次重绘，避免 GDI+ 异常中断 UI 消息循环
            }
            catch (ObjectDisposedException)
            {
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (_displayedImage != null && (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Left))
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
            if (_isPanning && (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Left))
            {
                _isPanning = false;
                Capture = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            if (_displayedImage != null) FitImage();
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
                Bitmap pendingPreview;
                Bitmap reusable;
                Bitmap reusableScaled;
                Bitmap displayed;
                Bitmap displayedPreview;
                lock (_frameSync)
                {
                    pending = _pendingImage;
                    pendingPreview = _pendingPreview;
                    _pendingImage = null;
                    _pendingPreview = null;
                    _pendingIsPooled = false;
                    reusable = _reusablePreviewBuffer;
                    _reusablePreviewBuffer = null;
                    reusableScaled = _reusableScaledPreviewBuffer;
                    _reusableScaledPreviewBuffer = null;
                    displayed = _displayedImage;
                    displayedPreview = _displayedPreview;
                    _displayedImage = null;
                    _displayedPreview = null;
                    _displayedIsPooled = false;
                    _presentScheduled = false;
                }
                if (pending != null) pending.Dispose();
                if (pendingPreview != null) pendingPreview.Dispose();
                if (reusable != null) reusable.Dispose();
                if (reusableScaled != null) reusableScaled.Dispose();
                if (displayed != null) displayed.Dispose();
                if (displayedPreview != null) displayedPreview.Dispose();
            }

            base.Dispose(disposing);
        }

        // 2x2 盒式/快速步进降采样：32bppPArgb -> 32bppPArgb（用于池化实时流后台线程）
        private static unsafe void DownsamplePArgbFast(Bitmap source, Bitmap target)
        {
            int srcW = source.Width;
            int srcH = source.Height;
            int dstW = target.Width;
            int dstH = target.Height;
            BitmapData srcData = source.LockBits(new Rectangle(0, 0, srcW, srcH), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                BitmapData dstData = target.LockBits(new Rectangle(0, 0, dstW, dstH), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    byte* srcBase = (byte*)srcData.Scan0;
                    byte* dstBase = (byte*)dstData.Scan0;
                    int srcStride = srcData.Stride;
                    int dstStride = dstData.Stride;

                    // 预计算水平映射索引，消除内层循环浮点乘法与除法
                    int[] mapX = new int[dstW];
                    long stepX = ((long)srcW << 16) / dstW;
                    for (int x = 0; x < dstW; x++)
                    {
                        int sx = (int)(((long)x * stepX + (stepX >> 1)) >> 16);
                        mapX[x] = sx < srcW ? sx : srcW - 1;
                    }

                    long stepY = ((long)srcH << 16) / dstH;
                    for (int y = 0; y < dstH; y++)
                    {
                        int sy0 = (int)(((long)y * stepY) >> 16);
                        if (sy0 >= srcH) sy0 = srcH - 1;
                        int sy1 = sy0 + 1 < srcH ? sy0 + 1 : sy0;
                        uint* sRow0 = (uint*)(srcBase + (long)sy0 * srcStride);
                        uint* sRow1 = (uint*)(srcBase + (long)sy1 * srcStride);
                        uint* dRow = (uint*)(dstBase + (long)y * dstStride);
                        for (int x = 0; x < dstW; x++)
                        {
                            int sx0 = mapX[x];
                            int sx1 = sx0 + 1 < srcW ? sx0 + 1 : sx0;
                            uint p0 = sRow0[sx0];
                            uint p1 = sRow0[sx1];
                            uint p2 = sRow1[sx0];
                            uint p3 = sRow1[sx1];
                            // 2x2 均值滤波：兼顾抗锯齿清晰度与极速整数运算
                            uint rb = (((p0 & 0x00FF00FFu) + (p1 & 0x00FF00FFu) + (p2 & 0x00FF00FFu) + (p3 & 0x00FF00FFu)) >> 2) & 0x00FF00FFu;
                            uint g = (((p0 & 0x0000FF00u) + (p1 & 0x0000FF00u) + (p2 & 0x0000FF00u) + (p3 & 0x0000FF00u)) >> 2) & 0x0000FF00u;
                            dRow[x] = 0xFF000000u | rb | g;
                        }
                    }
                }
                finally
                {
                    target.UnlockBits(dstData);
                }
            }
            finally
            {
                source.UnlockBits(srcData);
            }
        }

        private static unsafe void DownsampleAnyToPArgbFast(Bitmap source, Bitmap target)
        {
            PixelFormat fmt = source.PixelFormat;
            if (fmt == PixelFormat.Format32bppPArgb || fmt == PixelFormat.Format32bppArgb || fmt == PixelFormat.Format32bppRgb)
            {
                DownsamplePArgbFast(source, target);
                return;
            }

            if (fmt == PixelFormat.Format8bppIndexed || fmt == PixelFormat.Format24bppRgb)
            {
                int srcW = source.Width;
                int srcH = source.Height;
                int dstW = target.Width;
                int dstH = target.Height;
                BitmapData srcData = source.LockBits(new Rectangle(0, 0, srcW, srcH), ImageLockMode.ReadOnly, fmt);
                try
                {
                    BitmapData dstData = target.LockBits(new Rectangle(0, 0, dstW, dstH), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    try
                    {
                        byte* srcBase = (byte*)srcData.Scan0;
                        byte* dstBase = (byte*)dstData.Scan0;
                        int srcStride = srcData.Stride;
                        int dstStride = dstData.Stride;
                        int[] mapX = new int[dstW];
                        long stepX = ((long)srcW << 16) / dstW;
                        for (int x = 0; x < dstW; x++)
                        {
                            int sx = (int)(((long)x * stepX + (stepX >> 1)) >> 16);
                            mapX[x] = sx < srcW ? sx : srcW - 1;
                        }
                        long stepY = ((long)srcH << 16) / dstH;
                        if (fmt == PixelFormat.Format8bppIndexed)
                        {
                            for (int y = 0; y < dstH; y++)
                            {
                                int sy = (int)(((long)y * stepY) >> 16);
                                if (sy >= srcH) sy = srcH - 1;
                                byte* sRow = srcBase + (long)sy * srcStride;
                                uint* dRow = (uint*)(dstBase + (long)y * dstStride);
                                for (int x = 0; x < dstW; x++)
                                {
                                    uint g = sRow[mapX[x]];
                                    dRow[x] = 0xFF000000u | (g << 16) | (g << 8) | g;
                                }
                            }
                        }
                        else
                        {
                            for (int y = 0; y < dstH; y++)
                            {
                                int sy = (int)(((long)y * stepY) >> 16);
                                if (sy >= srcH) sy = srcH - 1;
                                byte* sRow = srcBase + (long)sy * srcStride;
                                uint* dRow = (uint*)(dstBase + (long)y * dstStride);
                                for (int x = 0; x < dstW; x++)
                                {
                                    int off = mapX[x] * 3;
                                    dRow[x] = 0xFF000000u | ((uint)sRow[off + 2] << 16) | ((uint)sRow[off + 1] << 8) | sRow[off];
                                }
                            }
                        }
                    }
                    finally { target.UnlockBits(dstData); }
                }
                finally { source.UnlockBits(srcData); }
                return;
            }

            using (Graphics g = Graphics.FromImage(target))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(source, new Rectangle(0, 0, target.Width, target.Height), new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
            }
        }

        // 通过 LockBits 直接展开像素到 32bppPArgb（DWM/GDI+ 原生加速格式），消除实时预览 GDI+ 锁竞争与格式转换开销
        private static unsafe void CopyToPArgb(Bitmap source, Bitmap target)
        {
            int width = source.Width;
            int height = source.Height;
            PixelFormat srcFormat = source.PixelFormat;
            if (srcFormat == PixelFormat.Format32bppPArgb ||
                srcFormat == PixelFormat.Format32bppRgb ||
                srcFormat == PixelFormat.Format32bppArgb ||
                srcFormat == PixelFormat.Format24bppRgb ||
                srcFormat == PixelFormat.Format8bppIndexed)
            {
                Rectangle bounds = new Rectangle(0, 0, width, height);
                BitmapData srcData = source.LockBits(bounds, ImageLockMode.ReadOnly, srcFormat);
                try
                {
                    BitmapData dstData = target.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    try
                    {
                        byte* srcBase = (byte*)srcData.Scan0;
                        byte* dstBase = (byte*)dstData.Scan0;
                        int srcStride = srcData.Stride;
                        int dstStride = dstData.Stride;

                        if (srcFormat == PixelFormat.Format32bppPArgb || srcFormat == PixelFormat.Format32bppArgb)
                        {
                            int rowBytes = checked(width * 4);
                            for (int y = 0; y < height; y++)
                            {
                                Buffer.MemoryCopy(
                                    srcBase + (long)y * srcStride,
                                    dstBase + (long)y * dstStride,
                                    Math.Abs(dstStride),
                                    rowBytes);
                            }
                        }
                        else if (srcFormat == PixelFormat.Format32bppRgb)
                        {
                            for (int y = 0; y < height; y++)
                            {
                                uint* s = (uint*)(srcBase + (long)y * srcStride);
                                uint* d = (uint*)(dstBase + (long)y * dstStride);
                                for (int x = 0; x < width; x++)
                                {
                                    d[x] = s[x] | 0xFF000000u;
                                }
                            }
                        }
                        else if (srcFormat == PixelFormat.Format24bppRgb)
                        {
                            for (int y = 0; y < height; y++)
                            {
                                byte* s = srcBase + (long)y * srcStride;
                                uint* d = (uint*)(dstBase + (long)y * dstStride);
                                for (int x = 0; x < width; x++)
                                {
                                    int offset = x * 3;
                                    d[x] = 0xFF000000u |
                                           ((uint)s[offset + 2] << 16) |
                                           ((uint)s[offset + 1] << 8) |
                                           s[offset];
                                }
                            }
                        }
                        else
                        {
                            // Format8bppIndexed 工业灰度相机常见格式：快速扩展为 BGRA 0xFFgggggg
                            for (int y = 0; y < height; y++)
                            {
                                byte* s = srcBase + (long)y * srcStride;
                                uint* d = (uint*)(dstBase + (long)y * dstStride);
                                for (int x = 0; x < width; x++)
                                {
                                    uint g = s[x];
                                    d[x] = 0xFF000000u | (g << 16) | (g << 8) | g;
                                }
                            }
                        }
                    }
                    finally
                    {
                        target.UnlockBits(dstData);
                    }
                }
                finally
                {
                    source.UnlockBits(srcData);
                }
                return;
            }

            using (Graphics graphics = Graphics.FromImage(target))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(source, 0, 0);
            }
        }

        private void QueueOwnedImage(Bitmap image, Bitmap preview, bool pooledPreview)
        {
            if (image == null)
            {
                if (preview != null) preview.Dispose();
                return;
            }
            Bitmap replaced = null;
            Bitmap replacedPreview = null;
            bool shouldSchedule = false;

            lock (_frameSync)
            {
                if (_disposed)
                {
                    replaced = image;
                    replacedPreview = preview;
                }
                else
                {
                    // 帧合并策略：丢弃尚未绘制的旧待显示帧并回收或释放，仅保留最新帧
                    replaced = _pendingImage;
                    replacedPreview = _pendingPreview;
                    bool replacedPooled = _pendingIsPooled;
                    _pendingImage = image;
                    _pendingPreview = preview;
                    _pendingIsPooled = pooledPreview;
                    if (replaced != null && replacedPooled && _reusablePreviewBuffer == null)
                    {
                        _reusablePreviewBuffer = replaced;
                        replaced = null;
                    }
                    if (replacedPreview != null && replacedPooled && _reusableScaledPreviewBuffer == null)
                    {
                        _reusableScaledPreviewBuffer = replacedPreview;
                        replacedPreview = null;
                    }
                    if (!_presentScheduled && IsHandleCreated)
                    {
                        _presentScheduled = true;
                        shouldSchedule = true;
                    }
                }
            }

            if (replaced != null) replaced.Dispose();
            if (replacedPreview != null) replacedPreview.Dispose();
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
            Bitmap pendingPreview;
            lock (_frameSync)
            {
                _presentScheduled = false;
                pending = _pendingImage;
                pendingPreview = _pendingPreview;
                _pendingImage = null;
                _pendingPreview = null;
                _pendingIsPooled = false;
            }
            if (pending != null) pending.Dispose();
            if (pendingPreview != null) pendingPreview.Dispose();
        }

        private void PresentPendingFrame()
        {
            Bitmap next;
            Bitmap nextPreview;
            bool nextPooled;
            lock (_frameSync)
            {
                next = _pendingImage;
                nextPreview = _pendingPreview;
                nextPooled = _pendingIsPooled;
                _pendingImage = null;
                _pendingPreview = null;
                _pendingIsPooled = false;
                _presentScheduled = false;
            }

            if (_disposed)
            {
                if (next != null) next.Dispose();
                if (nextPreview != null) nextPreview.Dispose();
                return;
            }

            if (next != null)
            {
                Size previousSize = _displayedImage == null ? Size.Empty : _displayedImage.Size;
                Bitmap previous;
                Bitmap previousPreview;
                bool previousPooled;
                Bitmap toDispose = null;
                Bitmap toDisposePreview = null;
                lock (_frameSync)
                {
                    previous = _displayedImage;
                    previousPreview = _displayedPreview;
                    previousPooled = _displayedIsPooled;
                    _displayedImage = next;
                    _displayedPreview = nextPreview;
                    _displayedIsPooled = nextPooled;
                    // 将上一帧已显示完毕的池化预览主图与降采样图归还至复用槽，实现零堆分配连续渲染
                    if (previous != null)
                    {
                        if (previousPooled && _reusablePreviewBuffer == null &&
                            previous.Width == next.Width && previous.Height == next.Height &&
                            previous.PixelFormat == PixelFormat.Format32bppPArgb)
                        {
                            _reusablePreviewBuffer = previous;
                        }
                        else
                        {
                            toDispose = previous;
                        }
                    }
                    if (previousPreview != null)
                    {
                        if (previousPooled && _reusableScaledPreviewBuffer == null &&
                            nextPreview != null &&
                            previousPreview.Width == nextPreview.Width &&
                            previousPreview.Height == nextPreview.Height &&
                            previousPreview.PixelFormat == PixelFormat.Format32bppPArgb)
                        {
                            _reusableScaledPreviewBuffer = previousPreview;
                        }
                        else
                        {
                            toDisposePreview = previousPreview;
                        }
                    }
                }
                if (toDispose != null) toDispose.Dispose();
                if (toDisposePreview != null) toDisposePreview.Dispose();
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

            if (rois.Count == 0 && contours.Count == 0 && points.Count == 0) return;

            graphics.SmoothingMode = _isPanning ? SmoothingMode.None : SmoothingMode.AntiAlias;
            // 单帧内统一按颜色/线宽复用 Pen 与 SolidBrush，避免 ROI、轮廓、十字点位反复创建释放 GDI+ 句柄
            Dictionary<long, Pen> penCache = new Dictionary<long, Pen>();
            Dictionary<int, SolidBrush> brushCache = new Dictionary<int, SolidBrush>();
            try
            {
                for (int i = 0; i < rois.Count; i++)
                {
                    DrawRoi(graphics, rois[i], scale, origin, penCache, brushCache);
                }

                for (int i = 0; i < contours.Count; i++)
                {
                    DrawContour(graphics, contours[i], scale, origin, penCache, brushCache);
                }

                for (int i = 0; i < points.Count; i++)
                {
                    ViewportPointOverlay point = points[i];
                    PointF center = Transform(point.ImagePoint, scale, origin);
                    float radius = Math.Max(3.0f, point.Radius);
                    float arm = point.CrossSize > 0.0f ? point.CrossSize : radius + 4.0f;
                    float lineWidth = point.LineWidth > 0.0f ? point.LineWidth : 1.5f;
                    if (lineWidth >= 1.8f)
                    {
                        Pen shadowPen = GetOrAddPen(penCache, Color.FromArgb(180, 10, 14, 22), lineWidth + 1.4f);
                        graphics.DrawLine(shadowPen, center.X - arm, center.Y, center.X + arm, center.Y);
                        graphics.DrawLine(shadowPen, center.X, center.Y - arm, center.X, center.Y + arm);
                    }
                    Pen pen = GetOrAddPen(penCache, point.Color, lineWidth);
                    SolidBrush brush = GetOrAddBrush(brushCache, point.Color);
                    graphics.DrawEllipse(pen, center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);
                    graphics.DrawLine(pen, center.X - arm, center.Y, center.X + arm, center.Y);
                    graphics.DrawLine(pen, center.X, center.Y - arm, center.X, center.Y + arm);
                    if (!string.IsNullOrWhiteSpace(point.Label))
                    {
                        float labelX = center.X + radius + 5.0f;
                        float labelY = center.Y - 8.0f + point.LabelOffsetY;
                        SizeF labelSize = graphics.MeasureString(point.Label, Font);
                        SolidBrush bgBrush = GetOrAddBrush(brushCache, Color.FromArgb(185, 15, 23, 42));
                        graphics.FillRectangle(bgBrush, labelX - 2.0f, labelY - 1.0f, labelSize.Width + 4.0f, labelSize.Height + 1.0f);
                        graphics.DrawString(point.Label, Font, brush, labelX, labelY);
                    }
                }
            }
            finally
            {
                foreach (Pen pen in penCache.Values) pen.Dispose();
                foreach (SolidBrush brush in brushCache.Values) brush.Dispose();
            }
        }

        private static Pen GetOrAddPen(Dictionary<long, Pen> cache, Color color, float lineWidth)
        {
            float width = Math.Max(1.0f, lineWidth);
            long key = (((long)color.ToArgb()) << 20) | (uint)(int)Math.Round(width * 100.0f);
            Pen pen;
            if (!cache.TryGetValue(key, out pen))
            {
                pen = new Pen(color, width);
                cache[key] = pen;
            }
            return pen;
        }

        private static SolidBrush GetOrAddBrush(Dictionary<int, SolidBrush> cache, Color color)
        {
            int key = color.ToArgb();
            SolidBrush brush;
            if (!cache.TryGetValue(key, out brush))
            {
                brush = new SolidBrush(color);
                cache[key] = brush;
            }
            return brush;
        }

        private void DrawRoi(
            Graphics graphics,
            ViewportRoiOverlay roi,
            float scale,
            PointF origin,
            Dictionary<long, Pen> penCache,
            Dictionary<int, SolidBrush> brushCache)
        {
            RectangleF bounds = roi.ImageBounds;
            PointF center = new PointF(bounds.Left + bounds.Width / 2.0f, bounds.Top + bounds.Height / 2.0f);
            _scratchRoiCorners[0] = new PointF(bounds.Left, bounds.Top);
            _scratchRoiCorners[1] = new PointF(bounds.Right, bounds.Top);
            _scratchRoiCorners[2] = new PointF(bounds.Right, bounds.Bottom);
            _scratchRoiCorners[3] = new PointF(bounds.Left, bounds.Bottom);

            // 绕 ROI 中心按角度旋转四角并变换至客户区坐标，支持旋转矩形绘制
            double radians = roi.AngleDegrees * Math.PI / 180.0;
            double cosine = Math.Cos(radians);
            double sine = Math.Sin(radians);
            for (int index = 0; index < 4; index++)
            {
                float dx = _scratchRoiCorners[index].X - center.X;
                float dy = _scratchRoiCorners[index].Y - center.Y;
                PointF rotated = new PointF(
                    center.X + (float)(dx * cosine - dy * sine),
                    center.Y + (float)(dx * sine + dy * cosine));
                _scratchRoiCorners[index] = Transform(rotated, scale, origin);
            }

            Pen pen = GetOrAddPen(penCache, roi.Color, roi.LineWidth);
            graphics.DrawPolygon(pen, _scratchRoiCorners);
            if (!string.IsNullOrWhiteSpace(roi.Label))
            {
                SolidBrush brush = GetOrAddBrush(brushCache, roi.Color);
                graphics.DrawString(roi.Label, Font, brush, _scratchRoiCorners[0].X + 4.0f, _scratchRoiCorners[0].Y + 4.0f);
            }
        }

        private void DrawContour(
            Graphics graphics,
            ViewportContourOverlay contour,
            float scale,
            PointF origin,
            Dictionary<long, Pen> penCache,
            Dictionary<int, SolidBrush> brushCache)
        {
            if (contour == null || contour.Points == null || contour.Points.Count == 0) return;

            float maxX = ClientSize.Width + 8.0f;
            float maxY = ClientSize.Height + 8.0f;
            bool anyVisible = false;

            // 复用缓冲列表并按步长抽稀剔除屏幕距离过近(小于 0.8px)的冗余点，限制单轮廓最多绘制约 5000 点；
            // 短折线（如 9+5 标定点位连线）保留全部顶点不做距离剔除，防止缩放时漏绘连线。
            _scratchContourPoints.Clear();
            PointF previous = PointF.Empty;
            bool hasPrevious = false;
            bool decimate = contour.Points.Count > 64;
            int stride = Math.Max(1, (int)Math.Ceiling(contour.Points.Count / 5000.0));
            for (int index = 0; index < contour.Points.Count; index += stride)
            {
                PointF next = Transform(contour.Points[index], scale, origin);
                if (!anyVisible && next.X >= -8.0f && next.X <= maxX && next.Y >= -8.0f && next.Y <= maxY)
                {
                    anyVisible = true;
                }
                if (decimate && hasPrevious)
                {
                    float dx = next.X - previous.X;
                    float dy = next.Y - previous.Y;
                    if ((dx * dx) + (dy * dy) < 0.64f) continue;
                }
                _scratchContourPoints.Add(next);
                previous = next;
                hasPrevious = true;
            }
            if (!anyVisible) return;

            SolidBrush brush = GetOrAddBrush(brushCache, contour.Color);
            if (_scratchContourPoints.Count == 1)
            {
                graphics.FillEllipse(brush, _scratchContourPoints[0].X - 1.5f, _scratchContourPoints[0].Y - 1.5f, 3.0f, 3.0f);
                return;
            }
            if (_scratchContourPoints.Count < 2) return;

            Pen pen = GetOrAddPen(penCache, contour.Color, contour.LineWidth);
            graphics.DrawLines(pen, _scratchContourPoints.ToArray());
            if (!string.IsNullOrWhiteSpace(contour.Label))
                graphics.DrawString(contour.Label, Font, brush, _scratchContourPoints[0].X + 4.0f, _scratchContourPoints[0].Y + 4.0f);
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
                Radius = source.Radius,
                LineWidth = source.LineWidth,
                CrossSize = source.CrossSize,
                LabelOffsetY = source.LabelOffsetY
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
