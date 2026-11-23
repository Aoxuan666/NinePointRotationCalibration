using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;
using NinePointRotationCalibration.WinForms.Geometry;
using NinePointRotationCalibration.WinForms.Models;

namespace NinePointRotationCalibration.WinForms.Controls
{
    /// <summary>
    /// 支持图像平移缩放、旋转 ROI 编辑、基准中心微调、掩膜涂抹及结果叠加绘制的交互画布控件。
    /// </summary>
    [DefaultEvent("RoiChanged")]
    public sealed class CalibrationCanvas : Control
    {
        private const double MinimumZoom = 0.02;
        private const double MaximumZoom = 50.0;
        private const float HandleRadius = 5.0f;
        private const float HandleHitRadius = 9.0f;
        private const float BorderHitDistance = 7.0f;
        private const float CoincidentBorderTolerance = 2.0f;
        private const float AnchorHitRadius = 9.0f;
        private const float RotateStemLength = 22.0f;
        private const float DragThresholdPixels = 3.0f;
        private const float CycleClickRadiusPixels = 10.0f;
        private const float CycleBreakMovePixels = 16.0f;

        // 重叠 ROI 本体点击测试的默认前景优先级顺序
        private static readonly CanvasRoiTarget[] RoiBodyHitOrder =
        {
            CanvasRoiTarget.Template,
            CanvasRoiTarget.Detection,
            CanvasRoiTarget.Search
        };

        private readonly List<MaskStroke> _maskStrokes = new List<MaskStroke>();
        private Bitmap _maskHistory;
        private Bitmap _maskLive;
        private int _paintedStrokes;
        private int _paintedLivePoints;
        private MaskStroke _liveStrokeOwner;
        private Rectangle _lastLiveShapeBounds;
        private bool _liveBaseSynced;
        private double _maskZoom, _maskX, _maskY;
        private readonly Timer _maskRefreshTimer = new Timer { Interval = 90 };
        private bool _maskRefreshPending;
        private Rectangle _lastActiveMaskRect;
        private Rectangle _lastBrushCursorRect;
        private double _brushRadiusImage = 12.0;
        private Bitmap _image;
        private Bitmap _interactivePreview;
        // 避免编辑 ROI 或平移时在每次鼠标移动重采样千万像素源图，按当前缩放率缓存一张降采样位图复用。
        private Bitmap _scaledImage;
        private int _scaledImageWidth;
        private int _scaledImageHeight;
        private double _scaledImageZoom = double.NaN;
        private double _zoom = 1.0;
        private double _offsetX;
        private double _offsetY;
        private bool _autoFit = true;
        private bool _viewInitialized;
        private RotatedRectangle _templateRoi;
        private RotatedRectangle _searchRoi;
        private RotatedRectangle _detectionRoi;
        private ImageCoordinate _anchor;
        private bool _anchorVisible = true;
        private CanvasEditTool _tool = CanvasEditTool.Select;
        private CanvasRoiTarget _selectedRoi = CanvasRoiTarget.Template;
        private CanvasRuntimeOverlay _editorOverlay = new CanvasRuntimeOverlay();
        private CanvasRuntimeOverlay _runtimeOverlay = new CanvasRuntimeOverlay();

        private DragOperation _dragOperation;
        private Point _dragStartScreen;
        private Point _lastScreen;
        private ImageCoordinate _dragStartImage;
        private CanvasRoiTarget _selectionAtMouseDown;
        private RotatedRectangle _dragStartRoi;
        private double _dragStartPointerAngleDegrees;
        private ImageCoordinate _dragStartAnchor;
        private ImageCoordinate _dragStartAnchorForTemplate;
        private RoiHandle _activeHandle;
        private MaskStroke _activeStroke;
        private readonly List<ImageCoordinate> _activePolygonPoints = new List<ImageCoordinate>();
        private bool _polygonDrawing;
        private bool _transactionChanged;
        private bool _dragThresholdExceeded;
        private bool _anchorSelected;
        private bool _anchorHovered;
        private CanvasRoiTarget _hoveredRoi = CanvasRoiTarget.None;
        private CanvasRoiTarget _hoveredLabelRoi = CanvasRoiTarget.None;
        private bool _hoveredAnchorLabel;
        private Point _hoverScreen;
        private bool _hasHoverScreen;
        private CanvasRoiTarget _creatingRoiTarget = CanvasRoiTarget.None;
        private RotatedRectangle _creatingRoiDraft;
        private bool _hasCycleAnchor;
        private Point _cycleAnchorScreen;
        private CanvasHitKind _dragStartHitKind = CanvasHitKind.None;
        private bool _lastRightClickConsumed;
        private readonly Dictionary<CanvasRoiTarget, RectangleF> _renderedRoiLabelRects = new Dictionary<CanvasRoiTarget, RectangleF>();
        private RectangleF _renderedAnchorLabelRect;

        public CalibrationCanvas()
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
            MinimumSize = new Size(240, 180);
            _maskRefreshTimer.Tick += (sender, args) =>
            {
                _maskRefreshTimer.Stop();
                if (_maskRefreshPending)
                {
                    _maskRefreshPending = false;
                    Invalidate();
                }
            };
        }

        public event EventHandler<RoiChangedEventArgs> RoiChanged;

        public event EventHandler<CanvasRoiTarget> RoiDrawRejected;

        public event EventHandler<AnchorChangedEventArgs> AnchorChanged;

        public event EventHandler<MaskStrokeEventArgs> MaskStrokeCreated;

        public event EventHandler<MaskPreviewEventArgs> MaskPreviewChanged;

        public event EventHandler BrushRadiusChangedByCanvas;

        public event EventHandler EditTransactionStarted;

        public event EventHandler SelectedRoiChanged;

        public event EventHandler ToolChanged;

        public event EventHandler ViewChanged;

        [Browsable(false)]
        public bool HasImage
        {
            get { return _image != null; }
        }

        [Browsable(false)]
        public bool IsInteracting
        {
            get { return _dragOperation != DragOperation.None || _polygonDrawing; }
        }

        [Browsable(false)]
        public bool LastRightClickConsumed
        {
            get { return _lastRightClickConsumed; }
        }

        [Browsable(false)]
        public Size ImageSize
        {
            get { return _image == null ? Size.Empty : _image.Size; }
        }

        [Browsable(false)]
        public double Zoom
        {
            get { return _zoom; }
        }

        [DefaultValue(CanvasEditTool.Select)]
        public CanvasEditTool Tool
        {
            get { return _tool; }
            set
            {
                if (_tool == value)
                {
                    return;
                }

                FinishDrag(false);
                CancelActivePolygon(false);
                _hasCycleAnchor = false;
                _hoveredRoi = CanvasRoiTarget.None;
                _hoveredLabelRoi = CanvasRoiTarget.None;
                _hoveredAnchorLabel = false;
                _anchorHovered = false;
                _tool = value;
                if (_tool == CanvasEditTool.MoveAnchor)
                {
                    _anchorSelected = true;
                    if (_selectedRoi != CanvasRoiTarget.None)
                    {
                        _selectedRoi = CanvasRoiTarget.None;
                        OnSelectedRoiChanged();
                    }
                }
                else if (IsDrawRoiTool(_tool))
                {
                    _anchorSelected = false;
                }
                _lastBrushCursorRect = _hasHoverScreen && (_tool == CanvasEditTool.EraseMask || _tool == CanvasEditTool.RestoreMask)
                    ? GetBrushCursorDirtyRect(_hoverScreen)
                    : Rectangle.Empty;
                Cursor = GetDefaultCursor();
                OnToolChanged();
                Invalidate();
            }
        }

        [DefaultValue(CanvasRoiTarget.Template)]
        public CanvasRoiTarget SelectedRoi
        {
            get { return _selectedRoi; }
            set
            {
                if (value != CanvasRoiTarget.None)
                {
                    _anchorSelected = false;
                }
                if (_selectedRoi == value)
                {
                    Invalidate();
                    return;
                }

                _selectedRoi = value;
                OnSelectedRoiChanged();
                Invalidate();
            }
        }

        [Browsable(false)]
        public bool IsAnchorSelected
        {
            get { return _anchorSelected; }
        }

        [Browsable(false)]
        public RotatedRectangle TemplateRoi
        {
            get { return _templateRoi == null ? null : _templateRoi.DeepClone(); }
            set
            {
                _templateRoi = value == null ? null : value.DeepClone();
                Invalidate();
            }
        }

        [Browsable(false)]
        public RotatedRectangle SearchRoi
        {
            get { return _searchRoi == null ? null : _searchRoi.DeepClone(); }
            set
            {
                _searchRoi = value == null ? null : value.DeepClone();
                Invalidate();
            }
        }

        [Browsable(false)]
        public RotatedRectangle DetectionRoi
        {
            get { return _detectionRoi == null ? null : _detectionRoi.DeepClone(); }
            set
            {
                _detectionRoi = value == null ? null : value.DeepClone();
                Invalidate();
            }
        }

        [Browsable(false)]
        public ImageCoordinate ReferenceAnchor
        {
            get { return _anchor; }
            set
            {
                _anchor = ClampToImage(value);
                Invalidate();
            }
        }

        [DefaultValue(true)]
        public bool AnchorVisible
        {
            get { return _anchorVisible; }
            set
            {
                _anchorVisible = value;
                Invalidate();
            }
        }

        [DefaultValue(false)]
        public bool AnchorLocked { get; set; }

        [DefaultValue(false)]
        public bool TemplateRoiLocked { get; set; }

        [DefaultValue(false)]
        public bool SearchRoiLocked { get; set; }

        [DefaultValue(true)]
        public bool MaskVisible { get; set; } = true;

        [DefaultValue(true)]
        public bool ShowRois { get; set; } = true;

        [DefaultValue(12.0)]
        public double BrushRadiusImage
        {
            get { return _brushRadiusImage; }
            set
            {
                double clamped = Clamp(value, 1.0, 500.0);
                if (Math.Abs(_brushRadiusImage - clamped) < 0.000001)
                {
                    return;
                }

                Rectangle previousCursor = _lastBrushCursorRect;
                _brushRadiusImage = clamped;
                if (_hasHoverScreen && (_tool == CanvasEditTool.EraseMask || _tool == CanvasEditTool.RestoreMask))
                {
                    Rectangle nextCursor = GetBrushCursorDirtyRect(_hoverScreen);
                    _lastBrushCursorRect = nextCursor;
                    Invalidate(previousCursor.IsEmpty ? nextCursor : Rectangle.Union(previousCursor, nextCursor));
                }
                else if (!previousCursor.IsEmpty)
                {
                    _lastBrushCursorRect = Rectangle.Empty;
                    Invalidate(previousCursor);
                }
            }
        }

        [Browsable(false)]
        public CanvasRuntimeOverlay EditorOverlay
        {
            get { return _editorOverlay; }
            set
            {
                _editorOverlay = value ?? new CanvasRuntimeOverlay();
                Invalidate();
            }
        }

        [Browsable(false)]
        public CanvasRuntimeOverlay RuntimeOverlay
        {
            get { return _runtimeOverlay; }
            set
            {
                _runtimeOverlay = value ?? new CanvasRuntimeOverlay();
                Invalidate();
            }
        }

        /// <summary>
        /// 设置当前显示图像（内部持有深拷贝）并适应窗口显示；默认不自动创建模板/搜索 ROI。
        /// </summary>
        public void SetImage(Bitmap image)
        {
            SetImageInternal(image == null ? null : CloneToFastPArgb(image));
        }

        /// <summary>
        /// 直接接管外部创建的图像所有权（不再额外拷贝 20MP 位图）并适应窗口显示。
        /// </summary>
        public void SetImageOwned(Bitmap ownedImage)
        {
            SetImageInternal(ownedImage);
        }

        private void SetImageInternal(Bitmap replacement)
        {
            Bitmap previous = _image;
            InvalidateImageCache();
            if (_interactivePreview != null)
            {
                _interactivePreview.Dispose();
                _interactivePreview = null;
            }
            _image = replacement;
            if (previous != null)
            {
                previous.Dispose();
            }

            _interactivePreview = CreateInteractivePreview(_image);

            _viewInitialized = false;
            if (_image != null && _templateRoi != null &&
                (!_anchorVisible || (_anchor.Row == 0.0 && _anchor.Column == 0.0)))
            {
                _anchor = new ImageCoordinate(_templateRoi.CenterRow, _templateRoi.CenterColumn);
            }

            FitImage();
        }

