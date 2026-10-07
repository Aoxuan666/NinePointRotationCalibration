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
    [DefaultEvent("RoiChanged")]
    public sealed class CalibrationCanvas : Control
    {
        private const double MinimumZoom = 0.02;
        private const double MaximumZoom = 50.0;
        private const float HandleRadius = 5.0f;
        private const float HandleHitRadius = 9.0f;
        private const float RotationHandleDistance = 28.0f;

        private readonly List<MaskStroke> _maskStrokes = new List<MaskStroke>();
        private Bitmap _image;
        private double _zoom = 1.0;
        private double _offsetX;
        private double _offsetY;
        private bool _autoFit = true;
        private bool _viewInitialized;
        private RotatedRectangle _templateRoi;
        private RotatedRectangle _searchRoi;
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
        private RotatedRectangle _dragStartRoi;
        private ImageCoordinate _dragStartAnchor;
        private RoiHandle _activeHandle;
        private MaskStroke _activeStroke;
        private bool _transactionChanged;

        public CalibrationCanvas()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable,
                true);
            BackColor = Color.FromArgb(22, 24, 27);
            ForeColor = Color.WhiteSmoke;
            TabStop = true;
            MinimumSize = new Size(240, 180);
        }

        public event EventHandler<RoiChangedEventArgs> RoiChanged;

        public event EventHandler<AnchorChangedEventArgs> AnchorChanged;

        public event EventHandler<MaskStrokeEventArgs> MaskStrokeCreated;

        public event EventHandler EditTransactionStarted;

        public event EventHandler SelectedRoiChanged;

        public event EventHandler ViewChanged;

        [Browsable(false)]
        public bool HasImage
        {
            get { return _image != null; }
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
                _tool = value;
                Cursor = GetDefaultCursor();
                Invalidate();
            }
        }

        [DefaultValue(CanvasRoiTarget.Template)]
        public CanvasRoiTarget SelectedRoi
        {
            get { return _selectedRoi; }
            set
            {
                if (_selectedRoi == value)
                {
                    return;
                }

                _selectedRoi = value;
                OnSelectedRoiChanged();
                Invalidate();
            }
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

        public void SetImage(Bitmap image)
        {
            Bitmap replacement = image == null ? null : new Bitmap(image);
            Bitmap previous = _image;
            _image = replacement;
            if (previous != null)
            {
                previous.Dispose();
            }

            _viewInitialized = false;
            if (_image != null)
            {
                if (_templateRoi == null)
                {
                    double halfWidth = Math.Max(10.0, _image.Width * 0.15);
                    double halfHeight = Math.Max(10.0, _image.Height * 0.15);
                    _templateRoi = new RotatedRectangle(
                        _image.Height / 2.0,
                        _image.Width / 2.0,
                        halfWidth,
                        halfHeight,
                        0.0);
                }

                if (_searchRoi == null)
                {
                    _searchRoi = new RotatedRectangle(
                        _image.Height / 2.0,
                        _image.Width / 2.0,
                        Math.Max(20.0, _image.Width * 0.42),
                        Math.Max(20.0, _image.Height * 0.42),
                        0.0);
                }

                if (!_anchorVisible || (_anchor.Row == 0.0 && _anchor.Column == 0.0))
                {
                    _anchor = new ImageCoordinate(_templateRoi.CenterRow, _templateRoi.CenterColumn);
                }
            }

            FitImage();
        }

        public Bitmap CopyImage()
        {
            return _image == null ? null : new Bitmap(_image);
        }

        public void ClearImage()
        {
            SetImage(null);
        }

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
            _offsetX = (ClientSize.Width - (_image.Width * _zoom)) / 2.0;
            _offsetY = (ClientSize.Height - (_image.Height * _zoom)) / 2.0;
            _autoFit = true;
            _viewInitialized = true;
            OnViewChanged();
            Invalidate();
        }

        public void ShowOneToOne()
        {
            if (_image == null)
            {
                return;
            }

            _zoom = 1.0;
            _offsetX = (ClientSize.Width - _image.Width) / 2.0;
            _offsetY = (ClientSize.Height - _image.Height) / 2.0;
            _autoFit = false;
            _viewInitialized = true;
            OnViewChanged();
            Invalidate();
        }

        public void SetMaskStrokes(IEnumerable<MaskStroke> strokes)
        {
            _maskStrokes.Clear();
            if (strokes != null)
            {
                _maskStrokes.AddRange(strokes.Where(item => item != null).Select(item => item.DeepClone()));
            }

            Invalidate();
        }

        public IList<MaskStroke> GetMaskStrokes()
        {
            return _maskStrokes.Select(item => item.DeepClone()).ToList();
        }

        public void ClearRuntimeOverlay()
        {
            RuntimeOverlay = new CanvasRuntimeOverlay();
        }

        public PointF ImageToClient(ImageCoordinate point)
        {
            return new PointF(
                (float)(_offsetX + (point.Column * _zoom)),
                (float)(_offsetY + (point.Row * _zoom)));
        }

        public ImageCoordinate ClientToImage(PointF point)
        {
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
            if (disposing && _image != null)
            {
                _image.Dispose();
                _image = null;
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
            bool interactive = _dragOperation != DragOperation.None;
            graphics.SmoothingMode = interactive ? SmoothingMode.None : SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = interactive ? PixelOffsetMode.HighSpeed : PixelOffsetMode.HighQuality;

            if (_image == null)
            {
                DrawEmptyState(graphics);
                return;
            }

            if (!_viewInitialized)
            {
                FitImage();
            }

            DrawImageLayer(graphics);
            DrawOverlayLayer(graphics, _editorOverlay, false);
            if (MaskVisible)
            {
                DrawMaskLayer(graphics);
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

            Focus();
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
            _transactionChanged = false;

            if (eventArgs.Button == MouseButtons.Middle)
            {
                _dragOperation = DragOperation.Pan;
                Capture = true;
                Cursor = Cursors.Hand;
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

                BeginEditTransaction();
                _activeStroke = new MaskStroke
                {
                    IsErase = _tool == CanvasEditTool.EraseMask,
                    Radius = Math.Max(1.0, BrushRadiusImage)
                };
                _activeStroke.Points.Add(ClampToImage(_dragStartImage));
                _dragOperation = DragOperation.Mask;
                Capture = true;
                _transactionChanged = true;
                Invalidate();
                return;
            }

            if (_tool == CanvasEditTool.DrawTemplateRoi || _tool == CanvasEditTool.DrawSearchRoi)
            {
                if (!IsInsideImage(_dragStartImage))
                {
                    return;
                }

                CanvasRoiTarget target = _tool == CanvasEditTool.DrawTemplateRoi
                    ? CanvasRoiTarget.Template
                    : CanvasRoiTarget.Search;
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

            if (_anchorVisible && !AnchorLocked &&
                (_tool == CanvasEditTool.MoveAnchor || HitAnchor(eventArgs.Location)))
            {
                BeginEditTransaction();
                _dragStartAnchor = _anchor;
                _dragOperation = DragOperation.Anchor;
                Capture = true;
                Cursor = Cursors.SizeAll;
                return;
            }

            RoiHit hit = HitTestRois(eventArgs.Location);
            if (hit.Target != CanvasRoiTarget.None)
            {
                SelectedRoi = hit.Target;
                if (IsTargetLocked(hit.Target))
                {
                    Cursor = Cursors.Default;
                    Invalidate();
                    return;
                }

                BeginEditTransaction();
                _dragStartRoi = GetRoi(hit.Target).DeepClone();
                _activeHandle = hit.Handle;
                if (hit.Handle.Kind == RoiHandleKind.Rotate)
                {
                    _dragOperation = DragOperation.RotateRoi;
                }
                else if (hit.Handle.Kind == RoiHandleKind.Resize)
                {
                    _dragOperation = DragOperation.ResizeRoi;
                }
                else
                {
                    _dragOperation = DragOperation.MoveRoi;
                }

                Capture = true;
                return;
            }

            SelectedRoi = CanvasRoiTarget.None;
        }

        protected override void OnMouseMove(MouseEventArgs eventArgs)
        {
            base.OnMouseMove(eventArgs);
            if (_dragOperation == DragOperation.None)
            {
                Cursor = HitTestCursor(eventArgs.Location);
                return;
            }

            if (_dragOperation == DragOperation.Pan)
            {
                _offsetX += eventArgs.X - _lastScreen.X;
                _offsetY += eventArgs.Y - _lastScreen.Y;
                _lastScreen = eventArgs.Location;
                _autoFit = false;
                _viewInitialized = true;
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
                    UpdateMovedRoi(current);
                    break;
                case DragOperation.ResizeRoi:
                    UpdateResizedRoi(current);
                    break;
                case DragOperation.RotateRoi:
                    UpdateRotatedRoi(current);
                    break;
                case DragOperation.Anchor:
                    _anchor = current;
                    _transactionChanged = true;
                    OnAnchorChanged(false);
                    Invalidate();
                    break;
                case DragOperation.Mask:
                    AppendStrokePoint(current);
                    break;
            }

            _lastScreen = eventArgs.Location;
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

            int rowDirection = eventArgs.KeyCode == Keys.Up ? -1 : eventArgs.KeyCode == Keys.Down ? 1 : 0;
            int columnDirection = eventArgs.KeyCode == Keys.Left ? -1 : eventArgs.KeyCode == Keys.Right ? 1 : 0;
            if (rowDirection == 0 && columnDirection == 0)
            {
                return;
            }

            double step = eventArgs.Shift ? 10.0 : 1.0;
            BeginEditTransaction();
            if ((_tool == CanvasEditTool.MoveAnchor || _selectedRoi == CanvasRoiTarget.None) &&
                _anchorVisible && !AnchorLocked)
            {
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

            if (eventArgs.Control && columnDirection != 0)
            {
                roi.AngleDegrees = RotatedRectangle.NormalizeAngle(
                    roi.AngleDegrees + (columnDirection * (eventArgs.Shift ? 5.0 : 1.0)));
            }
            else
            {
                roi.CenterRow += rowDirection * step;
                roi.CenterColumn += columnDirection * step;
                ClampRoiCenter(roi);
            }

            SetRoi(_selectedRoi, roi);
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
            bool interactive = _dragOperation != DragOperation.None;
            graphics.InterpolationMode = interactive
                ? InterpolationMode.Bilinear
                : _zoom >= 4.0
                ? InterpolationMode.NearestNeighbor
                : InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(
                _image,
                destination,
                new RectangleF(0, 0, _image.Width, _image.Height),
                GraphicsUnit.Pixel);
            using (Pen border = new Pen(Color.FromArgb(90, Color.White), 1.0f))
            {
                graphics.DrawRectangle(border, destination.X, destination.Y, destination.Width, destination.Height);
            }
        }

        private void DrawMaskLayer(Graphics graphics)
        {
            if ((_maskStrokes.Count == 0 && _activeStroke == null) || _templateRoi == null)
            {
                return;
            }

            GraphicsState state = graphics.Save();
            try
            {
                PointF[] clipPoints = _templateRoi.GetCorners().Select(ImageToClient).ToArray();
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddPolygon(clipPoints);
                    graphics.SetClip(path, CombineMode.Intersect);
                    foreach (MaskStroke stroke in _maskStrokes)
                    {
                        DrawMaskStroke(graphics, stroke);
                    }

                    if (_activeStroke != null)
                    {
                        DrawMaskStroke(graphics, _activeStroke);
                    }
                }
            }
            finally
            {
                graphics.Restore(state);
            }
        }

        private void DrawMaskStroke(Graphics graphics, MaskStroke stroke)
        {
            if (stroke == null || stroke.Points == null || stroke.Points.Count == 0)
            {
                return;
            }

            Color color = stroke.IsErase
                ? Color.FromArgb(105, 239, 68, 68)
                : Color.FromArgb(105, 61, 214, 140);
            float width = (float)Math.Max(2.0, stroke.Radius * 2.0 * _zoom);
            using (Pen pen = new Pen(color, width))
            using (Brush brush = new SolidBrush(color))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                PointF[] points = stroke.Points.Select(ImageToClient).ToArray();
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
            DrawRoi(graphics, _searchRoi, CanvasRoiTarget.Search, Color.DeepSkyBlue);
            DrawRoi(graphics, _templateRoi, CanvasRoiTarget.Template, Color.Gold);
            if (_anchorVisible)
            {
                DrawAnchor(graphics);
            }
        }

        private void DrawRoi(Graphics graphics, RotatedRectangle roi, CanvasRoiTarget target, Color color)
        {
            if (roi == null)
            {
                return;
            }

            bool selected = _selectedRoi == target;
            PointF[] corners = roi.GetCorners().Select(ImageToClient).ToArray();
            using (Pen pen = new Pen(Color.FromArgb(selected ? 255 : 205, color), selected ? 2.0f : 1.25f))
            {
                if (!selected)
                {
                    pen.DashStyle = DashStyle.Dash;
                }

                graphics.DrawPolygon(pen, corners);
            }

            string title = target == CanvasRoiTarget.Template ? "模板 ROI" : "搜索 ROI";
            DrawLabel(graphics, title, corners[0], color);

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
                    PointF top = ImageToClient(roi.LocalToImage(0.0, -roi.HalfHeight));
                    using (Pen connector = new Pen(color, 1.25f))
                    {
                        graphics.DrawLine(connector, top, location);
                    }

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

        private void DrawAnchor(Graphics graphics)
        {
            PointF center = ImageToClient(_anchor);
            Color color = AnchorLocked ? Color.Silver : Color.LimeGreen;
            const float radius = 5.0f;
            using (Brush fill = new SolidBrush(color))
            using (Pen outline = new Pen(Color.FromArgb(230, Color.Black), 2.0f))
            {
                graphics.FillEllipse(fill, center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);
                graphics.DrawEllipse(outline, center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);
            }
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
            if (overlay.Points != null)
            {
                foreach (CanvasPointOverlay point in overlay.Points.Where(item => item != null))
                {
                    PointF center = ImageToClient(point.Position);
                    float radius = Math.Max(3.0f, point.RadiusPixels);
                    using (Brush brush = new SolidBrush(Color.FromArgb(point.IsSelected ? 235 : 185, point.Color)))
                    using (Pen outline = new Pen(Color.FromArgb(230, Color.Black), 1.5f))
                    {
                        graphics.FillEllipse(brush, center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);
                        graphics.DrawEllipse(outline, center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);
                    }

                    if (!string.IsNullOrWhiteSpace(point.Label))
                    {
                        DrawAvoidingLabel(graphics, point.Label, center, point.Color, occupiedLabels);
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
            PointF end = new PointF(
                center.X + ((float)Math.Cos(radians) * length),
                center.Y + ((float)Math.Sin(radians) * length));
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
            OnRoiChanged(_selectedRoi, false);
        }

        private void UpdateResizedRoi(ImageCoordinate current)
        {
            if (_dragStartRoi == null)
            {
                return;
            }

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

            double deltaColumn = current.Column - _dragStartRoi.CenterColumn;
            double deltaRow = current.Row - _dragStartRoi.CenterRow;
            if ((deltaColumn * deltaColumn) + (deltaRow * deltaRow) < 0.0001)
            {
                return;
            }

            RotatedRectangle roi = _dragStartRoi.DeepClone();
            roi.AngleDegrees = RotatedRectangle.NormalizeAngle(
                (Math.Atan2(deltaRow, deltaColumn) * 180.0 / Math.PI) + 90.0);
            SetRoi(_selectedRoi, roi);
            _transactionChanged = true;
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
                Invalidate();
                return;
            }

            if (!_transactionChanged)
            {
                _activeStroke = null;
                return;
            }

            if (operation == DragOperation.Mask && _activeStroke != null)
            {
                MaskStroke completed = _activeStroke.DeepClone();
                _maskStrokes.Add(completed.DeepClone());
                _activeStroke = null;
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
            else if (operation == DragOperation.CreateRoi ||
                     operation == DragOperation.MoveRoi ||
                     operation == DragOperation.ResizeRoi ||
                     operation == DragOperation.RotateRoi)
            {
                OnRoiChanged(_selectedRoi, true);
            }

            Invalidate();
        }

        private RoiHit HitTestRois(Point location)
        {
            if (_selectedRoi != CanvasRoiTarget.None)
            {
                RotatedRectangle selected = GetRoi(_selectedRoi);
                RoiHandle handle = HitHandle(selected, location);
                if (handle.Kind != RoiHandleKind.None)
                {
                    return new RoiHit(_selectedRoi, handle);
                }
            }

            ImageCoordinate imagePoint = ClientToImage(location);
            if (_templateRoi != null && _templateRoi.Contains(imagePoint))
            {
                return new RoiHit(CanvasRoiTarget.Template, RoiHandle.Body);
            }

            if (_searchRoi != null && _searchRoi.Contains(imagePoint))
            {
                return new RoiHit(CanvasRoiTarget.Search, RoiHandle.Body);
            }

            return RoiHit.None;
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
            int[] values = { -1, 0, 1 };
            foreach (int vertical in values)
            {
                foreach (int horizontal in values)
                {
                    if (horizontal == 0 && vertical == 0)
                    {
                        continue;
                    }

                    if (horizontal != 0 || vertical != 0)
                    {
                        ImageCoordinate image = roi.LocalToImage(
                            horizontal * roi.HalfWidth,
                            vertical * roi.HalfHeight);
                        yield return new RoiHandle(
                            RoiHandleKind.Resize,
                            horizontal,
                            vertical,
                            ImageToClient(image));
                    }
                }
            }

            PointF center = ImageToClient(new ImageCoordinate(roi.CenterRow, roi.CenterColumn));
            PointF top = ImageToClient(roi.LocalToImage(0.0, -roi.HalfHeight));
            double vectorX = top.X - center.X;
            double vectorY = top.Y - center.Y;
            double length = Math.Sqrt((vectorX * vectorX) + (vectorY * vectorY));
            if (length < 0.001)
            {
                length = 1.0;
            }

            PointF rotation = new PointF(
                top.X + (float)((vectorX / length) * RotationHandleDistance),
                top.Y + (float)((vectorY / length) * RotationHandleDistance));
            yield return new RoiHandle(RoiHandleKind.Rotate, 0, -1, rotation);
        }

        private bool HitAnchor(Point point)
        {
            PointF anchor = ImageToClient(_anchor);
            double deltaX = point.X - anchor.X;
            double deltaY = point.Y - anchor.Y;
            return (deltaX * deltaX) + (deltaY * deltaY) <= 14.0 * 14.0;
        }

        private Cursor HitTestCursor(Point point)
        {
            if (_image == null)
            {
                return Cursors.Default;
            }

            if (_tool == CanvasEditTool.EraseMask || _tool == CanvasEditTool.RestoreMask)
            {
                return Cursors.Cross;
            }

            if (_tool == CanvasEditTool.DrawTemplateRoi || _tool == CanvasEditTool.DrawSearchRoi)
            {
                return Cursors.Cross;
            }

            if (_anchorVisible && !AnchorLocked &&
                (_tool == CanvasEditTool.MoveAnchor || HitAnchor(point)))
            {
                return Cursors.SizeAll;
            }

            RoiHit hit = HitTestRois(point);
            if (hit.Target == CanvasRoiTarget.None || IsTargetLocked(hit.Target))
            {
                return Cursors.Default;
            }

            if (hit.Handle.Kind == RoiHandleKind.Rotate)
            {
                return Cursors.Cross;
            }

            if (hit.Handle.Kind == RoiHandleKind.Resize)
            {
                if (hit.Handle.Horizontal == 0)
                {
                    return Cursors.SizeNS;
                }

                if (hit.Handle.Vertical == 0)
                {
                    return Cursors.SizeWE;
                }

                return hit.Handle.Horizontal == hit.Handle.Vertical ? Cursors.SizeNWSE : Cursors.SizeNESW;
            }

            return Cursors.SizeAll;
        }

        private Cursor GetDefaultCursor()
        {
            return _tool == CanvasEditTool.EraseMask ||
                   _tool == CanvasEditTool.RestoreMask ||
                   _tool == CanvasEditTool.DrawTemplateRoi ||
                   _tool == CanvasEditTool.DrawSearchRoi
                ? Cursors.Cross
                : Cursors.Default;
        }

        private RotatedRectangle GetRoi(CanvasRoiTarget target)
        {
            return target == CanvasRoiTarget.Template
                ? _templateRoi
                : target == CanvasRoiTarget.Search ? _searchRoi : null;
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

            roi.CenterRow = Clamp(roi.CenterRow, 0.0, _image.Height - 1.0);
            roi.CenterColumn = Clamp(roi.CenterColumn, 0.0, _image.Width - 1.0);
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

        private void OnSelectedRoiChanged()
        {
            EventHandler handler = SelectedRoiChanged;
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
            CreateRoi,
            MoveRoi,
            ResizeRoi,
            RotateRoi,
            Anchor,
            Mask
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
