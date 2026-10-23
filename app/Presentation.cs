using System.Drawing.Drawing2D;
using System.ComponentModel;
namespace DVCompanion;

internal static class Presentation
{
    public static readonly Color Canvas=Color.FromArgb(241,245,249),Ink=Color.FromArgb(26,47,67),Muted=Color.FromArgb(92,110,128),Line=Color.FromArgb(225,233,240),Soft=Color.FromArgb(247,250,252),Navy=Color.FromArgb(20,43,63),Accent=Color.FromArgb(8,116,121),AccentSoft=Color.FromArgb(230,245,243);
    public static GraphicsPath Round(RectangleF rect,float radius)
    {
        float d=Math.Min(radius*2,Math.Min(rect.Width,rect.Height));var path=new GraphicsPath();
        if(d<=0){path.AddRectangle(rect);return path;}
        path.AddArc(rect.X,rect.Y,d,d,180,90);path.AddArc(rect.Right-d,rect.Y,d,d,270,90);path.AddArc(rect.Right-d,rect.Bottom-d,d,d,0,90);path.AddArc(rect.X,rect.Bottom-d,d,d,90,90);path.CloseFigure();return path;
    }
    public static RoundedSurface Surface(Color? fill=null,Padding? padding=null)=>new(){Dock=DockStyle.Fill,Surface=fill??Color.White,Stroke=Line,Padding=padding??new Padding(16),Margin=Padding.Empty};
    public static Label Label(string text,float size=9.5f,bool bold=false,Color? color=null)=>new(){Text=text,AutoSize=true,Font=new Font("Microsoft YaHei UI",size,bold?FontStyle.Bold:FontStyle.Regular),ForeColor=color??Ink,Margin=Padding.Empty,BackColor=Color.Transparent};
    public static FlowLayoutPanel Flow()=>new(){Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty,BackColor=Color.White};
    public static void Button(StyledButton button,string tone="neutral")
    {
        button.AutoSize=true;button.MinimumSize=new Size(0,38);button.Padding=new Padding(15,0,15,0);button.Margin=new Padding(0,0,10,0);button.Font=new Font("Microsoft YaHei UI",9.5f);button.Cursor=Cursors.Hand;
        button.Tone=tone;
    }
    public static void Grid(DataGridView grid)
    {
        grid.BorderStyle=BorderStyle.None;grid.CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal;grid.GridColor=Color.FromArgb(235,240,245);grid.EnableHeadersVisualStyles=false;
        grid.ColumnHeadersBorderStyle=DataGridViewHeaderBorderStyle.None;grid.ColumnHeadersHeight=36;grid.ColumnHeadersDefaultCellStyle=new DataGridViewCellStyle{BackColor=Color.FromArgb(240,245,249),ForeColor=Muted,Font=new Font("Microsoft YaHei UI",9,FontStyle.Bold),Padding=new Padding(12,0,4,0),SelectionBackColor=Color.FromArgb(240,245,249),SelectionForeColor=Muted};
        grid.DefaultCellStyle=new DataGridViewCellStyle{BackColor=Color.White,ForeColor=Ink,Font=new Font("Microsoft YaHei UI",9.5f),SelectionBackColor=Color.FromArgb(224,242,240),SelectionForeColor=Color.FromArgb(15,86,92),Padding=new Padding(12,5,4,5)};
        grid.AlternatingRowsDefaultCellStyle.BackColor=Color.FromArgb(250,252,254);grid.RowTemplate.Height=38;grid.RowTemplate.MinimumHeight=36;
    }
    public static void Empty(DataGridView grid,string text)
    {
        grid.Paint+=(_,e)=>{if(grid.Rows.Count!=0)return;var bounds=new Rectangle(0,grid.ColumnHeadersHeight,grid.ClientSize.Width,Math.Max(0,grid.ClientSize.Height-grid.ColumnHeadersHeight));TextRenderer.DrawText(e.Graphics,text,grid.Font,bounds,Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);};
    }
    public static Control Section(string title,string note)
    {
        var row=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty,Padding=new Padding(0,6,0,0),BackColor=Color.White};
        var label=Label(title,10.5f,true);label.Margin=new Padding(0,0,14,0);row.Controls.Add(label);var hint=Label(note,9,false,Muted);hint.Margin=new Padding(0,3,0,0);row.Controls.Add(hint);return row;
    }
}

internal sealed class RoundedSurface:Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public Color Surface{get;set;}=Color.White;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public Color Stroke{get;set;}=Presentation.Line;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public int Radius{get;set;}=14;
    public RoundedSurface(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.SupportsTransparentBackColor,true);BackColor=Color.Transparent;}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);if(Width<2||Height<2)return;e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using var shape=Presentation.Round(new RectangleF(.5f,.5f,Width-1,Height-1),Radius*DeviceDpi/96f);using var brush=new SolidBrush(Surface);e.Graphics.FillPath(brush,shape);if(Stroke.A>0){using var pen=new Pen(Stroke);e.Graphics.DrawPath(pen,shape);}
    }
}