        private static Bitmap CloneToFastPArgb(Bitmap source)
        {
            Bitmap copy = new Bitmap(Math.Max(1, source.Width), Math.Max(1, source.Height), PixelFormat.Format32bppPArgb);
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

        /// <summary>
        /// 返回当前图像的一份独立副本。
        /// </summary>
        public Bitmap CopyImage()
        {
            return _image == null ? null : new Bitmap(_image);
        }

        /// <summary>
        /// 清空当前图像与缩放缓存。
        /// </summary>
        public void ClearImage()
        {
            SetImage(null);
        }

        /// <summary>
        /// 自适应缩放并居中显示整幅图像。
        /// </summary>
        public void FitImage()
        {
            if (_image == null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                _viewInitialized = false;
                Invalidate();
                return;
            }

            const double padding = 12.0;
            double availableWidth = Math.Max(1.0, ClientSize.Width - (padding * 2.0));
            double availableHeight = Math.Max(1.0, ClientSize.Height - (padding * 2.0));
            _zoom = Math.Min(availableWidth / _image.Width, availableHeight / _image.Height);
            _zoom = Clamp(_zoom, MinimumZoom, MaximumZoom);
            InvalidateImageCache();
            _offsetX = (ClientSize.Width - (_image.Width * _zoom)) / 2.0;
            _offsetY = (ClientSize.Height - (_image.Height * _zoom)) / 2.0;
            _autoFit = true;
            _viewInitialized = true;
            _hasCycleAnchor = false;
            _lastBrushCursorRect = Rectangle.Empty;
            if (_maskRefreshPending)
            {
                _maskRefreshTimer.Stop();
                _maskRefreshPending = false;
            }
            OnViewChanged();
            Invalidate();
        }

        /// <summary>
        /// 按 1:1 原始像素比例居中显示图像。
        /// </summary>
        public void ShowOneToOne()
        {
            if (_image == null)
            {
                return;
            }

            _zoom = 1.0;
            InvalidateImageCache();
            _offsetX = (ClientSize.Width - _image.Width) / 2.0;
            _offsetY = (ClientSize.Height - _image.Height) / 2.0;
            _autoFit = false;
            _viewInitialized = true;
            _hasCycleAnchor = false;
            _lastBrushCursorRect = Rectangle.Empty;
            if (_maskRefreshPending)
            {
                _maskRefreshTimer.Stop();
                _maskRefreshPending = false;
            }
            OnViewChanged();
            Invalidate();
        }

        /// <summary>
        /// 替换画布中显示的掩膜笔画列表。
        /// </summary>
        public void SetMaskStrokes(IEnumerable<MaskStroke> strokes)
        {
            ClearMaskCache();
            _maskStrokes.Clear();
            if (strokes != null)
            {
                _maskStrokes.AddRange(strokes.Where(item => item != null).Select(item => item.DeepClone()));
            }

            Invalidate();
        }

        /// <summary>
        /// 获取当前画布掩膜笔画列表的深拷贝。
        /// </summary>
        public IList<MaskStroke> GetMaskStrokes()
        {
            return _maskStrokes.Select(item => item.DeepClone()).ToList();
        }

        /// <summary>
        /// 命中测试客户端坐标处的最上层掩膜项索引；未命中返回 -1。
        /// </summary>
        public int HitTestMaskIndex(Point clientLocation)
        {
            if (_image == null || _maskStrokes.Count == 0) return -1;
            ImageCoordinate imagePoint = ClientToImage(clientLocation);
            for (int i = _maskStrokes.Count - 1; i >= 0; i--)
            {
                if (_maskStrokes[i] != null && _maskStrokes[i].IsValid && MaskGeometry.IsPointInsideMask(imagePoint, _maskStrokes[i]))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// 获取客户端坐标处命中的所有 ROI（支持标签、边框及内部命中），按编辑优先级排序（未锁定优先、小框优先）。
        /// </summary>
        public IList<CanvasRoiTarget> GetRoisAtClientPoint(Point clientLocation)
        {
            List<CanvasRoiTarget> hits = new List<CanvasRoiTarget>();
            if (_image == null || !ShowRois)
            {
                return hits;
            }

            ImageCoordinate imagePoint = ClientToImage(clientLocation);
            double borderDistSq = BorderHitDistance * BorderHitDistance;
            foreach (CanvasRoiTarget target in RoiBodyHitOrder)
            {
                RotatedRectangle candidate = GetRoi(target);
                if (candidate == null)
                {
                    continue;
                }

                RectangleF labelRect;
                bool hitLabel = _renderedRoiLabelRects.TryGetValue(target, out labelRect) &&
                                labelRect.Contains(clientLocation);
                if (hitLabel ||
                    candidate.Contains(imagePoint) ||
                    GetDistanceSquaredToBorder(candidate, clientLocation) <= borderDistSq)
                {
                    hits.Add(target);
                }
            }

            hits.Sort((a, b) => CompareRoiBodyPriority(a, b));
            return hits;
        }

        /// <summary>
        /// 清除运行时叠加图层（定位结果、样本点与 HUD）。
        /// </summary>
        public void ClearRuntimeOverlay()
        {
            RuntimeOverlay = new CanvasRuntimeOverlay();
        }

        /// <summary>
        /// 将图像坐标（Row/Column）变换为控件客户区屏幕坐标。
        /// </summary>
        public PointF ImageToClient(ImageCoordinate point)
        {
            EnsureViewInitialized();
            return new PointF(
                (float)(_offsetX + (point.Column * _zoom)),
                (float)(_offsetY + (point.Row * _zoom)));
        }

        /// <summary>
        /// 将控件客户区屏幕坐标反算为图像坐标（Row/Column）。
        /// </summary>
        public ImageCoordinate ClientToImage(PointF point)
        {
            EnsureViewInitialized();
            if (_zoom <= 0.0)
            {
                return new ImageCoordinate();
            }

            return new ImageCoordinate(
                (point.Y - _offsetY) / _zoom,
                (point.X - _offsetX) / _zoom);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _maskRefreshTimer.Stop();
                _maskRefreshTimer.Dispose();
                ClearMaskCache();
                InvalidateImageCache();
                if (_image != null)
                {
                    _image.Dispose();
                    _image = null;
                }
                if (_interactivePreview != null)
                {
                    _interactivePreview.Dispose();
                    _interactivePreview = null;
                }
            }

            base.Dispose(disposing);
        }

        protected override void OnResize(EventArgs eventArgs)
        {
            base.OnResize(eventArgs);
            if (_autoFit && _image != null)
            {
                FitImage();
            }
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            Graphics graphics = eventArgs.Graphics;
            graphics.Clear(BackColor);
            // 始终锁定一致的 PixelOffsetMode.Half，避免鼠标按下/松开、画笔涂抹或缩放结束时出现 0.5px 图像抖动
            graphics.SmoothingMode = _dragOperation != DragOperation.None ? SmoothingMode.None : SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;

            if (_image == null)
            {
                DrawEmptyState(graphics);
                return;
            }

            if (!_viewInitialized)
            {
                // 绘制阶段直接初始化视图参数，避免在 OnPaint 内触发 ViewChanged 或重入 Invalidate。
                EnsureViewInitialized();
            }

            DrawImageLayer(graphics);
            // 交互拖拽与缩放期间全程绘制高效缓冲特征点叠加层，保证轮廓跟随丝滑不闪退
            DrawOverlayLayer(graphics, _editorOverlay, false);
            if (MaskVisible)
            {
                DrawMaskLayer(graphics);
                DrawActiveMaskOverlay(graphics);
            }
            if (_hasHoverScreen && (_tool == CanvasEditTool.EraseMask || _tool == CanvasEditTool.RestoreMask))
            {
                DrawBrushCursor(graphics);
            }

            if (ShowRois)
            {
                DrawEditingLayer(graphics);
                DrawCreatingRoiDraft(graphics);
            }
            else
            {
                _renderedRoiLabelRects.Clear();
                _renderedAnchorLabelRect = RectangleF.Empty;
            }

            DrawOverlayLayer(graphics, _runtimeOverlay, true);
            DrawToolHintBanner(graphics);
            DrawViewStatus(graphics);
        }

        protected override void OnMouseWheel(MouseEventArgs eventArgs)
        {
            base.OnMouseWheel(eventArgs);
            if (_image == null || eventArgs.Delta == 0)
            {
                return;
            }

            // 画笔模式下按住 Ctrl + 滚轮快捷调整画笔半径
            if ((ModifierKeys & Keys.Control) == Keys.Control &&
                (_tool == CanvasEditTool.EraseMask || _tool == CanvasEditTool.RestoreMask))
            {
                double step = Math.Max(1.0, Math.Round(BrushRadiusImage * 0.15));
                BrushRadiusImage = Clamp(BrushRadiusImage + (eventArgs.Delta > 0 ? step : -step), 1.0, 500.0);
                EventHandler handler = BrushRadiusChangedByCanvas;
                if (handler != null) handler(this, EventArgs.Empty);
                Invalidate();
                return;
            }

            Focus();
            // 以鼠标指针所在的图像像素为锚点缩放，并启用 120ms 防抖延迟恢复高质量渲染
            ImageCoordinate anchor = ClientToImage(eventArgs.Location);
            double factor = eventArgs.Delta > 0 ? 1.15 : (1.0 / 1.15);
            double nextZoom = Clamp(_zoom * factor, MinimumZoom, MaximumZoom);
            if (Math.Abs(nextZoom - _zoom) < 0.000001)
            {
                return;
            }

            _zoom = nextZoom;
            _offsetX = eventArgs.X - (anchor.Column * _zoom);
            _offsetY = eventArgs.Y - (anchor.Row * _zoom);
            _autoFit = false;
            _viewInitialized = true;
            _hasCycleAnchor = false;
            _lastBrushCursorRect = Rectangle.Empty;
            if (_maskHistory != null && _maskStrokes.Count > 0)
            {
                _maskRefreshPending = true;
                _maskRefreshTimer.Stop();
                _maskRefreshTimer.Start();
            }
            OnViewChanged();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs eventArgs)
        {
            base.OnMouseDown(eventArgs);
            Focus();
            _hoverScreen = eventArgs.Location;
            _hasHoverScreen = true;
            _lastScreen = eventArgs.Location;
            _dragStartScreen = eventArgs.Location;
            _dragStartImage = ClientToImage(eventArgs.Location);
            _selectionAtMouseDown = _selectedRoi;
            _transactionChanged = false;
            _dragThresholdExceeded = false;

            if (eventArgs.Button == MouseButtons.Middle)
            {
                _dragOperation = DragOperation.Pan;
                Capture = true;
                Cursor = Cursors.Hand;
                return;
            }

            if (eventArgs.Button == MouseButtons.Right)
            {
                _lastRightClickConsumed = false;
                if (_dragOperation == DragOperation.CreateRoi)
                {
                    FinishDrag(false);
                    Tool = CanvasEditTool.Select;
                    _lastRightClickConsumed = true;
                    return;
                }
                if (IsDrawRoiTool(_tool) || _tool == CanvasEditTool.MoveAnchor)
                {
                    Tool = CanvasEditTool.Select;
                    _lastRightClickConsumed = true;
                    return;
                }
                if (_polygonDrawing)
                {
                    if (_activePolygonPoints.Count >= 3)
                    {
                        CommitActivePolygon();
                    }
                    else
                    {
                        CancelActivePolygon(true);
                    }
                    _lastRightClickConsumed = true;
                }
                return;
            }

            if (eventArgs.Button != MouseButtons.Left || _image == null)
            {
                return;
            }

            if (_tool == CanvasEditTool.EraseMask || _tool == CanvasEditTool.RestoreMask)
            {
                if (!IsInsideImage(_dragStartImage))
                {
                    return;
                }

                if (_maskRefreshPending)
                {
                    _maskRefreshTimer.Stop();
                    _maskRefreshPending = false;
                }

                ImageCoordinate startPt = ClampToImage(_dragStartImage);
                BeginEditTransaction();
                _activeStroke = new MaskStroke
                {
                    Kind = MaskShapeKind.Brush,
                    IsErase = _tool == CanvasEditTool.EraseMask,
                    Radius = Math.Max(1.0, BrushRadiusImage)
                };
                _activeStroke.Points.Add(startPt);
                _dragOperation = DragOperation.Mask;
                Capture = true;
                _transactionChanged = true;
                _lastBrushCursorRect = GetBrushCursorDirtyRect(eventArgs.Location);
                OnMaskPreviewChanged(_activeStroke, _activeStroke.IsErase, startPt, startPt);
                Invalidate();
                return;
            }

            if (_tool == CanvasEditTool.MaskRectangle || _tool == CanvasEditTool.MaskCircle)
            {
                if (!IsInsideImage(_dragStartImage))
                {
                    return;
                }

                if (_maskRefreshPending)
                {
                    _maskRefreshTimer.Stop();
                    _maskRefreshPending = false;
                }

                ImageCoordinate startPt = ClampToImage(_dragStartImage);
                BeginEditTransaction();
                if (_tool == CanvasEditTool.MaskRectangle)
                {
                    _activeStroke = new MaskStroke
                    {
                        Kind = MaskShapeKind.Rectangle,
                        IsErase = true,
                        Rectangle = new RotatedRectangle(startPt.Row, startPt.Column, 1.0, 1.0, 0.0)
                    };
                    _dragOperation = DragOperation.MaskRect;
                }
                else
                {
                    _activeStroke = new MaskStroke
                    {
                        Kind = MaskShapeKind.Circle,
                        IsErase = true,
                        Center = startPt,
                        Radius = 1.0
                    };
                    _dragOperation = DragOperation.MaskCircle;
                }
                Capture = true;
                _transactionChanged = false;
                _lastActiveMaskRect = Rectangle.Empty;
                _lastBrushCursorRect = Rectangle.Empty;
                Invalidate();
                return;
            }

            if (_tool == CanvasEditTool.MaskPolygon)
            {
                if (!IsInsideImage(_dragStartImage))
                {
                    return;
                }

                if (_maskRefreshPending)
                {
                    _maskRefreshTimer.Stop();
                    _maskRefreshPending = false;
                }

                ImageCoordinate pt = ClampToImage(_dragStartImage);
                if (_polygonDrawing && _activePolygonPoints.Count >= 3)
                {
                    PointF firstClient = ImageToClient(_activePolygonPoints[0]);
                    double dx = eventArgs.X - firstClient.X;
                    double dy = eventArgs.Y - firstClient.Y;
                    if ((dx * dx) + (dy * dy) <= 100.0)
                    {
                        CommitActivePolygon();
                        return;
                    }
                }

                if (!_polygonDrawing)
                {
                    _polygonDrawing = true;
                    _activePolygonPoints.Clear();
                }
                _activePolygonPoints.Add(pt);
                UpdatePolygonPreviewMask();
                Invalidate();
                return;
            }

            if (IsDrawRoiTool(_tool))
            {
                CanvasRoiTarget target = GetDrawToolTarget(_tool);
                if (IsTargetLocked(target))
                {
                    return;
                }

                // 允许从图像边缘附近起笔（例如紧贴边界框选全图搜索 ROI），自动钳位到图像范围内；
                // 按下时不立即覆盖旧 ROI 或生成默认尺寸框，待拖拽形成有效区域后再提交。
                _dragStartImage = ClampToImage(_dragStartImage);
                _creatingRoiTarget = target;
                _creatingRoiDraft = null;
                _hasCycleAnchor = false;
                _dragStartHitKind = CanvasHitKind.None;
                _dragOperation = DragOperation.CreateRoi;
                _dragThresholdExceeded = false;
                _transactionChanged = false;
                Capture = true;
                Invalidate();
                return;
            }

            if (_tool == CanvasEditTool.MoveAnchor)
            {
                if (HasVisibleAnchor && !AnchorLocked && IsInsideImage(_dragStartImage))
                {
                    _anchorSelected = true;
                    _hasCycleAnchor = false;
                    _dragStartHitKind = CanvasHitKind.Anchor;
                    SelectedRoi = CanvasRoiTarget.None;
                    if (HitAnchor(eventArgs.Location))
                    {
                        _dragStartAnchor = _anchor;
                        _dragOperation = DragOperation.Anchor;
                        _dragThresholdExceeded = false;
                    }
                    else
                    {
                        BeginEditTransaction();
                        _anchor = ClampToImage(_dragStartImage);
                        _dragStartAnchor = _anchor;
                        _dragOperation = DragOperation.Anchor;
                        _dragThresholdExceeded = true;
                        _transactionChanged = true;
                        OnAnchorChanged(false);
                        Invalidate();
                    }
                    Capture = true;
                    Cursor = Cursors.SizeAll;
                }
                return;
            }

            // Select 模式统一命中测试：与悬停预览完全同源（控制柄 > 标签 > 已选中锚点 > 最近边框 > 未遮挡锚点 > 内部区域）
            InteractiveHit hit = HitTestInteractive(eventArgs.Location);
            _dragStartHitKind = hit.Kind;
            if (hit.Kind == CanvasHitKind.RoiHandle)
            {
                _anchorSelected = false;
                _hasCycleAnchor = false;
                StartRoiEditDrag(new RoiHit(hit.Target, hit.Handle));
                return;
            }

            if (hit.Kind == CanvasHitKind.AnchorLabel || hit.Kind == CanvasHitKind.Anchor)
            {
                _anchorSelected = true;
                _hasCycleAnchor = false;
                if (_selectedRoi != CanvasRoiTarget.None)
                {
                    _selectedRoi = CanvasRoiTarget.None;
                    OnSelectedRoiChanged();
                }
                _dragStartAnchor = _anchor;
                _dragOperation = DragOperation.Anchor;
                _dragThresholdExceeded = false;
                Capture = true;
                Cursor = Cursors.SizeAll;
                Invalidate();
                return;
            }

            if (hit.Kind == CanvasHitKind.RoiLabel ||
                hit.Kind == CanvasHitKind.RoiBorder ||
                hit.Kind == CanvasHitKind.RoiBody)
            {
                _anchorSelected = false;
                SelectedRoi = hit.Target;
                if (hit.Kind == CanvasHitKind.RoiLabel)
                {
                    _hasCycleAnchor = false;
                }
                else
                {
                    _cycleAnchorScreen = eventArgs.Location;
                    _hasCycleAnchor = true;
                }

                if (IsTargetLocked(hit.Target))
                {
                    _activeHandle = RoiHandle.Body;
                    _dragOperation = DragOperation.SelectRoi;
                    _dragThresholdExceeded = false;
                    Capture = true;
                    Cursor = Cursors.Default;
                    Invalidate();
                    return;
                }

                StartRoiEditDrag(new RoiHit(hit.Target, RoiHandle.Body));
                return;
            }

            _anchorSelected = false;
            _hasCycleAnchor = false;
            SelectedRoi = CanvasRoiTarget.None;
            if (_tool == CanvasEditTool.Select)
            {
                _dragOperation = DragOperation.Pan;
                Capture = true;
                Cursor = Cursors.Hand;
            }
        }

        protected override void OnDoubleClick(EventArgs eventArgs)
        {
            base.OnDoubleClick(eventArgs);
            if (_polygonDrawing)
            {
                if (_activePolygonPoints.Count >= 3)
                {
                    CommitActivePolygon();
                }
                return;
            }

            if (_image == null || _tool != CanvasEditTool.Select)
            {
                return;
            }

            Point client = PointToClient(Cursor.Position);
            if (HitTestInteractive(client).Kind == CanvasHitKind.None)
            {
                FitImage();
            }
        }

        protected override void OnMouseMove(MouseEventArgs eventArgs)
        {
            base.OnMouseMove(eventArgs);
            _hoverScreen = eventArgs.Location;
            _hasHoverScreen = true;

            if (_dragOperation == DragOperation.None)
            {
                if (_hasCycleAnchor && !IsNearPoint(eventArgs.Location, _cycleAnchorScreen, CycleBreakMovePixels))
                {
                    _hasCycleAnchor = false;
                }
                UpdateHoverAndCursor(eventArgs.Location);
                if (_polygonDrawing)
                {
                    Invalidate();
                }
                else if (_tool == CanvasEditTool.EraseMask || _tool == CanvasEditTool.RestoreMask)
                {
                    // 画笔悬停仅重绘上一帧与当前帧画笔圆圈的极小脏区，彻底消除整屏高频重绘卡顿
                    Rectangle nextRect = GetBrushCursorDirtyRect(eventArgs.Location);
                    Rectangle dirty = _lastBrushCursorRect.IsEmpty ? nextRect : Rectangle.Union(_lastBrushCursorRect, nextRect);
                    _lastBrushCursorRect = nextRect;
                    Invalidate(dirty);
                }
                return;
            }

            if (_dragOperation == DragOperation.SelectRoi)
            {
                // 点击锁定 ROI 仅用于选中；若按住拖拽超过阈值则无缝切换为平移视图，避免锁定全图搜索框阻塞左键平移
                if (!_dragThresholdExceeded && ExceededDragThreshold(eventArgs.Location))
                {
                    _dragThresholdExceeded = true;
                    _hasCycleAnchor = false;
                    _dragOperation = DragOperation.Pan;
                    Cursor = Cursors.Hand;
                }
                else
                {
                    _lastScreen = eventArgs.Location;
                    return;
                }
            }

            if (_dragOperation == DragOperation.Pan)
            {
                _offsetX += eventArgs.X - _lastScreen.X;
                _offsetY += eventArgs.Y - _lastScreen.Y;
                _lastScreen = eventArgs.Location;
                _autoFit = false;
                _viewInitialized = true;
                _hasCycleAnchor = false;
                _lastBrushCursorRect = Rectangle.Empty;
                if (_maskHistory != null && _maskStrokes.Count > 0)
                {
                    _maskRefreshPending = true;
                    _maskRefreshTimer.Stop();
                    _maskRefreshTimer.Start();
                }
                OnViewChanged();
                Invalidate();
                return;
            }

            ImageCoordinate rawCurrent = ClientToImage(eventArgs.Location);
            ImageCoordinate current = ClampToImage(rawCurrent);
            switch (_dragOperation)
            {
                case DragOperation.CreateRoi:
                    if (!_dragThresholdExceeded)
                    {
                        if (!ExceededDragThreshold(eventArgs.Location))
                        {
                            _lastScreen = eventArgs.Location;
                            return;
                        }
                        _dragThresholdExceeded = true;
                    }
                    UpdateCreatedRoi(current);
                    break;
                case DragOperation.MoveRoi:
                    if (!_dragThresholdExceeded)
                    {
                        if (!ExceededDragThreshold(eventArgs.Location))
                        {
                            _lastScreen = eventArgs.Location;
                            return;
                        }
                        _dragThresholdExceeded = true;
                        _hasCycleAnchor = false;
                        BeginEditTransaction();
                    }
                    UpdateMovedRoi(current);
                    break;
                case DragOperation.ResizeRoi:
                    _hasCycleAnchor = false;
                    UpdateResizedRoi(current);
                    break;
                case DragOperation.RotateRoi:
                    _hasCycleAnchor = false;
                    UpdateRotatedRoi(rawCurrent);
                    break;
                case DragOperation.Anchor:
                    if (!_dragThresholdExceeded)
                    {
                        if (!ExceededDragThreshold(eventArgs.Location))
                        {
                            _lastScreen = eventArgs.Location;
                            return;
                        }
                        _dragThresholdExceeded = true;
                        _hasCycleAnchor = false;
                        BeginEditTransaction();
                    }
                    _anchor = ClampToImage(new ImageCoordinate(
                        _dragStartAnchor.Row + (current.Row - _dragStartImage.Row),
                        _dragStartAnchor.Column + (current.Column - _dragStartImage.Column)));
                    _transactionChanged = true;
                    OnAnchorChanged(false);
                    Invalidate();
                    break;
                case DragOperation.Mask:
                    Rectangle nextCursorRect = GetBrushCursorDirtyRect(eventArgs.Location);
                    Rectangle cursorDirty = _lastBrushCursorRect.IsEmpty
                        ? nextCursorRect
                        : Rectangle.Union(_lastBrushCursorRect, nextCursorRect);
                    _lastBrushCursorRect = nextCursorRect;
                    AppendStrokePoint(current, cursorDirty);
                    break;
                case DragOperation.MaskRect:
                    UpdateMaskRectangle(current);
                    break;
                case DragOperation.MaskCircle:
                    UpdateMaskCircle(current);
                    break;
            }

            _lastScreen = eventArgs.Location;
        }

        protected override void OnMouseLeave(EventArgs eventArgs)
        {
            base.OnMouseLeave(eventArgs);
            _hasHoverScreen = false;
            if (_hoveredRoi != CanvasRoiTarget.None ||
                _hoveredLabelRoi != CanvasRoiTarget.None ||
                _hoveredAnchorLabel ||
                _anchorHovered ||
                _polygonDrawing)
            {
                _hoveredRoi = CanvasRoiTarget.None;
                _hoveredLabelRoi = CanvasRoiTarget.None;
                _hoveredAnchorLabel = false;
                _anchorHovered = false;
                _lastBrushCursorRect = Rectangle.Empty;
                Invalidate();
            }
            else if (!_lastBrushCursorRect.IsEmpty)
            {
                Rectangle dirty = _lastBrushCursorRect;
                _lastBrushCursorRect = Rectangle.Empty;
                Invalidate(dirty);
            }
        }

        protected override void OnMouseUp(MouseEventArgs eventArgs)
        {
            base.OnMouseUp(eventArgs);
            if (eventArgs.Button == MouseButtons.Left || eventArgs.Button == MouseButtons.Middle)
            {
                FinishDrag(true);
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs eventArgs)
        {
            base.OnMouseCaptureChanged(eventArgs);
            if (!Capture && _dragOperation != DragOperation.None)
            {
                FinishDrag(true);
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys keyCode = keyData & Keys.KeyCode;
            if (keyCode == Keys.Up || keyCode == Keys.Down || keyCode == Keys.Left || keyCode == Keys.Right ||
                keyCode == Keys.Escape)
            {
                return true;
            }

            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs eventArgs)
        {
            base.OnKeyDown(eventArgs);
            if (_image == null)
            {
                return;
            }

            if (eventArgs.KeyCode == Keys.Escape)
            {
                if (_dragOperation == DragOperation.CreateRoi)
                {
                    FinishDrag(false);
                    Tool = CanvasEditTool.Select;
                    eventArgs.Handled = true;
                    return;
                }
                if (_polygonDrawing)
                {
                    CancelActivePolygon(true);
                    eventArgs.Handled = true;
                    return;
                }
                if (IsDrawRoiTool(_tool) || _tool == CanvasEditTool.MoveAnchor)
                {
                    Tool = CanvasEditTool.Select;
                    eventArgs.Handled = true;
                    return;
                }
            }

            if (_polygonDrawing)
            {
                if (eventArgs.KeyCode == Keys.Back && _activePolygonPoints.Count > 0)
                {
                    _activePolygonPoints.RemoveAt(_activePolygonPoints.Count - 1);
                    if (_activePolygonPoints.Count == 0) _polygonDrawing = false;
                    UpdatePolygonPreviewMask();
                    Invalidate();
                    eventArgs.Handled = true;
                    return;
                }
                if (eventArgs.KeyCode == Keys.Enter && _activePolygonPoints.Count >= 3)
                {
                    CommitActivePolygon();
                    eventArgs.Handled = true;
                    return;
                }
            }

            if ((_tool == CanvasEditTool.EraseMask || _tool == CanvasEditTool.RestoreMask) &&
                (eventArgs.KeyCode == Keys.OemOpenBrackets || eventArgs.KeyCode == Keys.OemCloseBrackets))
            {
                double brushStep = Math.Max(1.0, Math.Round(BrushRadiusImage * 0.15));
                BrushRadiusImage = Clamp(BrushRadiusImage + (eventArgs.KeyCode == Keys.OemCloseBrackets ? brushStep : -brushStep), 1.0, 500.0);
                EventHandler handler = BrushRadiusChangedByCanvas;
                if (handler != null) handler(this, EventArgs.Empty);
                Invalidate();
                eventArgs.Handled = true;
                return;
            }

            if (eventArgs.KeyCode == Keys.F)
            {
                FitImage();
                eventArgs.Handled = true;
                return;
            }

            if (eventArgs.KeyCode == Keys.D1 || eventArgs.KeyCode == Keys.NumPad1)
            {
                ShowOneToOne();
                eventArgs.Handled = true;
                return;
            }

            if (eventArgs.KeyCode == Keys.Delete || eventArgs.KeyCode == Keys.Back)
            {
                if (DeleteSelectedRoi())
                {
                    eventArgs.Handled = true;
                }
                return;
            }

            int rowDirection = eventArgs.KeyCode == Keys.Up ? -1 : eventArgs.KeyCode == Keys.Down ? 1 : 0;
            int columnDirection = eventArgs.KeyCode == Keys.Left ? -1 : eventArgs.KeyCode == Keys.Right ? 1 : 0;
            if (rowDirection == 0 && columnDirection == 0)
            {
                return;
            }

            double step = eventArgs.Shift ? 10.0 : 1.0;
            if ((_tool == CanvasEditTool.MoveAnchor || _anchorSelected || _selectedRoi == CanvasRoiTarget.None) &&
                HasVisibleAnchor && !AnchorLocked)
            {
                BeginEditTransaction();
                _anchor = ClampToImage(new ImageCoordinate(
                    _anchor.Row + (rowDirection * step),
                    _anchor.Column + (columnDirection * step)));
                OnAnchorChanged(true);
                Invalidate();
                eventArgs.Handled = true;
                return;
            }

            RotatedRectangle roi = GetRoi(_selectedRoi);
            if (roi == null || IsTargetLocked(_selectedRoi))
            {
                return;
            }

            BeginEditTransaction();
            double prevRow = roi.CenterRow;
            double prevCol = roi.CenterColumn;
            double prevAngle = roi.AngleDegrees;

            if (eventArgs.Control && columnDirection != 0)
            {
                roi.AngleDegrees = RotatedRectangle.NormalizeAngle(
                    roi.AngleDegrees + (columnDirection * (eventArgs.Shift ? 5.0 : 1.0)));
            }
            else
            {
                roi.CenterRow += rowDirection * step;
                roi.CenterColumn += columnDirection * step;
            }

            ClampRoiCenter(roi);
            SetRoi(_selectedRoi, roi);
            if (_selectedRoi == CanvasRoiTarget.Template && !AnchorLocked)
            {
                double deltaRow = roi.CenterRow - prevRow;
                double deltaCol = roi.CenterColumn - prevCol;
                double deltaAngle = RotatedRectangle.NormalizeAngle(roi.AngleDegrees - prevAngle);
                if (Math.Abs(deltaAngle) > 1e-6)
                {
                    _anchor = RotatePointAround(_anchor, roi.CenterRow, roi.CenterColumn, deltaAngle);
                }
                _anchor = ClampToImage(new ImageCoordinate(_anchor.Row + deltaRow, _anchor.Column + deltaCol));
                OnAnchorChanged(true);
            }
            OnRoiChanged(_selectedRoi, true);
            eventArgs.Handled = true;
        }

        private void DrawEmptyState(Graphics graphics)
        {
            const string message = "暂无图像";
            SizeF size = graphics.MeasureString(message, Font);
            using (Brush brush = new SolidBrush(Color.FromArgb(150, ForeColor)))
            {
                graphics.DrawString(
                    message,
                    Font,
                    brush,
                    (ClientSize.Width - size.Width) / 2.0f,
                    (ClientSize.Height - size.Height) / 2.0f);
            }
        }

        private void DrawImageLayer(Graphics graphics)
        {
            RectangleF destination = new RectangleF(
                (float)_offsetX,
                (float)_offsetY,
                (float)(_image.Width * _zoom),
                (float)(_image.Height * _zoom));

            // 当缩放到小于预降采样预览图比例时复用 _interactivePreview，否则直接按可见视口裁剪 _image 绘制；
            // 全程使用统一 PixelOffsetMode.Half 与稳定插值模式，彻底消除缩放、拖拽与掩膜画笔时的图像跳动与重采样卡顿。
            double previewRatio = _interactivePreview != null && _image.Width > 0
                ? (double)_interactivePreview.Width / _image.Width
                : 0.0;
            bool usePreview = _interactivePreview != null && _zoom <= previewRatio * 1.02;
            Bitmap source = usePreview ? _interactivePreview : _image;
            double scaleX = source.Width / (double)_image.Width;
            double scaleY = source.Height / (double)_image.Height;
            double effZoomX = _zoom / scaleX;
            double effZoomY = _zoom / scaleY;

            int srcLeft = Math.Max(0, (int)Math.Floor((-_offsetX) / effZoomX) - 1);
            int srcTop = Math.Max(0, (int)Math.Floor((-_offsetY) / effZoomY) - 1);
            int srcRight = Math.Min(source.Width, (int)Math.Ceiling((ClientSize.Width - _offsetX) / effZoomX) + 1);
            int srcBottom = Math.Min(source.Height, (int)Math.Ceiling((ClientSize.Height - _offsetY) / effZoomY) + 1);

            if (srcRight > srcLeft && srcBottom > srcTop)
            {
                RectangleF srcRect = new RectangleF(srcLeft, srcTop, srcRight - srcLeft, srcBottom - srcTop);
                RectangleF dstRect = new RectangleF(
                    (float)(_offsetX + (srcLeft * effZoomX)),
                    (float)(_offsetY + (srcTop * effZoomY)),
                    (float)((srcRight - srcLeft) * effZoomX),
                    (float)((srcBottom - srcTop) * effZoomY));

                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = _zoom >= 2.0
                    ? InterpolationMode.NearestNeighbor
                    : InterpolationMode.Bilinear;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.DrawImage(
                    source,
                    dstRect,
                    srcRect,
                    GraphicsUnit.Pixel);
                graphics.CompositingMode = CompositingMode.SourceOver;
            }
            using (Pen border = new Pen(Color.FromArgb(90, Color.White), 1.0f))
            {
                graphics.DrawRectangle(border, destination.X, destination.Y, destination.Width, destination.Height);
            }
        }

        private Bitmap GetDisplayImage()
        {
            if (_image == null)
            {
                return null;
            }

            int width = Math.Max(1, (int)Math.Round(_image.Width * _zoom));
            int height = Math.Max(1, (int)Math.Round(_image.Height * _zoom));

            // 放大或 1:1 显示时由 GDI 直接绘制原图，避免缓存占用与源图相当的内存。
            if (_zoom >= 1.0 || width >= _image.Width || height >= _image.Height)
            {
                InvalidateImageCache();
                return _image;
            }

            if (_scaledImage != null &&
                _scaledImageWidth == width &&
                _scaledImageHeight == height &&
                Math.Abs(_scaledImageZoom - _zoom) < 0.000001)
            {
                return _scaledImage;
            }

            InvalidateImageCache();
            Bitmap scaled = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            scaled.SetResolution(
                _image.HorizontalResolution > 0.0f ? _image.HorizontalResolution : 96.0f,
                _image.VerticalResolution > 0.0f ? _image.VerticalResolution : 96.0f);
            using (Graphics cacheGraphics = Graphics.FromImage(scaled))
            {
                cacheGraphics.CompositingMode = CompositingMode.SourceCopy;
                cacheGraphics.CompositingQuality = CompositingQuality.HighSpeed;
                cacheGraphics.SmoothingMode = SmoothingMode.None;
                cacheGraphics.PixelOffsetMode = PixelOffsetMode.Half;
                cacheGraphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                cacheGraphics.DrawImage(
                    _image,
                    new Rectangle(0, 0, width, height),
                    new Rectangle(0, 0, _image.Width, _image.Height),
                    GraphicsUnit.Pixel);
            }

            _scaledImage = scaled;
            _scaledImageWidth = width;
            _scaledImageHeight = height;
            _scaledImageZoom = _zoom;
            return _scaledImage;
        }

        private static Bitmap CreateInteractivePreview(Bitmap source)
        {
            if (source == null)
            {
                return null;
            }

            // 为超大图像预生成最长边不超过 1600px 的快速预览帧，保证滚轮连续缩放流畅
            const int maximumDimension = 1600;
            int previewWidth = source.Width;
            int previewHeight = source.Height;
            if (Math.Max(previewWidth, previewHeight) <= maximumDimension)
            {
                return null;
            }

            double scale = (double)maximumDimension / Math.Max(previewWidth, previewHeight);
            previewWidth = Math.Max(1, (int)Math.Round(previewWidth * scale));
            previewHeight = Math.Max(1, (int)Math.Round(previewHeight * scale));
            Bitmap preview = new Bitmap(previewWidth, previewHeight, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(preview))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.InterpolationMode = InterpolationMode.Bilinear;
                graphics.DrawImage(
                    source,
                    new Rectangle(0, 0, previewWidth, previewHeight),
                    new Rectangle(0, 0, source.Width, source.Height),
                    GraphicsUnit.Pixel);
            }
            return preview;
        }

        private void InvalidateImageCache()
        {
            if (_scaledImage != null)
            {
                _scaledImage.Dispose();
                _scaledImage = null;
            }

            _scaledImageWidth = 0;
            _scaledImageHeight = 0;
            _scaledImageZoom = double.NaN;
        }

        private void EnsureViewInitialized()
        {
            if (_viewInitialized || _image == null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                return;
            }

            const double padding = 12.0;
            double availableWidth = Math.Max(1.0, ClientSize.Width - (padding * 2.0));
            double availableHeight = Math.Max(1.0, ClientSize.Height - (padding * 2.0));
            _zoom = Clamp(Math.Min(availableWidth / _image.Width, availableHeight / _image.Height), MinimumZoom, MaximumZoom);
            _offsetX = (ClientSize.Width - (_image.Width * _zoom)) / 2.0;
            _offsetY = (ClientSize.Height - (_image.Height * _zoom)) / 2.0;
            _autoFit = true;
            _viewInitialized = true;
        }

        private bool HasActiveMaskFill()
        {
            if (_activeStroke != null && _activeStroke.IsValid)
            {
                if (_activeStroke.Kind == MaskShapeKind.Brush ||
                    _activeStroke.Kind == MaskShapeKind.Circle ||
                    (_activeStroke.Kind == MaskShapeKind.Rectangle && _activeStroke.Rectangle != null))
                {
                    return true;
                }
            }

            if (_polygonDrawing)
            {
                int vertexCount = _activePolygonPoints.Count + (_hasHoverScreen ? 1 : 0);
                if (vertexCount >= 3)
                {
                    return true;
                }
            }

            return false;
        }

        private void DrawMaskLayer(Graphics graphics)
        {
            bool hasActiveMaskFill = HasActiveMaskFill();
            if ((_maskStrokes.Count == 0 && !hasActiveMaskFill) || _templateRoi == null)
            {
                return;
            }

            GraphicsState state = graphics.Save();
            try
            {
                // 仅在模板 ROI 多边形区域内裁剪并显示掩膜笔画
                PointF[] clipPoints = _templateRoi.GetCorners().Select(ImageToClient).ToArray();
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddPolygon(clipPoints);
                    graphics.SetClip(path, CombineMode.Intersect);
                    if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

                    // 平移或连续滚轮缩放期间直接按增量变换贴图已有掩膜缓存，零重新栅格化开销，停顿 90ms 后自动高清重绘。
                    bool canTransformCachedMask =
                        _maskHistory != null &&
                        _maskZoom > 0.0 &&
                        _paintedStrokes == _maskStrokes.Count &&
                        !hasActiveMaskFill &&
                        (_dragOperation == DragOperation.Pan || _maskRefreshPending);
                    if (canTransformCachedMask)
                    {
                        float scaleRatio = (float)(_zoom / _maskZoom);
                        RectangleF dstRect = new RectangleF(
                            (float)(_offsetX - (_maskX * scaleRatio)),
                            (float)(_offsetY - (_maskY * scaleRatio)),
                            _maskHistory.Width * scaleRatio,
                            _maskHistory.Height * scaleRatio);
                        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                        graphics.DrawImage(
                            _maskHistory,
                            dstRect,
                            new RectangleF(0.0f, 0.0f, _maskHistory.Width, _maskHistory.Height),
                            GraphicsUnit.Pixel);
                        return;
                    }

                    UpdateMaskCache(hasActiveMaskFill);

                    Bitmap sourceMask = hasActiveMaskFill && _maskLive != null ? _maskLive : _maskHistory;
                    if (sourceMask == null) return;

                    // 仅贴图模板 ROI 客户端外接框与当前脏区的交集，避免每次局部重绘对整屏 32bpp 位图做全量 Alpha 混合
                    float minX = clipPoints[0].X, maxX = clipPoints[0].X;
                    float minY = clipPoints[0].Y, maxY = clipPoints[0].Y;
                    for (int i = 1; i < clipPoints.Length; i++)
                    {
                        if (clipPoints[i].X < minX) minX = clipPoints[i].X;
                        if (clipPoints[i].X > maxX) maxX = clipPoints[i].X;
                        if (clipPoints[i].Y < minY) minY = clipPoints[i].Y;
                        if (clipPoints[i].Y > maxY) maxY = clipPoints[i].Y;
                    }
                    Rectangle clientRect = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
                    Rectangle roiRect = ToEnclosingRectangle(RectangleF.FromLTRB(minX - 2.0f, minY - 2.0f, maxX + 2.0f, maxY + 2.0f));
                    Rectangle blitRect = Rectangle.Intersect(roiRect, clientRect);
                    Rectangle clipRect = ToEnclosingRectangle(graphics.ClipBounds);
                    if (!clipRect.IsEmpty)
                    {
                        clipRect.Inflate(1, 1);
                        blitRect = Rectangle.Intersect(blitRect, clipRect);
                    }
                    if (blitRect.Width <= 0 || blitRect.Height <= 0) return;

                    // 单层统一贴图：已提交掩膜与当前实时涂抹/选区已在离线缓冲中按 SourceCopy 合并，
                    // 彻底消除重复涂抹已掩膜区域时的双重 Alpha 叠加拖影与松手闪变。
                    graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                    graphics.DrawImage(sourceMask, blitRect, blitRect, GraphicsUnit.Pixel);
                }
            }
            finally
            {
                graphics.Restore(state);
            }
        }

        private void DrawActiveMaskOverlay(Graphics graphics)
        {
            if (_templateRoi == null) return;
            bool hasShapePreview = _activeStroke != null && _activeStroke.Kind != MaskShapeKind.Brush;
            if (!hasShapePreview && !_polygonDrawing) return;

            Color borderColor = Color.FromArgb(235, 248, 113, 113);
            using (Pen border = new Pen(borderColor, 1.5f))
            {
                border.DashStyle = DashStyle.Dash;
                if (_activeStroke != null && _activeStroke.Kind == MaskShapeKind.Rectangle && _activeStroke.Rectangle != null)
                {
                    PointF[] corners = _activeStroke.Rectangle.GetCorners().Select(ImageToClient).ToArray();
                    graphics.DrawPolygon(border, corners);
                }
                else if (_activeStroke != null && _activeStroke.Kind == MaskShapeKind.Circle)
                {
                    PointF center = ImageToClient(_activeStroke.Center);
                    float radius = (float)Math.Max(1.0, _activeStroke.Radius * _zoom);
                    graphics.DrawEllipse(border, center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);
                    graphics.DrawLine(border, center.X - 5, center.Y, center.X + 5, center.Y);
                    graphics.DrawLine(border, center.X, center.Y - 5, center.X, center.Y + 5);
                }
                else if (_polygonDrawing && _activePolygonPoints.Count > 0)
                {
                    List<PointF> screenPoints = _activePolygonPoints.Select(ImageToClient).ToList();
                    if (_hasHoverScreen) screenPoints.Add(_hoverScreen);
                    if (screenPoints.Count >= 2)
                    {
                        graphics.DrawLines(border, screenPoints.ToArray());
                    }
                    using (Brush vertexBrush = new SolidBrush(Color.White))
                    using (Pen vertexPen = new Pen(borderColor, 1.5f))
                    {
                        for (int i = 0; i < _activePolygonPoints.Count; i++)
                        {
                            PointF vp = ImageToClient(_activePolygonPoints[i]);
                            float r = (i == 0 && _activePolygonPoints.Count >= 3) ? 5.0f : 3.5f;
                            graphics.FillEllipse(vertexBrush, vp.X - r, vp.Y - r, r * 2.0f, r * 2.0f);
                            graphics.DrawEllipse(vertexPen, vp.X - r, vp.Y - r, r * 2.0f, r * 2.0f);
                        }
                    }
                }
            }
        }

        private void DrawBrushCursor(Graphics graphics)
        {
            float radius = (float)Math.Max(2.0, BrushRadiusImage * _zoom);
            Color color = _tool == CanvasEditTool.EraseMask
                ? Color.FromArgb(220, 248, 113, 113)
                : Color.FromArgb(220, 74, 222, 128);
            using (Pen pen = new Pen(color, 1.5f))
            {
                pen.DashStyle = DashStyle.Dot;
                graphics.DrawEllipse(pen, _hoverScreen.X - radius, _hoverScreen.Y - radius, radius * 2.0f, radius * 2.0f);
            }
        }

        private void ClearMaskCache()
        {
            if (_maskHistory != null) _maskHistory.Dispose();
            if (_maskLive != null) _maskLive.Dispose();
            _maskHistory = _maskLive = null;
            _paintedStrokes = 0;
            ResetLiveMaskState();
        }

        private void ResetLiveMaskState()
        {
            _liveStrokeOwner = null;
            _paintedLivePoints = 0;
            _lastLiveShapeBounds = Rectangle.Empty;
            _liveBaseSynced = false;
        }

        private void UpdateMaskCache(bool hasActiveMaskFill)
        {
            // Coordinates are viewport-relative. The template ROI clips the layers at presentation.
            bool sizeChanged = _maskHistory == null || _maskHistory.Size != ClientSize;
            bool viewChanged = _maskZoom != _zoom || _maskX != _offsetX || _maskY != _offsetY;
            if (sizeChanged)
            {
                ClearMaskCache();
                _maskHistory = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
                _maskZoom = _zoom;
                _maskX = _offsetX;
                _maskY = _offsetY;
            }
            else if (viewChanged)
            {
                // 客户区尺寸不变时复用已有 32bpp 位图直接清空重绘，消除反复分配 LOH 位图引发的 GC 卡顿
                using (Graphics clearLayer = Graphics.FromImage(_maskHistory))
                {
                    clearLayer.Clear(Color.Transparent);
                }
                _paintedStrokes = 0;
                ResetLiveMaskState();
                _maskZoom = _zoom;
                _maskX = _offsetX;
                _maskY = _offsetY;
            }

            if (_paintedStrokes < _maskStrokes.Count)
            {
                using (Graphics layer = Graphics.FromImage(_maskHistory))
                {
                    // GDI+ 中 CompositingMode.SourceCopy 不能与 SmoothingMode.AntiAlias 混用，
                    // 否则在 32bppPArgb 位图上会静默丢弃绘制导致掩膜无痕迹；此处使用 None 确保像素精确写入。
                    layer.SmoothingMode = SmoothingMode.None;
                    layer.PixelOffsetMode = PixelOffsetMode.Half;
                    layer.CompositingMode = CompositingMode.SourceCopy;
                    while (_paintedStrokes < _maskStrokes.Count)
                    {
                        DrawMaskItem(layer, _maskStrokes[_paintedStrokes], 0, false);
                        _paintedStrokes++;
                    }
                }
                ResetLiveMaskState();
            }

            if (!hasActiveMaskFill)
            {
                if (_liveBaseSynced || _liveStrokeOwner != null || !_lastLiveShapeBounds.IsEmpty)
                {
                    ResetLiveMaskState();
                }
                return;
            }

            if (_maskLive == null || _maskLive.Size != ClientSize)
            {
                if (_maskLive != null) _maskLive.Dispose();
                _maskLive = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
                ResetLiveMaskState();
            }

            Rectangle fullRect = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);

            if (_activeStroke != null && _activeStroke.Kind == MaskShapeKind.Brush)
            {
                if (_liveBaseSynced && ReferenceEquals(_liveStrokeOwner, _activeStroke) && _paintedLivePoints == _activeStroke.Points.Count)
                {
                    return;
                }

                using (Graphics live = Graphics.FromImage(_maskLive))
                {
                    live.SmoothingMode = SmoothingMode.None;
                    live.PixelOffsetMode = PixelOffsetMode.Half;
                    live.InterpolationMode = InterpolationMode.NearestNeighbor;
                    live.CompositingMode = CompositingMode.SourceCopy;
                    if (!_liveBaseSynced || !ReferenceEquals(_liveStrokeOwner, _activeStroke))
                    {
                        live.DrawImage(_maskHistory, fullRect, fullRect, GraphicsUnit.Pixel);
                        _liveBaseSynced = true;
                        _liveStrokeOwner = _activeStroke;
                        _paintedLivePoints = 0;
                        _lastLiveShapeBounds = Rectangle.Empty;
                    }
                    DrawMaskItem(live, _activeStroke, Math.Max(0, _paintedLivePoints - 1), true);
                    _paintedLivePoints = _activeStroke.Points.Count;
                }
                return;
            }

            // 矩形 / 圆形 / 多边形实时选区预览：在 _maskLive 上仅还原上一帧选区脏区并按 SourceCopy 写入当前选区
            using (Graphics live = Graphics.FromImage(_maskLive))
            {
                live.SmoothingMode = SmoothingMode.None;
                live.PixelOffsetMode = PixelOffsetMode.Half;
                live.InterpolationMode = InterpolationMode.NearestNeighbor;
                live.CompositingMode = CompositingMode.SourceCopy;

                if (!_liveBaseSynced || _liveStrokeOwner != null)
                {
                    live.DrawImage(_maskHistory, fullRect, fullRect, GraphicsUnit.Pixel);
                    _liveBaseSynced = true;
                    _liveStrokeOwner = null;
                    _paintedLivePoints = 0;
                    _lastLiveShapeBounds = Rectangle.Empty;
                }
                else if (!_lastLiveShapeBounds.IsEmpty)
                {
                    Rectangle restoreRect = Rectangle.Intersect(_lastLiveShapeBounds, fullRect);
                    if (restoreRect.Width > 0 && restoreRect.Height > 0)
                    {
                        live.DrawImage(_maskHistory, restoreRect, restoreRect, GraphicsUnit.Pixel);
                    }
                }

                if (_polygonDrawing)
                {
                    List<PointF> screenPoints = _activePolygonPoints.Select(ImageToClient).ToList();
                    if (_hasHoverScreen) screenPoints.Add(_hoverScreen);
                    if (screenPoints.Count >= 3)
                    {
                        PointF[] pts = screenPoints.ToArray();
                        using (Brush brush = new SolidBrush(Color.FromArgb(115, 239, 68, 68)))
                        {
                            live.FillPolygon(brush, pts);
                        }
                        _lastLiveShapeBounds = GetPointsDirtyRect(pts, fullRect);
                    }
                    else
                    {
                        _lastLiveShapeBounds = Rectangle.Empty;
                    }
                }
                else if (_activeStroke != null)
                {
                    DrawMaskItem(live, _activeStroke, 0, true);
                    _lastLiveShapeBounds = GetStrokeClientDirtyRect(_activeStroke, fullRect);
                }
                else
                {
                    _lastLiveShapeBounds = Rectangle.Empty;
                }
            }
        }

        private static Rectangle ToEnclosingRectangle(RectangleF rect)
        {
            if (rect.Width <= 0.0f || rect.Height <= 0.0f)
            {
                return Rectangle.Empty;
            }

            int left = (int)Math.Floor(rect.Left);
            int top = (int)Math.Floor(rect.Top);
            int right = (int)Math.Ceiling(rect.Right);
            int bottom = (int)Math.Ceiling(rect.Bottom);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        private static Rectangle GetPointsDirtyRect(PointF[] points, Rectangle boundsLimit)
        {
            if (points == null || points.Length == 0)
            {
                return Rectangle.Empty;
            }

            float minX = points[0].X, maxX = points[0].X;
            float minY = points[0].Y, maxY = points[0].Y;
            for (int i = 1; i < points.Length; i++)
            {
                if (points[i].X < minX) minX = points[i].X;
                if (points[i].X > maxX) maxX = points[i].X;
                if (points[i].Y < minY) minY = points[i].Y;
                if (points[i].Y > maxY) maxY = points[i].Y;
            }
            Rectangle rect = ToEnclosingRectangle(RectangleF.FromLTRB(minX - 4.0f, minY - 4.0f, maxX + 4.0f, maxY + 4.0f));
            return Rectangle.Intersect(rect, boundsLimit);
        }

        private Rectangle GetStrokeClientDirtyRect(MaskStroke stroke, Rectangle boundsLimit)
        {
            if (stroke == null || !stroke.IsValid)
            {
                return Rectangle.Empty;
            }

            if (stroke.Kind == MaskShapeKind.Rectangle && stroke.Rectangle != null)
            {
                PointF[] corners = stroke.Rectangle.GetCorners().Select(ImageToClient).ToArray();
                return GetPointsDirtyRect(corners, boundsLimit);
            }

            if (stroke.Kind == MaskShapeKind.Circle)
            {
                PointF center = ImageToClient(stroke.Center);
                float radius = (float)Math.Max(1.0, stroke.Radius * _zoom) + 4.0f;
                Rectangle rect = ToEnclosingRectangle(RectangleF.FromLTRB(
                    center.X - radius,
                    center.Y - radius,
                    center.X + radius,
                    center.Y + radius));
                return Rectangle.Intersect(rect, boundsLimit);
            }

            if (stroke.Kind == MaskShapeKind.Polygon && stroke.Points != null && stroke.Points.Count > 0)
            {
                PointF[] pts = stroke.Points.Select(ImageToClient).ToArray();
                return GetPointsDirtyRect(pts, boundsLimit);
            }

            return Rectangle.Empty;
        }

        private void DrawMaskItem(Graphics graphics, MaskStroke stroke, int firstPoint, bool isLivePreview)
        {
            if (stroke == null || !stroke.IsValid)
            {
                return;
            }

            // 使用 SourceCopy：擦除统一着半透明红（重复涂抹不变黑/无叠加拖影），恢复直接擦回透明露出清晰原图
            Color color = stroke.IsErase
                ? Color.FromArgb(115, 239, 68, 68)
                : Color.Transparent;

            if (stroke.Kind == MaskShapeKind.Rectangle && stroke.Rectangle != null)
            {
                PointF[] corners = stroke.Rectangle.GetCorners().Select(ImageToClient).ToArray();
                using (Brush brush = new SolidBrush(color))
                {
                    graphics.FillPolygon(brush, corners);
                }
                if (stroke.IsErase && !isLivePreview)
                {
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    using (Pen outline = new Pen(Color.FromArgb(215, 248, 113, 113), 1.35f))
                    {
                        outline.DashStyle = DashStyle.Dash;
                        graphics.DrawPolygon(outline, corners);
                    }
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                }
                return;
            }

            if (stroke.Kind == MaskShapeKind.Circle)
            {
                PointF center = ImageToClient(stroke.Center);
                float radius = (float)Math.Max(1.0, stroke.Radius * _zoom);
                using (Brush brush = new SolidBrush(color))
                {
                    graphics.FillEllipse(brush, center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);
                }
                if (stroke.IsErase && !isLivePreview)
                {
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    using (Pen outline = new Pen(Color.FromArgb(215, 248, 113, 113), 1.35f))
                    {
                        outline.DashStyle = DashStyle.Dash;
                        graphics.DrawEllipse(outline, center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);
                    }
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                }
                return;
            }

            if (stroke.Kind == MaskShapeKind.Polygon)
            {
                PointF[] pts = stroke.Points.Select(ImageToClient).ToArray();
                if (pts.Length >= 3)
                {
                    using (Brush brush = new SolidBrush(color))
                    {
                        graphics.FillPolygon(brush, pts);
                    }
                    if (stroke.IsErase && !isLivePreview)
                    {
                        graphics.CompositingMode = CompositingMode.SourceOver;
                        using (Pen outline = new Pen(Color.FromArgb(215, 248, 113, 113), 1.35f))
                        {
                            outline.DashStyle = DashStyle.Dash;
                            graphics.DrawPolygon(outline, pts);
                        }
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                    }
                }
                return;
            }

            float width = (float)Math.Max(2.0, stroke.Radius * 2.0 * _zoom);
            using (Pen pen = new Pen(color, width))
            using (Brush brush = new SolidBrush(color))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                PointF[] points = new PointF[stroke.Points.Count - firstPoint];
                for (int i = 0; i < points.Length; i++) points[i] = ImageToClient(stroke.Points[firstPoint + i]);
                if (points.Length == 1)
                {
                    graphics.FillEllipse(brush, points[0].X - (width / 2.0f), points[0].Y - (width / 2.0f), width, width);
                }
                else
                {
                    graphics.DrawLines(pen, points);
                }
            }
        }

        private void DrawEditingLayer(Graphics graphics)
        {
            _renderedRoiLabelRects.Clear();
            _renderedAnchorLabelRect = RectangleF.Empty;

            List<RectangleF> occupiedLabels = new List<RectangleF>();
            CanvasRoiTarget visualSelected = (IsDrawRoiTool(_tool) || _tool == CanvasEditTool.MoveAnchor)
                ? CanvasRoiTarget.None
                : _selectedRoi;

            // 预留当前选中 ROI 顶部旋转控制柄的空间，避免标签遮挡旋转手柄
            if (visualSelected != CanvasRoiTarget.None && !IsTargetLocked(visualSelected))
            {
                RotatedRectangle selectedRoi = GetRoi(visualSelected);
                if (selectedRoi != null)
                {
                    PointF rotateCenter = GetRotateHandleLocation(selectedRoi);
                    occupiedLabels.Add(new RectangleF(
                        rotateCenter.X - HandleRadius - 4.0f,
                        rotateCenter.Y - HandleRadius - 4.0f,
                        (HandleRadius + 4.0f) * 2.0f,
                        (HandleRadius + 4.0f) * 2.0f));
                }
            }

            CanvasRoiTarget[] baseOrder =
            {
                CanvasRoiTarget.Search,
                CanvasRoiTarget.Detection,
                CanvasRoiTarget.Template
            };

            // 先绘制所有未选中 ROI，最后将当前选中 ROI 置顶绘制，确保边框、控制柄与标签永不被遮挡
            for (int i = 0; i < baseOrder.Length; i++)
            {
                CanvasRoiTarget target = baseOrder[i];
                if (target == visualSelected) continue;
                DrawRoi(graphics, GetRoi(target), target, GetRoiColor(target), visualSelected, occupiedLabels);
            }

            if (visualSelected != CanvasRoiTarget.None)
            {
                DrawRoi(graphics, GetRoi(visualSelected), visualSelected, GetRoiColor(visualSelected), visualSelected, occupiedLabels);
            }

            if (HasVisibleAnchor)
            {
                DrawAnchor(graphics, occupiedLabels);
            }
        }

        private static Color GetRoiColor(CanvasRoiTarget target)
        {
            return target == CanvasRoiTarget.Template
                ? Color.Gold
                : target == CanvasRoiTarget.Search ? Color.DeepSkyBlue : Color.Cyan;
        }

        private static string GetRoiTitle(CanvasRoiTarget target)
        {
            return target == CanvasRoiTarget.Template
                ? "模板 ROI"
                : target == CanvasRoiTarget.Search ? "搜索 ROI" : "检测 ROI";
        }

        private void DrawRoi(
            Graphics graphics,
            RotatedRectangle roi,
            CanvasRoiTarget target,
            Color color,
            CanvasRoiTarget visualSelected,
            IList<RectangleF> occupiedLabels)
        {
            if (roi == null)
            {
                return;
            }

            bool selected = visualSelected == target;
            bool hovered = !selected && (_hoveredRoi == target || _hoveredLabelRoi == target);
            bool dimmedForRedraw = _dragOperation == DragOperation.CreateRoi &&
                                   _creatingRoiTarget == target &&
                                   _creatingRoiDraft != null;
            PointF[] corners = roi.GetCorners().Select(ImageToClient).ToArray();

            if (selected)
            {
                using (Pen shadow = new Pen(Color.FromArgb(145, Color.Black), 3.8f))
                {
                    graphics.DrawPolygon(shadow, corners);
                }
            }

            int borderAlpha = dimmedForRedraw ? 110 : (selected ? 255 : (hovered ? 245 : 195));
            float borderWidth = selected ? 2.2f : (hovered ? 2.0f : 1.35f);
            using (Pen pen = new Pen(Color.FromArgb(borderAlpha, color), borderWidth))
            {
                if (!selected)
                {
                    pen.DashStyle = DashStyle.Dash;
                }

                graphics.DrawPolygon(pen, corners);
            }

            string title = GetRoiTitle(target);
            bool emphasizeLabel = selected || hovered;
            RectangleF labelRect = DrawAvoidingLabel(
                graphics,
                title,
                corners[0],
                dimmedForRedraw ? Color.FromArgb(140, color) : color,
                occupiedLabels,
                emphasizeLabel);
            _renderedRoiLabelRects[target] = labelRect;

            if (!selected || IsTargetLocked(target))
            {
                if (selected && IsTargetLocked(target))
                {
                    DrawLockGlyph(graphics, corners[1], color);
                }

                return;
            }

            // 绘制顶边中点到旋转控制柄的连接杆
            PointF topMid = ImageToClient(roi.LocalToImage(0.0, -roi.HalfHeight));
            PointF rotatePos = GetRotateHandleLocation(roi);
            using (Pen stemShadow = new Pen(Color.FromArgb(165, Color.Black), 3.0f))
            using (Pen stemPen = new Pen(color, 1.5f))
            {
                graphics.DrawLine(stemShadow, topMid, rotatePos);
                graphics.DrawLine(stemPen, topMid, rotatePos);
            }

            foreach (RoiHandle handle in GetHandles(roi))
            {
                PointF location = handle.Location;
                if (handle.Kind == RoiHandleKind.Rotate)
                {
                    using (Brush fill = new SolidBrush(Color.FromArgb(240, 24, 28, 34)))
                    using (Pen outline = new Pen(color, 2.0f))
                    using (Brush dot = new SolidBrush(color))
                    {
                        graphics.FillEllipse(fill, location.X - HandleRadius, location.Y - HandleRadius, HandleRadius * 2.0f, HandleRadius * 2.0f);
                        graphics.DrawEllipse(outline, location.X - HandleRadius, location.Y - HandleRadius, HandleRadius * 2.0f, HandleRadius * 2.0f);
                        graphics.FillEllipse(dot, location.X - 1.8f, location.Y - 1.8f, 3.6f, 3.6f);
                    }
                }
                else
                {
                    using (Brush fill = new SolidBrush(Color.FromArgb(245, 24, 28, 34)))
                    using (Pen outline = new Pen(color, 1.6f))
                    {
                        RectangleF square = new RectangleF(
                            location.X - HandleRadius,
                            location.Y - HandleRadius,
                            HandleRadius * 2.0f,
                            HandleRadius * 2.0f);
                        graphics.FillRectangle(fill, square);
                        graphics.DrawRectangle(outline, square.X, square.Y, square.Width, square.Height);
                    }
                }
            }
        }

        private void DrawCreatingRoiDraft(Graphics graphics)
        {
            if (_dragOperation != DragOperation.CreateRoi ||
                _creatingRoiDraft == null ||
                _creatingRoiTarget == CanvasRoiTarget.None)
            {
                return;
            }

            Color color = GetRoiColor(_creatingRoiTarget);
            PointF[] corners = _creatingRoiDraft.GetCorners().Select(ImageToClient).ToArray();
            using (Brush fill = new SolidBrush(Color.FromArgb(30, color)))
            using (Pen shadow = new Pen(Color.FromArgb(165, Color.Black), 3.6f))
            using (Pen border = new Pen(color, 2.1f))
            {
                graphics.FillPolygon(fill, corners);
                graphics.DrawPolygon(shadow, corners);
                graphics.DrawPolygon(border, corners);
            }

            using (Brush cornerFill = new SolidBrush(Color.White))
            using (Pen cornerOutline = new Pen(color, 1.5f))
            {
                for (int i = 0; i < corners.Length; i++)
                {
                    graphics.FillRectangle(cornerFill, corners[i].X - 3.0f, corners[i].Y - 3.0f, 6.0f, 6.0f);
                    graphics.DrawRectangle(cornerOutline, corners[i].X - 3.0f, corners[i].Y - 3.0f, 6.0f, 6.0f);
                }
            }

            double widthPx = _creatingRoiDraft.HalfWidth * 2.0;
            double heightPx = _creatingRoiDraft.HalfHeight * 2.0;
            string sizeLabel = string.Format("{0}  {1:0} × {2:0} px", GetRoiTitle(_creatingRoiTarget), widthPx, heightPx);
            DrawLabel(graphics, sizeLabel, corners[0], color);
        }

        private void DrawAnchor(Graphics graphics, IList<RectangleF> occupiedLabels)
        {
            PointF center = ImageToClient(_anchor);
            bool emphasized = _anchorSelected || _anchorHovered || _hoveredAnchorLabel || _tool == CanvasEditTool.MoveAnchor;
            Color color = AnchorLocked ? Color.Silver : Color.FromArgb(255, 255, 80, 180);
            float radius = emphasized ? 6.5f : 5.0f;
            float arm = emphasized ? 14.0f : 10.0f;
            using (Pen shadow = new Pen(Color.FromArgb(220, Color.Black), 3.0f))
            using (Pen cross = new Pen(color, emphasized ? 2.0f : 1.5f))
            using (Brush fill = new SolidBrush(color))
            {
                graphics.DrawLine(shadow, center.X - arm, center.Y, center.X + arm, center.Y);
                graphics.DrawLine(shadow, center.X, center.Y - arm, center.X, center.Y + arm);
                graphics.DrawLine(cross, center.X - arm, center.Y, center.X + arm, center.Y);
                graphics.DrawLine(cross, center.X, center.Y - arm, center.X, center.Y + arm);
                graphics.FillEllipse(fill, center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);
                graphics.DrawEllipse(shadow, center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);
                if (emphasized && !AnchorLocked)
                {
                    float ring = radius + 4.0f;
                    using (Pen ringPen = new Pen(Color.White, 1.5f))
                    {
                        graphics.DrawEllipse(ringPen, center.X - ring, center.Y - ring, ring * 2.0f, ring * 2.0f);
                    }
                }
            }

            RectangleF anchorLabel = DrawAvoidingLabel(
                graphics,
                AnchorLocked ? "中心(锁)" : "中心",
                center,
                color,
                occupiedLabels,
                emphasized && !AnchorLocked);
            _renderedAnchorLabelRect = AnchorLocked ? RectangleF.Empty : anchorLabel;
        }

        private void DrawOverlayLayer(Graphics graphics, CanvasRuntimeOverlay overlay, bool drawHud)
        {
            if (overlay == null)
            {
                return;
            }

            if (overlay.Residuals != null)
            {
                foreach (CanvasResidualOverlay residual in overlay.Residuals.Where(item => item != null))
                {
                    PointF actual = ImageToClient(residual.Actual);
                    PointF expected = ImageToClient(residual.Expected);
                    using (Pen pen = new Pen(residual.Color, 1.5f))
                    {
                        pen.EndCap = LineCap.ArrowAnchor;
                        graphics.DrawLine(pen, expected, actual);
                    }
                }
            }

            if (overlay.Polylines != null)
            {
                foreach (CanvasPolylineOverlay polyline in overlay.Polylines.Where(item => item != null))
                {
                    if (polyline.Points == null || polyline.Points.Count < 2)
                    {
                        continue;
                    }

                    PointF[] points = polyline.Points.Select(ImageToClient).ToArray();
                    using (Pen pen = new Pen(polyline.Color, Math.Max(1.0f, polyline.WidthPixels)))
                    {
                        if (polyline.Closed && points.Length >= 3)
                        {
                            graphics.DrawPolygon(pen, points);
                        }
                        else
                        {
                            graphics.DrawLines(pen, points);
                        }
                    }
                }
            }

            List<RectangleF> occupiedLabels = new List<RectangleF>();
            if (overlay.Points != null && overlay.Points.Count > 0)
            {
                Dictionary<int, List<RectangleF>> fastBuckets = null;
                HashSet<long> fastPixelKeys = null;
                List<CanvasPointOverlay> detailedPoints = null;
                RectangleF clipBounds = graphics.ClipBounds;
                float clipMinX = Math.Max(-6.0f, clipBounds.Left - 6.0f);
                float clipMinY = Math.Max(-6.0f, clipBounds.Top - 6.0f);
                float clipMaxX = Math.Min(ClientSize.Width + 6.0f, clipBounds.Right + 6.0f);
                float clipMaxY = Math.Min(ClientSize.Height + 6.0f, clipBounds.Bottom + 6.0f);

                foreach (CanvasPointOverlay point in overlay.Points)
                {
                    if (point == null) continue;
                    bool hasLabel = !string.IsNullOrWhiteSpace(point.Label);
                    bool isPlainFeature = !hasLabel && !point.IsSelected && point.RadiusPixels < 3.0f;
                    if (!isPlainFeature)
                    {
                        if (detailedPoints == null) detailedPoints = new List<CanvasPointOverlay>();
                        detailedPoints.Add(point);
                        continue;
                    }

                    PointF center = ImageToClient(point.Position);
                    if (center.X < clipMinX || center.Y < clipMinY || center.X > clipMaxX || center.Y > clipMaxY)
                    {
                        continue;
                    }

                    // 特征预览点按颜色分组、屏幕像素格去重并复用画刷批量 FillRectangles，降低千点重绘开销
                    Color brushColor = Color.FromArgb(200, point.Color);
                    int argb = brushColor.ToArgb();
                    int px = (int)Math.Round(center.X);
                    int py = (int)Math.Round(center.Y);
                    long key = (((long)argb & 0xFFFFL) << 40) | (((long)(px & 0xFFFFF)) << 20) | (uint)(py & 0xFFFFF);
                    if (fastPixelKeys == null) fastPixelKeys = new HashSet<long>();
                    if (!fastPixelKeys.Add(key)) continue;

                    if (fastBuckets == null) fastBuckets = new Dictionary<int, List<RectangleF>>();
                    List<RectangleF> bucket;
                    if (!fastBuckets.TryGetValue(argb, out bucket))
                    {
                        bucket = new List<RectangleF>();
                        fastBuckets[argb] = bucket;
                    }

                    float size = Math.Max(2.0f, Math.Min(4.0f, point.RadiusPixels * 2.0f));
                    float half = size * 0.5f;
                    bucket.Add(new RectangleF(center.X - half, center.Y - half, size, size));
                }

                if (fastBuckets != null)
                {
                    SmoothingMode previousSmoothing = graphics.SmoothingMode;
                    graphics.SmoothingMode = SmoothingMode.None;
                    try
                    {
                        foreach (KeyValuePair<int, List<RectangleF>> entry in fastBuckets)
                        {
                            using (SolidBrush brush = new SolidBrush(Color.FromArgb(entry.Key)))
                            {
                                graphics.FillRectangles(brush, entry.Value.ToArray());
                            }
                        }
                    }
                    finally
                    {
                        graphics.SmoothingMode = previousSmoothing;
                    }
                }

                if (detailedPoints != null)
                {
                    foreach (CanvasPointOverlay point in detailedPoints)
                    {
                        PointF center = ImageToClient(point.Position);
                        float radius = Math.Max(1.5f, point.RadiusPixels);
                        bool hasLabel = !string.IsNullOrWhiteSpace(point.Label);
                        bool drawOutline = radius >= 3.0f || point.IsSelected || hasLabel;
                        using (Brush brush = new SolidBrush(Color.FromArgb(point.IsSelected ? 235 : 185, point.Color)))
                        {
                            graphics.FillEllipse(brush, center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);
                        }

                        if (drawOutline)
                        {
                            using (Pen outline = new Pen(Color.FromArgb(230, Color.Black), 1.5f))
                            {
                                graphics.DrawEllipse(outline, center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);
                            }
                        }

                        if (hasLabel)
                        {
                            DrawAvoidingLabel(graphics, point.Label, center, point.Color, occupiedLabels);
                        }
                    }
                }
            }

            if (overlay.Poses != null)
            {
                foreach (CanvasPoseOverlay pose in overlay.Poses.Where(item => item != null))
                {
                    DrawPose(graphics, pose, occupiedLabels);
                }
            }

            if (overlay.RotationCenter.HasValue)
            {
                PointF center = ImageToClient(overlay.RotationCenter.Value);
                const float radius = 11.0f;
                using (Pen pen = new Pen(Color.Magenta, 2.0f))
                {
                    graphics.DrawEllipse(pen, center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);
                    graphics.DrawLine(pen, center.X - radius - 4.0f, center.Y, center.X + radius + 4.0f, center.Y);
                    graphics.DrawLine(pen, center.X, center.Y - radius - 4.0f, center.X, center.Y + radius + 4.0f);
                }

                DrawAvoidingLabel(graphics, "旋转中心", center, Color.Magenta, occupiedLabels);
            }

            if (drawHud)
            {
                DrawHud(graphics, overlay);
            }
        }

        private void DrawPose(Graphics graphics, CanvasPoseOverlay pose, IList<RectangleF> occupiedLabels)
        {
            PointF center = ImageToClient(pose.Position);
            double radians = pose.AngleDegrees * Math.PI / 180.0;
            const float length = 34.0f;
            // HALCON 位姿角在行向下图像坐标系中以逆时针为正，因此 Y 分量需取负号
            PointF end = new PointF(
                center.X + ((float)Math.Cos(radians) * length),
                center.Y - ((float)Math.Sin(radians) * length));
            using (Pen pen = new Pen(pose.Color, 2.0f))
            {
                pen.EndCap = LineCap.ArrowAnchor;
                graphics.DrawLine(pen, center, end);
                graphics.DrawEllipse(pen, center.X - 6.0f, center.Y - 6.0f, 12.0f, 12.0f);
            }

            string label = pose.Label;
            if (string.IsNullOrWhiteSpace(label))
            {
                label = string.Format("{0:0.000} / {1:0.00}°", pose.Score, pose.AngleDegrees);
            }

            DrawAvoidingLabel(graphics, label, center, pose.Color, occupiedLabels);
        }

        private void DrawHud(Graphics graphics, CanvasRuntimeOverlay overlay)
        {
            List<string> lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(overlay.StatusText))
            {
                lines.Add(overlay.StatusText);
            }

            if (overlay.HudLines != null)
            {
                lines.AddRange(overlay.HudLines.Where(line => !string.IsNullOrWhiteSpace(line)));
            }

            if (lines.Count == 0 && !overlay.IsOk.HasValue)
            {
                return;
            }

            Color accent = !overlay.IsOk.HasValue
                ? Color.DeepSkyBlue
                : overlay.IsOk.Value ? Color.LimeGreen : Color.OrangeRed;
            if (lines.Count == 0)
            {
                lines.Add(overlay.IsOk.Value ? "OK" : "NG");
            }

            float maxWidth = 0.0f;
            float lineHeight = Font.GetHeight(graphics) + 3.0f;
            foreach (string line in lines)
            {
                maxWidth = Math.Max(maxWidth, graphics.MeasureString(line, Font).Width);
            }

            RectangleF box = new RectangleF(12.0f, 12.0f, maxWidth + 24.0f, (lineHeight * lines.Count) + 16.0f);
            using (Brush background = new SolidBrush(Color.FromArgb(205, 18, 20, 23)))
            using (Brush accentBrush = new SolidBrush(accent))
            using (Pen border = new Pen(accent, 1.5f))
            using (Brush textBrush = new SolidBrush(Color.WhiteSmoke))
            {
                graphics.FillRectangle(background, box);
                graphics.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
                graphics.FillRectangle(accentBrush, box.X, box.Y, 4.0f, box.Height);
                for (int index = 0; index < lines.Count; index++)
                {
                    graphics.DrawString(lines[index], Font, textBrush, box.X + 13.0f, box.Y + 8.0f + (index * lineHeight));
                }
            }
        }

