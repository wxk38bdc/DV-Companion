using System.Diagnostics;
using System.Xml.Linq;
namespace DVCompanion;

public sealed record StereoCamera(string Name,string Path,string ConfiguredSerial,string Serial,string Running,string Requested,string Role,string Status,string Offset,string SyncWith,bool SupportsSync)
{
    public string RoleText=>Role switch{"true"=>"主机","false"=>"从机",_=>"待确认"};
    public string CaptureText=>Running=="true"?(Requested=="false"?"正在停止":"采集中"):Requested=="true"?"正在启动 / 打开失败":Running=="false"?"未采集":"状态未知";
    public string SyncText=>Running!="true"?"未采集，状态无效":Status switch{"SYNC_OK"=>"同步完成（SYNC_OK）","NO_SYNC"=>"尚未同步（NO_SYNC）","WAIT_RESET"=>"等待复位（WAIT_RESET）","GOT_RESET"=>"已复位，等待完成",_=>"未知 / 不支持"};
}
public sealed record StereoState(IReadOnlyList<StereoCamera> Cameras,string BlockReason,string Recording)
{
    public bool CanSync=>BlockReason.Length==0;
    public StereoCamera Master=>Cameras.Single(c=>c.Role=="true");
    public StereoCamera Secondary=>Cameras.Single(c=>c.Role=="false");
}

public static class StereoSync
{
    static string Info(ModuleConfig m,string key)=>m.Element.Elements("node").FirstOrDefault(n=>(string?)n.Attribute("name")=="sourceInfo")?.Elements("attr").FirstOrDefault(a=>(string?)a.Attribute("key")==key)?.Value??"";
    public static StereoState Inspect(XDocument snapshot)
    {
        var modules=Configuration.Modules(snapshot);
        var cameras=modules.Where(m=>m.Camera).Select(m=>new StereoCamera(m.Name,m.Path,m.Get("serialNumber"),Info(m,"serialNumber"),m.Get("isRunning"),m.Get("running"),Info(m,"deviceIsMaster"),Info(m,"syncStatus"),Info(m,"tsOffset"),m.Get("syncWith"),m.Values.ContainsKey("syncTime")&&m.Values.ContainsKey("syncWith"))).ToList();
        string recording=Configuration.RecordingStatus(snapshot);
        StereoState Block(string reason)=>new(cameras,reason,recording);
        if(cameras.Count!=2)return Block("需要同一 DV 后台中恰好两个相机模块；当前 "+cameras.Count+" 个。");
        foreach(var writer in modules.Where(m=>m.Writer))
            if(writer.Get("running")!="false"||writer.Get("isRunning")!="false")return Block("录制中或录制状态无法确认（"+writer.Name+"）。请先在 DV 中停止录制。");
        if(cameras.Any(c=>c.Running!="true"||c.Requested!="true"))return Block("请先在 DV 中启动两台相机采集；启动请求不代表设备已成功打开。");
        if(cameras.Any(c=>!c.SupportsSync||string.IsNullOrEmpty(c.Status)||!long.TryParse(c.Offset,out _)))return Block("相机同步状态不完整或不支持时间同步，请检查后台和设备。");
        if(cameras.Any(c=>c.Serial.Length==0)||cameras.Select(c=>c.Serial).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=2)return Block("无法确认两台独立相机的实际序列号。");
        if(cameras.Any(c=>c.ConfiguredSerial.Length>0&&c.ConfiguredSerial!=c.Serial))return Block("配置序列号与已打开的相机不一致，请先核对设备身份。");
        if(cameras.Count(c=>c.Role=="true")!=1||cameras.Count(c=>c.Role=="false")!=1)return Block("未检测到一主一从。请检查同步线方向和连接，并按需重启相机采集。");
        var master=cameras.Single(c=>c.Role=="true");var secondary=cameras.Single(c=>c.Role=="false");
        if(secondary.SyncWith.Length>0)return Block("从机 sync with 应为空，请在 DV 中修正。");
        if(master.SyncWith.Length>0&&master.SyncWith!=secondary.Name)return Block("主机 sync with 未准确指向当前从机（"+secondary.Name+"），请在 DV 中修正。");
        return new(cameras,"",recording);
    }
    static StereoState Require(XDocument snapshot)
    {
        var state=Inspect(snapshot);if(!state.CanSync)throw new InvalidOperationException(state.BlockReason);return state;
    }
    static bool SamePair(StereoState first,StereoState next)=>first.CanSync&&next.CanSync&&first.Master==next.Master&&first.Secondary==next.Secondary;
    static bool IdentityMatches(StereoState a,StereoState b)=>b.CanSync&&a.Master.Name==b.Master.Name&&a.Master.Serial==b.Master.Serial&&a.Secondary.Name==b.Secondary.Name&&a.Secondary.Serial==b.Secondary.Serial;

