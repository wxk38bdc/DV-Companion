using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace DVCompanion;

public sealed partial class MainForm:Form
{
    readonly TextBox root=new(){Text=@"C:\Program Files\DV",Dock=DockStyle.Fill};
    readonly StyledButton start=new(){Text="启动 DV",AutoSize=true},stop=new(){Text="正常退出本次 DV",AutoSize=true},refresh=new(){Text="刷新设备与进程",AutoSize=true};
    readonly StyledButton recover=new(){Text="正常关闭所选后台",AutoSize=true},force=new(){Text="强制结束所选进程…",AutoSize=true};
    readonly CheckBox patched=new(){Text="使用修复后台（保留原 DV 界面）",AutoSize=true,Checked=File.Exists(Session.PatchedExecutable)};
    readonly Label status=new(){Text="就绪",AutoSize=true,Font=new Font("Microsoft YaHei UI",15,FontStyle.Bold)},detail=new(){AutoSize=true,MaximumSize=new Size(970,0)};
    readonly DataGridView cameras=Grid(),processes=Grid(),findings=Grid();
    readonly TextBox activity=new(){Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,BackColor=Color.White,Font=new Font("Microsoft YaHei UI",9)},editor=new(){Multiline=true,ScrollBars=ScrollBars.Both,WordWrap=false,Dock=DockStyle.Fill,Font=new Font("Consolas",10),AcceptsTab=true};
    readonly Label configPath=new(){Text="尚未选择配置；不会更改 DV 现有项目。",AutoSize=true,MaximumSize=new Size(1000,0)};
    readonly CheckBox dump=new(){Text="诊断时包含所选进程的简要转储（仅保存在本机）",AutoSize=true};
    readonly System.Windows.Forms.Timer timer=new(){Interval=2000};
    Session? session;List<CameraDevice> deviceList=[];bool busy,monitoring,closing,orphanHandled,hangCaptured;int tick,hungTicks;string? loadedFile;
    readonly string startupLog=Path.Combine(Storage.NewFolder("manager"),"events.log");
    public MainForm()
    {
        Text="DV Companion · DV 配套管理器";Size=new Size(1240,940);MinimumSize=new Size(1080,800);StartPosition=FormStartPosition.CenterScreen;
        using(var stream=typeof(MainForm).Assembly.GetManifestResourceStream("DVCompanion.Brand.ico")!)
        using(var embeddedIcon=new Icon(stream))Icon=(Icon)embeddedIcon.Clone();
        Font=new Font("Microsoft YaHei UI",9.5f);BackColor=Presentation.Canvas;AutoScaleMode=AutoScaleMode.Dpi;
        findings.DataBindingComplete+=(_,_)=>{if(findings.Columns[nameof(Finding.Code)] is {} code)code.Visible=false;if(findings.Columns[nameof(Finding.Level)] is {} level){level.HeaderText="结果";level.FillWeight=15;}if(findings.Columns[nameof(Finding.Message)] is {} message){message.HeaderText="检查说明";message.FillWeight=85;}findings.DefaultCellStyle.WrapMode=DataGridViewTriState.True;findings.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells;};
        var (browse,open,inspect,save,snapshot,restore,export,folder)=BuildPresentation();
        browse.Click+=(_,_)=>{if(session!=null){Warn("请先结束本次会话。");return;}using var d=new FolderBrowserDialog{Description="选择包含 gui 和 runtime 的 DV 安装目录"};if(d.ShowDialog()==DialogResult.OK)root.Text=d.SelectedPath;};
        start.Click+=async(_,_)=>await Run(StartDv);stop.Click+=async(_,_)=>await Run(StopDv);refresh.Click+=async(_,_)=>await Run(RefreshAll);
        recover.Click+=async(_,_)=>await Run(RecoverSelected);force.Click+=async(_,_)=>await Run(ForceSelected);
        open.Click+=(_,_)=>OpenConfig();inspect.Click+=async(_,_)=>await Run(()=>CheckConfig());save.Click+=async(_,_)=>await Run(SaveConfig);snapshot.Click+=async(_,_)=>await Run(ReadSnapshot);
        restore.Click+=async(_,_)=>await Run(RestoreProject);
        export.Click+=async(_,_)=>await Run(async()=>{var p=Selected();string zip=await Diagnostics.Capture(root.Text,session,p==null?null:[p],dump.Checked);Log("诊断包："+zip);MessageBox.Show(this,"已保存到：\n"+zip,"诊断已保存",MessageBoxButtons.OK,MessageBoxIcon.Information);});
        folder.Click+=(_,_)=>{Directory.CreateDirectory(Storage.Root);Process.Start(new ProcessStartInfo("explorer.exe",Native.Quote(Storage.Root)){UseShellExecute=true});};
        timer.Tick+=async(_,_)=>await Monitor();Shown+=async(_,_)=>{Log("管理器已启动，所有操作均在本机执行。");foreach(var note in PreferencesLease.RecoverPending())Log(note);await Run(RefreshAll);timer.Start();};
        FormClosing+=OnClosing;
    }
    static DataGridView Grid()=>new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,RowHeadersVisible=false,BackgroundColor=Color.White,BorderStyle=BorderStyle.FixedSingle,AutoGenerateColumns=true};
    void Log(string text)
    {
        if(InvokeRequired){BeginInvoke(()=>Log(text));return;}string line=DateTime.Now.ToString("HH:mm:ss")+"  "+text;
        File.AppendAllText(startupLog,line+Environment.NewLine,Encoding.UTF8);activity.AppendText(line+Environment.NewLine);if(activity.TextLength>100000)activity.Text=activity.Text[^70000..];
    }
    void Warn(string text)=>MessageBox.Show(this,text,"DV Companion",MessageBoxButtons.OK,MessageBoxIcon.Information);
    bool Ask(string text)=>MessageBox.Show(this,text,"请确认",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)==DialogResult.Yes;
    void State(string title,string info){status.Text=title;detail.Text=info;}
    async Task Run(Func<Task> action)
    {
        if(busy)return;busy=true;Buttons(false);try{await action();}catch(Exception ex){Log("操作未完成："+ex.Message);State("需要处理",ex.Message);Warn(ex.Message);}finally{busy=false;Buttons(true);}
    }
    void Buttons(bool enabled){start.Enabled=enabled&&session==null;stop.Enabled=enabled&&session!=null;refresh.Enabled=recover.Enabled=force.Enabled=enabled;root.Enabled=patched.Enabled=enabled&&session==null;syncNow.Enabled=enabled&&stereoState?.CanSync==true;stereoRefresh.Enabled=enabled;UpdateRecordingButtons(enabled);}
    async Task RefreshAll()
    {
        var result=await Task.Run(()=>(devices:Native.Cameras(),procs:Native.DvProcesses(root.Text)));deviceList=result.devices;
        cameras.DataSource=deviceList.Select(d=>new{设备=d.Name,序列号=d.Serial,状态=d.State}).ToList();BindProcesses(result.procs);
        if(session==null)State(result.procs.Count>0?"发现其他 DV 进程":"可以启动",result.procs.Count>0?"请先确认原 DV 是否已关闭。后台残留可选中后请求正常退出。":"检测到 "+deviceList.Count+" 台相机；运行中故障会显示提示并记录。");
        await RefreshStereo();
    }
    void BindProcesses(List<ProcessIdentity> all)
    {
        var selected=Selected()?.Pid;var owned=session?.Job.Members()??[];
        all=all.Concat(owned).GroupBy(p=>(p.Pid,p.StartedUtcTicks)).Select(g=>g.First()).ToList();
        processes.DataSource=all.Select(p=>new ProcessRow(p,owned.Any(x=>x.Pid==p.Pid&&x.StartedUtcTicks==p.StartedUtcTicks)?"本次管理":"其他 DV")).ToList();
        if(processes.Columns[nameof(ProcessRow.Identity)] is { } identityColumn)identityColumn.Visible=false;
        if(selected!=null)foreach(DataGridViewRow r in processes.Rows)if(r.DataBoundItem is ProcessRow item&&item.Identity.Pid==selected){r.Selected=true;processes.CurrentCell=r.Cells[nameof(ProcessRow.PID)];break;}
    }
    public record ProcessRow(ProcessIdentity Identity,string 归属){public int PID=>Identity.Pid;public string 进程=>Identity.Name;public string 启动时间=>new DateTime(Identity.StartedUtcTicks,DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss");}
    ProcessIdentity? Selected()=>(processes.CurrentRow?.DataBoundItem as ProcessRow)?.Identity;
    async Task StartDv()
    {
        if(session!=null)return;
        if(!Directory.Exists(root.Text))throw new DirectoryNotFoundException("找不到 DV 安装目录");
        var others=await Task.Run(()=>Native.DvProcesses(root.Text));if(others.Count>0){await RefreshAll();throw new InvalidOperationException("检测到其他 DV 进程。请先关闭原 DV；确认残留后，可选中后台请求正常退出。不会自动结束它们。");}
        if(!string.IsNullOrWhiteSpace(editor.Text)){await CheckConfig();if(findings.DataSource is List<Finding> list&&list.Any(f=>f.Level=="错误"))throw new InvalidOperationException("配置检查尚未通过，请先处理错误。");Log("配置检查通过。DV 启动完成后，在配置页点击“恢复此项目到 DV”。");}
        var created=new Session(root.Text,patched.Checked);created.Message+=Log;
        try{await created.StartGui(editor.Text);session=created;orphanHandled=hangCaptured=false;hungTicks=0;State("DV 正在启动","关闭 DV 后，管理器会检查并请求本次残留后台正常退出。");}catch{created.Dispose();throw;}
        await RefreshAll();
    }
    async Task StopDv()
    {
        if(session==null)return;var current=session;
        foreach(var p in current.Runtimes().ToList())
        {
            try{string state=Configuration.RecordingStatus(await current.Snapshot(p));if(state!="未发现活动录制"&&!Ask("后台状态："+state+"。\n正常退出会停止本次任务并请求写盘完成。是否继续？"))return;}
            catch(Exception ex){Log("录制状态无法确认："+ex.Message);if(!Ask("无法确认后台录制状态。\n可以请求 DV 正常退出，但不会自动强杀。是否继续？"))return;}
        }
        current.StopRequested=true;
        if(current.Gui?.Alive==true)
        {
            using var gui=Process.GetProcessById(current.Gui.Pid);gui.CloseMainWindow();Log("已请求 DV 界面关闭。请处理 DV 自己的保存提示。");
            var end=DateTime.UtcNow.AddSeconds(18);while(current.Gui.Alive&&DateTime.UtcNow<end)await Task.Delay(200);
            if(current.Gui.Alive){await Diagnostics.Capture(root.Text,current);State("DV 尚未退出","请检查 DV 的 Shutdown runtime 确认窗口并选择 Shutdown，也可能正在等待保存。诊断已保存；本工具未强制结束。");current.StopRequested=false;return;}
        }
        foreach(var p in current.Runtimes().ToList())try{await current.ShutdownRuntime(p);}catch(Exception ex){Log("正常退出未完成："+ex.Message);}
        var members=current.Remember();if(members.Count==0)FinishSession("本次 DV 已全部退出，无残留。");else{await Diagnostics.Capture(root.Text,current);State("仍有本次进程待处理","诊断已保存。可以选中后台再次正常关闭；强制结束需要单独确认。");current.StopRequested=false;}
        await RefreshAll();
    }
    void FinishSession(string message){if(session==null)return;session.Note(message);session.Dispose();session=null;Log(message);State("已退出",message);stereoEpoch++;SetStereoUnavailable("DV 已退出","重新启动两台相机采集后，再检查并同步。");SetRecordingsUnavailable("DV 已退出，文件录制状态不再实时更新。");Buttons(!busy);}
    async Task RecoverSelected()
    {
        var p=Selected()??throw new InvalidOperationException("请先在进程表中选中一个 DV 后台。");
        if(!p.Name.Equals("dv-runtime.exe",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请通过 DV 自身窗口关闭界面，或使用“正常退出本次 DV”。");
        if(!p.Alive)throw new InvalidOperationException("该进程已退出，请刷新。");
        string recording="无法确认";try{recording=Configuration.RecordingStatus(await Diagnostics.Snapshot(p));}catch(Exception ex){Log(ex.Message);}
        if(!Ask("所选后台 PID "+p.Pid+"，录制状态："+recording+"。\n将保存诊断，并请求它停止任务、写盘后正常退出。\n如果原 DV 界面仍在使用它，界面将断开。是否继续？"))return;
        await Diagnostics.Capture(root.Text,session,[p]);if(!p.Alive)return;int port=Diagnostics.RuntimePort(p);
        await DvProtocol.CallAsync(port,10,"/system/","running","false");var deadline=DateTime.UtcNow.AddSeconds(20);while(p.Alive&&DateTime.UtcNow<deadline)await Task.Delay(200);
        Log(p.Alive?"后台没有完成退出；未强制结束。":"所选后台已正常退出，可以重新启动 DV。");await RefreshAll();
    }
    async Task ForceSelected()
    {
        var p=Selected()??throw new InvalidOperationException("请先选中一个 DV 进程。");
        if(!p.Alive||(!Native.IsUnder(p.Path,root.Text)&&session?.Owns(p)!=true))throw new InvalidOperationException("进程身份发生变化，拒绝结束。");
        if(!Ask("强制结束 PID "+p.Pid+"（"+p.Name+"）可能使当前录制文件不完整，并丢失未保存配置。\n仅结束所选进程，不按名称批量结束。\n确认当前任务可以中断后，再选择“是”。"))return;
        await Diagnostics.Capture(root.Text,session,[p],dump.Checked);if(!p.Alive)return;
        using(var proc=Process.GetProcessById(p.Pid)){if(!p.Alive)throw new InvalidOperationException("进程身份已改变");proc.Kill(false);await proc.WaitForExitAsync();}
        Log("用户确认后强制结束了 PID "+p.Pid+"。请检查最后一次录制文件。");await RefreshAll();
    }
    void OpenConfig()
    {
        if(busy)return;using var d=new OpenFileDialog{Filter="DV 项目 XML|*.xml|所有文件|*.*"};if(d.ShowDialog()!=DialogResult.OK)return;
        try{if(new FileInfo(d.FileName).Length>8_000_000)throw new InvalidDataException("文件超过 8 MB 限制");editor.Text=File.ReadAllText(d.FileName);loadedFile=d.FileName;configPath.Text="检查文件："+loadedFile+"；请点击“恢复此项目到 DV”。";Log("已读取配置 "+loadedFile);}catch(Exception ex){Warn(ex.Message);}
    }
    async Task CheckConfig()
    {
        var doc=Configuration.Parse(editor.Text);deviceList=await Task.Run(Native.Cameras);var list=Configuration.Validate(doc,deviceList);findings.DataSource=list;Log("配置检查："+list.Count(f=>f.Level=="错误")+" 个错误、"+list.Count(f=>f.Level=="提醒")+" 个提醒。");
    }
    ProcessIdentity SnapshotTarget()
    {
        if(session!=null)
        {
            var runtimes=session.Runtimes().ToList();
            if(runtimes.Count!=1)throw new InvalidOperationException("本次 DV 后台数量不唯一，无法读取当前结构。请检查设备与运行页；未保存编辑区旧副本。");
            return runtimes[0];
        }
        var selected=Selected();
        if(selected==null||!selected.Name.Equals("dv-runtime.exe",StringComparison.OrdinalIgnoreCase)||!selected.Alive)
            throw new InvalidOperationException("请先通过本工具启动 DV，或在设备与运行页选择正在使用的 DV 后台。保存会读取当前 DV 的最新结构，不会把编辑区旧副本当作当前项目。");
        return selected;
    }
    async Task SaveConfig()
    {
        var p=SnapshotTarget();var currentSession=session;
        using var d=new SaveFileDialog{Filter="DV 项目 XML|*.xml",FileName=loadedFile==null?"current-project.xml":Path.GetFileNameWithoutExtension(loadedFile)+"-updated.xml"};if(d.ShowDialog()!=DialogResult.OK)return;
        State("正在保存当前 DV 项目","正在读取最新模块、参数、连线及界面布局，请暂时不要修改 DV。");
        try
        {
            // Read after the file dialog, so edits made while choosing a filename are included.
            var saved=await ProjectSave.Current(d.FileName,()=>currentSession!=null?currentSession.Snapshot(p):Diagnostics.Snapshot(p));
            editor.Text=saved.Xml;loadedFile=d.FileName;
            configPath.Text="已保存当前 DV："+d.FileName+"；读取时间 "+saved.CapturedAt.ToString("HH:mm:ss")+"，"+saved.Modules+" 个模块。下次保存会再次读取最新结构。";
            Log("已读取后台 PID "+p.Pid+" 并保存可恢复项目："+d.FileName+(saved.Backup.Length>0?"；原文件备份："+saved.Backup:""));
            State("当前项目已保存","最新结构、参数与连线已写入 XML；项目内的录制模块保持停止。");
        }
        catch{State("项目未保存","读取或写入失败；未使用编辑区旧副本代替当前 DV。请查看具体错误后重试。");throw;}
    }
    async Task ReadSnapshot()
    {
        var p=SnapshotTarget();
        editor.Text=(await Diagnostics.Snapshot(p)).ToString();loadedFile=null;configPath.Text="后台 PID "+p.Pid+" 的运行快照；编辑区不实时更新，保存时会重新读取当前 DV。原项目文件未改动。";findings.DataSource=Configuration.Validate(Configuration.Parse(editor.Text),deviceList);Log("已读取后台配置快照。");
    }
    async Task RestoreProject()
    {
        if(session==null||!session.UsePatched)throw new InvalidOperationException("请先通过本工具启动并勾选修复后台，再恢复项目。");
        var runtimes=session.Runtimes().ToList();if(runtimes.Count!=1)throw new InvalidOperationException("本次后台数量不唯一，未恢复项目。");
        var doc=Configuration.Parse(editor.Text);ProjectRestore.Order(ProjectRestore.Portable(doc));
        if(!Ask("将停止当前采集并替换本次 DV 的模块、连线和参数。会先保存当前结构，录制模块不会自动启动。\n请勿同时在原 DV 中编辑或加载项目。是否继续？"))return;
        stereoEpoch++;SetStereoUnavailable("正在恢复项目","恢复完成后将重新读取双机状态。");
        SetRecordingsUnavailable("正在恢复项目，原录制列表已失效。");
        string folder=Storage.NewFolder("project-restores");State("正在恢复项目","正在重建结构并核对参数，请暂时不要操作原 DV。");
        await ProjectRestore.Restore(Diagnostics.RuntimePort(runtimes[0]),doc,folder,Log);Log("恢复记录："+folder);configPath.Text="项目已恢复。可继续在 DV 中修改，再点击“保存可恢复项目…”直接保存最新结构。";State("项目恢复已完成","模块、参数及连线已核对；文件录制保持停止。请在原 DV 的 Structure 页查看。");
        await RefreshStereo();
    }
    async Task Monitor()
    {
        if(busy||monitoring||closing)return;monitoring=true;
        try
        {
            if(++tick%5==0)await RefreshAll();
            else if(tick%3==0)await RefreshStereo();
            if(busy||closing)return;
            if(session==null)return;var current=session;var members=current.Remember();BindProcesses(Native.DvProcesses(root.Text));
            if(members.Count==0){FinishSession("本次 DV 已全部退出，无残留。");return;}
            string text=current.NewRuntimeText();string? error=text.Split('\n').LastOrDefault(l=>l.Contains(": ERROR:")&&!l.Contains("ConfigServer: Client"));if(error!=null){string advice=ErrorAdvice.Explain(error);State("检测到运行错误",advice);current.Note(error.Trim());Log(advice);}
            if(current.Gui?.Alive==true)
            {
                if(status.Text=="DV 正在启动")State("DV 运行中","本次进程已纳入管理。遇到问题可先保存诊断，再尝试正常退出或恢复。");
                try{using var p=Process.GetProcessById(current.Gui.Pid);hungTicks=p.Responding?0:hungTicks+1;}catch{hungTicks=0;}
                if(hungTicks>=8&&!hangCaptured){hangCaptured=true;State("DV 界面持续无响应","正在保存诊断；不会自动中断录制。可尝试正常退出或手动恢复。");Log("DV 界面持续无响应，自动保存诊断。");await Diagnostics.Capture(root.Text,current);}
            }
            else if(!current.StopRequested&&!orphanHandled)
            {
                orphanHandled=true;State("DV 界面已退出","正在检查本次后台是否仍在运行。");
                foreach(var p in current.Runtimes().ToList())
                {
                    try{string recording=Configuration.RecordingStatus(await current.Snapshot(p));if(recording!="未发现活动录制"){State("后台仍有任务","录制状态："+recording+"。请选中后台正常关闭；未自动结束。");await Diagnostics.Capture(root.Text,current);continue;}await current.ShutdownRuntime(p);}
                    catch(Exception ex){Log("退出检查失败："+ex.Message);State("后台仍待处理","无法确认录制状态。诊断已保存；请使用正常关闭或恢复入口。");await Diagnostics.Capture(root.Text,current);}
                }
                if(current.Remember().Count==0)FinishSession("界面和本次后台均已退出，无残留。");
            }
        }
        catch(Exception ex){Log("监测："+ex.Message);}finally{monitoring=false;}
    }
    async void OnClosing(object? sender,FormClosingEventArgs e)
    {
        if(!closing&&busy){e.Cancel=true;Warn("正在处理任务，请完成后再关闭管理器。");return;}
        if(closing)return;if(session!=null){e.Cancel=true;if(busy||monitoring){Warn("正在处理任务，请稍后关闭。");return;}if(!Ask("本次 DV 仍在运行。先请求正常退出，完成后关闭管理器？"))return;await Run(StopDv);if(session!=null)return;}
        closing=true;timer.Stop();if(e.Cancel)BeginInvoke(Close);
    }
    internal void RenderPreviews(string folder,string? stereoSnapshot=null)
    {
        Directory.CreateDirectory(folder);deviceList=Native.Cameras();cameras.DataSource=deviceList.Select(d=>new{设备=d.Name,序列号=d.Serial,状态=d.State}).ToList();BindProcesses(Native.DvProcesses(root.Text));State("设备检查完成","已识别 "+deviceList.Count+" 台相机。启动前请先处理其他 DV 实例，避免重复打开设备。");
        var tab=workspaceTabs;
        editor.Text="<dv version=\"2.0\">\r\n  <node name=\"\" path=\"/\">\r\n    <node name=\"mainloop\" path=\"/mainloop/\" />\r\n  </node>\r\n</dv>";
        findings.DataSource=Configuration.Validate(Configuration.Parse(editor.Text));activity.Text="管理器已启动。\r\n设备和进程检查完成。\r\n所有诊断记录只保存在本机。";
        var shell=Controls.OfType<TableLayoutPanel>().Single();Controls.Remove(shell);shell.BindingContext=new BindingContext();shell.Dock=DockStyle.None;shell.Size=ClientSize;shell.Visible=true;shell.CreateControl();shell.PerformLayout();
        object LayoutInfo(Control c)=>new{type=c.GetType().Name,text=c.Text,bounds=new{c.Left,c.Top,c.Width,c.Height},font=c.Font.Size,fontHeight=c.Font.Height,dpi=c.DeviceDpi,children=c.Controls.Cast<Control>().Select(LayoutInfo).ToArray()};
        Buttons(true);
        foreach(string mode in new[]{"preview","minimum"})
        {
            if(mode=="minimum")shell.Size=new Size(MinimumSize.Width-(Width-ClientSize.Width),MinimumSize.Height-(Height-ClientSize.Height));
            for(int i=0;i<tab.TabPages.Count;i++){tab.SelectedIndex=i;tab.SelectedTab!.CreateControl();if(i==3&&stereoSnapshot!=null){ShowStereo(StereoSync.Inspect(Configuration.Parse(File.ReadAllText(stereoSnapshot))),ProcessIdentity.Read(Environment.ProcessId)!);stereoUpdated.Text="界面验证 · 已完成的实物同步快照（非实时连接）";syncNow.Enabled=false;}if(i==4&&stereoSnapshot!=null){ShowRecordings(Configuration.Parse(File.ReadAllText(stereoSnapshot)),ProcessIdentity.Read(Environment.ProcessId)!);recordingUpdated.Text="界面预览 · 已完成的实物录制快照（非实时连接）";recordingOnline=false;UpdateRecordingButtons(false);}shell.PerformLayout();using var bitmap=new Bitmap(shell.Width,shell.Height);shell.DrawToBitmap(bitmap,new Rectangle(Point.Empty,shell.Size));bitmap.Save(Path.Combine(folder,mode+"-"+i+".png"),System.Drawing.Imaging.ImageFormat.Png);Storage.WriteJson(Path.Combine(folder,mode+"-"+i+"-layout.json"),LayoutInfo(shell));}
        }
        shell.Dispose();
    }
}