        private void DrawToolHintBanner(Graphics graphics)
        {
            string message = null;
            Color accent = Color.DeepSkyBlue;
            if (IsDrawRoiTool(_tool))
            {
                CanvasRoiTarget target = GetDrawToolTarget(_tool);
                accent = GetRoiColor(target);
                string action = GetRoi(target) == null ? "绘制" : "重绘";
                message = string.Format("{0}{1}：按住左键拖拽框选区域 · 右键 / Esc 取消", action, GetRoiTitle(target));
            }
            else if (_tool == CanvasEditTool.MoveAnchor && !AnchorLocked)
            {
                accent = Color.FromArgb(255, 255, 80, 180);
                message = "移动基准中心：点击或拖动设置中心 · 右键 / Esc 退出";
            }

            if (string.IsNullOrEmpty(message) || ClientSize.Width < 120)
            {
                return;
            }

            SizeF textSize = graphics.MeasureString(message, Font);
            float boxWidth = textSize.Width + 24.0f;
            float boxHeight = textSize.Height + 10.0f;
            float left = Math.Max(10.0f, (ClientSize.Width - boxWidth) * 0.5f);
            RectangleF box = new RectangleF(left, 10.0f, boxWidth, boxHeight);
            using (Brush background = new SolidBrush(Color.FromArgb(215, 15, 23, 42)))
            using (Brush accentBrush = new SolidBrush(accent))
            using (Pen border = new Pen(Color.FromArgb(220, accent), 1.3f))
            using (Brush foreground = new SolidBrush(Color.WhiteSmoke))
            {
                graphics.FillRectangle(background, box);
                graphics.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
                graphics.FillRectangle(accentBrush, box.X, box.Y, 4.0f, box.Height);
                graphics.DrawString(message, Font, foreground, box.X + 12.0f, box.Y + 5.0f);
            }
        }

