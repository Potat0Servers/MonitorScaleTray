using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MonitorScaleTray {
    internal static class Program {
        [STAThread] static void Main(string[] args) {
            if(args.Contains("--diagnose")) {
                try {
                    var lines=Native.Enumerate().Select(m=>m.Name+" | "+m.Current+"% | supported="+String.Join(",",m.Supported)+" | source="+m.Source+" | "+m.DevicePath+" | "+m.Error);
                    File.WriteAllLines(args.Length>1 ? args[1] : "diagnostics.txt",lines); Environment.ExitCode=0;
                } catch(Exception ex) { File.WriteAllText(args.Length>1 ? args[1] : "diagnostics.txt",ex.ToString()); Environment.ExitCode=1; }
                return;
            }
            if(args.Contains("--self-test")) { Environment.ExitCode=SelfTest.Run(args.Length>1 ? args[1] : "test-results.txt"); return; }
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch(EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            bool first;
            using(var mutex=new Mutex(true,"Local\\MonitorScaleTray.Instance",out first)) {
                if(!first) { MessageBox.Show("程序已经在运行，请点击任务栏右侧的显示器图标（可能位于 ^ 隐藏图标中）。","屏幕缩放"); return; }
                try { Application.Run(new TrayContext(args.Contains("--show"))); }
                catch(Exception ex) { Store.Log(ex.ToString()); MessageBox.Show("程序无法启动："+ex.Message+"\n配置位置："+Store.FilePath,"屏幕缩放",MessageBoxButtons.OK,MessageBoxIcon.Error); }
            }
        }
    }
    internal sealed class TrayContext : ApplicationContext {
        readonly NotifyIcon tray;
        readonly System.Windows.Forms.Timer poll;
        readonly Control dispatcher=new Control();
        MainWindow window;
        Configuration config;
        string signature="", lastNotice="";
        readonly AutoApply automatic;
        bool stopping;
        bool busy;
        string pendingReason;
        public List<MonitorInfo> Monitors=new List<MonitorInfo>();
        public string Status="等待检测显示器";
        public Configuration Config {get {return config;}}
        const string RunKey="Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        public TrayContext(bool show) {
            Theme.Initialize();
            config=Store.Read(Store.FilePath); if(!File.Exists(Store.FilePath)) Store.Save(config,Store.FilePath);
            var handle=dispatcher.Handle;
            var menu=Theme.Menu();
            var startup=new ToolStripMenuItem("开机启动");
            startup.Click+=delegate { try { using(var key=Registry.CurrentUser.CreateSubKey(RunKey)) {
                if(IsStartup()) key.DeleteValue("MonitorScaleTray",false); else key.SetValue("MonitorScaleTray","\""+Application.ExecutablePath+"\"");
            } startup.Checked=IsStartup(); } catch(Exception ex) { Error(ex); } };
            menu.Opening+=delegate {startup.Checked=IsStartup();};
            menu.Items.Add(startup); menu.Items.Add("打开窗口",null,delegate {ShowWindow();});
            menu.Items.Add("退出程序",null,delegate { if(window!=null && !window.ResolveDirty()) return; ExitThread(); });
            tray=new NotifyIcon {Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath),Text="屏幕缩放 · 自动控制",ContextMenuStrip=menu,Visible=true};
            tray.MouseClick+=delegate(object s,MouseEventArgs e) {if(e.Button==MouseButtons.Left) ShowWindow();};
            poll=new System.Windows.Forms.Timer {Interval=2000}; poll.Tick+=delegate {Tick();}; poll.Start();
            automatic=new AutoApply(ReadMonitors,()=>config,(monitors,reason)=> {
                Store.Log("自动应用触发："+reason);
                Apply(false,true,monitors);
            },()=>DateTime.UtcNow,ex=> {Status="自动检查失败："+ex.Message; Store.Log(Status);});
            SystemEvents.DisplaySettingsChanged+=DisplayChanged;
            SystemEvents.PowerModeChanged+=PowerChanged;
            SystemEvents.SessionSwitch+=SessionChanged;
            automatic.Trigger("程序启动",true);
            if(show || config.displays.Count==0) ShowWindow();
        }
        bool IsStartup() {using(var key=Registry.CurrentUser.OpenSubKey(RunKey)) { return key!=null && key.GetValue("MonitorScaleTray")!=null; }}
        void Schedule(string reason) {
            if(stopping || dispatcher.IsDisposed) return;
            try {dispatcher.BeginInvoke((Action)delegate {
                if(stopping) return;
                if(busy) {pendingReason=reason; return;}
                automatic.Trigger(reason,false,reason=="系统解锁" || reason=="睡眠恢复");
                if(window!=null && !window.IsDisposed) window.UpdateStatus();
            });} catch(InvalidOperationException) {}
        }
        void DisplayChanged(object sender,EventArgs e) {Schedule("显示器插拔 / 显示设置切换");}
        void PowerChanged(object sender,PowerModeChangedEventArgs e) {if(e.Mode==PowerModes.Resume) Schedule("睡眠恢复");}
        void SessionChanged(object sender,SessionSwitchEventArgs e) {
            if(e.Reason==SessionSwitchReason.SessionUnlock) Schedule("系统解锁");
        }
        List<MonitorInfo> ReadMonitors() {
            var current=Native.Enumerate(); Monitors=current;
            signature=String.Join("|",current.Select(m=>m.DevicePath+":"+m.Source).OrderBy(x=>x));
            if(current.Any(m=>m.Error!=null)) Status=String.Join("  |  ",current.Where(m=>m.Error!=null).Select(m=>m.Name+"："+m.Error));
            return current;
        }
        void Tick() {
            Theme.RefreshSystem();
            if(busy || stopping) return;
            try {
                string previous=signature;
                ReadMonitors();
                if(signature!=previous) automatic.Trigger("轮询检测到显示器切换");
            } catch(Exception ex) {Status="检测失败："+ex.Message; Store.Log(Status);}
            // Retry even if the first query after wake-up failed or no display was active yet.
            automatic.Tick();
            if(window!=null && !window.IsDisposed) window.UpdateStatus();
        }
        public void Save(Configuration value) {Store.Save(value,Store.FilePath); config=value;}
        public void Apply(bool interactive,bool force=false,List<MonitorInfo> snapshot=null) {
            if(busy) return; busy=true;
            var reports=new List<string>(); bool failed=false;
            try {
                Monitors=snapshot??Native.Enumerate();
                foreach(var group in Monitors.GroupBy(m=>m.Source)) {
                    var matches=group.Select(m=>new {Monitor=m,Key=Store.Match(config,m,Monitors)}).Where(x=>x.Key!=null).ToList();
                    if(matches.Count==0) continue;
                    if(matches.Select(x=>config.displays[x.Key]).Distinct().Count()>1) {
                        failed=true; reports.Add("复制模式下共享同一画面的显示器配置冲突，请设置相同缩放值。"); continue;
                    }
                    var match=matches[0];
                    try {reports.Add(match.Key+"："+Native.Apply(match.Monitor,config.displays[match.Key],force));}
                    catch(Exception ex) {failed=true; reports.Add(match.Key+"："+ex.Message);}
                }
                if(reports.Count==0) reports.Add("当前没有匹配的配置；请刷新检测并添加显示器。");
                Status=String.Join("  |  ",reports); Store.Log(Status);
                if(interactive) MessageBox.Show(Status,"应用结果",MessageBoxButtons.OK,failed?MessageBoxIcon.Warning:MessageBoxIcon.Information);
                else if(failed && lastNotice!=Status) {tray.ShowBalloonTip(5000,"部分缩放配置未应用",Status,ToolTipIcon.Warning); lastNotice=Status;}
            } catch(Exception ex) {Status="应用失败："+ex.Message; Store.Log(Status); if(interactive) Error(ex);}
            finally {
                busy=false;
                if(pendingReason!=null) {string reason=pendingReason; pendingReason=null; Schedule(reason);}
            }
        }
        public void ShowWindow() {
            if(window==null || window.IsDisposed) window=new MainWindow(this);
            window.Show(); window.WindowState=FormWindowState.Normal;
            if(!Screen.AllScreens.Any(s=>s.WorkingArea.IntersectsWith(window.Bounds))) window.Location=Screen.PrimaryScreen.WorkingArea.Location;
            window.Activate();
        }
        public static void Error(Exception ex) {MessageBox.Show(ex.Message,"屏幕缩放",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        protected override void ExitThreadCore() {
            stopping=true;
            SystemEvents.DisplaySettingsChanged-=DisplayChanged; SystemEvents.PowerModeChanged-=PowerChanged; SystemEvents.SessionSwitch-=SessionChanged;
            poll.Stop(); poll.Dispose(); tray.Visible=false; tray.Dispose(); dispatcher.Dispose();
            if(window!=null) {window.ForceClose=true; window.Close();} base.ExitThreadCore();
        }
    }
    internal sealed class MainWindow : Form {
        readonly TrayContext app;
        readonly DataGridView grid=new DataGridView();
        readonly Label status=new Label();
        bool loading,dirty;
        public bool ForceClose;
        readonly ToolTip tips=new ToolTip();
        public MainWindow(TrayContext context) {
            SuspendLayout(); AutoScaleDimensions=new SizeF(96,96);
            app=context; Text="屏幕缩放 · Monitor Scale"; Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font=Theme.Font(10); ForeColor=Theme.Ink; BackColor=Theme.Background; StartPosition=FormStartPosition.CenterScreen;
            AutoScaleMode=AutoScaleMode.Dpi; ClientSize=new Size(740,234); MinimumSize=new Size(640,210);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(12,10,12,8),ColumnCount=1,RowCount=4};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach(int height in new[]{40,0,28,34}) layout.RowStyles.Add(new RowStyle(height==0?SizeType.Percent:SizeType.Absolute,height==0?100:height));
            var toolbar=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,112));
            var actions=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
            AddButton(actions,"新建",delegate {grid.Rows.Add("","100","手动条目","—"); MarkDirty(); grid.CurrentCell=grid.Rows[grid.Rows.Count-1].Cells[0]; grid.BeginEdit(true);});
            AddButton(actions,"检测显示器",RefreshDisplays).Width=96;
            AddButton(actions,"删除",DeleteSelected).Destructive=true;
            var more=AddButton(actions,"更多 ▾",delegate {});
            var menu=Theme.Menu();
            menu.Items.Add("全选",null,delegate {grid.SelectAll();});
            menu.Items.Add("批量缩放",null,delegate {try {BatchScale();} catch(Exception ex) {TrayContext.Error(ex);}});
            menu.Items.Add("系统显示设置",null,delegate {try {Process.Start("ms-settings:display");} catch(Exception ex) {TrayContext.Error(ex);}});
            menu.Items.Add(new ToolStripSeparator());
            foreach(string mode in new[]{"light","dark","auto"}) {
                string choice=mode;
                var item=new ToolStripMenuItem(mode=="auto"?"外观 · 跟随系统":mode=="dark"?"外观 · 深色":"外观 · 浅色") {Tag=mode};
                item.Click+=delegate {try {Theme.SetMode(choice);} catch(Exception ex) {TrayContext.Error(ex);}}; menu.Items.Add(item);
            }
            menu.Opening+=delegate {foreach(ToolStripItem entry in menu.Items) {var item=entry as ToolStripMenuItem; if(item!=null && item.Tag!=null) item.Checked=(string)item.Tag==Theme.Mode;}};
            more.Click+=delegate {menu.Show(more,new Point(0,more.Height));}; more.Disposed+=delegate {menu.Dispose();};
            toolbar.Controls.Add(actions,0,0);
            var save=MakeButton("保存并应用",delegate {if(Save()) app.Apply(true); UpdateStatus();}); save.Primary=true; save.Width=112; save.Margin=Padding.Empty;
            toolbar.Controls.Add(save,1,0); layout.Controls.Add(toolbar,0,0);

            grid.Dock=DockStyle.Fill; grid.Margin=Padding.Empty; grid.BackgroundColor=Theme.Surface; grid.BorderStyle=BorderStyle.None;
            grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill; grid.RowHeadersVisible=false;
            grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect=true; grid.AllowUserToAddRows=false; grid.AllowUserToDeleteRows=false;
            grid.AllowUserToResizeRows=false; grid.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.EnableHeadersVisualStyles=false; grid.GridColor=Theme.Line; grid.CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal; grid.ColumnHeadersBorderStyle=DataGridViewHeaderBorderStyle.None;
            grid.DefaultCellStyle.BackColor=Theme.Surface; grid.DefaultCellStyle.ForeColor=Theme.Ink; grid.DefaultCellStyle.SelectionBackColor=Theme.PrimaryContainer; grid.DefaultCellStyle.SelectionForeColor=Theme.OnPrimaryContainer;
            grid.ColumnHeadersDefaultCellStyle.BackColor=Theme.SurfaceVariant; grid.ColumnHeadersDefaultCellStyle.ForeColor=Theme.Muted;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor=Theme.Muted;
            grid.ColumnHeadersDefaultCellStyle.Font=Theme.Font(9.5f,true); grid.ColumnHeadersDefaultCellStyle.SelectionBackColor=Theme.SurfaceVariant;
            grid.AlternatingRowsDefaultCellStyle.BackColor=Theme.Container;
            grid.Columns.Add(new DataGridViewTextBoxColumn {Name="Name",HeaderText="显示器名称",FillWeight=31});
            grid.Columns.Add(new DataGridViewTextBoxColumn {Name="Scale",HeaderText="目标 % · 点击修改",FillWeight=25});
            grid.Columns.Add(new DataGridViewTextBoxColumn {Name="Connection",HeaderText="连接状态",ReadOnly=true,FillWeight=20});
            grid.Columns.Add(new DataGridViewTextBoxColumn {Name="Current",HeaderText="当前",ReadOnly=true,FillWeight=24});
            grid.Columns[1].DefaultCellStyle.ForeColor=Theme.Blue; grid.Columns[1].DefaultCellStyle.Font=Theme.Font(10,true);
            grid.CellValueChanged+=delegate {if(!loading) MarkDirty();};
            grid.CurrentCellDirtyStateChanged+=delegate {if(grid.IsCurrentCellDirty) MarkDirty();};
            grid.DataBindingComplete+=delegate {UpdateGridMetrics();};
            layout.Controls.Add(grid,0,1);
            tips.SetToolTip(grid,"双击名称或目标比例编辑；Ctrl / Shift 多选；悬停当前比例查看支持范围。");
            grid.RowsAdded+=delegate {if(!loading) FitRows();}; grid.RowsRemoved+=delegate {if(!loading) FitRows();};
            status.Dock=DockStyle.Fill; status.TextAlign=ContentAlignment.MiddleLeft; status.AutoEllipsis=true; status.ForeColor=Theme.Muted; status.Font=Theme.Font(9); status.Margin=Padding.Empty; status.Cursor=Cursors.Hand;
            status.Click+=delegate {MessageBox.Show(this,app.Status,"运行状态",MessageBoxButtons.OK,MessageBoxIcon.Information);};
            layout.Controls.Add(status,0,2);
            var configBar=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=Padding.Empty};
            configBar.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            configBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,40)); configBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); configBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,112));
            configBar.Controls.Add(new Label {Text="配置",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Font=Theme.Font(9),ForeColor=Theme.Muted,Margin=Padding.Empty},0,0);
            var pathLink=new LinkLabel {Text=Store.FilePath,Dock=DockStyle.Fill,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft,LinkColor=Theme.Muted,ActiveLinkColor=Theme.Blue,LinkBehavior=LinkBehavior.HoverUnderline,Font=Theme.Font(9),Margin=new Padding(0,0,8,0)};
            pathLink.LinkClicked+=delegate {try {Process.Start("explorer.exe","/select,\""+Store.FilePath+"\"");} catch(Exception ex) {TrayContext.Error(ex);}};
            tips.SetToolTip(pathLink,Store.FilePath); configBar.Controls.Add(pathLink,1,0);
            var folder=MakeButton("打开文件夹",delegate {Process.Start("explorer.exe",Store.Folder);}); folder.Width=112; folder.Anchor=AnchorStyles.Right; folder.Margin=Padding.Empty;
            configBar.Controls.Add(folder,2,0); layout.Controls.Add(configBar,0,3);
            Controls.Add(layout); LoadRows();
            HandleCreated+=delegate {Theme.PaintTitle(this);};
            FormClosing+=delegate(object s,FormClosingEventArgs e) {if(!ForceClose && e.CloseReason==CloseReason.UserClosing) {e.Cancel=true; if(ResolveDirty()) Hide();}};
            Shown+=delegate {
                FitRows();
                var area=Screen.FromControl(this).WorkingArea;
                MinimumSize=new Size(Math.Min(MinimumSize.Width,area.Width-32),Math.Min(MinimumSize.Height,area.Height-32));
                Size=new Size(Math.Min(Width,area.Width-32),Math.Min(Height,area.Height-32));
                Location=new Point(area.Left+(area.Width-Width)/2,area.Top+(area.Height-Height)/2);
                UpdateGridMetrics();
                Theme.PaintTitle(this);
            };
            ResumeLayout(true);
        }
        void FitRows() {
            if(!IsHandleCreated) return;
            UpdateGridMetrics();
            using(var graphics=CreateGraphics()) {
                int height=(int)((120+32+32*Math.Max(2,Math.Min(8,grid.Rows.Count)))*graphics.DpiX/96f);
                int limit=Screen.FromControl(this).WorkingArea.Height-(Height-ClientSize.Height)-32;
                ClientSize=new Size(ClientSize.Width,Math.Min(height,limit));
            }
        }
        void UpdateGridMetrics() {
            if(!IsHandleCreated) return;
            using(var graphics=CreateGraphics()) {
                float scale=graphics.DpiX/96f;
                grid.RowTemplate.Height=(int)(32*scale); grid.ColumnHeadersHeight=(int)(32*scale);
                grid.DefaultCellStyle.Padding=new Padding((int)(8*scale),0,(int)(6*scale),0);
                grid.ColumnHeadersDefaultCellStyle.Padding=grid.DefaultCellStyle.Padding;
                foreach(DataGridViewRow row in grid.Rows) row.Height=grid.RowTemplate.Height;
            }
        }
        protected override void WndProc(ref Message message) {
            base.WndProc(ref message);
            if(message.Msg==0x02E0 && IsHandleCreated) BeginInvoke((Action)UpdateGridMetrics);
        }
        protected override void Dispose(bool disposing) {if(disposing) tips.Dispose(); base.Dispose(disposing);}
        ThemeButton MakeButton(string text,Action click) {
            var button=new ThemeButton {Text=text,Width=64,Height=30,Margin=new Padding(0,0,6,0)};
            button.Click+=delegate {try {click();} catch(Exception ex) {TrayContext.Error(ex);}}; return button;
        }
        ThemeButton AddButton(FlowLayoutPanel bar,string text,Action click) {var button=MakeButton(text,click); bar.Controls.Add(button); return button;}
        void MarkDirty() {dirty=true; Text="屏幕缩放 · Monitor Scale * 未保存";}
        void LoadRows() {
            loading=true; grid.Rows.Clear();
            foreach(var p in app.Config.displays) {
                int i=grid.Rows.Add(p.Key,p.Value.ToString(),"","—");
                grid.Rows[i].Tag=app.Config.devicePaths.ContainsKey(p.Key)?app.Config.devicePaths[p.Key]:null;
            }
            loading=false; dirty=false; Text="屏幕缩放 · Monitor Scale"; UpdateStatus(); UpdateGridMetrics();
        }
        public void UpdateStatus() {
            loading=true;
            foreach(DataGridViewRow row in grid.Rows) {
                string key=Convert.ToString(row.Cells[0].Value),path=row.Tag as string;
                var matches=app.Monitors.Where(item=>path!=null ? String.Equals(path,item.DevicePath,StringComparison.OrdinalIgnoreCase) : String.Equals(key,item.Name,StringComparison.OrdinalIgnoreCase)).ToList();
                var m=matches.Count==1?matches[0]:null;
                row.Cells[2].Value=matches.Count>1?"同名歧义，请检测绑定":m==null?"未连接 / 未匹配":"已连接";
                row.Cells[3].Value=m==null?"—":m.Error!=null?(m.Current>0?m.Current+"% / 自定义缩放":"读取失败"):m.Current+"%";
                row.Cells[3].ToolTipText=m==null?"":m.Error??("支持："+String.Join(", ",m.Supported)+"%");
            }
            loading=false; status.Text=app.Status; tips.SetToolTip(status,app.Status+"\n点击查看完整状态。");
        }
        bool Save() {
            try {
                grid.EndEdit(); var config=new Configuration();
                foreach(DataGridViewRow row in grid.Rows) {
                    string key=Convert.ToString(row.Cells[0].Value).Trim(); int value;
                    if(!Int32.TryParse(Convert.ToString(row.Cells[1].Value).Trim().TrimEnd('%'),out value)) throw new InvalidDataException("缩放必须是整数百分比。");
                    if(config.displays.ContainsKey(key)) throw new InvalidDataException("名称重复："+key);
                    config.displays.Add(key,value); if(row.Tag!=null) config.devicePaths.Add(key,(string)row.Tag);
                }
                config.Validate(); app.Save(config); LoadRows(); return true;
            } catch(Exception ex) {TrayContext.Error(ex); return false;}
        }
        public bool ResolveDirty() {
            grid.EndEdit(); if(!dirty) return true;
            var result=MessageBox.Show(this,"有未保存的修改。是否保存并应用？","屏幕缩放",MessageBoxButtons.YesNoCancel,MessageBoxIcon.Question);
            if(result==DialogResult.Cancel) return false;
            if(result==DialogResult.No) {LoadRows(); return true;}
            if(!Save()) return false; app.Apply(false); return true;
        }
        void DeleteSelected() {
            if(grid.SelectedRows.Count==0) return;
            if(MessageBox.Show(this,"删除选中的 "+grid.SelectedRows.Count+" 项配置？保存后生效。","删除配置",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes) return;
            foreach(DataGridViewRow row in grid.SelectedRows.Cast<DataGridViewRow>().ToArray()) grid.Rows.Remove(row); MarkDirty();
        }
        void BatchScale() {
            if(grid.SelectedRows.Count==0) return;
            using(var dialog=Theme.Dialog("批量设置缩放",new Size(340,160))) {
                dialog.Controls.Add(new Label {Text="为选中的 "+grid.SelectedRows.Count+" 项配置设置比例",Left=16,Top=12,Width=308,Height=24,Font=Theme.Font(10)});
                var values=new ComboBox {Left=16,Top=46,Width=308,DropDownStyle=ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat,BackColor=Theme.SurfaceVariant,ForeColor=Theme.Ink};
                values.Items.AddRange(Native.Scales.Select(x=>(object)(x+"%")).ToArray()); values.SelectedIndex=0;
                var ok=new ThemeButton {Text="设置所选条目",Left=204,Top=108,Width=120,Height=32,Primary=true,DialogResult=DialogResult.OK};
                var cancel=new ThemeButton {Text="取消",Left=92,Top=108,Width=100,Height=32,DialogResult=DialogResult.Cancel};
                dialog.Controls.Add(values); dialog.Controls.Add(ok); dialog.Controls.Add(cancel); dialog.AcceptButton=ok; dialog.CancelButton=cancel; dialog.ResumeLayout(true);
                if(dialog.ShowDialog(this)==DialogResult.OK) {foreach(DataGridViewRow row in grid.SelectedRows) row.Cells[1].Value=Native.Scales[values.SelectedIndex].ToString(); MarkDirty();}
            }
        }
        void RefreshDisplays() {
            grid.EndEdit(); app.Monitors=Native.Enumerate(); UpdateStatus();
            var paths=new HashSet<string>(grid.Rows.Cast<DataGridViewRow>().Where(r=>r.Tag!=null).Select(r=>(string)r.Tag),StringComparer.OrdinalIgnoreCase);
            var fresh=app.Monitors.Where(m=>!paths.Contains(m.DevicePath)).ToList();
            if(fresh.Count==0) {MessageBox.Show(this,"当前工作的显示器均已添加。","刷新检测"); return;}
            using(var dialog=Theme.Dialog("添加检测到的显示器",new Size(580,80+28*Math.Max(2,Math.Min(6,fresh.Count))))) {
                var content=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(16)};
                content.RowStyles.Add(new RowStyle(SizeType.Percent,100)); content.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
                var list=new CheckedListBox {Dock=DockStyle.Fill,CheckOnClick=true,HorizontalScrollbar=true,BorderStyle=BorderStyle.None,BackColor=Theme.Surface,ForeColor=Theme.Ink};
                foreach(var m in fresh) list.Items.Add(m.Name+"  ·  当前 "+(m.Current==0?"未知":m.Current+"%")+"  ·  "+m.DevicePath,true);
                var bottom=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(0,10,0,0),Margin=Padding.Empty};
                var ok=new ThemeButton {Text="添加所选",Height=32,Primary=true,DialogResult=DialogResult.OK,Margin=Padding.Empty}; var cancel=new ThemeButton {Text="取消",Height=32,DialogResult=DialogResult.Cancel};
                bottom.Controls.Add(ok); bottom.Controls.Add(cancel); content.Controls.Add(list,0,0); content.Controls.Add(bottom,0,1); dialog.Controls.Add(content); dialog.AcceptButton=ok; dialog.CancelButton=cancel; dialog.ResumeLayout(true);
                if(dialog.ShowDialog(this)!=DialogResult.OK) return;
                foreach(int index in list.CheckedIndices) {
                    var m=fresh[index];
                    var existing=grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r=>r.Tag==null && String.Equals(Convert.ToString(r.Cells[0].Value),m.Name,StringComparison.OrdinalIgnoreCase));
                    if(existing!=null && app.Monitors.Count(x=>x.Name==m.Name)==1) {existing.Tag=m.DevicePath; MarkDirty(); continue;}
                    string name=m.Name; int suffix=2;
                    while(grid.Rows.Cast<DataGridViewRow>().Any(r=>String.Equals(Convert.ToString(r.Cells[0].Value),name,StringComparison.OrdinalIgnoreCase))) name=m.Name+" ("+(suffix++)+")";
                    int row=grid.Rows.Add(name,m.Current==0?"100":m.Current.ToString(),"已连接",""); grid.Rows[row].Tag=m.DevicePath; MarkDirty();
                }
                UpdateStatus();
            }
        }
    }
}
