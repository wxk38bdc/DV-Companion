using System.ComponentModel;
using System.Xml.Linq;
namespace DVCompanion;

public sealed partial class MainForm
{
    readonly StyledButton recordingRefresh=new(){Text="刷新录制模块"},recordingStart=new(){Text="开始所选录制"},recordingStop=new(){Text="停止所选录制"},recordingAll=new(){Text="全选"},recordingNone=new(){Text="清空"};
    readonly DataGridView recordingGrid=Grid();
    readonly CheckBox recordingSync=new(){Text="多相机数据源开始前检查时间同步",Checked=true,AutoSize=true};
    readonly Label recordingSummary=Presentation.Label("尚未连接 DV 后台",11,true),recordingUpdated=Presentation.Label("只显示当前后台，不会自动加载或改写 XML。",9,false,Presentation.Muted);
    readonly TextBox recordingDetails=new(){ReadOnly=true,Multiline=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None,BackColor=Presentation.Soft,ForeColor=Presentation.Ink};
    bool recordingOnline;ProcessIdentity? recordingTarget;string recordingSourceKey="";string recordingLast="";
    sealed class RecordingRow
    {
        public bool 选择{get;set;}
        public bool Online{get;set;}=true;
        public string 模块=>Model.Name;
        public string 状态=>Online?Model.State:"待重新确认";
        public string 输入=>string.Join(" · ",System.Text.RegularExpressions.Regex.Matches(Model.Inputs,@"([A-Za-z0-9_.-]+)\[([^\]]+)\]").Cast<System.Text.RegularExpressions.Match>().GroupBy(m=>m.Groups[1].Value).Select(g=>g.Key+": "+string.Join(" / ",g.Select(m=>m.Groups[2].Value))));
        public string 输出目录=>Model.Directory;
        public string 文件前缀=>RecordingControl.BasePrefix(Model.Prefix);
        public string 写入=>!Online?"待确认":long.TryParse(Model.Bytes,out var bytes)?(bytes/1048576d).ToString("0.0")+" MiB":"待确认";
        public RecordingModule Model{get;init;}=null!;
    }
    TabPage BuildRecordingPage()
    {
        var page=new TabPage("录制控制"){Padding=new Padding(14,12,14,2),BackColor=Color.White};
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Margin=Padding.Empty,BackColor=Color.White};layout.ColumnStyles.Add(new(SizeType.Percent,100));
        layout.RowStyles.Add(new(SizeType.Absolute,52));layout.RowStyles.Add(new(SizeType.Absolute,40));layout.RowStyles.Add(new(SizeType.Absolute,56));layout.RowStyles.Add(new(SizeType.Percent,62));layout.RowStyles.Add(new(SizeType.Percent,38));layout.RowStyles.Add(new(SizeType.Absolute,42));page.Controls.Add(layout);
        var actions=Presentation.Flow();foreach(var button in new[]{recordingStart,recordingStop,recordingRefresh,recordingAll,recordingNone})Presentation.Button(button,button==recordingStart?"primary":button==recordingStop?"accent":"neutral");actions.Controls.AddRange([recordingStart,recordingStop,recordingRefresh,recordingAll,recordingNone]);layout.Controls.Add(actions,0,0);
        recordingSync.ForeColor=Presentation.Muted;recordingSync.Margin=new Padding(0,3,0,0);recordingSync.Font=new Font("Microsoft YaHei UI",9);layout.Controls.Add(recordingSync,0,1);
        var statusLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,BackColor=Color.White,Margin=Padding.Empty};statusLayout.ColumnStyles.Add(new(SizeType.Percent,100));statusLayout.RowStyles.Add(new(SizeType.Absolute,29));statusLayout.RowStyles.Add(new(SizeType.Percent,100));recordingUpdated.AutoSize=false;recordingUpdated.Dock=DockStyle.Fill;recordingUpdated.AutoEllipsis=true;statusLayout.Controls.Add(recordingSummary,0,0);statusLayout.Controls.Add(recordingUpdated,0,1);layout.Controls.Add(statusLayout,0,2);
        recordingGrid.ReadOnly=false;recordingGrid.EditMode=DataGridViewEditMode.EditOnEnter;recordingGrid.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells;recordingGrid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;Presentation.Grid(recordingGrid);Presentation.Empty(recordingGrid,"启动并恢复项目后，点击刷新；自动识别 AEDAT4 文件录制模块");layout.Controls.Add(recordingGrid,0,3);
        recordingGrid.DataBindingComplete+=(_,_)=>
        {
            foreach(DataGridViewColumn column in recordingGrid.Columns)column.ReadOnly=column.Name!="选择";
            if(recordingGrid.Columns[nameof(RecordingRow.Model)] is {} model)model.Visible=false;
            if(recordingGrid.Columns[nameof(RecordingRow.Online)] is {} online)online.Visible=false;
            foreach(var (name,weight) in new[]{("选择",7),("模块",18),("状态",12),("输入",24),("输出目录",15),("文件前缀",15),("写入",9)})if(recordingGrid.Columns[name] is {} column){column.HeaderText=name;column.FillWeight=weight;}
            UpdateRecordingButtons(!busy);
        };
        recordingGrid.CurrentCellDirtyStateChanged+=(_,_)=>{if(recordingGrid.IsCurrentCellDirty)recordingGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);};
        recordingGrid.CellValueChanged+=(_,_)=>UpdateRecordingButtons(!busy);recordingGrid.SelectionChanged+=(_,_)=>UpdateRecordingDetails();
        var detailsSurface=Presentation.Surface(Presentation.Soft,new Padding(10,7,10,7));detailsSurface.Margin=new Padding(0,8,0,5);detailsSurface.Radius=8;recordingDetails.Font=new Font("Microsoft YaHei UI",9);detailsSurface.Controls.Add(recordingDetails);layout.Controls.Add(detailsSurface,0,4);
        var note=Presentation.Label("联合启动减少操作间隔；精确对齐仍依赖同步时间戳。文件名前缀自动附加批次号，避免快速重录覆盖文件。",8.5f,false,Presentation.Muted);note.AutoSize=false;note.Dock=DockStyle.Fill;layout.Controls.Add(note,0,5);
        recordingRefresh.Click+=async(_,_)=>await Run(()=>RefreshStereo(true));
        recordingStart.Click+=async(_,_)=>await Run(()=>ControlRecording(true));recordingStop.Click+=async(_,_)=>await Run(()=>ControlRecording(false));
        recordingAll.Click+=(_,_)=>SelectRecordings(true);recordingNone.Click+=(_,_)=>SelectRecordings(false);
        workspaceTabs.SelectedIndexChanged+=async(_,_)=>{if(workspaceTabs.SelectedTab==page&&!busy&&!monitoring)await RefreshStereo();};
        SetRecordingsUnavailable("请先通过本工具启动 DV，并恢复需要录制的项目。");return page;
    }
    void SelectRecordings(bool selected)
    {
        recordingGrid.EndEdit();foreach(DataGridViewRow row in recordingGrid.Rows)if(row.DataBoundItem is RecordingRow item&&item.Model.Supported)item.选择=selected;recordingGrid.Refresh();UpdateRecordingButtons(!busy);
    }
    string[] SelectedRecordings(){recordingGrid.EndEdit();return recordingGrid.Rows.Cast<DataGridViewRow>().Select(r=>r.DataBoundItem).OfType<RecordingRow>().Where(r=>r.选择).Select(r=>r.Model.Name).ToArray();}
    void UpdateRecordingButtons(bool enabled)
    {
        bool any=recordingGrid.Rows.Cast<DataGridViewRow>().Any(r=>r.DataBoundItem is RecordingRow row&&row.选择);
        recordingStart.Enabled=recordingStop.Enabled=enabled&&recordingOnline&&any;recordingRefresh.Enabled=recordingAll.Enabled=recordingNone.Enabled=enabled;recordingSync.Enabled=recordingGrid.Enabled=enabled;
    }
    void SetRecordingsUnavailable(string reason)
    {
        recordingOnline=false;recordingTarget=null;recordingSummary.Text="录制状态未确认";recordingUpdated.Text=reason;
        // Retain names and checks for recovery, but do not display stale writing counts as live.
        foreach(DataGridViewRow row in recordingGrid.Rows)if(row.DataBoundItem is RecordingRow item){item.Online=false;row.DefaultCellStyle.ForeColor=Presentation.Muted;}recordingGrid.Refresh();
        UpdateRecordingButtons(!busy);UpdateRecordingDetails();
    }
    void ShowRecordings(XDocument snapshot,ProcessIdentity target)
    {
        string source=target.Pid+":"+target.StartedUtcTicks;bool sameSource=source==recordingSourceKey;
        var selected=recordingGrid.Rows.Cast<DataGridViewRow>().Select(r=>r.DataBoundItem).OfType<RecordingRow>().Where(r=>r.选择).ToDictionary(r=>r.Model.Name,r=>r.Model);
        string? current=(recordingGrid.CurrentRow?.DataBoundItem as RecordingRow)?.Model.Name;
        var modules=RecordingControl.Inspect(snapshot);var rows=modules.Select(m=>new RecordingRow{Model=m,选择=sameSource&&selected.TryGetValue(m.Name,out var old)&&old.Library==m.Library&&old.Inputs==m.Inputs}).ToList();
        recordingSourceKey=source;recordingTarget=target;recordingOnline=true;recordingGrid.DataSource=new BindingList<RecordingRow>(rows);
        foreach(DataGridViewRow row in recordingGrid.Rows)if(row.DataBoundItem is RecordingRow item&&item.Model.Name==current){recordingGrid.CurrentCell=row.Cells["模块"];break;}
        int active=modules.Count(m=>m.Active=="true"),transitions=modules.Count(m=>m.Requested!=m.Active);
        recordingSummary.Text="发现 "+modules.Count+" 个录制模块 · "+active+" 个正在录制"+(transitions>0?" · "+transitions+" 个状态待确认":"");recordingSummary.ForeColor=active>0?Presentation.Accent:Presentation.Ink;
        recordingUpdated.Text="读取 "+DateTime.Now.ToString("HH:mm:ss")+" · 后台 PID "+target.Pid+" · 勾选一个单独录制，勾选多个联合启停";
        UpdateRecordingButtons(!busy);UpdateRecordingDetails();
    }
    void UpdateRecordingDetails()
    {
        var row=recordingGrid.CurrentRow?.DataBoundItem as RecordingRow;
        recordingDetails.Text=(!recordingOnline?"当前状态已失效，不能据此确认是否仍在录制。\r\n":"")+(row==null?"勾选需要控制的录制模块；支持任意名称及输入连线，不依赖指定 XML。":row.Model.Name+" · "+row.Model.Library+" · "+(recordingOnline?row.Model.State:"待确认")+"\r\n"+row.Model.Inputs+"\r\n目录："+row.Model.Directory+"\r\n实际文件："+(row.Model.File.Length==0?"尚未生成":row.Model.File))+ (recordingLast.Length>0?"\r\n最近操作："+recordingLast:"");
    }
    async Task ControlRecording(bool startRecording)
    {
        var selected=SelectedRecordings();if(selected.Length==0)throw new InvalidOperationException("请先勾选需要控制的录制模块。");
        var target=StereoTarget();if(recordingTarget==null||recordingTarget.Pid!=target.Pid||recordingTarget.StartedUtcTicks!=target.StartedUtcTicks)throw new InvalidOperationException("目标后台已变化，请刷新并重新勾选模块。");
        stereoEpoch++;string folder=Storage.NewFolder("recording-operations");Storage.WriteJson(Path.Combine(folder,"target.json"),new{target,installation=root.Text,selected,operation=startRecording?"start":"stop"});
        SetStereoUnavailable("正在核对录制状态","录制操作完成后将重新读取同步状态。");
        State(startRecording?"正在启动所选录制":"正在停止所选录制",startRecording?"正在检查输入、设备、同步及输出目录；请勿同时在 DV 中编辑或启停这些模块。":"正在请求停止所选模块并等待写盘；不影响未勾选模块。");
        async Task<BatchWriteResult> Batch(IReadOnlyList<WireMessage> writes)
        {
            if(!target.Alive||(session!=null&&!session.Owns(target))||(session==null&&!Native.IsUnder(target.Path,root.Text)))throw new InvalidOperationException("后台身份发生变化，未下发录制控制。");
            return await DvProtocol.PutBatchAsync(Diagnostics.RuntimePort(target),writes);
        }
        try
        {
            var result=startRecording?await RecordingControl.Start(()=>StereoRead(target),Batch,selected,folder,recordingSync.Checked,Log):await RecordingControl.Stop(()=>StereoRead(target),Batch,selected,folder);
            recordingLast=(startRecording?"已启动 ":"已停止 ")+string.Join("、",selected)+"；记录："+folder;
            ShowRecordings(result.Snapshot,target);ShowStereo(StereoSync.Inspect(result.Snapshot),target);Log(recordingLast);
            State(startRecording?"所选录制已开始":"所选录制已停止",startRecording?"全部所选模块正在录制，输出文件已核对。请用采集时间戳对齐数据。":"所选模块已核对停止，文件已保留；其他录制模块按原状态运行。");
        }
        catch(Exception ex){recordingLast="操作未完成："+ex.Message;SetRecordingsUnavailable(recordingLast);Log("录制操作记录："+folder);throw;}
    }
    internal void UseRecordingTestBackend(ProcessIdentity target)
    {
        root.Text=Path.GetDirectoryName(target.Path)!;BindProcesses([target]);
        Text+=" · 录制验证（测试文件保存到工作区）";
    }
}
