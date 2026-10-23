using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
namespace DVCompanion;

public sealed record RecordingModule(string Name,string Path,string Library,string Requested,string Active,string Directory,string Prefix,string File,string Bytes,string Inputs,string Signature)
{
    public bool Supported=>Library=="dv_output_file";
    public bool Idle=>Requested=="false"&&Active=="false";
    public string State=>!Supported?"此类型暂不支持控制":Requested=="true"&&Active=="true"?"录制中":Requested=="true"?"启动中 / 启动失败":Active=="true"?"正在停止并写盘":Idle?"已停止":"状态未确认";
}
public sealed record RecordingPlan(IReadOnlyList<RecordingModule> Modules,IReadOnlyList<string> Cameras,string Signature);
public sealed record RecordingOperation(string Id,bool Started,string Folder,XDocument Snapshot,BatchWriteResult Batch);

public static class RecordingControl
{
    public static string BasePrefix(string prefix)=>Regex.Replace(prefix,@"__DVC_[0-9a-f]{32}_[0-9]{2}$","");
    static IEnumerable<XElement> Inputs(ModuleConfig module)=>module.Element.Elements("node").Where(n=>(string?)n.Attribute("name")=="inputs").Elements("node");
    static string Ref(XElement input)=>input.Elements("attr").FirstOrDefault(a=>(string?)a.Attribute("key")=="from")?.Value??"";
    static string Signature(ModuleConfig m)=>string.Join("\n",m.Name,m.Path,m.Library,m.Get("directory"),m.Get("prefix"),m.Get("compression"),m.Get("timeout"),string.Join("|",Inputs(m).Select(i=>(string?)i.Attribute("name")+"="+Ref(i)).Order()));
    static string Info(ModuleConfig module,string key)=>module.Element.Elements("node").FirstOrDefault(n=>(string?)n.Attribute("name")=="sourceInfo")?.Elements("attr").FirstOrDefault(a=>(string?)a.Attribute("key")==key)?.Value??"";
    public static List<RecordingModule> Inspect(XDocument snapshot)=>Configuration.Modules(snapshot).Where(m=>m.Writer).Select(m=>new RecordingModule(m.Name,m.Path,m.Library,m.Get("running"),m.Get("isRunning"),m.Get("directory"),m.Get("prefix"),m.Get("file"),m.Get("writtenDataSize"),string.Join("; ",Inputs(m).Where(i=>Ref(i).Length>0).Select(i=>(string?)i.Attribute("name")+" ← "+Ref(i))),Signature(m))).ToList();

