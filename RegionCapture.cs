using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// The frozen desktop and the selection use the same physical-pixel coordinate system.
sealed class RegionCapture : Form
{
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h,int id);
    bool escapeRegistered;
    readonly Bitmap desktop;
    Point anchor;
    Rectangle selection;
    bool dragging;

    RegionCapture(Rectangle bounds, Bitmap desktop) {
        this.desktop = desktop;
        Text = "EOpen · 区域截图";
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        Shown += delegate { Activate(); Focus(); escapeRegistered=RegisterHotKey(Handle,1,0x4000,(uint)Keys.Escape); };
        FormClosed += delegate { if(escapeRegistered) UnregisterHotKey(Handle,1); };
        KeyDown += delegate(object sender, KeyEventArgs e) { if(e.KeyCode == Keys.Escape) Close(); };
        MouseDown += delegate(object sender, MouseEventArgs e) {
            if(e.Button == MouseButtons.Right) { Close(); return; }
            if(e.Button != MouseButtons.Left) return;
            anchor=e.Location; dragging=true; Capture=true;
        };
        MouseMove += delegate(object sender, MouseEventArgs e) { if(dragging) UpdateSelection(e.Location); };
        MouseUp += delegate(object sender, MouseEventArgs e) {
            if(!dragging || e.Button != MouseButtons.Left) return;
            UpdateSelection(e.Location); dragging=false; Capture=false;
            if(selection.Width>1 && selection.Height>1) DialogResult=DialogResult.OK;
        };
    }
    protected override void WndProc(ref Message m) {
        if(m.Msg==0x312 && m.WParam.ToInt32()==1) { Close(); return; }
        base.WndProc(ref m);
    }
    void UpdateSelection(Point end) {
        var next=Rectangle.Intersect(ClientRectangle,Rectangle.FromLTRB(Math.Min(anchor.X,end.X), Math.Min(anchor.Y,end.Y),
            Math.Max(anchor.X,end.X), Math.Max(anchor.Y,end.Y)));
        if(next==selection) return;
        using(var dirty=new Region(Rectangle.Inflate(next,2,2))) {
            if(!selection.IsEmpty) dirty.Union(Rectangle.Inflate(selection,2,2));
            var common=Rectangle.Intersect(selection,next);
            if(common.Width>4 && common.Height>4) dirty.Exclude(Rectangle.Inflate(common,-2,-2));
            selection=next;
            Invalidate(dirty);
        }
    }
    protected override void OnPaint(PaintEventArgs e) {
        var g=e.Graphics;
        var clip=e.ClipRectangle;
        g.DrawImage(desktop,clip,clip,GraphicsUnit.Pixel);
        using(var shade=new SolidBrush(Color.FromArgb(62,0,0,0))) g.FillRectangle(shade,clip);
        if(selection.Width>0 && selection.Height>0) {
            var visible=Rectangle.Intersect(clip,selection);
            if(visible.Width>0 && visible.Height>0) g.DrawImage(desktop,visible,visible,GraphicsUnit.Pixel);
            using(var pen=new Pen(Color.White,2)) g.DrawRectangle(pen,selection);
        }
    }
    public static bool CopySelection() {
        IntPtr old=SetThreadDpiAwarenessContext(new IntPtr(-4));
        try {
            DwmFlush();
            var bounds=SystemInformation.VirtualScreen;
            using(var desktop=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppRgb)) {
                using(var g=Graphics.FromImage(desktop)) g.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size);
                using(var overlay=new RegionCapture(bounds,desktop)) {
                    if(overlay.ShowDialog()!=DialogResult.OK) return false;
                    using(var crop=desktop.Clone(overlay.selection,PixelFormat.Format24bppRgb))
                        Clipboard.SetDataObject(crop,true,5,80);
                    return true;
                }
            }
        } finally { if(old!=IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
    }
}