    // Injected transport lets tests verify refusals, timeouts and stale success without hardware writes.
    public static async Task<StereoState> Execute(Func<Task<XDocument>> read,Func<string,string,string,Task> put,string folder,Action<string>? progress=null,int timeoutMs=14000)
    {
        Directory.CreateDirectory(folder);bool triggered=false;string phase="preflight";
        void Report(string state,string message)=>Storage.WriteJson(Path.Combine(folder,"sync-result.json"),new{time=DateTimeOffset.Now,state,phase,triggered,message});
        try
        {
            var before=await read();before.Save(Path.Combine(folder,"before.xml"));var initial=Require(before);
            // A second fresh read catches a writer starting or a project changing during preflight.
            var checkedState=Require(await read());if(!SamePair(initial,checkedState))throw new InvalidOperationException("相机状态刚刚改变，请刷新后重试。");
            if(initial.Master.SyncWith.Length==0)
            {
                phase="set-sync-with";progress?.Invoke("根据实际主从角色设置 sync with："+initial.Master.Name+" → "+initial.Secondary.Name);
                await put(initial.Master.Path,"syncWith",initial.Secondary.Name);
            }
            var ready=Require(await read());if(!IdentityMatches(initial,ready)||ready.Master.SyncWith!=ready.Secondary.Name)throw new InvalidOperationException("同步关系或设备身份发生变化，未触发时间同步。");
            phase="sync-time";Report("in-progress","准备触发主机时间戳复位");
            progress?.Invoke("正在同步时间戳；请勿在 DV 中开始录制、修改结构或拔插相机。");
            triggered=true;await put(ready.Master.Path,"syncTime","true");
            phase="verify";var watch=Stopwatch.StartNew();
            do
            {
                await Task.Delay(250);var after=await read();after.Save(Path.Combine(folder,"after.xml"));var state=Require(after);
                if(!IdentityMatches(ready,state)||state.Master.SyncWith!=state.Secondary.Name)throw new InvalidOperationException("同步过程中设备或同步关系改变，结果无法确认。");
                // SYNC_OK alone can be left over from an earlier click. Both fresh offsets must agree
                // and must have changed on this request; otherwise do not report a new success.
                if(state.Cameras.All(c=>c.Status=="SYNC_OK")&&state.Master.Offset==state.Secondary.Offset&&state.Master.Offset!=ready.Master.Offset&&state.Secondary.Offset!=ready.Secondary.Offset)
                {
                    Report("verified","两台相机 SYNC_OK，时间偏移一致且已更新；这是后台结果，不代表已测量长期同步误差。");return state;
                }
            }while(watch.ElapsedMilliseconds<timeoutMs);
            throw new TimeoutException("时间同步未得到本次完成确认。请检查同步线、主从角色和 DV 日志；不要直接开始双目录制。");
        }
        catch(Exception ex){Report(triggered?"unconfirmed":"blocked",ex.Message);throw;}
    }
}
