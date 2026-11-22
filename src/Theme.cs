using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace MonitorScaleTray {
    internal static class Theme {
        public static string Mode {get;private set;}
        static Theme() {Mode="auto";}
        public static bool Dark {get;private set;}
        public static readonly string SettingsPath=Path.Combine(Store.Folder,"appearance.json");
        public static Color Background {get {return Dark?Color.FromArgb(32,32,32):Color.FromArgb(243,243,243);}}
        public static Color Ink {get {return Dark?Color.FromArgb(255,255,255):Color.FromArgb(26,26,26);}}
        public static Color Muted {get {return Dark?Color.FromArgb(197,197,197):Color.FromArgb(93,93,93);}}
        public static Color Blue {get {return Dark?Color.FromArgb(96,205,255):Color.FromArgb(0,95,184);}}
        public static Color Line {get {return Dark?Color.FromArgb(69,69,69):Color.FromArgb(209,209,209);}}
        public static Color Surface {get {return Dark?Color.FromArgb(43,43,43):Color.FromArgb(255,255,255);}}
        public static Color Container {get {return Dark?Color.FromArgb(39,39,39):Color.FromArgb(249,249,249);}}
        public static Color SurfaceVariant {get {return Dark?Color.FromArgb(50,50,50):Color.FromArgb(235,235,235);}}
        public static Color PrimaryContainer {get {return Dark?Color.FromArgb(31,60,76):Color.FromArgb(221,235,250);}}
        public static Color OnPrimaryContainer {get {return Ink;}}
        public static Color OnAccent {get {return Dark?Color.FromArgb(0,45,66):Color.White;}}
        public static Color Error {get {return Dark?Color.FromArgb(255,153,164):Color.FromArgb(196,43,28);}}
        public static bool ResolveDark(string mode,bool systemDark) {return mode=="dark" || (mode=="auto" && systemDark);}
        public static bool ReadSystemDark() {
            try {using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
                return key!=null && Convert.ToInt32(key.GetValue("AppsUseLightTheme",1))==0;
            }} catch {return false;}
        }
        public static void Initialize() {
            try {
                if(File.Exists(SettingsPath)) {
                    var settings=new JavaScriptSerializer().Deserialize<Dictionary<string,string>>(File.ReadAllText(SettingsPath));
                    string value; if(settings!=null && settings.TryGetValue("theme",out value) && ValidMode(value)) Mode=value;
                }
            } catch(Exception ex) {Store.Log("读取外观设置失败，使用自动模式："+ex.Message);}
            Dark=ResolveDark(Mode,ReadSystemDark());
        }
        static bool ValidMode(string mode) {return mode=="auto" || mode=="light" || mode=="dark";}
        public static void SavePreference(string path,string mode) {
            if(!ValidMode(mode)) throw new ArgumentException("未知的外观模式。");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path+".tmp",new JavaScriptSerializer().Serialize(new Dictionary<string,string>{{"theme",mode}}));
            if(File.Exists(path)) File.Replace(path+".tmp",path,path+".bak"); else File.Move(path+".tmp",path);
        }
        public static void SetMode(string mode) {
            SavePreference(SettingsPath,mode); Mode=mode; RefreshSystem();
        }
        static Color[] Colors() {return new[]{Background,Ink,Muted,Blue,Line,Surface,Container,SurfaceVariant,PrimaryContainer};}
        public static void RefreshSystem() {
            bool next=ResolveDark(Mode,ReadSystemDark()); if(next==Dark) return;
            var old=Colors(); Dark=next; var current=Colors();
            Func<Color,Color> map=delegate(Color color) {
                if(color.IsEmpty || color.A==0) return color;
                for(int i=0;i<old.Length;i++) if(color.ToArgb()==old[i].ToArgb()) return current[i];
                return color;
            };
            foreach(Form form in Application.OpenForms) {Recolor(form,map); PaintTitle(form); form.Invalidate(true);}
        }
        static void Style(DataGridViewCellStyle style,Func<Color,Color> map) {
            style.BackColor=map(style.BackColor); style.ForeColor=map(style.ForeColor);
            style.SelectionBackColor=map(style.SelectionBackColor); style.SelectionForeColor=map(style.SelectionForeColor);
        }
        static void Recolor(Control control,Func<Color,Color> map) {
            // Snapshot descendants first so inherited colors are read before their parents change.
            foreach(Control child in control.Controls) Recolor(child,map);
            var grid=control as DataGridView;
            if(grid!=null) {
                grid.BackgroundColor=Surface; grid.GridColor=Line;
                Style(grid.DefaultCellStyle,map); Style(grid.ColumnHeadersDefaultCellStyle,map); Style(grid.AlternatingRowsDefaultCellStyle,map);
                foreach(DataGridViewColumn column in grid.Columns) Style(column.DefaultCellStyle,map);
            }
            var link=control as LinkLabel;
            if(link!=null) {link.LinkColor=map(link.LinkColor); link.ActiveLinkColor=Blue; link.VisitedLinkColor=Muted;}
            control.BackColor=map(control.BackColor); control.ForeColor=map(control.ForeColor);
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
        public static void PaintTitle(Form form) {
            if(!form.IsHandleCreated) return;
            try {
                int dark=Dark?1:0; DwmSetWindowAttribute(form.Handle,20,ref dark,4);
                int background=Background.R|(Background.G<<8)|(Background.B<<16), text=Ink.R|(Ink.G<<8)|(Ink.B<<16);
                DwmSetWindowAttribute(form.Handle,35,ref background,4); DwmSetWindowAttribute(form.Handle,36,ref text,4);
            } catch(DllNotFoundException) {} catch(EntryPointNotFoundException) {}
        }
        public static ContextMenuStrip Menu() {
            var menu=new ContextMenuStrip {Font=Font(10),BackColor=Container,ForeColor=Ink,ShowImageMargin=false,ShowCheckMargin=true,Padding=new Padding(6),Renderer=new ToolStripProfessionalRenderer(new ThemeMenuColors())};
            menu.Opening+=delegate {menu.BackColor=Container; menu.ForeColor=Ink; foreach(ToolStripItem item in menu.Items) item.ForeColor=Ink;};
            return menu;
        }
        public static Font Font(float size, bool bold=false) {return new Font("Microsoft YaHei UI",size,bold?FontStyle.Bold:FontStyle.Regular);}
        public static Form Dialog(string title,Size size) {
            var form=new Form(); form.SuspendLayout();
            form.AutoScaleDimensions=new SizeF(96,96); form.AutoScaleMode=AutoScaleMode.Dpi;
            form.Text=title; form.Font=Font(10); form.BackColor=Background; form.ForeColor=Ink;
            form.ClientSize=size; form.StartPosition=FormStartPosition.CenterParent;
            form.FormBorderStyle=FormBorderStyle.FixedDialog; form.MaximizeBox=false; form.MinimizeBox=false;
            form.HandleCreated+=delegate {PaintTitle(form);};
            return form;
        }
    }
    internal sealed class ThemeMenuColors : ProfessionalColorTable {
        public override Color ToolStripDropDownBackground {get {return Theme.Container;}}
        public override Color MenuBorder {get {return Theme.Line;}}
        public override Color MenuItemBorder {get {return Theme.PrimaryContainer;}}
        public override Color MenuItemSelected {get {return Theme.PrimaryContainer;}}
        public override Color MenuItemSelectedGradientBegin {get {return Theme.PrimaryContainer;}}
        public override Color MenuItemSelectedGradientEnd {get {return Theme.PrimaryContainer;}}
        public override Color CheckBackground {get {return Theme.PrimaryContainer;}}
        public override Color CheckSelectedBackground {get {return Theme.PrimaryContainer;}}
        public override Color CheckPressedBackground {get {return Theme.SurfaceVariant;}}
    }
    internal sealed class ThemeButton : Button {
        public bool Primary,Destructive;
        bool hover,pressed;
        public ThemeButton() {
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
            FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0; UseVisualStyleBackColor=false;
            Font=Theme.Font(9.5f); Size=new Size(104,38); Margin=new Padding(0,0,8,0); Cursor=Cursors.Hand;
        }
        protected override void OnMouseEnter(EventArgs e) {hover=true; Invalidate(); base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e) {hover=false; pressed=false; Invalidate(); base.OnMouseLeave(e);}
        protected override void OnMouseDown(MouseEventArgs e) {pressed=true; Invalidate(); base.OnMouseDown(e);}
        protected override void OnMouseUp(MouseEventArgs e) {pressed=false; Invalidate(); base.OnMouseUp(e);}
        protected override void OnPaint(PaintEventArgs e) {
            float scale=e.Graphics.DpiX/96f, radius=8*scale;
            var bounds=new RectangleF(0.5f,0.5f,Width-1,Height-1);
            Color fill=Primary?(pressed?(Theme.Dark?Color.FromArgb(79,171,214):Color.FromArgb(0,74,145)):hover?(Theme.Dark?Color.FromArgb(85,190,240):Color.FromArgb(0,85,165)):Theme.Blue):
                pressed?Theme.SurfaceVariant:hover?Theme.Container:Theme.Surface;
            Color ink=!Enabled?Theme.Muted:Primary?Theme.OnAccent:Destructive?Theme.Error:Theme.Ink;
            e.Graphics.Clear(Parent==null?Theme.Background:Parent.BackColor); e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=new GraphicsPath()) {
                float d=radius*2;
                path.AddArc(bounds.Left,bounds.Top,d,d,180,90); path.AddArc(bounds.Right-d,bounds.Top,d,d,270,90);
                path.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0,90); path.AddArc(bounds.Left,bounds.Bottom-d,d,d,90,90); path.CloseFigure();
                using(var brush=new SolidBrush(fill)) e.Graphics.FillPath(brush,path);
                using(var pen=new Pen(Primary?fill:Theme.Line,scale)) e.Graphics.DrawPath(pen,path);
            }
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,ink,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine);
            if(Focused && ShowFocusCues) {var focus=ClientRectangle; focus.Inflate(-(int)(5*scale),-(int)(5*scale)); ControlPaint.DrawFocusRectangle(e.Graphics,focus,ink,fill);}
        }
    }
}
