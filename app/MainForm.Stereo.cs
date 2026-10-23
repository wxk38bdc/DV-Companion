using System.Xml.Linq;
namespace DVCompanion;

public sealed partial class MainForm
{
    readonly StyledButton syncNow=new(){Text="一键同步时间戳"},stereoRefresh=new(){Text="刷新双机状态"};
    readonly Label stereoSummary=Presentation.Label("尚未连接 DV 后台",11,true),stereoUpdated=Presentation.Label("尚未读取实时状态",9,false,Presentation.Muted);
    readonly Label stereoHint=Presentation.Label("通过本工具启动 DV，并在 DV 中启动两台相机采集。",9.5f,false,Presentation.Muted);
    readonly Label[] stereoTitles=[Presentation.Label("相机 1",12,true),Presentation.Label("相机 2",12,true)];
    readonly Label[] stereoDetails=[Presentation.Label("未读取后台状态"),Presentation.Label("未读取后台状态")];
    bool stereoPolling;long stereoEpoch;StereoState? stereoState;DateTimeOffset? stereoReadAt;
    string stereoSource="";
    TabPage BuildStereoPage()
    {
        var page=new TabPage("双机同步"){Padding=new Padding(14,12,14,2),BackColor=Color.White,AutoScroll=true};
        var layout=new TableLayoutPanel{Dock=DockStyle.Top,Height=500,ColumnCount=1,RowCount=5,Margin=Padding.Empty,BackColor=Color.White};layout.ColumnStyles.Add(new(SizeType.Percent,100));
        page.SizeChanged+=(_,_)=>layout.Height=Math.Max(500,page.ClientSize.Height-page.Padding.Vertical);
        layout.RowStyles.Add(new(SizeType.Absolute,54));layout.RowStyles.Add(new(SizeType.Absolute,64));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Absolute,72));layout.RowStyles.Add(new(SizeType.Absolute,55));page.Controls.Add(layout);
        var actions=Presentation.Flow();Presentation.Button(syncNow,"primary");Presentation.Button(stereoRefresh);actions.Controls.AddRange([syncNow,stereoRefresh]);layout.Controls.Add(actions,0,0);
        var heading=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,BackColor=Color.White,Margin=Padding.Empty};heading.ColumnStyles.Add(new(SizeType.Percent,100));heading.RowStyles.Add(new(SizeType.Absolute,32));heading.RowStyles.Add(new(SizeType.Percent,100));heading.Controls.Add(stereoSummary,0,0);heading.Controls.Add(stereoUpdated,0,1);layout.Controls.Add(heading,0,1);
        var pair=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,BackColor=Color.White,Margin=Padding.Empty};pair.RowStyles.Add(new(SizeType.Percent,100));pair.ColumnStyles.Add(new(SizeType.Percent,50));pair.ColumnStyles.Add(new(SizeType.Percent,50));layout.Controls.Add(pair,0,2);
        for(int i=0;i<2;i++)
        {
            var card=Presentation.Surface(Presentation.Soft,new Padding(18,14,18,12));card.Radius=12;card.Margin=i==0?new Padding(0,0,6,0):new Padding(6,0,0,0);pair.Controls.Add(card,i,0);
            var content=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,BackColor=Presentation.Soft,Margin=Padding.Empty};content.ColumnStyles.Add(new(SizeType.Percent,100));content.RowStyles.Add(new(SizeType.Absolute,39));content.RowStyles.Add(new(SizeType.Percent,100));card.Controls.Add(content);
            stereoTitles[i].AutoSize=false;stereoTitles[i].Dock=DockStyle.Fill;stereoTitles[i].AutoEllipsis=true;content.Controls.Add(stereoTitles[i],0,0);
            stereoDetails[i].AutoSize=false;stereoDetails[i].Dock=DockStyle.Fill;stereoDetails[i].Font=new Font("Microsoft YaHei UI",9.5f);content.Controls.Add(stereoDetails[i],0,1);
        }
        stereoHint.AutoSize=false;stereoHint.Dock=DockStyle.Fill;stereoHint.Padding=new Padding(0,10,0,0);layout.Controls.Add(stereoHint,0,3);
        var note=Presentation.Label("先接好同步线，再启动两台采集；停止录制后点击同步。状态自动刷新。SYNC_OK 为后台报告，未测量同步误差。",9,false,Presentation.Muted);note.AutoSize=false;note.Dock=DockStyle.Fill;layout.Controls.Add(note,0,4);
        syncNow.Click+=async(_,_)=>await Run(SynchronizeStereo);stereoRefresh.Click+=async(_,_)=>await Run(()=>RefreshStereo(true));
        workspaceTabs.SelectedIndexChanged+=async(_,_)=>{if(workspaceTabs.SelectedTab==page&&!busy&&!monitoring)await RefreshStereo();};
        SetStereoUnavailable("尚未连接 DV 后台","通过本工具启动 DV，并在 DV 中启动两台相机采集。");return page;
    }
    ProcessIdentity StereoTarget()
    {
        if(session!=null)
        {
            var runtimes=session.Runtimes().ToList();if(runtimes.Count==1&&runtimes[0].Alive)return runtimes[0];
            throw new InvalidOperationException("本次 DV 后台未就绪或数量不唯一，请检查设备与运行页。");
        }
        var p=Selected();if(p?.Alive==true&&p.Name.Equals("dv-runtime.exe",StringComparison.OrdinalIgnoreCase))return p;
        throw new InvalidOperationException("请先启动 DV，或在设备与运行页选中需要查看的 DV 后台。");
    }
    Task<XDocument> StereoRead(ProcessIdentity target)
    {
        if(session!=null)return session.Snapshot(target);
        if(!target.Alive||!Native.IsUnder(target.Path,root.Text))throw new InvalidOperationException("后台身份已改变或不属于所选 DV 安装。");
        return Diagnostics.Snapshot(target);
    }
    void SetStereoUnavailable(string title,string reason)
    {
        stereoState=null;stereoSummary.Text=title;stereoSummary.ForeColor=Presentation.Ink;stereoHint.Text=reason;syncNow.Enabled=false;
        stereoUpdated.Text=stereoReadAt==null?"尚未读取实时状态":"状态已失效；上次读取 "+stereoReadAt.Value.ToString("HH:mm:ss")+" · "+stereoSource;
        for(int i=0;i<2;i++)
        {
            var usb=deviceList.ElementAtOrDefault(i);stereoTitles[i].Text="相机 "+(i+1);stereoDetails[i].Text=(usb==null?"Windows：未发现对应设备":"Windows：已识别 "+usb.Serial)+"\n\nDV 采集：待确认\n主从角色：待确认\n同步状态：待确认";
        }
    }
    void ShowStereo(StereoState state,ProcessIdentity target)
    {
        stereoState=state;stereoReadAt=DateTimeOffset.Now;stereoSource="后台 PID "+target.Pid;stereoUpdated.Text="实时读取 "+stereoReadAt.Value.ToString("HH:mm:ss")+" · "+stereoSource;
        bool rolesKnown=state.Cameras.Count==2&&state.Cameras.Count(c=>c.Role=="true")==1&&state.Cameras.Count(c=>c.Role=="false")==1;
        bool synced=rolesKnown&&state.Master.SyncWith==state.Secondary.Name&&state.Cameras.All(c=>c.Running=="true"&&c.Status=="SYNC_OK")&&long.TryParse(state.Master.Offset,out _)&&state.Master.Offset==state.Secondary.Offset;
        stereoSummary.Text=state.Cameras.Count!=2?"双机尚未就绪":synced?"两台相机报告同步完成"+(state.CanSync?"":" · 同步操作已锁定"):state.CanSync?"双机已就绪，可以同步":"双机需要检查";
        stereoSummary.ForeColor=synced||state.CanSync?Presentation.Accent:Presentation.Ink;
        stereoHint.Text=state.CanSync?(synced?"两台均为 SYNC_OK，时间偏移一致。重新启动采集或恢复项目后，请再次检查并同步。":"将同步 "+state.Master.Name+" → "+state.Secondary.Name+(state.Master.SyncWith.Length==0?"，并自动填写主机 sync with。":"。")+"请勿同时在 DV 中开始录制或编辑结构。"):state.BlockReason;
        syncNow.Enabled=!busy&&state.CanSync;
        for(int i=0;i<2;i++)
        {
            var camera=state.Cameras.ElementAtOrDefault(i);if(camera==null){stereoTitles[i].Text="相机 "+(i+1);stereoDetails[i].Text="后台尚未配置第二个相机模块。";continue;}
            string serial=camera.Serial.Length>0?camera.Serial:camera.ConfiguredSerial;
            var detected=deviceList.FirstOrDefault(d=>d.Serial.Equals(serial,StringComparison.OrdinalIgnoreCase));
            stereoTitles[i].Text=camera.Name+" · "+camera.RoleText;
            stereoDetails[i].Text="序列号："+(serial.Length==0?"待确认":serial)+"\nWindows："+(detected==null?"未检测到 / 待刷新":detected.State.Replace("Windows ",""))+"\nDV 采集："+camera.CaptureText+"\n主从角色："+camera.RoleText+"\n同步："+camera.SyncText+"\nsync with："+(camera.SyncWith.Length==0?"（空）":camera.SyncWith)+"\n时间偏移："+(camera.Offset.Length==0?"待确认":camera.Offset+" µs");
        }
    }
    async Task RefreshStereo(bool report=false)
    {
        if(stereoPolling)return;stereoPolling=true;long epoch=stereoEpoch;
        try
        {
            var target=StereoTarget();var snapshot=await StereoRead(target);var state=StereoSync.Inspect(snapshot);
            if(epoch==stereoEpoch&&!closing&&target.Alive){ShowStereo(state,target);ShowRecordings(snapshot,target);}
        }
        catch(Exception ex){if(epoch==stereoEpoch&&!closing){SetStereoUnavailable("双机状态未确认",ex.Message);SetRecordingsUnavailable(ex.Message);if(report)Log("后台状态："+ex.Message);}}
        finally{stereoPolling=false;}
    }
    async Task SynchronizeStereo()
    {
        stereoEpoch++;var target=StereoTarget();string folder=Storage.NewFolder("stereo-sync");
        Storage.WriteJson(Path.Combine(folder,"target.json"),new{time=DateTimeOffset.Now,target,installation=root.Text});
        State("正在同步双机时间戳","正在核对实际设备、主从角色和录制状态；请勿同时在 DV 中修改配置或开始录制。");
        try
        {
            var state=await StereoSync.Execute(()=>StereoRead(target),async(path,key,value)=>
            {
                // Recheck process ownership/identity immediately before each write.
                if(!target.Alive||(session!=null&&!session.Owns(target)))throw new InvalidOperationException("目标后台已改变，停止同步。");
                _=await DvProtocol.CallAsync(Diagnostics.RuntimePort(target),10,path,key,value);
            },folder,Log);
            ShowStereo(state,target);Log("双机时间同步已核对完成；记录："+folder);State("双机同步完成","两台相机均报告 SYNC_OK，时间偏移一致且已更新。可以在 DV 中按原流程开始录制。");
        }
        catch(Exception ex){SetStereoUnavailable("本次同步未确认",ex.Message+"；记录："+folder);Log("同步未确认，记录："+folder);throw;}
    }
}
