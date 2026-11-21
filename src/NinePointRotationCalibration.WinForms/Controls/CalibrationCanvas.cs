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
        private const float BorderHitDistance = 6.0f;
        private const float AnchorHitRadius = 9.0f;
        private const float DragThresholdPixels = 3.0f;

        // 重叠 ROI 本体点击测试的默认前景优先级顺序
        private static readonly CanvasRoiTarget[] RoiBodyHitOrder =
        {
            CanvasRoiTarget.Template,
            CanvasRoiTarget.Detection,
            CanvasRoiTarget.Search
        };

        private readonly List<MaskStroke> _maskStrokes = new List<MaskStroke>();
        private Bitmap _maskHistory;
        private Bitmap _maskInk;
        private int _paintedStrokes;
        private int _paintedInkPoints;
        private MaskStroke _inkOwner;
        private double _maskZoom, _maskX, _maskY;
        private readonly Timer _maskRefreshTimer = new Timer { Interval = 90 };
        private bool _maskRefreshPending;
        private Rectangle _lastActiveMaskRect;
        private Rectangle _lastBrushCursorRect;
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
        private Point _hoverScreen;
        private bool _hasHoverScreen;

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
        public double BrushRadiusImage { get; set; } = 12.0;

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
            }

            DrawOverlayLayer(graphics, _runtimeOverlay, true);
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
                Invalidate();
                return;
            }

            if (_tool == CanvasEditTool.MaskPolygon)
            {
                if (!IsInsideImage(_dragStartImage))
                {
                    return;
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

            if (_tool == CanvasEditTool.DrawTemplateRoi ||
                _tool == CanvasEditTool.DrawSearchRoi ||
                _tool == CanvasEditTool.DrawDetectionRoi)
            {
                if (!IsInsideImage(_dragStartImage))
                {
                    return;
                }

                CanvasRoiTarget target = _tool == CanvasEditTool.DrawTemplateRoi
                    ? CanvasRoiTarget.Template
                    : _tool == CanvasEditTool.DrawSearchRoi
                        ? CanvasRoiTarget.Search
                        : CanvasRoiTarget.Detection;
                if (IsTargetLocked(target))
                {
                    return;
                }

                BeginEditTransaction();
                SelectedRoi = target;
                _dragOperation = DragOperation.CreateRoi;
                Capture = true;
                SetRoi(target, new RotatedRectangle(
                    _dragStartImage.Row,
                    _dragStartImage.Column,
                    1.0,
                    1.0,
                    0.0));
                _transactionChanged = true;
                return;
            }

            if (_tool == CanvasEditTool.MoveAnchor)
            {
                if (HasVisibleAnchor && !AnchorLocked && IsInsideImage(_dragStartImage))
                {
                    _anchorSelected = true;
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

            // Select 模式下：优先检测当前选中 ROI 的缩放/旋转控制柄，防止缩小视图时小 ROI 控制柄被中心锚点遮挡
            RoiHit selectedHandleHit = HitSelectedHandle(eventArgs.Location);
            if (selectedHandleHit.Target != CanvasRoiTarget.None)
            {
                _anchorSelected = false;
                StartRoiEditDrag(selectedHandleHit);
                return;
            }

            if (HasVisibleAnchor && !AnchorLocked && HitAnchor(eventArgs.Location))
            {
                _anchorSelected = true;
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

            RoiHit hit = HitTestRois(eventArgs.Location);
            if (hit.Target != CanvasRoiTarget.None)
            {
                _anchorSelected = false;
                SelectedRoi = hit.Target;
                if (IsTargetLocked(hit.Target))
                {
                    _activeHandle = hit.Handle;
                    _dragOperation = DragOperation.SelectRoi;
                    Capture = true;
                    Cursor = Cursors.Default;
                    Invalidate();
                    return;
                }

                StartRoiEditDrag(hit);
                return;
            }

            _anchorSelected = false;
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
            if ((!HasVisibleAnchor || !HitAnchor(client)) && HitTestRois(client).Target == CanvasRoiTarget.None)
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

            if (_dragOperation == DragOperation.Pan)
            {
                _offsetX += eventArgs.X - _lastScreen.X;
                _offsetY += eventArgs.Y - _lastScreen.Y;
                _lastScreen = eventArgs.Location;
                _autoFit = false;
                _viewInitialized = true;
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

            ImageCoordinate current = ClampToImage(ClientToImage(eventArgs.Location));
            switch (_dragOperation)
            {
                case DragOperation.CreateRoi:
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
                        BeginEditTransaction();
                    }
                    UpdateMovedRoi(current);
                    break;
                case DragOperation.ResizeRoi:
                    UpdateResizedRoi(current);
                    break;
                case DragOperation.RotateRoi:
                    UpdateRotatedRoi(current);
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
                    AppendStrokePoint(current);
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
            if (_hoveredRoi != CanvasRoiTarget.None || _anchorHovered)
            {
                _hoveredRoi = CanvasRoiTarget.None;
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

        protected override void OnKeyDown(KeyEventArgs eventArgs)
        {
            base.OnKeyDown(eventArgs);
            if (_image == null)
            {
                return;
            }

            if (_polygonDrawing)
            {
                if (eventArgs.KeyCode == Keys.Escape)
                {
                    CancelActivePolygon(true);
                    eventArgs.Handled = true;
                    return;
                }
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

        private void DrawMaskLayer(Graphics graphics)
        {
            if ((_maskStrokes.Count == 0 && (_activeStroke == null || _activeStroke.Kind != MaskShapeKind.Brush)) || _templateRoi == null)
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
                        (_activeStroke == null || _activeStroke.Kind != MaskShapeKind.Brush) &&
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

                    UpdateMaskCache();

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
                    Rectangle roiRect = Rectangle.Ceiling(RectangleF.FromLTRB(minX - 2.0f, minY - 2.0f, maxX + 2.0f, maxY + 2.0f));
                    Rectangle blitRect = Rectangle.Intersect(roiRect, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
                    Rectangle clipRect = Rectangle.Ceiling(graphics.ClipBounds);
                    if (!clipRect.IsEmpty) blitRect = Rectangle.Intersect(blitRect, clipRect);
                    if (blitRect.Width <= 0 || blitRect.Height <= 0) return;

                    graphics.DrawImage(_maskHistory, blitRect, blitRect, GraphicsUnit.Pixel);

                    if (_activeStroke != null && _activeStroke.Kind == MaskShapeKind.Brush)
                    {
                        graphics.DrawImage(_maskInk, blitRect, blitRect, GraphicsUnit.Pixel);
                    }
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

            Color fillColor = Color.FromArgb(100, 239, 68, 68);
            Color borderColor = Color.FromArgb(235, 248, 113, 113);
            using (Brush fill = new SolidBrush(fillColor))
            using (Pen border = new Pen(borderColor, 1.5f))
            {
                border.DashStyle = DashStyle.Dash;
                if (_activeStroke != null && _activeStroke.Kind == MaskShapeKind.Rectangle && _activeStroke.Rectangle != null)
                {
                    PointF[] corners = _activeStroke.Rectangle.GetCorners().Select(ImageToClient).ToArray();
                    graphics.FillPolygon(fill, corners);
                    graphics.DrawPolygon(border, corners);
                }
                else if (_activeStroke != null && _activeStroke.Kind == MaskShapeKind.Circle)
                {
                    PointF center = ImageToClient(_activeStroke.Center);
                    float radius = (float)Math.Max(1.0, _activeStroke.Radius * _zoom);
                    graphics.FillEllipse(fill, center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);
                    graphics.DrawEllipse(border, center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);
                    graphics.DrawLine(border, center.X - 5, center.Y, center.X + 5, center.Y);
                    graphics.DrawLine(border, center.X, center.Y - 5, center.X, center.Y + 5);
                }
                else if (_polygonDrawing && _activePolygonPoints.Count > 0)
                {
                    List<PointF> screenPoints = _activePolygonPoints.Select(ImageToClient).ToList();
                    if (_hasHoverScreen) screenPoints.Add(_hoverScreen);
                    if (screenPoints.Count >= 3)
                    {
                        graphics.FillPolygon(fill, screenPoints.ToArray());
                    }
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
            if (_maskInk != null) _maskInk.Dispose();
            _maskHistory = _maskInk = null;
            _inkOwner = null;
            _paintedStrokes = _paintedInkPoints = 0;
        }

        private void UpdateMaskCache()
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
                _inkOwner = null;
                _paintedInkPoints = 0;
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
                    layer.CompositingMode = CompositingMode.SourceCopy;
                    while (_paintedStrokes < _maskStrokes.Count)
                    {
                        DrawMaskItem(layer, _maskStrokes[_paintedStrokes], 0);
                        _paintedStrokes++;
                    }
                }
            }
            if (_activeStroke == null || _activeStroke.Kind != MaskShapeKind.Brush) return;
            if (_maskInk == null)
                _maskInk = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
            if (_inkOwner == _activeStroke && _paintedInkPoints == _activeStroke.Points.Count) return;
            using (Graphics ink = Graphics.FromImage(_maskInk))
            {
                ink.SmoothingMode = SmoothingMode.None;
                ink.CompositingMode = CompositingMode.SourceCopy;
                if (_inkOwner != _activeStroke)
                {
                    ink.Clear(Color.Transparent);
                    _inkOwner = _activeStroke;
                    _paintedInkPoints = 0;
                }
                DrawMaskItem(ink, _activeStroke, Math.Max(0, _paintedInkPoints - 1));
                _paintedInkPoints = _activeStroke.Points.Count;
            }
        }

        private void DrawMaskItem(Graphics graphics, MaskStroke stroke, int firstPoint)
        {
            if (stroke == null || !stroke.IsValid)
            {
                return;
            }

            // 使用 SourceCopy：擦除统一着半透明红（重复涂抹不变黑），恢复直接擦回透明露出清晰原图
            bool isInkLayer = ReferenceEquals(stroke, _activeStroke);
            Color color = stroke.IsErase
                ? Color.FromArgb(115, 239, 68, 68)
                : (isInkLayer ? Color.FromArgb(105, 74, 222, 128) : Color.Transparent);

            if (stroke.Kind == MaskShapeKind.Rectangle && stroke.Rectangle != null)
            {
                PointF[] corners = stroke.Rectangle.GetCorners().Select(ImageToClient).ToArray();
                using (Brush brush = new SolidBrush(color))
                {
                    graphics.FillPolygon(brush, corners);
                }
                if (stroke.IsErase && !isInkLayer)
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
                if (stroke.IsErase && !isInkLayer)
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
                    if (stroke.IsErase && !isInkLayer)
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
            List<RectangleF> occupiedLabels = new List<RectangleF>();
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
                if (target == _selectedRoi) continue;
                DrawRoi(graphics, GetRoi(target), target, GetRoiColor(target), occupiedLabels);
            }

            if (_selectedRoi != CanvasRoiTarget.None)
            {
                DrawRoi(graphics, GetRoi(_selectedRoi), _selectedRoi, GetRoiColor(_selectedRoi), occupiedLabels);
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

        private void DrawRoi(Graphics graphics, RotatedRectangle roi, CanvasRoiTarget target, Color color, IList<RectangleF> occupiedLabels)
        {
            if (roi == null)
            {
                return;
            }

            bool selected = _selectedRoi == target;
            bool hovered = !selected && _hoveredRoi == target;
            PointF[] corners = roi.GetCorners().Select(ImageToClient).ToArray();
            using (Pen pen = new Pen(Color.FromArgb(selected ? 255 : (hovered ? 235 : 195), color), selected ? 2.2f : (hovered ? 1.8f : 1.25f)))
            {
                if (!selected)
                {
                    pen.DashStyle = DashStyle.Dash;
                }

                graphics.DrawPolygon(pen, corners);
            }

            string title = target == CanvasRoiTarget.Template
                ? "模板 ROI"
                : target == CanvasRoiTarget.Search ? "搜索 ROI" : "检测 ROI";
            DrawAvoidingLabel(graphics, title, corners[0], color, occupiedLabels);

            if (!selected || IsTargetLocked(target))
            {
                if (selected && IsTargetLocked(target))
                {
                    DrawLockGlyph(graphics, corners[1], color);
                }

                return;
            }

            foreach (RoiHandle handle in GetHandles(roi))
            {
                PointF location = handle.Location;
                if (handle.Kind == RoiHandleKind.Rotate)
                {
                    using (Brush fill = new SolidBrush(Color.FromArgb(235, 35, 38, 42)))
                    using (Pen outline = new Pen(color, 2.0f))
                    {
                        graphics.FillEllipse(fill, location.X - HandleRadius, location.Y - HandleRadius, HandleRadius * 2.0f, HandleRadius * 2.0f);
                        graphics.DrawEllipse(outline, location.X - HandleRadius, location.Y - HandleRadius, HandleRadius * 2.0f, HandleRadius * 2.0f);
                    }
                }
                else
                {
                    using (Brush fill = new SolidBrush(Color.FromArgb(245, 35, 38, 42)))
                    using (Pen outline = new Pen(color, 1.5f))
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

        private void DrawAnchor(Graphics graphics, IList<RectangleF> occupiedLabels)
        {
            PointF center = ImageToClient(_anchor);
            bool emphasized = _anchorSelected || _anchorHovered || _tool == CanvasEditTool.MoveAnchor;
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

            DrawAvoidingLabel(graphics, AnchorLocked ? "中心(锁)" : "中心", center, color, occupiedLabels);
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
            RectangleF box = new RectangleF(location.X + 5.0f, location.Y - size.Height - 5.0f, size.Width + 8.0f, size.Height + 3.0f);
            using (Brush background = new SolidBrush(Color.FromArgb(190, 18, 20, 23)))
            using (Brush foreground = new SolidBrush(color))
            {
                graphics.FillRectangle(background, box);
                graphics.DrawString(text, Font, foreground, box.X + 4.0f, box.Y + 1.0f);
            }
        }

        private void DrawAvoidingLabel(
            Graphics graphics,
            string text,
            PointF anchor,
            Color color,
            IList<RectangleF> occupied)
        {
            SizeF textSize = graphics.MeasureString(text, Font);
            PointF[] offsets =
            {
                new PointF(9.0f, -textSize.Height - 7.0f),
                new PointF(9.0f, 7.0f),
                new PointF(-textSize.Width - 14.0f, -textSize.Height - 7.0f),
                new PointF(-textSize.Width - 14.0f, 7.0f)
            };
            RectangleF chosen = RectangleF.Empty;
            foreach (PointF offset in offsets)
            {
                RectangleF candidate = new RectangleF(
                    anchor.X + offset.X,
                    anchor.Y + offset.Y,
                    textSize.Width + 8.0f,
                    textSize.Height + 3.0f);
                if (occupied.All(existing => !existing.IntersectsWith(candidate)))
                {
                    chosen = candidate;
                    break;
                }
            }

            if (chosen.IsEmpty)
            {
                chosen = new RectangleF(anchor.X + 9.0f, anchor.Y + 7.0f, textSize.Width + 8.0f, textSize.Height + 3.0f);
            }

            occupied.Add(chosen);
            using (Brush background = new SolidBrush(Color.FromArgb(190, 18, 20, 23)))
            using (Brush foreground = new SolidBrush(color))
            {
                graphics.FillRectangle(background, chosen);
                graphics.DrawString(text, Font, foreground, chosen.X + 4.0f, chosen.Y + 1.0f);
            }
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
            double centerRow = (_dragStartImage.Row + current.Row) / 2.0;
            double centerColumn = (_dragStartImage.Column + current.Column) / 2.0;
            RotatedRectangle roi = new RotatedRectangle(
                centerRow,
                centerColumn,
                Math.Max(1.0, Math.Abs(current.Column - _dragStartImage.Column) / 2.0),
                Math.Max(1.0, Math.Abs(current.Row - _dragStartImage.Row) / 2.0),
                0.0);
            SetRoi(_selectedRoi, roi);
            _transactionChanged = true;
            OnRoiChanged(_selectedRoi, false);
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

        private void AppendStrokePoint(ImageCoordinate current)
        {
            if (_activeStroke == null)
            {
                return;
            }

            ImageCoordinate previous = _activeStroke.Points[_activeStroke.Points.Count - 1];
            double deltaRow = current.Row - previous.Row;
            double deltaColumn = current.Column - previous.Column;
            double minimumDistance = Math.Max(0.5, _activeStroke.Radius * 0.12);
            if ((deltaRow * deltaRow) + (deltaColumn * deltaColumn) < minimumDistance * minimumDistance)
            {
                return;
            }

            _activeStroke.Points.Add(current);
            _transactionChanged = true;
            PointF start = ImageToClient(previous);
            PointF end = ImageToClient(current);
            float margin = (float)Math.Max(1, _activeStroke.Radius * _zoom) + 3;
            OnMaskPreviewChanged(_activeStroke, _activeStroke.IsErase, previous, current);
            Invalidate(Rectangle.Ceiling(RectangleF.FromLTRB(Math.Min(start.X, end.X) - margin,
                Math.Min(start.Y, end.Y) - margin, Math.Max(start.X, end.X) + margin, Math.Max(start.Y, end.Y) + margin)));
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
                    nextRect = Rectangle.Ceiling(RectangleF.FromLTRB(
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
            if (notifyPreview)
            {
                OnMaskPreviewChanged(null);
            }
            Invalidate();
        }

        private void FinishDrag(bool commit)
        {
            DragOperation operation = _dragOperation;
            _dragOperation = DragOperation.None;
            if (Capture)
            {
                Capture = false;
            }

            Cursor = GetDefaultCursor();
            if (!commit)
            {
                _activeStroke = null;
                _lastActiveMaskRect = Rectangle.Empty;
                OnMaskPreviewChanged(null);
                Invalidate();
                return;
            }

            if (operation == DragOperation.Pan && _maskRefreshPending)
            {
                _maskRefreshTimer.Stop();
                _maskRefreshPending = false;
            }

            if (!_transactionChanged)
            {
                _activeStroke = null;
                _lastActiveMaskRect = Rectangle.Empty;
                if (operation == DragOperation.MaskRect || operation == DragOperation.MaskCircle)
                {
                    OnMaskPreviewChanged(null);
                }
                // 在 ROI 本体上单击（未超出拖拽死区）且按下时未从外层大框切换到内层小框时，在重叠 ROI 间循环切换
                if ((operation == DragOperation.MoveRoi || operation == DragOperation.SelectRoi) &&
                    _activeHandle.Kind == RoiHandleKind.Body &&
                    _selectedRoi == _selectionAtMouseDown)
                {
                    CycleOverlappingRoiSelection(_dragStartScreen);
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
            else if (operation == DragOperation.CreateRoi)
            {
                RotatedRectangle created = GetRoi(_selectedRoi);
                if (created != null && _image != null && (created.HalfWidth < 6.0 || created.HalfHeight < 6.0))
                {
                    double fallbackWidth = _selectedRoi == CanvasRoiTarget.Search
                        ? Math.Max(24.0, _image.Width * 0.35)
                        : Math.Max(16.0, _image.Width * 0.12);
                    double fallbackHeight = _selectedRoi == CanvasRoiTarget.Search
                        ? Math.Max(24.0, _image.Height * 0.35)
                        : Math.Max(16.0, _image.Height * 0.12);
                    created.HalfWidth = created.HalfWidth < 6.0 ? fallbackWidth : created.HalfWidth;
                    created.HalfHeight = created.HalfHeight < 6.0 ? fallbackHeight : created.HalfHeight;
                    ClampRoiCenter(created);
                    SetRoi(_selectedRoi, created);
                }

                if (_selectedRoi == CanvasRoiTarget.Template && created != null &&
                    !AnchorLocked && _anchor.Row == 0.0 && _anchor.Column == 0.0)
                {
                    _anchor = new ImageCoordinate(created.CenterRow, created.CenterColumn);
                    OnAnchorChanged(true);
                }

                OnRoiChanged(_selectedRoi, true);
                if (_tool == CanvasEditTool.DrawTemplateRoi ||
                    _tool == CanvasEditTool.DrawSearchRoi ||
                    _tool == CanvasEditTool.DrawDetectionRoi)
                {
                    // 画完即切回选择模式，防止继续拖动时意外覆盖刚画好的 ROI
                    _tool = CanvasEditTool.Select;
                    Cursor = GetDefaultCursor();
                    OnToolChanged();
                }
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

        private void CycleOverlappingRoiSelection(Point location)
        {
            if (_tool != CanvasEditTool.Select)
            {
                return;
            }

            ImageCoordinate imagePoint = ClientToImage(location);
            List<CanvasRoiTarget> hits = new List<CanvasRoiTarget>();
            foreach (CanvasRoiTarget target in RoiBodyHitOrder)
            {
                RotatedRectangle candidate = GetRoi(target);
                if (candidate != null && candidate.Contains(imagePoint))
                {
                    hits.Add(target);
                }
            }
            // 面积相近/相等时保留 RoiBodyHitOrder（Template -> Detection -> Search）；不同面积时优先内层小框
            hits.Sort((a, b) => CompareRoiPriority(a, b));

            if (hits.Count <= 1)
            {
                return;
            }

            int selectedIndex = hits.IndexOf(_selectionAtMouseDown);
            SelectedRoi = selectedIndex < 0
                ? hits[0]
                : hits[(selectedIndex + 1) % hits.Count];
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

        private RoiHit HitTestRois(Point location)
        {
            ImageCoordinate imagePoint = ClientToImage(location);
            // 1. 优先命中当前选中且未锁定 ROI 的缩放/旋转控制柄
            if (_selectedRoi != CanvasRoiTarget.None)
            {
                RotatedRectangle selected = GetRoi(_selectedRoi);
                RoiHandle handle = HitHandle(selected, location);
                if (handle.Kind != RoiHandleKind.None)
                {
                    return new RoiHit(_selectedRoi, handle);
                }
            }

            // 2. 其次命中 ROI 边框（±6px）：点在某内层 ROI 边框上可直接精准选中它，不再被外层大框吞掉
            List<CanvasRoiTarget> borderHits = new List<CanvasRoiTarget>();
            foreach (CanvasRoiTarget target in RoiBodyHitOrder)
            {
                RotatedRectangle candidate = GetRoi(target);
                if (candidate != null && HitRoiBorder(candidate, location))
                {
                    borderHits.Add(target);
                }
            }
            if (borderHits.Count > 0)
            {
                borderHits.Sort((a, b) => CompareRoiPriority(a, b));
                if (_selectedRoi != CanvasRoiTarget.None && borderHits.Contains(_selectedRoi))
                {
                    return new RoiHit(_selectedRoi, RoiHandle.Body);
                }
                return new RoiHit(borderHits[0], RoiHandle.Body);
            }

            // 3. 本体命中：按面积从小到大排序（小 ROI 在内层优先），同尺寸重叠时保留当前已选中目标以便直接拖动或点击循环
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
                return RoiHit.None;
            }

            bodyHits.Sort((a, b) => CompareRoiPriority(a, b));
            CanvasRoiTarget smallest = bodyHits[0];
            if (_selectedRoi != CanvasRoiTarget.None && bodyHits.Contains(_selectedRoi) &&
                GetRoiArea(GetRoi(_selectedRoi)) <= GetRoiArea(GetRoi(smallest)) * 1.15)
            {
                return new RoiHit(_selectedRoi, RoiHandle.Body);
            }

            return new RoiHit(smallest, RoiHandle.Body);
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

        private bool HitRoiBorder(RotatedRectangle roi, Point location)
        {
            PointF[] corners = roi.GetCorners().Select(ImageToClient).ToArray();
            double maxDistSq = BorderHitDistance * BorderHitDistance;
            for (int i = 0; i < 4; i++)
            {
                PointF a = corners[i];
                PointF b = corners[(i + 1) % 4];
                if (DistanceSquaredToSegment(location, a, b) <= maxDistSq)
                {
                    return true;
                }
            }
            return false;
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
            int radius = (int)Math.Ceiling(Math.Max(2.0, BrushRadiusImage * _zoom)) + 4;
            return new Rectangle(location.X - radius, location.Y - radius, (radius * 2) + 1, (radius * 2) + 1);
        }

        private void UpdateHoverAndCursor(Point location)
        {
            if (_image == null)
            {
                Cursor = Cursors.Default;
                SetHoverState(CanvasRoiTarget.None, false);
                return;
            }

            if (_tool == CanvasEditTool.EraseMask ||
                _tool == CanvasEditTool.RestoreMask ||
                _tool == CanvasEditTool.MaskRectangle ||
                _tool == CanvasEditTool.MaskCircle ||
                _tool == CanvasEditTool.MaskPolygon ||
                _tool == CanvasEditTool.DrawTemplateRoi ||
                _tool == CanvasEditTool.DrawSearchRoi ||
                _tool == CanvasEditTool.DrawDetectionRoi)
            {
                Cursor = Cursors.Cross;
                SetHoverState(CanvasRoiTarget.None, false);
                return;
            }

            if (_tool == CanvasEditTool.MoveAnchor)
            {
                bool activeAnchor = HasVisibleAnchor && !AnchorLocked;
                Cursor = activeAnchor ? Cursors.Cross : Cursors.Default;
                SetHoverState(CanvasRoiTarget.None, activeAnchor);
                return;
            }

            // 单次命中检测同时更新鼠标光标与悬停高亮状态，避免 MouseMove 重复计算 ROI 四角投影
            RoiHit handleHit = HitSelectedHandle(location);
            if (handleHit.Target != CanvasRoiTarget.None)
            {
                Cursor = handleHit.Handle.Kind == RoiHandleKind.Rotate
                    ? Cursors.Cross
                    : handleHit.Handle.Horizontal == 0 ? Cursors.SizeNS : Cursors.SizeWE;
                SetHoverState(handleHit.Target, false);
                return;
            }

            if (HasVisibleAnchor && !AnchorLocked && HitAnchor(location))
            {
                Cursor = Cursors.SizeAll;
                SetHoverState(CanvasRoiTarget.None, true);
                return;
            }

            RoiHit hit = HitTestRois(location);
            SetHoverState(hit.Target, false);
            if (hit.Target == CanvasRoiTarget.None || IsTargetLocked(hit.Target))
            {
                Cursor = Cursors.Default;
                return;
            }

            if (hit.Handle.Kind == RoiHandleKind.Rotate)
            {
                Cursor = Cursors.Cross;
            }
            else if (hit.Handle.Kind == RoiHandleKind.Resize)
            {
                Cursor = hit.Handle.Horizontal == 0 ? Cursors.SizeNS : Cursors.SizeWE;
            }
            else
            {
                Cursor = Cursors.SizeAll;
            }
        }

        private void SetHoverState(CanvasRoiTarget nextRoi, bool nextAnchor)
        {
            if (nextRoi != _hoveredRoi || nextAnchor != _anchorHovered)
            {
                _hoveredRoi = nextRoi;
                _anchorHovered = nextAnchor;
                Invalidate();
            }
        }

        private RoiHandle HitHandle(RotatedRectangle roi, Point location)
        {
            if (roi == null)
            {
                return RoiHandle.None;
            }

            foreach (RoiHandle handle in GetHandles(roi))
            {
                double deltaX = location.X - handle.Location.X;
                double deltaY = location.Y - handle.Location.Y;
                if ((deltaX * deltaX) + (deltaY * deltaY) <= HandleHitRadius * HandleHitRadius)
                {
                    return handle;
                }
            }

            return RoiHandle.None;
        }

        private IEnumerable<RoiHandle> GetHandles(RotatedRectangle roi)
        {
            yield return new RoiHandle(
                RoiHandleKind.Resize,
                1,
                0,
                ImageToClient(roi.LocalToImage(roi.HalfWidth, 0.0)));
            yield return new RoiHandle(
                RoiHandleKind.Resize,
                -1,
                0,
                ImageToClient(roi.LocalToImage(-roi.HalfWidth, 0.0)));
            yield return new RoiHandle(
                RoiHandleKind.Resize,
                0,
                1,
                ImageToClient(roi.LocalToImage(0.0, roi.HalfHeight)));
            yield return new RoiHandle(
                RoiHandleKind.Resize,
                0,
                -1,
                ImageToClient(roi.LocalToImage(0.0, -roi.HalfHeight)));

            yield return new RoiHandle(
                RoiHandleKind.Rotate,
                1,
                -1,
                ImageToClient(roi.LocalToImage(roi.HalfWidth, -roi.HalfHeight)));
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