        private void DrawViewStatus(Graphics graphics)
        {
            string text = string.Format("{0:0.#}%   {1} × {2}", _zoom * 100.0, _image.Width, _image.Height);
            SizeF size = graphics.MeasureString(text, Font);
            RectangleF box = new RectangleF(
                ClientSize.Width - size.Width - 18.0f,
                ClientSize.Height - size.Height - 12.0f,
                size.Width + 10.0f,
                size.Height + 4.0f);
            using (Brush background = new SolidBrush(Color.FromArgb(165, 0, 0, 0)))
            using (Brush foreground = new SolidBrush(Color.Gainsboro))
            {
                graphics.FillRectangle(background, box);
                graphics.DrawString(text, Font, foreground, box.X + 5.0f, box.Y + 2.0f);
            }
        }

        private void DrawLabel(Graphics graphics, string text, PointF location, Color color)
        {
            SizeF size = graphics.MeasureString(text, Font);
            float boxWidth = size.Width + 8.0f;
            float boxHeight = size.Height + 3.0f;
            float x = location.X + 5.0f;
            float y = location.Y - boxHeight - 5.0f;
            if (y < 4.0f)
            {
                y = location.Y + 6.0f;
            }
            if (x + boxWidth > ClientSize.Width - 4.0f)
            {
                x = Math.Max(4.0f, ClientSize.Width - boxWidth - 4.0f);
            }
            if (x < 4.0f)
            {
                x = 4.0f;
            }
            RectangleF box = new RectangleF(x, y, boxWidth, boxHeight);
            using (Brush background = new SolidBrush(Color.FromArgb(205, 18, 20, 23)))
            using (Brush foreground = new SolidBrush(color))
            {
                graphics.FillRectangle(background, box);
                graphics.DrawString(text, Font, foreground, box.X + 4.0f, box.Y + 1.0f);
            }
        }