internal sealed class StyledButton:Control,IButtonControl
{
    bool hover,pressed;[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public string Tone{get;set;}="neutral";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public DialogResult DialogResult{get;set;}
    public StyledButton()
    {
        // A native BUTTON window can still draw themed edges over owner painting.
        // Use an ordinary managed control, retaining Click, keyboard and accessibility actions.
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.Selectable|ControlStyles.StandardClick,true);
        SetStyle(ControlStyles.StandardDoubleClick,false);TabStop=true;AccessibleRole=AccessibleRole.PushButton;
    }
    public override Size GetPreferredSize(Size proposedSize){var text=TextRenderer.MeasureText(Text,Font);return new Size(Math.Max(MinimumSize.Width,text.Width+Padding.Horizontal+6),Math.Max(MinimumSize.Height,text.Height+Padding.Vertical+8));}
    public void NotifyDefault(bool value){Invalidate();}
    public void PerformClick(){if(Enabled&&CanSelect)OnClick(EventArgs.Empty);}
    protected override void OnClick(EventArgs e){if(!Enabled)return;if(DialogResult!=DialogResult.None&&FindForm() is {} form)form.DialogResult=DialogResult;base.OnClick(e);}
    protected override bool IsInputKey(Keys keyData)=>(keyData&Keys.KeyCode) is Keys.Enter or Keys.Space||base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode is Keys.Space or Keys.Enter){if(!pressed){pressed=true;if(e.KeyCode==Keys.Enter)PerformClick();}e.Handled=true;Invalidate();}base.OnKeyDown(e);}
    protected override void OnKeyPress(KeyPressEventArgs e){if(e.KeyChar is ' ' or '\r')e.Handled=true;base.OnKeyPress(e);}
    protected override void OnKeyUp(KeyEventArgs e){if(e.KeyCode is Keys.Space or Keys.Enter){bool click=pressed&&e.KeyCode==Keys.Space;pressed=false;e.Handled=true;Invalidate();if(click)PerformClick();}base.OnKeyUp(e);}
    protected override AccessibleObject CreateAccessibilityInstance()=>new StyledButtonAccessibility(this);
    sealed class StyledButtonAccessibility(StyledButton owner):ControlAccessibleObject(owner)
    {
        public override string? DefaultAction=>"按下";
        public override void DoDefaultAction()=>owner.PerformClick();
    }
    protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){hover=pressed=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left){Focus();pressed=true;}Invalidate();base.OnMouseDown(e);}
    protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
    protected override void OnEnabledChanged(EventArgs e){if(!Enabled)pressed=false;Invalidate();base.OnEnabledChanged(e);}
    protected override void OnGotFocus(EventArgs e){Invalidate();base.OnGotFocus(e);}
    protected override void OnLostFocus(EventArgs e){pressed=false;Invalidate();base.OnLostFocus(e);}
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Every button is hosted on a solid surface; repaint the entire rectangle first.
        e.Graphics.Clear(Parent?.BackColor??Presentation.Canvas);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var color=Tone switch{"primary"=>Presentation.Accent,"accent" or "tabSelected"=>Presentation.AccentSoft,"danger"=>Color.FromArgb(255,246,244),_=>Color.White};
        var ink=Tone switch{"primary"=>Color.White,"accent" or "tabSelected"=>Color.FromArgb(9,100,104),"tab"=>Presentation.Muted,"danger"=>Color.FromArgb(161,70,67),_=>Presentation.Ink};
        var border=Tone.StartsWith("tab")?Color.Transparent:Tone=="primary"?Presentation.Accent:Tone=="accent"?Color.FromArgb(201,230,226):Tone=="danger"?Color.FromArgb(240,219,215):Color.FromArgb(216,226,235);
        if(!Enabled){color=Color.FromArgb(243,246,248);ink=Color.FromArgb(162,174,185);border=Color.FromArgb(231,236,241);}
        else if(hover||pressed){color=Tone=="primary"?(pressed?Color.FromArgb(5,86,92):Color.FromArgb(6,101,107)):Tone=="danger"?Color.FromArgb(252,235,232):Color.FromArgb(235,244,246);}
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using var path=Presentation.Round(new RectangleF(.5f,.5f,Width-1,Height-1),8*DeviceDpi/96f);using var brush=new SolidBrush(color);e.Graphics.FillPath(brush,path);using var pen=new Pen(border);e.Graphics.DrawPath(pen,path);
        var flags=TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine;if(!ShowKeyboardCues)flags|=TextFormatFlags.HidePrefix;TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,ink,flags);
        if(Focused&&ShowFocusCues){var focus=ClientRectangle;focus.Inflate(-4,-4);ControlPaint.DrawFocusRectangle(e.Graphics,focus,ink,color);}
    }
}

internal sealed class WorkspaceTabs:TabControl
{
    public WorkspaceTabs(){Appearance=TabAppearance.FlatButtons;SizeMode=TabSizeMode.Fixed;ItemSize=new Size(0,1);}
    public override Rectangle DisplayRectangle=>ClientRectangle;
    protected override void WndProc(ref Message m)
    {
        // The native page host and keyboard handling stay intact; the separate strip paints navigation.
        if(m.Msg==0x1328){m.Result=(IntPtr)1;return;} // TCM_ADJUSTRECT: no native tab border/header inset.
        base.WndProc(ref m);
    }
}