    public static RecordingPlan Plan(XDocument snapshot,IReadOnlyCollection<string> selected,bool requireSync=true,bool starting=true)
    {
        if(selected.Count is <1 or >32||selected.Distinct(StringComparer.Ordinal).Count()!=selected.Count)throw new InvalidOperationException("请勾选 1 到 32 个不同录制模块。");
        var modules=Configuration.Modules(snapshot).ToDictionary(m=>m.Name,StringComparer.Ordinal);var all=Inspect(snapshot).ToDictionary(m=>m.Name,StringComparer.Ordinal);var writers=new List<RecordingModule>();var ancestors=new HashSet<string>();var cameras=new HashSet<string>();
        foreach(string name in selected.Order(StringComparer.Ordinal))
        {
            if(!all.TryGetValue(name,out var writer)||!writer.Supported)throw new InvalidOperationException("模块不存在或不是支持的 AEDAT4 文件录制模块："+name);
            if(starting&&!writer.Idle)throw new InvalidOperationException(name+" 不是已确认停止状态。已在运行的录制不会被重新启动。");
            writers.Add(writer);
            if(!starting)continue;
            var inputs=Inputs(modules[name]).ToList();if(!inputs.Any(i=>(string?)i.Attribute("name")=="output0"&&Ref(i).Length>0))throw new InvalidOperationException(name+" 必需的 output0 未连接。");
            var visiting=new HashSet<string>();
            void Visit(ModuleConfig module,bool upstream)
            {
                if(!visiting.Add(module.Name))throw new InvalidOperationException("录制输入连线存在循环："+module.Name);
                if(upstream)
                {
                    if(module.Get("running")!="true"||module.Get("isRunning")!="true")throw new InvalidOperationException("输入模块尚未成功运行："+module.Name+"。请先在 DV 中启动采集及中间处理模块。");
                    if(module.Writer)throw new InvalidOperationException("文件录制模块不能作为录制数据源："+module.Name);
                    ancestors.Add(module.Name);if(module.Camera)cameras.Add(module.Name);
                }
                foreach(var input in Inputs(module))
                {
                    string reference=Ref(input);if(reference.Length==0)continue;
                    var match=Regex.Match(reference,@"^([A-Za-z0-9_.-]+)\[([^\]]+)\]$");
                    if(!match.Success||!modules.TryGetValue(match.Groups[1].Value,out var source))throw new InvalidOperationException(module.Name+" 输入无效："+reference);
                    // A live source must publish the referenced output; names are not hardcoded.
                    var output=source.Element.Elements("node").FirstOrDefault(n=>(string?)n.Attribute("name")=="outputs")?.Elements("node").FirstOrDefault(n=>(string?)n.Attribute("name")==match.Groups[2].Value);
                    if(output==null)throw new InvalidOperationException(reference+" 当前没有发布可用输出。");
                    Visit(source,true);
                }
                visiting.Remove(module.Name);
            }
            Visit(modules[name],false);
        }
        if(starting)
        {
            foreach(string name in cameras)
            {
                var camera=modules[name];string serial=Info(camera,"serialNumber");if(serial.Length==0||(camera.Get("serialNumber").Length>0&&camera.Get("serialNumber")!=serial))throw new InvalidOperationException("无法核对实际相机身份："+name);
            }
            if(cameras.Select(n=>Info(modules[n],"serialNumber")).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=cameras.Count)throw new InvalidOperationException("数据源引用了重复相机身份。");
            if(requireSync&&cameras.Count>1)
            {
                var sources=cameras.Select(n=>modules[n]).ToList();var masters=sources.Where(m=>Info(m,"deviceIsMaster")=="true").ToList();
                if(masters.Count!=1||sources.Where(m=>m.Name!=masters[0].Name).Any(m=>Info(m,"deviceIsMaster")!="false"))throw new InvalidOperationException("双目数据源未确认一主多从，请先检查同步线和主从角色。");
                var master=masters[0];var targets=master.Get("syncWith").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
                if(sources.Any(m=>Info(m,"syncStatus")!="SYNC_OK"||!long.TryParse(Info(m,"tsOffset"),out _))||sources.Select(m=>Info(m,"tsOffset")).Distinct().Count()!=1||sources.Where(m=>m.Name!=master.Name).Any(m=>!targets.Contains(m.Name,StringComparer.Ordinal)))throw new InvalidOperationException("双目数据源尚未确认时间同步。请先在“双机同步”页完成同步，再开始录制。");
            }
        }
        string signature=string.Join("\n---\n",writers.Select(m=>m.Signature).Concat(ancestors.Order().Select(n=>Signature(modules[n])+"\n"+Info(modules[n],"serialNumber"))));
        return new(writers,cameras.Order().ToList(),signature);
    }
    public static void ProbeDirectory(string directory)
    {
        if(string.IsNullOrWhiteSpace(directory)||!Path.IsPathFullyQualified(directory))throw new InvalidOperationException("输出目录必须是绝对路径："+directory);
        Directory.CreateDirectory(directory);string path=Path.Combine(directory,".dvcompanion-write-"+Guid.NewGuid().ToString("N"));
        using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.DeleteOnClose|FileOptions.WriteThrough)){file.WriteByte(0);file.Flush(true);}
        string root=Path.GetPathRoot(Path.GetFullPath(directory))!;
        if(root.Length<=3&&new DriveInfo(root).AvailableFreeSpace<64L*1024*1024)throw new IOException("输出磁盘剩余空间不足 64 MiB："+root);
    }
    static void PrefixValid(string directory,string prefix)
    {
        if(prefix.IndexOfAny(Path.GetInvalidFileNameChars())>=0||prefix.Any(char.IsControl)||Path.Combine(directory,prefix+"-2026_10_23_12_34_56.aedat4").Length>=250)throw new InvalidOperationException("文件前缀含无效字符或完整路径过长："+prefix);
    }
    static bool SameWriter(RecordingModule expected,RecordingModule actual)=>expected.Name==actual.Name&&expected.Path==actual.Path&&expected.Library==actual.Library&&expected.Inputs==actual.Inputs&&expected.Directory==actual.Directory&&expected.Prefix==actual.Prefix;
    static void EnsureSame(RecordingPlan expected,RecordingPlan actual){if(expected.Signature!=actual.Signature)throw new InvalidOperationException("录制配置刚刚发生变化，请刷新后重试。");}
    static IReadOnlyList<WireMessage> Running(IEnumerable<RecordingModule> writers,bool value)=>writers.Select(m=>new WireMessage(10,m.Path,"running",value?"true":"false",0)).ToList();
    public static async Task<RecordingOperation> Start(Func<Task<XDocument>> read,Func<IReadOnlyList<WireMessage>,Task<BatchWriteResult>> batch,IReadOnlyCollection<string> selected,string folder,bool requireSync=true,Action<string>? progress=null,int timeoutMs=14000,Action<string>? probe=null)
    {
        Directory.CreateDirectory(folder);string id=Guid.NewGuid().ToString("N");bool attempted=false;RecordingPlan? original=null,prepared=null;BatchWriteResult? startBatch=null;string phase="preflight";
        void Journal(string state,string message,object? cleanup=null)=>Storage.WriteJson(Path.Combine(folder,"recording-result.json"),new{time=DateTimeOffset.Now,id,operation="start",state,phase,attempted,selected,requireSync,message,startBatch,original,prepared,cleanup});
        try
        {
            var before=await read();before.Save(Path.Combine(folder,"before.xml"));original=Plan(before,selected,requireSync);
            foreach(var directory in original.Modules.Select(m=>m.Directory).Distinct(StringComparer.OrdinalIgnoreCase))(probe??ProbeDirectory)(directory);
            for(int i=0;i<original.Modules.Count;i++)PrefixValid(original.Modules[i].Directory,BasePrefix(original.Modules[i].Prefix)+"__DVC_"+id+"_"+i.ToString("00"));
            EnsureSame(original,Plan(await read(),selected,requireSync));
            phase="unique-filenames";
            var prefixes=original.Modules.Select((m,i)=>new WireMessage(10,m.Path,"prefix",BasePrefix(m.Prefix)+"__DVC_"+id+"_"+i.ToString("00"),5)).ToList();
            var prefixBatch=await batch(prefixes);Storage.WriteJson(Path.Combine(folder,"prefix-batch.json"),prefixBatch);if(!prefixBatch.Success)throw new IOException("文件名准备失败，未请求启动录制。");
            var ready=await read();prepared=Plan(ready,selected,requireSync);
            var expectedReady=new XDocument(before);foreach(var prefix in prefixes)Configuration.Modules(expectedReady).Single(m=>m.Path==prefix.Node).Element.Elements("attr").Single(a=>(string?)a.Attribute("key")=="prefix").Value=prefix.Value;
            EnsureSame(Plan(expectedReady,selected,requireSync),prepared);
            for(int i=0;i<original.Modules.Count;i++)
            {
                var expected=original.Modules[i] with{Prefix=prefixes[i].Value};if(!SameWriter(expected,prepared.Modules[i]))throw new InvalidOperationException("录制模块在准备期间被修改，未请求启动。");
            }
            ready.Save(Path.Combine(folder,"prepared.xml"));phase="start";Journal("in-progress","已通过检查，准备联合发送启动请求。");
            progress?.Invoke("正在联合启动 "+prepared.Modules.Count+" 个录制模块；请勿同时在 DV 中编辑或启停这些模块。");
            attempted=true;startBatch=await batch(Running(prepared.Modules,true));Storage.WriteJson(Path.Combine(folder,"start-batch.json"),startBatch);
            if(!startBatch.Success)throw new IOException("启动请求未全部确认，将核对并停止本次选择的模块。");
            phase="verify";var watch=Stopwatch.StartNew();
            do
            {
                var after=await read();after.Save(Path.Combine(folder,"after.xml"));var live=Inspect(after).ToDictionary(m=>m.Name);
                foreach(var expected in prepared.Modules)if(!live.TryGetValue(expected.Name,out var actual)||!SameWriter(expected,actual))throw new InvalidOperationException("启动期间模块结构或设置发生变化，录制结果未确认。");
                if(prepared.Modules.All(m=>live[m.Name].Requested=="true"&&live[m.Name].Active=="true"&&live[m.Name].File.Length>0&&live[m.Name].File!=original.Modules.Single(o=>o.Name==m.Name).File))
                {
                    foreach(var expected in prepared.Modules)
                    {
                        string file=live[expected.Name].File;
                        if(!File.Exists(file)||!string.Equals(Path.GetFullPath(Path.GetDirectoryName(file)!),Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected.Directory)),StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(file).StartsWith(expected.Prefix+"-",StringComparison.Ordinal)||new FileInfo(file).Length<14)throw new IOException("后台报告已启动，但无法确认本次输出文件："+expected.Name);
                    }
                    Journal("recording","全部所选模块正在录制，输出文件已确认。批量下发不是原子启动，数据对齐请使用同步时间戳。");return new(id,true,folder,after,startBatch);
                }
                if(prepared.Modules.Any(m=>live[m.Name].Requested=="false"))throw new IOException("至少一个录制模块提前停止或启动失败。");
                await Task.Delay(200);
            }while(watch.ElapsedMilliseconds<timeoutMs);
            throw new TimeoutException("所选录制未全部完成启动确认。");
        }
        catch(Exception ex)
        {
            phase="cleanup";var cleanup=new List<object>();bool stopped=!attempted;
            if(attempted&&prepared!=null)
            {
                try
                {
                    var live=Inspect(await read()).ToDictionary(m=>m.Name);var safe=prepared.Modules.Where(m=>live.TryGetValue(m.Name,out var current)&&SameWriter(m,current)).ToList();
                    var result=safe.Count>0?await batch(Running(safe,false)):null;cleanup.Add(new{action="stop-attempted-selection",matched=safe.Select(m=>m.Name).ToArray(),result});
                    var check=await WaitStopped(read,safe,Math.Min(timeoutMs,10000));check.Save(Path.Combine(folder,"cleanup.xml"));stopped=safe.Count==prepared.Modules.Count;
                }
                catch(Exception clean){stopped=false;cleanup.Add(new{error=clean.Message});}
            }
            // Restore only prefixes that still match this operation and are confirmed idle.
            if(original!=null)try
            {
                var live=Inspect(await read()).ToDictionary(m=>m.Name);var restores=original.Modules.Select((m,i)=>(old:m,generated:BasePrefix(m.Prefix)+"__DVC_"+id+"_"+i.ToString("00"))).Where(x=>live.TryGetValue(x.old.Name,out var current)&&current.Idle&&SameWriter(x.old with{Prefix=x.generated},current)).Select(x=>new WireMessage(10,x.old.Path,"prefix",x.old.Prefix,5)).ToList();
                if(restores.Count>0)cleanup.Add(new{action="restore-prefix",result=await batch(restores)});
            }catch(Exception clean){cleanup.Add(new{error=clean.Message});}
            Journal(stopped?"failed-stopped":"unconfirmed",ex.Message,cleanup);
            throw new InvalidOperationException(ex.Message+(attempted?(stopped?" 本次所选模块已核对停止，已产生的文件保留。":" 无法确认全部停止，请在 DV 中检查所选录制模块；未强制结束后台。") :" 未启动录制。")+" 记录："+folder,ex);
        }
    }
    static async Task<XDocument> WaitStopped(Func<Task<XDocument>> read,IReadOnlyList<RecordingModule> expected,int timeoutMs)
    {
        var watch=Stopwatch.StartNew();do
        {
            var after=await read();var live=Inspect(after).ToDictionary(m=>m.Name);
            foreach(var m in expected)if(!live.TryGetValue(m.Name,out var actual)||!SameWriter(m,actual))throw new InvalidOperationException("停止期间模块身份或配置发生变化："+m.Name);
            if(expected.All(m=>live[m.Name].Idle))return after;await Task.Delay(200);
        }while(watch.ElapsedMilliseconds<timeoutMs);
        throw new TimeoutException("录制尚未全部完成停止与写盘。");
    }
    public static async Task<RecordingOperation> Stop(Func<Task<XDocument>> read,Func<IReadOnlyList<WireMessage>,Task<BatchWriteResult>> batch,IReadOnlyCollection<string> selected,string folder,int timeoutMs=18000)
    {
        Directory.CreateDirectory(folder);string id=Guid.NewGuid().ToString("N");BatchWriteResult? result=null;
        try
        {
            var before=await read();before.Save(Path.Combine(folder,"before.xml"));var plan=Plan(before,selected,starting:false);var again=Plan(await read(),selected,starting:false);EnsureSame(plan,again);
            result=await batch(Running(plan.Modules,false));Storage.WriteJson(Path.Combine(folder,"stop-batch.json"),result);
            var after=await WaitStopped(read,plan.Modules,timeoutMs);after.Save(Path.Combine(folder,"after.xml"));
            Storage.WriteJson(Path.Combine(folder,"recording-result.json"),new{time=DateTimeOffset.Now,id,operation="stop",state="stopped",selected,result,message="所选模块 running/isRunning 均为 false；正常停止流程已完成，文件保留。"});
            return new(id,false,folder,after,result);
        }
        catch(Exception ex)
        {
            Storage.WriteJson(Path.Combine(folder,"recording-result.json"),new{time=DateTimeOffset.Now,id,operation="stop",state="unconfirmed",selected,result,error=ex.ToString()});
            throw new InvalidOperationException("停止结果未确认："+ex.Message+"。请在 DV 中检查；未强制结束后台。记录："+folder,ex);
        }
    }
}