        private RectangleF DrawAvoidingLabel(
            Graphics graphics,
            string text,
            PointF anchor,
            Color color,
            IList<RectangleF> occupied,
            bool emphasize = false)
        {
            SizeF textSize = graphics.MeasureString(text, Font);
            float boxWidth = textSize.Width + 8.0f;
            float boxHeight = textSize.Height + 3.0f;
            PointF[] offsets =
            {
                new PointF(9.0f, -textSize.Height - 7.0f),
                new PointF(9.0f, 7.0f),
                new PointF(-textSize.Width - 14.0f, -textSize.Height - 7.0f),
                new PointF(-textSize.Width - 14.0f, 7.0f)
            };
            RectangleF chosen = RectangleF.Empty;
            RectangleF fallbackInView = RectangleF.Empty;
            foreach (PointF offset in offsets)
            {
                RectangleF candidate = new RectangleF(
                    anchor.X + offset.X,
                    anchor.Y + offset.Y,
                    boxWidth,
                    boxHeight);
                bool insideClient = candidate.Left >= 2.0f &&
                                    candidate.Top >= 2.0f &&
                                    candidate.Right <= ClientSize.Width - 2.0f &&
                                    candidate.Bottom <= ClientSize.Height - 2.0f;
                bool nonOverlapping = occupied.All(existing => !existing.IntersectsWith(candidate));
                if (insideClient && nonOverlapping)
                {
                    chosen = candidate;
                    break;
                }
                if (fallbackInView.IsEmpty && insideClient)
                {
                    fallbackInView = candidate;
                }
            }

            if (chosen.IsEmpty)
            {
                foreach (PointF offset in offsets)
                {
                    RectangleF candidate = new RectangleF(
                        anchor.X + offset.X,
                        anchor.Y + offset.Y,
                        boxWidth,
                        boxHeight);
                    if (occupied.All(existing => !existing.IntersectsWith(candidate)))
                    {
                        chosen = candidate;
                        break;
                    }
                }
            }

            if (chosen.IsEmpty)
            {
                chosen = !fallbackInView.IsEmpty
                    ? fallbackInView
                    : new RectangleF(anchor.X + 9.0f, anchor.Y + 7.0f, boxWidth, boxHeight);
            }

            occupied.Add(chosen);
            using (Brush background = new SolidBrush(Color.FromArgb(emphasize ? 225 : 190, 18, 20, 23)))
            using (Brush foreground = new SolidBrush(color))
            {
                graphics.FillRectangle(background, chosen);
                if (emphasize)
                {
                    using (Pen outline = new Pen(Color.FromArgb(225, color), 1.2f))
                    {
                        graphics.DrawRectangle(outline, chosen.X, chosen.Y, chosen.Width, chosen.Height);
                    }
                }
                graphics.DrawString(text, Font, foreground, chosen.X + 4.0f, chosen.Y + 1.0f);
            }
            return chosen;
        }

