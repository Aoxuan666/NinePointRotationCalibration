using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NinePointRotationCalibration.Demo
{
    internal sealed class ZoomImageView : ScrollableControl
    {
        private const int ImageMargin = 24;
        private Bitmap _image;
        private TemplateMatchResult _match;
        private float _zoom = 1.0f;
        private bool _fitToWindow = true;

        public ZoomImageView()
        {
            BackColor = Color.FromArgb(30, 33, 37);
            TabStop = true;
            AutoScroll = true;
            DoubleBuffered = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
        }

        public event EventHandler ViewChanged;

        public int ZoomPercent
        {
            get { return (int)Math.Round(GetEffectiveScale() * 100.0f); }
        }

        public bool IsFitToWindow
        {
            get { return _fitToWindow; }
        }

        public void SetImage(Bitmap image)
        {
            _image = image;
            _match = null;
            FitToWindow();
        }

        public void SetMatch(TemplateMatchResult match)
        {
            _match = match == null ? null : match.DeepClone();
            Invalidate();
        }

        public void FitToWindow()
        {
            _fitToWindow = true;
            AutoScrollMinSize = Size.Empty;
            AutoScrollPosition = Point.Empty;
            Invalidate();
            RaiseViewChanged();
        }

        public void ShowActualSize()
        {
            SetManualZoom(1.0f);
        }

        public void ZoomIn()
        {
            SetManualZoom(GetEffectiveScale() * 1.25f);
        }

        public void ZoomOut()
        {
            SetManualZoom(GetEffectiveScale() / 1.25f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(BackColor);
            if (_image == null)
            {
                DrawEmptyState(e.Graphics);
                return;
            }

            float scale = GetEffectiveScale();
            RectangleF imageBounds = GetImageBounds(scale);
            e.Graphics.InterpolationMode = scale < 1.0f
                ? InterpolationMode.HighQualityBicubic
                : InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(_image, imageBounds);
            DrawMatchOverlay(e.Graphics, imageBounds, scale);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_fitToWindow)
            {
                Invalidate();
                RaiseViewChanged();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (_image == null)
            {
                base.OnMouseWheel(e);
                return;
            }

            float factor = e.Delta > 0 ? 1.15f : 1.0f / 1.15f;
            SetManualZoom(GetEffectiveScale() * factor);
        }

        private void SetManualZoom(float zoom)
        {
            if (_image == null) return;
            _fitToWindow = false;
            _zoom = Math.Max(0.05f, Math.Min(16.0f, zoom));
            AutoScrollMinSize = new Size(
                (int)Math.Ceiling((_image.Width * _zoom) + (ImageMargin * 2.0f)),
                (int)Math.Ceiling((_image.Height * _zoom) + (ImageMargin * 2.0f)));
            Invalidate();
            RaiseViewChanged();
        }

        private float GetEffectiveScale()
        {
            if (_image == null) return 1.0f;
            if (!_fitToWindow) return _zoom;

            float availableWidth = Math.Max(1.0f, ClientSize.Width - (ImageMargin * 2.0f));
            float availableHeight = Math.Max(1.0f, ClientSize.Height - (ImageMargin * 2.0f));
            return Math.Max(0.01f, Math.Min(availableWidth / _image.Width, availableHeight / _image.Height));
        }

        private RectangleF GetImageBounds(float scale)
        {
            float width = _image.Width * scale;
            float height = _image.Height * scale;
            if (_fitToWindow)
            {
                return new RectangleF(
                    (ClientSize.Width - width) / 2.0f,
                    (ClientSize.Height - height) / 2.0f,
                    width,
                    height);
            }

            Point scroll = AutoScrollPosition;
            float x = scroll.X + ImageMargin;
            float y = scroll.Y + ImageMargin;
            if (AutoScrollMinSize.Width < ClientSize.Width) x = (ClientSize.Width - width) / 2.0f;
            if (AutoScrollMinSize.Height < ClientSize.Height) y = (ClientSize.Height - height) / 2.0f;
            return new RectangleF(x, y, width, height);
        }

        private void DrawMatchOverlay(Graphics graphics, RectangleF imageBounds, float scale)
        {
            if (_match == null || !_match.Success) return;

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen contourPen = new Pen(Color.FromArgb(80, 230, 135), 1.5f))
            using (Pen markerPen = new Pen(Color.FromArgb(255, 205, 55), 2.0f))
            using (Brush textBrush = new SolidBrush(Color.White))
            using (Brush textBackground = new SolidBrush(Color.FromArgb(190, 20, 23, 27)))
            using (Font overlayFont = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Bold, GraphicsUnit.Point))
            {
                foreach (List<ImagePoint> segment in _match.ContourSegments ?? new List<List<ImagePoint>>())
                {
                    if (segment == null || segment.Count < 2) continue;
                    PointF[] points = new PointF[segment.Count];
                    for (int index = 0; index < segment.Count; index++)
                    {
                        points[index] = ToClient(segment[index], imageBounds, scale);
                    }
                    graphics.DrawLines(contourPen, points);
                }

                PointF anchor = ToClient(_match.Anchor, imageBounds, scale);
                const float radius = 9.0f;
                graphics.DrawLine(markerPen, anchor.X - radius, anchor.Y, anchor.X + radius, anchor.Y);
                graphics.DrawLine(markerPen, anchor.X, anchor.Y - radius, anchor.X, anchor.Y + radius);
                graphics.DrawEllipse(markerPen, anchor.X - 4.0f, anchor.Y - 4.0f, 8.0f, 8.0f);

                string label = string.Format("R {0:0.00}  C {1:0.00}  {2:0.0} deg  {3:0.000}",
                    _match.Anchor.Row,
                    _match.Anchor.Column,
                    _match.AngleDegrees,
                    _match.Score);
                SizeF labelSize = graphics.MeasureString(label, overlayFont);
                RectangleF labelBounds = new RectangleF(
                    Math.Min(anchor.X + 12.0f, Math.Max(4.0f, ClientSize.Width - labelSize.Width - 12.0f)),
                    Math.Max(4.0f, anchor.Y - labelSize.Height - 12.0f),
                    labelSize.Width + 8.0f,
                    labelSize.Height + 4.0f);
                graphics.FillRectangle(textBackground, labelBounds);
                graphics.DrawString(label, overlayFont, textBrush, labelBounds.X + 4.0f, labelBounds.Y + 2.0f);
            }
        }

        private static PointF ToClient(ImagePoint point, RectangleF imageBounds, float scale)
        {
            return new PointF(
                imageBounds.Left + ((float)point.Column * scale),
                imageBounds.Top + ((float)point.Row * scale));
        }

        private void DrawEmptyState(Graphics graphics)
        {
            const string message = "尚未加载图像";
            using (Font font = new Font("Microsoft YaHei UI", 12.0f, FontStyle.Regular, GraphicsUnit.Point))
            using (Brush brush = new SolidBrush(Color.FromArgb(155, 163, 173)))
            {
                SizeF size = graphics.MeasureString(message, font);
                graphics.DrawString(
                    message,
                    font,
                    brush,
                    (ClientSize.Width - size.Width) / 2.0f,
                    (ClientSize.Height - size.Height) / 2.0f);
            }
        }

        private void RaiseViewChanged()
        {
            EventHandler handler = ViewChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }
}
