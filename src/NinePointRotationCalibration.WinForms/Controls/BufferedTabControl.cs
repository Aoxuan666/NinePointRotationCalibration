using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NinePointRotationCalibration.WinForms.Controls
{
    /// <summary>
    /// 支持无闪烁原子切页的 TabControl：切页期间暂挂绘制与布局，待布局全部就绪后再一次性提交完整画面。
    /// </summary>
    [DesignerCategory("Code")]
    public class BufferedTabControl : TabControl
    {
        private IDisposable _activeTransition;
        private bool _disposing;
        internal bool SuppressTransition { get; set; }

        /// <summary>可选的外部切页过渡作用域工厂（参数为当前页与目标页，返回会在切页完成后释放的作用域）。</summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<TabPage, TabPage, IDisposable> TransitionScopeFactory { get; set; }

        protected override void OnSelecting(TabControlCancelEventArgs e)
        {
            base.OnSelecting(e);
            if (_disposing || SuppressTransition || e == null || e.Cancel || !IsHandleCreated) return;
            if (e.TabPageIndex < 0 || ReferenceEquals(e.TabPage, SelectedTab)) return;

            EndActiveTransition();
            Func<TabPage, TabPage, IDisposable> factory = TransitionScopeFactory;
            _activeTransition = factory != null
                ? factory(SelectedTab, e.TabPage)
                : UiRenderOptimizer.BeginFreezeScope(this);
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            try
            {
                base.OnSelectedIndexChanged(e);
            }
            finally
            {
                EndActiveTransition();
            }
        }

        protected override void WndProc(ref Message m)
        {
            bool hadTransition = _activeTransition != null;
            try
            {
                base.WndProc(ref m);
            }
            finally
            {
                // 若因异常或禁用页被系统拦截而未触发 SelectedIndexChanged，仍在消息返回前自动解除冻结，避免界面卡死
                if (!hadTransition && _activeTransition != null)
                {
                    EndActiveTransition();
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposing = true;
                EndActiveTransition();
            }
            base.Dispose(disposing);
        }

        private void EndActiveTransition()
        {
            IDisposable scope = _activeTransition;
            _activeTransition = null;
            if (scope != null)
            {
                scope.Dispose();
            }
        }
    }

    /// <summary>WinForms 界面双缓冲、批量重绘冻结与标签页启动预热辅助工具。</summary>
    public static class UiRenderOptimizer
    {
        private const int WmSetRedraw = 0x000B;
        private const uint RdwInvalidate = 0x0001;
        private const uint RdwErase = 0x0004;
        private const uint RdwUpdateNow = 0x0100;
        private const uint RdwAllChildren = 0x0080;

        private static readonly PropertyInfo DoubleBufferedProperty = typeof(Control).GetProperty(
            "DoubleBuffered",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly Dictionary<IntPtr, int> FreezeDepthByHandle = new Dictionary<IntPtr, int>();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        /// <summary>为容器控件树递归开启双缓冲绘制，减少 GDI 背景擦除闪烁。</summary>
        public static void EnableDoubleBufferingRecursive(Control root)
        {
            if (root == null || root.IsDisposed || DoubleBufferedProperty == null) return;

            Stack<Control> stack = new Stack<Control>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                Control current = stack.Pop();
                if (current == null || current.IsDisposed) continue;

                if (ShouldEnableContainerDoubleBuffer(current))
                {
                    try
                    {
                        DoubleBufferedProperty.SetValue(current, true, null);
                    }
                    catch
                    {
                    }
                }

                for (int i = current.Controls.Count - 1; i >= 0; i--)
                {
                    stack.Push(current.Controls[i]);
                }
            }
        }

        /// <summary>启动时预创建所有未选中 TabPage 的句柄，并静默激活一轮所有页签以提前完成布局与显示初始化。</summary>
        public static void WarmUpTabControlPages(TabControl tabControl)
        {
            if (tabControl == null || tabControl.IsDisposed || tabControl.TabPages.Count == 0) return;

            BufferedTabControl buffered = tabControl as BufferedTabControl;
            using (BeginFreezeScope(tabControl, tabControl))
            {
                if (buffered != null) buffered.SuppressTransition = true;
                try
                {
                    ForceCreateHandlesTopDown(tabControl);
                    Rectangle displayRect = tabControl.DisplayRectangle;
                    int savedIndex = tabControl.SelectedIndex;
                    for (int index = tabControl.TabPages.Count - 1; index >= 0; index--)
                    {
                        TabPage page = tabControl.TabPages[index];
                        if (page == null || page.IsDisposed) continue;
                        if (displayRect.Width > 0 && displayRect.Height > 0 && page.Bounds != displayRect)
                        {
                            page.Bounds = displayRect;
                        }
                        if (tabControl.IsHandleCreated && tabControl.TabPages.Count > 1)
                        {
                            tabControl.SelectedIndex = index;
                        }
                        PerformLayoutRecursive(page);
                    }

                    if (tabControl.IsHandleCreated &&
                        savedIndex >= 0 &&
                        savedIndex < tabControl.TabPages.Count &&
                        tabControl.SelectedIndex != savedIndex)
                    {
                        tabControl.SelectedIndex = savedIndex;
                    }
                }
                finally
                {
                    if (buffered != null) buffered.SuppressTransition = false;
                }
            }
        }

        /// <summary>冻结目标控件重绘并暂挂指定布局容器，释放时统一恢复并在需要时一次性原子重绘。</summary>
        public static IDisposable BeginFreezeScope(Control redrawTarget, params Control[] layoutTargets)
        {
            return new RedrawFreezeScope(redrawTarget, layoutTargets);
        }

        private static bool ShouldEnableContainerDoubleBuffer(Control control)
        {
            return control is Form ||
                   control is Panel ||
                   control is TableLayoutPanel ||
                   control is FlowLayoutPanel ||
                   control is GroupBox ||
                   control is SplitContainer ||
                   control is DataGridView ||
                   control is ToolStrip;
        }

        private static void ForceCreateHandlesTopDown(Control root)
        {
            Stack<Control> stack = new Stack<Control>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                Control current = stack.Pop();
                if (current == null || current.IsDisposed) continue;
                if (!current.IsHandleCreated)
                {
                    IntPtr unused = current.Handle;
                    GC.KeepAlive(unused);
                }

                for (int i = current.Controls.Count - 1; i >= 0; i--)
                {
                    stack.Push(current.Controls[i]);
                }
            }
        }

        private static void PerformLayoutRecursive(Control root)
        {
            if (root == null || root.IsDisposed) return;
            root.PerformLayout();
            for (int i = 0; i < root.Controls.Count; i++)
            {
                Control child = root.Controls[i];
                if (child != null && !child.IsDisposed && child.Controls.Count > 0)
                {
                    PerformLayoutRecursive(child);
                }
            }
        }

        private sealed class RedrawFreezeScope : IDisposable
        {
            private readonly Control _redrawTarget;
            private readonly IntPtr _frozenHandle;
            private readonly List<Control> _suspended = new List<Control>();
            private bool _disposed;

            public RedrawFreezeScope(Control redrawTarget, Control[] layoutTargets)
            {
                _redrawTarget = redrawTarget;

                if (layoutTargets != null && layoutTargets.Length > 0)
                {
                    for (int i = 0; i < layoutTargets.Length; i++)
                    {
                        Control target = layoutTargets[i];
                        if (target != null && !target.IsDisposed && !_suspended.Contains(target))
                        {
                            target.SuspendLayout();
                            _suspended.Add(target);
                        }
                    }
                }
                else if (redrawTarget != null && !redrawTarget.IsDisposed)
                {
                    redrawTarget.SuspendLayout();
                    _suspended.Add(redrawTarget);
                }

                if (redrawTarget != null && !redrawTarget.IsDisposed && redrawTarget.IsHandleCreated)
                {
                    IntPtr handle = redrawTarget.Handle;
                    int depth;
                    FreezeDepthByHandle.TryGetValue(handle, out depth);
                    FreezeDepthByHandle[handle] = depth + 1;
                    _frozenHandle = handle;
                    if (depth == 0)
                    {
                        SendMessage(handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
                    }
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                for (int i = _suspended.Count - 1; i >= 0; i--)
                {
                    Control target = _suspended[i];
                    if (target != null && !target.IsDisposed)
                    {
                        target.ResumeLayout(true);
                    }
                }

                if (_frozenHandle == IntPtr.Zero) return;

                int depth;
                if (FreezeDepthByHandle.TryGetValue(_frozenHandle, out depth))
                {
                    if (depth <= 1)
                    {
                        FreezeDepthByHandle.Remove(_frozenHandle);
                        if (_redrawTarget != null && !_redrawTarget.IsDisposed && _redrawTarget.IsHandleCreated)
                        {
                            SendMessage(_frozenHandle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
                            uint flags = RdwInvalidate | RdwErase | RdwAllChildren;
                            if (_redrawTarget.Visible) flags |= RdwUpdateNow;
                            RedrawWindow(_frozenHandle, IntPtr.Zero, IntPtr.Zero, flags);
                        }
                    }
                    else
                    {
                        FreezeDepthByHandle[_frozenHandle] = depth - 1;
                    }
                }
            }
        }
    }
}