        private static void DrawLockGlyph(Graphics graphics, PointF point, Color color)
        {
            RectangleF body = new RectangleF(point.X - 5.0f, point.Y - 2.0f, 10.0f, 8.0f);
            using (Pen pen = new Pen(color, 1.5f))
            using (Brush brush = new SolidBrush(Color.FromArgb(210, 25, 27, 30)))
            {
                graphics.FillRectangle(brush, body);
                graphics.DrawRectangle(pen, body.X, body.Y, body.Width, body.Height);
                graphics.DrawArc(pen, point.X - 4.0f, point.Y - 8.0f, 8.0f, 9.0f, 180.0f, 180.0f);
            }
        }

        private void BeginEditTransaction()
        {
            EventHandler handler = EditTransactionStarted;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void UpdateCreatedRoi(ImageCoordinate current)
        {
            double halfWidth = Math.Abs(current.Column - _dragStartImage.Column) * 0.5;
            double halfHeight = Math.Abs(current.Row - _dragStartImage.Row) * 0.5;
            if (halfWidth < 0.5 && halfHeight < 0.5)
            {
                _creatingRoiDraft = null;
                _transactionChanged = false;
                Invalidate();
                return;
            }

            double centerRow = (_dragStartImage.Row + current.Row) * 0.5;
            double centerColumn = (_dragStartImage.Column + current.Column) * 0.5;
            _creatingRoiDraft = new RotatedRectangle(
                centerRow,
                centerColumn,
                Math.Max(1.0, halfWidth),
                Math.Max(1.0, halfHeight),
                0.0);
            // 至少 4×4 图像像素（半宽 >= 2 && 半高 >= 2）才视为有效绘制，杜绝误点生成默认大框
            _transactionChanged = halfWidth >= 2.0 && halfHeight >= 2.0;
            Invalidate();
        }

        private void UpdateMovedRoi(ImageCoordinate current)
        {
            if (_dragStartRoi == null)
            {
                return;
            }

            RotatedRectangle roi = _dragStartRoi.DeepClone();
            roi.CenterRow += current.Row - _dragStartImage.Row;
            roi.CenterColumn += current.Column - _dragStartImage.Column;
            ClampRoiCenter(roi);
            SetRoi(_selectedRoi, roi);
            _transactionChanged = true;
            if (_selectedRoi == CanvasRoiTarget.Template && !AnchorLocked)
            {
                _anchor = ClampToImage(new ImageCoordinate(
                    _dragStartAnchorForTemplate.Row + (roi.CenterRow - _dragStartRoi.CenterRow),
                    _dragStartAnchorForTemplate.Column + (roi.CenterColumn - _dragStartRoi.CenterColumn)));
                OnAnchorChanged(false);
            }
            OnRoiChanged(_selectedRoi, false);
        }

        private void UpdateResizedRoi(ImageCoordinate current)
        {
            if (_dragStartRoi == null)
            {
                return;
            }

            // 将当前指针映射回拖拽初值 ROI 的局部坐标系，保持对侧边固定后再反算新中心
            PointF local = _dragStartRoi.ImageToLocal(current);
            double halfWidth = _dragStartRoi.HalfWidth;
            double halfHeight = _dragStartRoi.HalfHeight;
            double centerLocalColumn = 0.0;
            double centerLocalRow = 0.0;
            const double minimumSize = 2.0;

            if (_activeHandle.Horizontal != 0)
            {
                double opposite = -_activeHandle.Horizontal * _dragStartRoi.HalfWidth;
                double moving = local.X;
                moving = _activeHandle.Horizontal > 0
                    ? Math.Max(opposite + (minimumSize * 2.0), moving)
                    : Math.Min(opposite - (minimumSize * 2.0), moving);
                halfWidth = Math.Abs(moving - opposite) / 2.0;
                centerLocalColumn = (moving + opposite) / 2.0;
            }

            if (_activeHandle.Vertical != 0)
            {
                double opposite = -_activeHandle.Vertical * _dragStartRoi.HalfHeight;
                double moving = local.Y;
                moving = _activeHandle.Vertical > 0
                    ? Math.Max(opposite + (minimumSize * 2.0), moving)
                    : Math.Min(opposite - (minimumSize * 2.0), moving);
                halfHeight = Math.Abs(moving - opposite) / 2.0;
                centerLocalRow = (moving + opposite) / 2.0;
            }

            ImageCoordinate center = _dragStartRoi.LocalToImage(centerLocalColumn, centerLocalRow);
            RotatedRectangle roi = new RotatedRectangle(
                center.Row,
                center.Column,
                halfWidth,
                halfHeight,
                _dragStartRoi.AngleDegrees);
            ClampRoiCenter(roi);
            SetRoi(_selectedRoi, roi);
            _transactionChanged = true;
            OnRoiChanged(_selectedRoi, false);
        }

        private void UpdateRotatedRoi(ImageCoordinate current)
        {
            if (_dragStartRoi == null)
            {
                return;
            }

            double centerDeltaColumn = current.Column - _dragStartRoi.CenterColumn;
            double centerDeltaRow = current.Row - _dragStartRoi.CenterRow;
            if ((centerDeltaColumn * centerDeltaColumn) + (centerDeltaRow * centerDeltaRow) < 0.0001)
            {
                return;
            }

            // 按指针相对 ROI 中心的方位角增量旋转，保证拖拽手感连续无跳变
            double currentPointerAngleDegrees = GetPointerAngleDegrees(_dragStartRoi, current);
            double pointerDeltaDegrees = RotatedRectangle.NormalizeAngle(
                currentPointerAngleDegrees - _dragStartPointerAngleDegrees);
            RotatedRectangle roi = _dragStartRoi.DeepClone();
            roi.AngleDegrees = RotatedRectangle.NormalizeAngle(
                _dragStartRoi.AngleDegrees + pointerDeltaDegrees);
            ClampRoiCenter(roi);
            SetRoi(_selectedRoi, roi);
            _transactionChanged = true;
            if (_selectedRoi == CanvasRoiTarget.Template && !AnchorLocked)
            {
                ImageCoordinate rotated = RotatePointAround(
                    _dragStartAnchorForTemplate,
                    _dragStartRoi.CenterRow,
                    _dragStartRoi.CenterColumn,
                    pointerDeltaDegrees);
                _anchor = ClampToImage(new ImageCoordinate(
                    rotated.Row + (roi.CenterRow - _dragStartRoi.CenterRow),
                    rotated.Column + (roi.CenterColumn - _dragStartRoi.CenterColumn)));
                OnAnchorChanged(false);
            }
            OnRoiChanged(_selectedRoi, false);
        }

        private void AppendStrokePoint(ImageCoordinate current, Rectangle cursorDirty)
        {
            if (_activeStroke == null)
            {
                if (!cursorDirty.IsEmpty) Invalidate(cursorDirty);
                return;
            }

            ImageCoordinate previous = _activeStroke.Points[_activeStroke.Points.Count - 1];
            double deltaRow = current.Row - previous.Row;
            double deltaColumn = current.Column - previous.Column;
            double minimumDistance = Math.Max(0.5, _activeStroke.Radius * 0.12);
            if ((deltaRow * deltaRow) + (deltaColumn * deltaColumn) < minimumDistance * minimumDistance)
            {
                // 即使未达到笔画采样阈值，仍刷新画笔虚线圈脏区，防止慢速移动或越界拖拽时残留圆环拖影
                if (!cursorDirty.IsEmpty) Invalidate(cursorDirty);
                return;
            }

            _activeStroke.Points.Add(current);
            _transactionChanged = true;
            PointF start = ImageToClient(previous);
            PointF end = ImageToClient(current);
            float margin = (float)Math.Max(2.0, _activeStroke.Radius * _zoom) + 6.0f;
            OnMaskPreviewChanged(_activeStroke, _activeStroke.IsErase, previous, current);
            Rectangle strokeDirty = ToEnclosingRectangle(RectangleF.FromLTRB(
                Math.Min(start.X, end.X) - margin,
                Math.Min(start.Y, end.Y) - margin,
                Math.Max(start.X, end.X) + margin,
                Math.Max(start.Y, end.Y) + margin));
            Rectangle totalDirty = cursorDirty.IsEmpty
                ? strokeDirty
                : (strokeDirty.IsEmpty ? cursorDirty : Rectangle.Union(strokeDirty, cursorDirty));
            if (!totalDirty.IsEmpty)
            {
                Invalidate(totalDirty);
            }
        }

        private void UpdateMaskRectangle(ImageCoordinate current)
        {
            if (_activeStroke == null) return;
            double halfW = Math.Abs(current.Column - _dragStartImage.Column) * 0.5;
            double halfH = Math.Abs(current.Row - _dragStartImage.Row) * 0.5;
            _activeStroke.Rectangle = new RotatedRectangle(
                (_dragStartImage.Row + current.Row) * 0.5,
                (_dragStartImage.Column + current.Column) * 0.5,
                Math.Max(1.0, halfW),
                Math.Max(1.0, halfH),
                0.0);
            _transactionChanged = halfW >= 2.0 && halfH >= 2.0;
            OnMaskPreviewChanged(_activeStroke);
            InvalidateActiveMaskBounds();
        }

        private void UpdateMaskCircle(ImageCoordinate current)
        {
            if (_activeStroke == null) return;
            double dr = current.Row - _dragStartImage.Row;
            double dc = current.Column - _dragStartImage.Column;
            double radius = Math.Sqrt((dr * dr) + (dc * dc));
            _activeStroke.Radius = Math.Max(1.0, radius);
            _transactionChanged = radius >= 2.0;
            OnMaskPreviewChanged(_activeStroke);
            InvalidateActiveMaskBounds();
        }

        private void InvalidateActiveMaskBounds()
        {
            Rectangle nextRect = Rectangle.Empty;
            if (_activeStroke != null)
            {
                double minRow, maxRow, minCol, maxCol;
                if (_activeStroke.TryGetBounds(out minRow, out maxRow, out minCol, out maxCol))
                {
                    PointF tl = ImageToClient(new ImageCoordinate(minRow, minCol));
                    PointF br = ImageToClient(new ImageCoordinate(maxRow, maxCol));
                    nextRect = ToEnclosingRectangle(RectangleF.FromLTRB(
                        Math.Min(tl.X, br.X) - 10.0f,
                        Math.Min(tl.Y, br.Y) - 10.0f,
                        Math.Max(tl.X, br.X) + 10.0f,
                        Math.Max(tl.Y, br.Y) + 10.0f));
                }
            }

            Rectangle dirty = _lastActiveMaskRect.IsEmpty
                ? nextRect
                : (nextRect.IsEmpty ? _lastActiveMaskRect : Rectangle.Union(_lastActiveMaskRect, nextRect));
            _lastActiveMaskRect = nextRect;
            if (dirty.IsEmpty) Invalidate();
            else Invalidate(dirty);
        }

        private void UpdatePolygonPreviewMask()
        {
            if (_polygonDrawing && _activePolygonPoints.Count >= 3)
            {
                _activeStroke = new MaskStroke
                {
                    Kind = MaskShapeKind.Polygon,
                    IsErase = true,
                    Points = new List<ImageCoordinate>(_activePolygonPoints)
                };
                OnMaskPreviewChanged(_activeStroke);
            }
            else
            {
                _activeStroke = null;
                OnMaskPreviewChanged(null);
            }
        }

        private void CommitActivePolygon()
        {
            if (_activePolygonPoints.Count < 3)
            {
                CancelActivePolygon(true);
                return;
            }

            BeginEditTransaction();
            MaskStroke completed = new MaskStroke
            {
                Kind = MaskShapeKind.Polygon,
                IsErase = true,
                Points = new List<ImageCoordinate>(_activePolygonPoints)
            };
            _polygonDrawing = false;
            _activePolygonPoints.Clear();
            _activeStroke = null;
            ResetLiveMaskState();
            _maskStrokes.Add(completed);
            EventHandler<MaskStrokeEventArgs> strokeHandler = MaskStrokeCreated;
            if (strokeHandler != null)
            {
                strokeHandler(this, new MaskStrokeEventArgs(completed));
            }
            Invalidate();
        }

        private void CancelActivePolygon(bool notifyPreview)
        {
            if (!_polygonDrawing && _activePolygonPoints.Count == 0) return;
            _polygonDrawing = false;
            _activePolygonPoints.Clear();
            _activeStroke = null;
            ResetLiveMaskState();
            if (notifyPreview)
            {
                OnMaskPreviewChanged(null);
            }
            Invalidate();
        }

        private void FinishDrag(bool commit)
        {
            DragOperation operation = _dragOperation;
            CanvasRoiTarget creatingTarget = _creatingRoiTarget;
            RotatedRectangle creatingDraft = _creatingRoiDraft;
            _dragOperation = DragOperation.None;
            _creatingRoiTarget = CanvasRoiTarget.None;
            _creatingRoiDraft = null;
            if (Capture)
            {
                Capture = false;
            }

            Cursor = GetDefaultCursor();
            _lastBrushCursorRect = _hasHoverScreen && (_tool == CanvasEditTool.EraseMask || _tool == CanvasEditTool.RestoreMask)
                ? GetBrushCursorDirtyRect(_hoverScreen)
                : Rectangle.Empty;
            if (!commit)
            {
                _activeStroke = null;
                _lastActiveMaskRect = Rectangle.Empty;
                ResetLiveMaskState();
                OnMaskPreviewChanged(null);
                Invalidate();
                return;
            }

            if (operation == DragOperation.Pan && _maskRefreshPending)
            {
                _maskRefreshTimer.Stop();
                _maskRefreshPending = false;
            }

            if (operation == DragOperation.CreateRoi)
            {
                if (!_dragThresholdExceeded ||
                    !_transactionChanged ||
                    creatingDraft == null ||
                    creatingTarget == CanvasRoiTarget.None ||
                    creatingDraft.HalfWidth < 2.0 ||
                    creatingDraft.HalfHeight < 2.0)
                {
                    // 单击或框选过小时不自动生成默认大小 ROI，保留当前绘制模式以便继续拖动框选
                    if (creatingTarget != CanvasRoiTarget.None)
                    {
                        OnRoiDrawRejected(creatingTarget);
                    }
                    Invalidate();
                    return;
                }

                BeginEditTransaction();
                ClampRoiCenter(creatingDraft);
                SetRoi(creatingTarget, creatingDraft);
                _anchorSelected = false;
                SelectedRoi = creatingTarget;
                if (creatingTarget == CanvasRoiTarget.Template && !AnchorLocked)
                {
                    // 手动框选或重绘模板 ROI 后，自动将未锁定的基准中心置于新模板几何中心
                    _anchor = ClampToImage(new ImageCoordinate(creatingDraft.CenterRow, creatingDraft.CenterColumn));
                    OnAnchorChanged(true);
                }

                OnRoiChanged(creatingTarget, true);
                if (IsDrawRoiTool(_tool))
                {
                    // 画完即切回选择模式，防止继续拖动时意外覆盖刚画好的 ROI
                    _tool = CanvasEditTool.Select;
                    Cursor = GetDefaultCursor();
                    OnToolChanged();
                }
                Invalidate();
                return;
            }

            if (!_transactionChanged)
            {
                _activeStroke = null;
                _lastActiveMaskRect = Rectangle.Empty;
                ResetLiveMaskState();
                if (operation == DragOperation.MaskRect || operation == DragOperation.MaskCircle)
                {
                    OnMaskPreviewChanged(null);
                }
                // 在重叠 ROI 边框/本体上原地单击（未拖动）且未刚切换到新框时，依次循环切换该位置下的所有重叠 ROI
                if ((operation == DragOperation.MoveRoi || operation == DragOperation.SelectRoi) &&
                    (_dragStartHitKind == CanvasHitKind.RoiBody || _dragStartHitKind == CanvasHitKind.RoiBorder) &&
                    _selectedRoi == _selectionAtMouseDown)
                {
                    CycleOverlappingRoiSelection(_dragStartScreen, _dragStartHitKind);
                }
                Invalidate();
                return;
            }

            if ((operation == DragOperation.Mask || operation == DragOperation.MaskRect || operation == DragOperation.MaskCircle) && _activeStroke != null)
            {
                MaskStroke completed = _activeStroke;
                _maskStrokes.Add(completed);
                _activeStroke = null;
                _lastActiveMaskRect = Rectangle.Empty;

                // 画笔结束提交时，若离线 _maskLive 已基于最新 _maskHistory 同步，补齐尾段并 O(1) 交换缓冲，
                // 消除松手瞬间的重复栅格化与任何视觉跳变。
                if (operation == DragOperation.Mask &&
                    _liveBaseSynced &&
                    ReferenceEquals(_liveStrokeOwner, completed) &&
                    _maskHistory != null &&
                    _maskLive != null &&
                    _maskHistory.Size == ClientSize &&
                    _maskLive.Size == ClientSize &&
                    _maskZoom == _zoom &&
                    _maskX == _offsetX &&
                    _maskY == _offsetY &&
                    _paintedStrokes == _maskStrokes.Count - 1)
                {
                    if (_paintedLivePoints < completed.Points.Count)
                    {
                        using (Graphics live = Graphics.FromImage(_maskLive))
                        {
                            live.SmoothingMode = SmoothingMode.None;
                            live.PixelOffsetMode = PixelOffsetMode.Half;
                            live.CompositingMode = CompositingMode.SourceCopy;
                            DrawMaskItem(live, completed, Math.Max(0, _paintedLivePoints - 1), true);
                        }
                    }
                    Bitmap swapped = _maskHistory;
                    _maskHistory = _maskLive;
                    _maskLive = swapped;
                    _paintedStrokes = _maskStrokes.Count;
                }

                ResetLiveMaskState();
                EventHandler<MaskStrokeEventArgs> strokeHandler = MaskStrokeCreated;
                if (strokeHandler != null)
                {
                    strokeHandler(this, new MaskStrokeEventArgs(completed));
                }
            }
            else if (operation == DragOperation.Anchor)
            {
                OnAnchorChanged(true);
            }
            else if (operation == DragOperation.MoveRoi ||
                     operation == DragOperation.ResizeRoi ||
                     operation == DragOperation.RotateRoi)
            {
                if (_selectedRoi == CanvasRoiTarget.Template && !AnchorLocked &&
                    (operation == DragOperation.MoveRoi || operation == DragOperation.RotateRoi))
                {
                    OnAnchorChanged(true);
                }
                OnRoiChanged(_selectedRoi, true);
            }

            Invalidate();
        }

        /// <summary>
        /// 删除当前选中的非锁定 ROI，并同步更新选中焦点与编辑历史。
        /// </summary>
        public bool DeleteSelectedRoi()
        {
            if (_selectedRoi == CanvasRoiTarget.None || GetRoi(_selectedRoi) == null || IsTargetLocked(_selectedRoi))
            {
                return false;
            }

            BeginEditTransaction();
            _hasCycleAnchor = false;
            CanvasRoiTarget removed = _selectedRoi;
            SetRoi(removed, null);
            if (removed == CanvasRoiTarget.Template && !AnchorLocked)
            {
                _anchor = new ImageCoordinate(0.0, 0.0);
                OnAnchorChanged(true);
            }

            SelectedRoi = _templateRoi != null
                ? CanvasRoiTarget.Template
                : _detectionRoi != null
                    ? CanvasRoiTarget.Detection
                    : _searchRoi != null ? CanvasRoiTarget.Search : CanvasRoiTarget.None;
            OnRoiChanged(removed, true);
            return true;
        }

        private void CycleOverlappingRoiSelection(Point location, CanvasHitKind hitKind)
        {
            if (_tool != CanvasEditTool.Select)
            {
                return;
            }

            List<CanvasRoiTarget> hits = null;
            if (hitKind == CanvasHitKind.RoiBorder)
            {
                List<CanvasRoiTarget> coincidentBorders = GetCoincidentBorderTargets(location);
                if (coincidentBorders.Count > 1)
                {
                    hits = coincidentBorders;
                }
            }

            if (hits == null || hits.Count <= 1)
            {
                ImageCoordinate imagePoint = ClientToImage(location);
                hits = new List<CanvasRoiTarget>();
                foreach (CanvasRoiTarget target in RoiBodyHitOrder)
                {
                    RotatedRectangle candidate = GetRoi(target);
                    if (candidate != null && candidate.Contains(imagePoint))
                    {
                        hits.Add(target);
                    }
                }
                hits.Sort((a, b) => CompareRoiBodyPriority(a, b));
            }

            if (hits.Count <= 1)
            {
                return;
            }

            int selectedIndex = hits.IndexOf(_selectionAtMouseDown);
            SelectedRoi = selectedIndex < 0
                ? hits[0]
                : hits[(selectedIndex + 1) % hits.Count];
            _cycleAnchorScreen = location;
            _hasCycleAnchor = true;
        }

        private List<CanvasRoiTarget> GetCoincidentBorderTargets(Point location)
        {
            double maxDistSq = BorderHitDistance * BorderHitDistance;
            double bestDist = double.PositiveInfinity;
            List<KeyValuePair<CanvasRoiTarget, double>> candidates = new List<KeyValuePair<CanvasRoiTarget, double>>();
            foreach (CanvasRoiTarget target in RoiBodyHitOrder)
            {
                RotatedRectangle roi = GetRoi(target);
                if (roi == null) continue;
                double distSq = GetDistanceSquaredToBorder(roi, location);
                if (distSq <= maxDistSq)
                {
                    double dist = Math.Sqrt(distSq);
                    if (dist < bestDist) bestDist = dist;
                    candidates.Add(new KeyValuePair<CanvasRoiTarget, double>(target, dist));
                }
            }

            List<CanvasRoiTarget> coincident = new List<CanvasRoiTarget>();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Value - bestDist <= CoincidentBorderTolerance)
                {
                    coincident.Add(candidates[i].Key);
                }
            }
            coincident.Sort((a, b) => CompareRoiBodyPriority(a, b));
            return coincident;
        }

        private void StartRoiEditDrag(RoiHit hit)
        {
            _dragStartRoi = GetRoi(hit.Target).DeepClone();
            _dragStartAnchorForTemplate = _anchor;
            _activeHandle = hit.Handle;
            if (hit.Handle.Kind == RoiHandleKind.Rotate)
            {
                BeginEditTransaction();
                _dragThresholdExceeded = true;
                _dragStartPointerAngleDegrees = GetPointerAngleDegrees(_dragStartRoi, _dragStartImage);
                _dragOperation = DragOperation.RotateRoi;
            }
            else if (hit.Handle.Kind == RoiHandleKind.Resize)
            {
                BeginEditTransaction();
                _dragThresholdExceeded = true;
                _dragOperation = DragOperation.ResizeRoi;
            }
            else
            {
                _dragThresholdExceeded = false;
                _dragOperation = DragOperation.MoveRoi;
            }

            Capture = true;
        }

        private RoiHit HitSelectedHandle(Point location)
        {
            if (_selectedRoi == CanvasRoiTarget.None || IsTargetLocked(_selectedRoi))
            {
                return RoiHit.None;
            }

            RotatedRectangle selected = GetRoi(_selectedRoi);
            RoiHandle handle = HitHandle(selected, location);
            return handle.Kind != RoiHandleKind.None ? new RoiHit(_selectedRoi, handle) : RoiHit.None;
        }

        private InteractiveHit HitTestInteractive(Point location)
        {
            if (_image == null || !ShowRois)
            {
                return InteractiveHit.None;
            }

            // 1. 最高优先级：当前选中且未锁定 ROI 的缩放/旋转控制柄
            RoiHit handleHit = HitSelectedHandle(location);
            if (handleHit.Target != CanvasRoiTarget.None)
            {
                return new InteractiveHit(CanvasHitKind.RoiHandle, handleHit.Target, handleHit.Handle);
            }

            // 2. 次高优先级：点击可见标签徽章可直接精准选中对应 ROI 或基准中心（完全避免重叠误选）
            CanvasRoiTarget labelHit = HitTestRoiLabel(location);
            if (labelHit != CanvasRoiTarget.None)
            {
                return new InteractiveHit(CanvasHitKind.RoiLabel, labelHit, RoiHandle.Body);
            }
            if (HasVisibleAnchor && !AnchorLocked && !_renderedAnchorLabelRect.IsEmpty && _renderedAnchorLabelRect.Contains(location))
            {
                return new InteractiveHit(CanvasHitKind.AnchorLabel, CanvasRoiTarget.None, RoiHandle.None);
            }

            // 3. 若当前已选中基准中心，优先允许继续拖拽基准中心
            if (HasVisibleAnchor && !AnchorLocked && _anchorSelected && HitAnchor(location))
            {
                return new InteractiveHit(CanvasHitKind.Anchor, CanvasRoiTarget.None, RoiHandle.None);
            }

            bool atCycleSpot = _hasCycleAnchor && IsNearPoint(location, _cycleAnchorScreen, CycleClickRadiusPixels);

            // 4. ROI 边框命中（±7px）：取屏幕距离最近的边框；重合边框间支持保持当前选中或原地点击循环
            List<CanvasRoiTarget> coincidentBorders = GetCoincidentBorderTargets(location);
            if (coincidentBorders.Count > 0)
            {
                if (_selectedRoi != CanvasRoiTarget.None && coincidentBorders.Contains(_selectedRoi) &&
                    (atCycleSpot || !IsTargetLocked(_selectedRoi) || coincidentBorders.All(IsTargetLocked)))
                {
                    return new InteractiveHit(CanvasHitKind.RoiBorder, _selectedRoi, RoiHandle.Body);
                }
                return new InteractiveHit(CanvasHitKind.RoiBorder, coincidentBorders[0], RoiHandle.Body);
            }

            // 5. 基准中心点命中：仅当中心不遮挡模板 ROI 几何中心（位于 ROI 外，或已相对模板中心偏移且模板在屏幕上足够大）时直接命中，
            //    彻底避免拖拽模板 ROI 中心区域时误将基准中心拖偏。
            if (HasVisibleAnchor && !AnchorLocked && HitAnchor(location) && CanDirectlyHitAnchorInSelectMode())
            {
                return new InteractiveHit(CanvasHitKind.Anchor, CanvasRoiTarget.None, RoiHandle.None);
            }

            // 6. ROI 内部区域命中：未锁定优先、内层小框优先；原地连续点击保持当前选中以支持完整循环并允许直接拖动刚切换到的 ROI
            ImageCoordinate imagePoint = ClientToImage(location);
            List<CanvasRoiTarget> bodyHits = new List<CanvasRoiTarget>();
            foreach (CanvasRoiTarget target in RoiBodyHitOrder)
            {
                RotatedRectangle candidate = GetRoi(target);
                if (candidate != null && candidate.Contains(imagePoint))
                {
                    bodyHits.Add(target);
                }
            }
            if (bodyHits.Count == 0)
            {
                return InteractiveHit.None;
            }

            bodyHits.Sort((a, b) => CompareRoiBodyPriority(a, b));
            if (atCycleSpot && _selectedRoi != CanvasRoiTarget.None && bodyHits.Contains(_selectedRoi))
            {
                return new InteractiveHit(CanvasHitKind.RoiBody, _selectedRoi, RoiHandle.Body);
            }

            CanvasRoiTarget top = bodyHits[0];
            if (_selectedRoi != CanvasRoiTarget.None &&
                bodyHits.Contains(_selectedRoi) &&
                !IsTargetLocked(_selectedRoi) &&
                GetRoiArea(GetRoi(_selectedRoi)) <= GetRoiArea(GetRoi(top)) * 1.15)
            {
                return new InteractiveHit(CanvasHitKind.RoiBody, _selectedRoi, RoiHandle.Body);
            }

            return new InteractiveHit(CanvasHitKind.RoiBody, top, RoiHandle.Body);
        }

        private CanvasRoiTarget HitTestRoiLabel(Point location)
        {
            if (_renderedRoiLabelRects.Count == 0)
            {
                return CanvasRoiTarget.None;
            }

            // 按渲染置顶逆序检查：当前选中 ROI 标签最先，随后 Template -> Detection -> Search
            if (_selectedRoi != CanvasRoiTarget.None)
            {
                RectangleF selectedRect;
                if (_renderedRoiLabelRects.TryGetValue(_selectedRoi, out selectedRect) && selectedRect.Contains(location))
                {
                    return _selectedRoi;
                }
            }

            foreach (CanvasRoiTarget target in RoiBodyHitOrder)
            {
                if (target == _selectedRoi) continue;
                RectangleF rect;
                if (_renderedRoiLabelRects.TryGetValue(target, out rect) && rect.Contains(location))
                {
                    return target;
                }
            }

            return CanvasRoiTarget.None;
        }

        private bool CanDirectlyHitAnchorInSelectMode()
        {
            if (_templateRoi == null)
            {
                return true;
            }

            if (!_templateRoi.Contains(_anchor))
            {
                return true;
            }

            double screenWidth = _templateRoi.HalfWidth * 2.0 * _zoom;
            double screenHeight = _templateRoi.HalfHeight * 2.0 * _zoom;
            if (screenWidth < 80.0 || screenHeight < 80.0)
            {
                return false;
            }

            PointF anchorClient = ImageToClient(_anchor);
            PointF centerClient = ImageToClient(new ImageCoordinate(_templateRoi.CenterRow, _templateRoi.CenterColumn));
            double dx = anchorClient.X - centerClient.X;
            double dy = anchorClient.Y - centerClient.Y;
            return ((dx * dx) + (dy * dy)) >= 144.0;
        }

        private int CompareRoiBodyPriority(CanvasRoiTarget a, CanvasRoiTarget b)
        {
            bool lockedA = IsTargetLocked(a);
            bool lockedB = IsTargetLocked(b);
            if (lockedA != lockedB)
            {
                return lockedA ? 1 : -1;
            }

            return CompareRoiPriority(a, b);
        }

        private int CompareRoiPriority(CanvasRoiTarget a, CanvasRoiTarget b)
        {
            double areaA = GetRoiArea(GetRoi(a));
            double areaB = GetRoiArea(GetRoi(b));
            double ratio = Math.Min(areaA, areaB) > 0.0 ? Math.Max(areaA, areaB) / Math.Min(areaA, areaB) : 1.0;
            if (ratio > 1.15)
            {
                return areaA.CompareTo(areaB);
            }
            return Array.IndexOf(RoiBodyHitOrder, a).CompareTo(Array.IndexOf(RoiBodyHitOrder, b));
        }

        private static double GetRoiArea(RotatedRectangle roi)
        {
            return roi == null ? double.PositiveInfinity : Math.Max(1.0, roi.HalfWidth * roi.HalfHeight * 4.0);
        }

        private double GetDistanceSquaredToBorder(RotatedRectangle roi, Point location)
        {
            PointF[] corners = roi.GetCorners().Select(ImageToClient).ToArray();
            double best = double.PositiveInfinity;
            for (int i = 0; i < 4; i++)
            {
                PointF a = corners[i];
                PointF b = corners[(i + 1) % 4];
                double distSq = DistanceSquaredToSegment(location, a, b);
                if (distSq < best)
                {
                    best = distSq;
                }
            }
            return best;
        }

        private static double DistanceSquaredToSegment(Point p, PointF a, PointF b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double len2 = (dx * dx) + (dy * dy);
            if (len2 <= 1e-6)
            {
                double px = p.X - a.X;
                double py = p.Y - a.Y;
                return (px * px) + (py * py);
            }
            double t = (((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / len2;
            if (t < 0.0) t = 0.0;
            else if (t > 1.0) t = 1.0;
            double ex = p.X - (a.X + (t * dx));
            double ey = p.Y - (a.Y + (t * dy));
            return (ex * ex) + (ey * ey);
        }

        private bool ExceededDragThreshold(Point location)
        {
            double dx = location.X - _dragStartScreen.X;
            double dy = location.Y - _dragStartScreen.Y;
            return (dx * dx) + (dy * dy) >= DragThresholdPixels * DragThresholdPixels;
        }

        private static bool IsNearPoint(Point a, Point b, double radiusPixels)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return (dx * dx) + (dy * dy) <= radiusPixels * radiusPixels;
        }

        private static ImageCoordinate RotatePointAround(ImageCoordinate point, double centerRow, double centerCol, double deltaDegrees)
        {
            double rad = deltaDegrees * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);
            double dc = point.Column - centerCol;
            double dr = point.Row - centerRow;
            return new ImageCoordinate(
                centerRow + (dc * sin) + (dr * cos),
                centerCol + (dc * cos) - (dr * sin));
        }

        private Rectangle GetBrushCursorDirtyRect(Point location)
        {
            int radius = (int)Math.Ceiling(Math.Max(2.0, BrushRadiusImage * _zoom)) + 6;
            return new Rectangle(location.X - radius, location.Y - radius, (radius * 2) + 1, (radius * 2) + 1);
        }

        private void UpdateHoverAndCursor(Point location)
        {
            if (_image == null)
            {
                Cursor = Cursors.Default;
                SetHoverState(CanvasRoiTarget.None, CanvasRoiTarget.None, false, false);
                return;
            }

            if (_tool == CanvasEditTool.EraseMask ||
                _tool == CanvasEditTool.RestoreMask ||
                _tool == CanvasEditTool.MaskRectangle ||
                _tool == CanvasEditTool.MaskCircle ||
                _tool == CanvasEditTool.MaskPolygon ||
                IsDrawRoiTool(_tool))
            {
                Cursor = Cursors.Cross;
                SetHoverState(CanvasRoiTarget.None, CanvasRoiTarget.None, false, false);
                return;
            }

            if (_tool == CanvasEditTool.MoveAnchor)
            {
                bool activeAnchor = HasVisibleAnchor && !AnchorLocked;
                Cursor = activeAnchor ? Cursors.Cross : Cursors.Default;
                SetHoverState(CanvasRoiTarget.None, CanvasRoiTarget.None, activeAnchor, false);
                return;
            }

            InteractiveHit hit = HitTestInteractive(location);
            switch (hit.Kind)
            {
                case CanvasHitKind.RoiHandle:
                    RotatedRectangle handleRoi = GetRoi(hit.Target);
                    Cursor = hit.Handle.Kind == RoiHandleKind.Rotate
                        ? Cursors.Cross
                        : GetResizeCursorForHandle(handleRoi, hit.Handle);
                    SetHoverState(hit.Target, CanvasRoiTarget.None, false, false);
                    return;
                case CanvasHitKind.RoiLabel:
                    Cursor = Cursors.Hand;
                    SetHoverState(hit.Target, hit.Target, false, false);
                    return;
                case CanvasHitKind.AnchorLabel:
                    Cursor = Cursors.Hand;
                    SetHoverState(CanvasRoiTarget.None, CanvasRoiTarget.None, true, true);
                    return;
                case CanvasHitKind.Anchor:
                    Cursor = Cursors.SizeAll;
                    SetHoverState(CanvasRoiTarget.None, CanvasRoiTarget.None, true, false);
                    return;
                case CanvasHitKind.RoiBorder:
                case CanvasHitKind.RoiBody:
                    SetHoverState(hit.Target, CanvasRoiTarget.None, false, false);
                    Cursor = IsTargetLocked(hit.Target) ? Cursors.Default : Cursors.SizeAll;
                    return;
                default:
                    SetHoverState(CanvasRoiTarget.None, CanvasRoiTarget.None, false, false);
                    Cursor = Cursors.Default;
                    return;
            }
        }

        private void SetHoverState(CanvasRoiTarget nextRoi, CanvasRoiTarget nextLabelRoi, bool nextAnchor, bool nextAnchorLabel)
        {
            if (nextRoi != _hoveredRoi ||
                nextLabelRoi != _hoveredLabelRoi ||
                nextAnchor != _anchorHovered ||
                nextAnchorLabel != _hoveredAnchorLabel)
            {
                _hoveredRoi = nextRoi;
                _hoveredLabelRoi = nextLabelRoi;
                _anchorHovered = nextAnchor;
                _hoveredAnchorLabel = nextAnchorLabel;
                Invalidate();
            }
        }

        private static Cursor GetResizeCursorForHandle(RotatedRectangle roi, RoiHandle handle)
        {
            double angleRad = roi == null ? 0.0 : roi.AngleDegrees * Math.PI / 180.0;
            double cos = Math.Cos(angleRad);
            double sin = Math.Sin(angleRad);
            double sx = (handle.Horizontal * cos) - (handle.Vertical * sin);
            double sy = (handle.Horizontal * sin) + (handle.Vertical * cos);
            double deg = Math.Atan2(sy, sx) * 180.0 / Math.PI;
            deg = ((deg % 180.0) + 180.0) % 180.0;
            if (deg < 22.5 || deg >= 157.5) return Cursors.SizeWE;
            if (deg < 67.5) return Cursors.SizeNWSE;
            if (deg < 112.5) return Cursors.SizeNS;
            return Cursors.SizeNESW;
        }

        private RoiHandle HitHandle(RotatedRectangle roi, Point location)
        {
            if (roi == null)
            {
                return RoiHandle.None;
            }

            // 极小 ROI 在屏幕上按比例收敛角点缩放命中半径，保留中心可拖动空间，同时旋转外柄保持完整命中半径
            double minScreenDim = Math.Min(roi.HalfWidth * 2.0 * _zoom, roi.HalfHeight * 2.0 * _zoom);
            double resizeHitRadius = minScreenDim < 18.0
                ? Math.Max(5.0, minScreenDim * 0.35)
                : HandleHitRadius;
            double resizeHitSq = resizeHitRadius * resizeHitRadius;
            double rotateHitSq = HandleHitRadius * HandleHitRadius;

            RoiHandle closest = RoiHandle.None;
            double closestDistSq = double.PositiveInfinity;
            foreach (RoiHandle handle in GetHandles(roi))
            {
                double deltaX = location.X - handle.Location.X;
                double deltaY = location.Y - handle.Location.Y;
                double distSq = (deltaX * deltaX) + (deltaY * deltaY);
                double limitSq = handle.Kind == RoiHandleKind.Rotate ? rotateHitSq : resizeHitSq;
                if (distSq <= limitSq && distSq < closestDistSq)
                {
                    closestDistSq = distSq;
                    closest = handle;
                }
            }

            return closest;
        }

        private PointF GetRotateHandleLocation(RotatedRectangle roi)
        {
            PointF topMid = ImageToClient(roi.LocalToImage(0.0, -roi.HalfHeight));
            double rad = roi.AngleDegrees * Math.PI / 180.0;
            return new PointF(
                topMid.X + (float)(Math.Sin(rad) * RotateStemLength),
                topMid.Y - (float)(Math.Cos(rad) * RotateStemLength));
        }

        private IEnumerable<RoiHandle> GetHandles(RotatedRectangle roi)
        {
            // 四角缩放柄
            yield return new RoiHandle(RoiHandleKind.Resize, -1, -1, ImageToClient(roi.LocalToImage(-roi.HalfWidth, -roi.HalfHeight)));
            yield return new RoiHandle(RoiHandleKind.Resize, 1, -1, ImageToClient(roi.LocalToImage(roi.HalfWidth, -roi.HalfHeight)));
            yield return new RoiHandle(RoiHandleKind.Resize, 1, 1, ImageToClient(roi.LocalToImage(roi.HalfWidth, roi.HalfHeight)));
            yield return new RoiHandle(RoiHandleKind.Resize, -1, 1, ImageToClient(roi.LocalToImage(-roi.HalfWidth, roi.HalfHeight)));

            // 屏幕尺寸足够时显示四边中点缩放柄，避免极小 ROI 控制柄重叠遮挡本体
            if ((roi.HalfWidth * 2.0 * _zoom) >= 26.0 && (roi.HalfHeight * 2.0 * _zoom) >= 26.0)
            {
                yield return new RoiHandle(RoiHandleKind.Resize, 1, 0, ImageToClient(roi.LocalToImage(roi.HalfWidth, 0.0)));
                yield return new RoiHandle(RoiHandleKind.Resize, -1, 0, ImageToClient(roi.LocalToImage(-roi.HalfWidth, 0.0)));
                yield return new RoiHandle(RoiHandleKind.Resize, 0, 1, ImageToClient(roi.LocalToImage(0.0, roi.HalfHeight)));
                yield return new RoiHandle(RoiHandleKind.Resize, 0, -1, ImageToClient(roi.LocalToImage(0.0, -roi.HalfHeight)));
            }

            // 顶边中点外伸旋转柄
            yield return new RoiHandle(RoiHandleKind.Rotate, 0, -1, GetRotateHandleLocation(roi));
        }

        private static bool IsDrawRoiTool(CanvasEditTool tool)
        {
            return tool == CanvasEditTool.DrawTemplateRoi ||
                   tool == CanvasEditTool.DrawSearchRoi ||
                   tool == CanvasEditTool.DrawDetectionRoi;
        }

        private static CanvasRoiTarget GetDrawToolTarget(CanvasEditTool tool)
        {
            return tool == CanvasEditTool.DrawTemplateRoi
                ? CanvasRoiTarget.Template
                : tool == CanvasEditTool.DrawSearchRoi
                    ? CanvasRoiTarget.Search
                    : tool == CanvasEditTool.DrawDetectionRoi ? CanvasRoiTarget.Detection : CanvasRoiTarget.None;
        }

        private bool HitAnchor(Point point)
        {
            PointF anchor = ImageToClient(_anchor);
            double deltaX = point.X - anchor.X;
            double deltaY = point.Y - anchor.Y;
            return (deltaX * deltaX) + (deltaY * deltaY) <= AnchorHitRadius * AnchorHitRadius;
        }

        private Cursor GetDefaultCursor()
        {
            return _tool == CanvasEditTool.EraseMask ||
                   _tool == CanvasEditTool.RestoreMask ||
                   _tool == CanvasEditTool.MaskRectangle ||
                   _tool == CanvasEditTool.MaskCircle ||
                   _tool == CanvasEditTool.MaskPolygon ||
                   _tool == CanvasEditTool.MoveAnchor ||
                   _tool == CanvasEditTool.DrawTemplateRoi ||
                   _tool == CanvasEditTool.DrawSearchRoi ||
                   _tool == CanvasEditTool.DrawDetectionRoi
                ? Cursors.Cross
                : Cursors.Default;
        }

        private RotatedRectangle GetRoi(CanvasRoiTarget target)
        {
            return target == CanvasRoiTarget.Template
                ? _templateRoi
                : target == CanvasRoiTarget.Search
                    ? _searchRoi
                    : target == CanvasRoiTarget.Detection ? _detectionRoi : null;
        }

        private void SetRoi(CanvasRoiTarget target, RotatedRectangle roi)
        {
            if (target == CanvasRoiTarget.Template)
            {
                _templateRoi = roi;
            }
            else if (target == CanvasRoiTarget.Search)
            {
                _searchRoi = roi;
            }
            else if (target == CanvasRoiTarget.Detection)
            {
                _detectionRoi = roi;
            }

            Invalidate();
        }

        private bool IsTargetLocked(CanvasRoiTarget target)
        {
            return target == CanvasRoiTarget.Template
                ? TemplateRoiLocked
                : target == CanvasRoiTarget.Search && SearchRoiLocked;
        }

        private ImageCoordinate ClampToImage(ImageCoordinate point)
        {
            if (_image == null)
            {
                return point;
            }

            return new ImageCoordinate(
                Clamp(point.Row, 0.0, Math.Max(0.0, _image.Height - 1.0)),
                Clamp(point.Column, 0.0, Math.Max(0.0, _image.Width - 1.0)));
        }

        private void ClampRoiCenter(RotatedRectangle roi)
        {
            if (_image == null || roi == null)
            {
                return;
            }

            // 根据旋转后的轴对齐外接框将 ROI 限制在图像边界内；若外接框宽/高超过图像则自动居中
            RectangleF bounds = roi.GetAxisAlignedBounds();
            double maximumRow = Math.Max(0.0, _image.Height - 1.0);
            double maximumColumn = Math.Max(0.0, _image.Width - 1.0);
            double rowOffset = GetBoundsContainmentOffset(bounds.Top, bounds.Bottom, maximumRow);
            double columnOffset = GetBoundsContainmentOffset(bounds.Left, bounds.Right, maximumColumn);
            roi.CenterRow += rowOffset;
            roi.CenterColumn += columnOffset;
        }

        private static double GetBoundsContainmentOffset(double minimum, double maximum, double imageMaximum)
        {
            double span = maximum - minimum;
            if (span > imageMaximum)
            {
                return (imageMaximum * 0.5) - ((minimum + maximum) * 0.5);
            }

            if (minimum < 0.0)
            {
                return -minimum;
            }

            return maximum > imageMaximum ? imageMaximum - maximum : 0.0;
        }

        private static double GetPointerAngleDegrees(RotatedRectangle roi, ImageCoordinate point)
        {
            return Math.Atan2(
                point.Row - roi.CenterRow,
                point.Column - roi.CenterColumn) * 180.0 / Math.PI;
        }

        private bool IsInsideImage(ImageCoordinate point)
        {
            return _image != null &&
                   point.Row >= 0.0 && point.Row < _image.Height &&
                   point.Column >= 0.0 && point.Column < _image.Width;
        }

        private void OnRoiChanged(CanvasRoiTarget target, bool isFinal)
        {
            EventHandler<RoiChangedEventArgs> handler = RoiChanged;
            if (handler != null)
            {
                handler(this, new RoiChangedEventArgs(target, GetRoi(target), isFinal));
            }
        }

        private void OnRoiDrawRejected(CanvasRoiTarget target)
        {
            EventHandler<CanvasRoiTarget> handler = RoiDrawRejected;
            if (handler != null)
            {
                handler(this, target);
            }
        }

        private void OnAnchorChanged(bool isFinal)
        {
            EventHandler<AnchorChangedEventArgs> handler = AnchorChanged;
            if (handler != null)
            {
                handler(this, new AnchorChangedEventArgs(_anchor, isFinal));
            }
        }

        private void OnMaskPreviewChanged(
            MaskStroke activeMask,
            bool isIncrementalEraseSegment = false,
            ImageCoordinate segmentStart = default(ImageCoordinate),
            ImageCoordinate segmentEnd = default(ImageCoordinate))
        {
            EventHandler<MaskPreviewEventArgs> handler = MaskPreviewChanged;
            if (handler != null)
            {
                handler(this, new MaskPreviewEventArgs(activeMask, isIncrementalEraseSegment, segmentStart, segmentEnd));
            }
        }

        private bool HasVisibleAnchor
        {
            get
            {
                return _anchorVisible &&
                       (_templateRoi != null || _anchor.Row != 0.0 || _anchor.Column != 0.0);
            }
        }

        private void OnSelectedRoiChanged()
        {
            EventHandler handler = SelectedRoiChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void OnToolChanged()
        {
            EventHandler handler = ToolChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void OnViewChanged()
        {
            EventHandler handler = ViewChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private enum DragOperation
        {
            None,
            Pan,
            SelectRoi,
            CreateRoi,
            MoveRoi,
            ResizeRoi,
            RotateRoi,
            Anchor,
            Mask,
            MaskRect,
            MaskCircle
        }

        private enum RoiHandleKind
        {
            None,
            Body,
            Resize,
            Rotate
        }

        private struct RoiHandle
        {
            public static readonly RoiHandle None = new RoiHandle(RoiHandleKind.None, 0, 0, PointF.Empty);
            public static readonly RoiHandle Body = new RoiHandle(RoiHandleKind.Body, 0, 0, PointF.Empty);

            public RoiHandle(RoiHandleKind kind, int horizontal, int vertical, PointF location)
            {
                Kind = kind;
                Horizontal = horizontal;
                Vertical = vertical;
                Location = location;
            }

            public RoiHandleKind Kind { get; private set; }

            public int Horizontal { get; private set; }

            public int Vertical { get; private set; }

            public PointF Location { get; private set; }
        }

        private enum CanvasHitKind
        {
            None,
            RoiHandle,
            RoiLabel,
            AnchorLabel,
            RoiBorder,
            Anchor,
            RoiBody
        }

        private struct InteractiveHit
        {
            public static readonly InteractiveHit None = new InteractiveHit(CanvasHitKind.None, CanvasRoiTarget.None, RoiHandle.None);

            public InteractiveHit(CanvasHitKind kind, CanvasRoiTarget target, RoiHandle handle)
            {
                Kind = kind;
                Target = target;
                Handle = handle;
            }

            public CanvasHitKind Kind { get; private set; }

            public CanvasRoiTarget Target { get; private set; }

            public RoiHandle Handle { get; private set; }
        }

        private struct RoiHit
        {
            public static readonly RoiHit None = new RoiHit(CanvasRoiTarget.None, RoiHandle.None);

            public RoiHit(CanvasRoiTarget target, RoiHandle handle)
            {
                Target = target;
                Handle = handle;
            }

            public CanvasRoiTarget Target { get; private set; }

            public RoiHandle Handle { get; private set; }
        }
    }
}
