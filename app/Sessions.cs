using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DVCompanion;

public static class Storage
{
    public static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    public static string Root {get;set;}=Path.Combine(AppContext.BaseDirectory,"data");
    public static string WriteJson(string path,object data){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,JsonSerializer.Serialize(data,Json),new UTF8Encoding(false));return path;}
    public static string NewFolder(string kind){string p=Path.Combine(Root,kind,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")[..5]);Directory.CreateDirectory(p);return p;}
}
public sealed class Session:IDisposable
{
    public string DvRoot {get;}
    public string Folder {get;}
    public Native.OwnedJob Job {get;}
    public ProcessIdentity? Gui {get;private set;}
    public List<ProcessIdentity> History {get;}=[];
    public long LogOffset {get;private set;}
    public bool StopRequested {get;set;}
    public bool UsePatched {get;}
    public static string PatchedExecutable=>Path.Combine(AppContext.BaseDirectory,"patched-runtime","dv-runtime.exe");
    public string RuntimeLog=>UsePatched?Path.Combine(Folder,"runtime.log"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".dv-runtime.log");
    PreferencesLease? preferences;
    public event Action<string>? Message;
    readonly object logLock=new();
    public Session(string dvRoot,bool usePatched=false)
    {
        DvRoot=Path.GetFullPath(dvRoot);UsePatched=usePatched;Folder=Storage.NewFolder("sessions");Job=new();
        try{LogOffset=new FileInfo(RuntimeLog).Length;}catch{LogOffset=0;}
        Note("会话已建立。只管理本次启动的进程；不会自动接管其他 DV。");
    }
    public void Note(string text)
    {
        lock(logLock)File.AppendAllText(Path.Combine(Folder,"timeline.jsonl"),JsonSerializer.Serialize(new{time=DateTimeOffset.Now,message=text})+"\n",Encoding.UTF8);
        Message?.Invoke(text);
    }
    public async Task StartGui(string? projectText=null)
    {
        string java=Path.Combine(DvRoot,"gui","bin","javaw.exe");
        if(!File.Exists(java)||!File.Exists(Path.Combine(DvRoot,"runtime","dv-runtime.exe")))throw new FileNotFoundException("DV 安装目录不完整，请选择包含 gui 和 runtime 的文件夹。");
        string release=File.ReadAllText(Path.Combine(DvRoot,"gui","release"));string version=Regex.Match(release,"JAVA_VERSION=\"([^\"]+)\"").Groups[1].Value;
        if(!Regex.IsMatch(version,"^[0-9.]+$"))throw new InvalidDataException("无法识别 DV 自带 Java 版本");
        if(UsePatched)
        {
            if(!File.Exists(PatchedExecutable))throw new FileNotFoundException("修复后台文件缺失，请重新解压完整工具包。");
            if(File.ReadAllText(Path.Combine(DvRoot,"runtime","git_commit")).Trim()!="879b9d94d5d97892c4d4e3951e26349cd012084e")throw new InvalidOperationException("此修复后台只验证过当前 DV 1.7.1 版本。安装版本已改变，请取消勾选修复后台，或使用匹配版本后再启动。");
            var reservation=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);reservation.Start();int port=((System.Net.IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
            var args=new List<string>{"-i","127.0.0.1","-p",port.ToString(),"-l",RuntimeLog,"--console","0"};
            if(!string.IsNullOrWhiteSpace(projectText))
            {
                var doc=ProjectRestore.Portable(Configuration.Parse(projectText));
                string config=Path.Combine(Folder,"checked-project.xml");doc.Save(config);Note("可恢复项目副本已保存："+config+"。DV 启动后，请在本工具配置页点击“恢复此项目到 DV”；录制模块保持停止。");
            }
            string modulePaths=Path.Combine(AppContext.BaseDirectory,"patched-runtime","dv_modules")+"|"+Path.Combine(DvRoot,"runtime","dv_modules");
            var backend=Job.Launch(PatchedExecutable,args,Path.Combine(DvRoot,"runtime"),true,new Dictionary<string,string>{{"DV_MODULES_PATH",modulePaths}});
            var deadline=DateTime.UtcNow.AddSeconds(15);while(backend.Alive&&!Native.ListeningPorts(backend.Pid).Contains(port)&&DateTime.UtcNow<deadline)await Task.Delay(150);
            if(!backend.Alive||!Native.ListeningPorts(backend.Pid).Contains(port))throw new InvalidOperationException("修复后台未成功启动，请查看本次 runtime.log。");
            Diagnostics.RegisterRuntime(backend,port);
            if((await DvProtocol.CallAsync(port,9,"/system/","running"))[0].Value!="true")throw new InvalidOperationException("修复后台状态异常");
            preferences=new PreferencesLease(Folder,port);Note("修复后台已就绪；临时使用 DV 官方外部连接模式，退出后恢复原连接设置。");
        }
        var logging=Path.Combine(Folder,"java-logging.properties");
        File.WriteAllText(logging,"handlers=java.util.logging.FileHandler\n.level=INFO\njava.util.logging.FileHandler.pattern="+Path.Combine(Folder,"ui-%u.log").Replace('\\','/')+"\njava.util.logging.FileHandler.limit=2097152\njava.util.logging.FileHandler.count=3\njava.util.logging.FileHandler.formatter=java.util.logging.SimpleFormatter\n",Encoding.ASCII);
        string cache=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"openjfx",version);
        Gui=Job.Launch(java,["-Djavafx.cachedir="+cache,"-Djava.util.logging.config.file="+logging,"-XX:ErrorFile="+Path.Combine(Folder,"java-crash-%p.log"),"-p",Path.Combine(DvRoot,"gui","app"),"-m","com.inivation.dvgui/com.inivation.dv.gui.MainApp"],Path.Combine(DvRoot,"gui","bin"),false);
        Remember();Note("已启动 DV 界面，PID "+Gui.Pid+"。后台子进程自动纳入本次会话。");
    }
    public List<ProcessIdentity> Remember()
    {
        var members=Job.Members();foreach(var p in members)if(!History.Any(x=>x.Pid==p.Pid&&x.StartedUtcTicks==p.StartedUtcTicks))History.Add(p);
        Storage.WriteJson(Path.Combine(Folder,"processes.json"),History);return members;
    }
    public IEnumerable<ProcessIdentity> Runtimes()=>Remember().Where(p=>p.Name.Equals("dv-runtime.exe",StringComparison.OrdinalIgnoreCase));
    public bool Owns(ProcessIdentity p)=>p.Alive&&Job.Members().Any(x=>x.Pid==p.Pid&&x.StartedUtcTicks==p.StartedUtcTicks&&x.Path.Equals(p.Path,StringComparison.OrdinalIgnoreCase));
    public async Task<XDocument> Snapshot(ProcessIdentity p)
    {
        if(!Owns(p))throw new InvalidOperationException("目标不属于当前会话或身份已改变。");
        return await Diagnostics.Snapshot(p);
    }
    public async Task<bool> ShutdownRuntime(ProcessIdentity p)
    {
        if(!Owns(p))throw new InvalidOperationException("拒绝操作不属于本次会话的进程。");
        int port=Diagnostics.RuntimePort(p);Note("请求后台正常退出：PID "+p.Pid);
        await DvProtocol.CallAsync(port,10,"/system/","running","false");
        var deadline=DateTime.UtcNow.AddSeconds(20);while(p.Alive&&DateTime.UtcNow<deadline)await Task.Delay(200);
        Note(p.Alive?"后台未完成正常退出，已保留诊断入口，不自动强杀。":"后台已正常退出。");return !p.Alive;
    }
    public string NewRuntimeText()
    {
        try{using var f=new FileStream(RuntimeLog,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);if(f.Length<LogOffset)LogOffset=0;f.Position=Math.Max(LogOffset,f.Length-131072);using var r=new StreamReader(f,Encoding.UTF8,true);string text=r.ReadToEnd();LogOffset=f.Position;return text;}catch{return "";}
    }
    public void Dispose(){try{preferences?.Dispose();}finally{Job.Dispose();}}
}

public static class Diagnostics
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(int,long,string),int> controlPorts=new();
    public static void RegisterRuntime(ProcessIdentity p,int port)=>controlPorts[(p.Pid,p.StartedUtcTicks,p.Path)]=port;
    public static int RuntimePort(ProcessIdentity p)
    {
        if(!p.Alive||!p.Name.Equals("dv-runtime.exe",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("后台进程身份无法确认。");
        var ports=Native.ListeningPorts(p.Pid);
        if(controlPorts.TryGetValue((p.Pid,p.StartedUtcTicks,p.Path),out int known)&&ports.Contains(known))return known;
        if(ports.Count!=1)throw new InvalidOperationException("未找到唯一的本机配置端口，未发送控制命令。");
        if(!p.Alive)throw new InvalidOperationException("后台已经退出");return ports[0];
    }
    public static async Task<XDocument> Snapshot(ProcessIdentity p)
    {
        int port=RuntimePort(p);var doc=await DvProtocol.SnapshotAsync(port);
        if(!p.Alive)throw new InvalidOperationException("查询期间进程发生变化，请重试。");return doc;
    }
    public static string Tail(string path,int maxBytes=262144)
    {
        using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);long start=Math.Max(0,f.Length-maxBytes);f.Position=start;using var r=new StreamReader(f,Encoding.UTF8,true);if(start>0)r.ReadLine();return r.ReadToEnd();
    }
    public static async Task<string> Capture(string dvRoot,Session? session,IEnumerable<ProcessIdentity>? selected=null,bool dumps=false)
    {
        string folder=Storage.NewFolder("diagnostics");var errors=new List<string>();
        var procs=(selected??(session!=null?session.Remember():Native.DvProcesses(dvRoot))).ToList();
        Storage.WriteJson(Path.Combine(folder,"summary.json"),new{time=DateTimeOffset.Now,appVersion=typeof(Session).Assembly.GetName().Version?.ToString(3),os=Environment.OSVersion.ToString(),memory=GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,dvRoot,session=session?.Folder,processes=procs});
        try{Storage.WriteJson(Path.Combine(folder,"devices.json"),Native.Cameras());}catch(Exception ex){errors.Add("设备枚举："+ex.Message);}
        foreach(var file in new[]{"gui/release","gui/git_commit","runtime/git_commit"})try{File.Copy(Path.Combine(dvRoot,file.Replace('/',Path.DirectorySeparatorChar)),Path.Combine(folder,file.Replace('/','_')+".txt"));}catch(Exception ex){errors.Add(file+"："+ex.Message);}
        string runtimeLog=session?.RuntimeLog??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".dv-runtime.log");
        try{File.WriteAllText(Path.Combine(folder,"runtime-tail.log"),Tail(runtimeLog));}catch(Exception ex){errors.Add("后台日志："+ex.Message);}
        if(session!=null)
        {
            foreach(string file in Directory.GetFiles(session.Folder).Where(p=>new FileInfo(p).Length<8_000_000))try{File.Copy(file,Path.Combine(folder,"session-"+Path.GetFileName(file)));}catch(Exception ex){errors.Add(ex.Message);}
        }
        foreach(var p in procs)
        {
            if(!p.Alive)continue;
            if(p.Name.Equals("dv-runtime.exe",StringComparison.OrdinalIgnoreCase))
            {
                try{var doc=await Snapshot(p);doc.Save(Path.Combine(folder,"config-"+p.Pid+".xml"));}catch(Exception ex){errors.Add("后台快照 "+p.Pid+"："+ex.Message);}
            }
            if(dumps)try{await Task.Run(()=>Native.WriteMiniDump(p,Path.Combine(folder,"process-"+p.Pid+".dmp")));}catch(Exception ex){errors.Add("转储 "+p.Pid+"："+ex.Message);}
        }
        File.WriteAllLines(Path.Combine(folder,"collection-notes.txt"),errors.Count>0?errors:["已完成本地诊断采集。"]);
        File.WriteAllText(Path.Combine(folder,"说明.txt"),"此诊断包仅保存在本机，未自动上传。包含软件版本、相机序列号、文件路径、配置和日志。后台日志为有上限的历史尾部，可能包括本次会话之前的错误；timeline 带时间。未采集完整原始事件数据。转储只在用户选择时采集，可能包含部分内存内容。\n");
        string zip=folder+".zip";ZipFile.CreateFromDirectory(folder,zip);session?.Note("诊断已保存："+zip);return zip;
    }
}
