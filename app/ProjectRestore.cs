using System.Text.RegularExpressions;
using System.Xml.Linq;
namespace DVCompanion;

public static class ProjectRestore
{
    static readonly HashSet<string> RuntimeKeys=new(["isRunning","cpuTime","cpuUsage","cycleTime","cycleTimeAverage","cycleTimeMaximum","cycleTimeMinimum","file","readPacketsNumber","readPacketsElements","queueCapacity","queueLength","typeIdentifier","typeDescription","optional","writtenDataSize","writtenPacketsElements","writtenPacketsNumber","writtenPacketsSize"],StringComparer.Ordinal);
    public static XDocument Portable(XDocument source)
    {
        var modules=Configuration.Modules(source);var loop=new XElement("node",new XAttribute("name","mainloop"),new XAttribute("path","/mainloop/"));
        foreach(var module in modules)
        {
            var copy=new XElement(module.Element);
            foreach(var n in copy.Descendants("node").Where(n=>new[]{"sourceInfo","outputs","outInfo","statistics","cycleTime"}.Contains((string?)n.Attribute("name"))).ToList())n.Remove();
            foreach(var a in copy.Descendants("attr").ToList())
            {
                string k=(string?)a.Attribute("key")??"";
                if(RuntimeKeys.Contains(k)||(k.StartsWith('_')&&k!="_guiSupport")||(a.Ancestors("node").Any(n=>(string?)n.Attribute("name")=="inputs")&&k!="from"))a.Remove();
            }
            if(module.Camera&&string.IsNullOrWhiteSpace(module.Get("serialNumber")))
            {
                string serial=module.Element.Elements("node").FirstOrDefault(n=>(string?)n.Attribute("name")=="sourceInfo")?.Elements("attr").FirstOrDefault(a=>(string?)a.Attribute("key")=="serialNumber")?.Value??"";
                if(serial.Length>0)Set(copy,"serialNumber","string",serial);
            }
            if(module.Writer)Set(copy,"running","bool","false");
            foreach(var frames in copy.Elements("node").Where(n=>(string?)n.Attribute("name")=="frames"&&n.Elements("attr").Any(a=>(string?)a.Attribute("key")=="autoExposure"&&a.Value=="true")))frames.Elements("attr").Where(a=>(string?)a.Attribute("key")=="exposureDuration").Remove();
            foreach(var a in copy.Descendants("attr").Where(a=>(string?)a.Attribute("key")=="syncTime"))a.Value="false";
            if(module.Name.StartsWith("_visualizer_")&&module.Library=="dv_output_net_tcp_server")Set(copy,"portNumber","int","0");
            void Paths(XElement n,string path){n.SetAttributeValue("path",path);foreach(var child in n.Elements("node")){string name=(string?)child.Attribute("name")??"";if(!Regex.IsMatch(name,@"^[A-Za-z0-9_.-]+$")||name is "." or "..")throw new InvalidDataException("无效配置节点名："+name);Paths(child,path+name+"/");}}
            if(!Regex.IsMatch(module.Name,@"^[A-Za-z0-9_.-]+$")||module.Name is "." or "..")throw new InvalidDataException("无效模块名");Paths(copy,"/mainloop/"+module.Name+"/");
            loop.Add(copy);
        }
        return new(new XElement("dv",new XAttribute("version","2.0"),loop));
    }
    static void Set(XElement node,string key,string type,string value){var a=node.Elements("attr").FirstOrDefault(a=>(string?)a.Attribute("key")==key);if(a==null)node.Add(new XElement("attr",new XAttribute("key",key),new XAttribute("type",type),value));else a.Value=value;}
    public static List<ModuleConfig> Order(XDocument doc)
    {
        var modules=Configuration.Modules(doc);var map=modules.ToDictionary(m=>m.Name);var seen=new HashSet<string>();var active=new HashSet<string>();var result=new List<ModuleConfig>();
        void Visit(ModuleConfig m)
        {
            if(seen.Contains(m.Name))return;if(!active.Add(m.Name))throw new InvalidDataException("模块输入连线存在循环："+m.Name);
            foreach(var a in m.Element.Descendants("attr").Where(a=>(string?)a.Attribute("key")=="from"&&a.Value.Length>0))
            {
                var match=Regex.Match(a.Value,@"^([A-Za-z0-9_.-]+)\[([^\]]+)\]$");if(!match.Success||!map.TryGetValue(match.Groups[1].Value,out var upstream))throw new InvalidDataException(m.Name+" 的输入连线无效："+a.Value);Visit(upstream);
            }
            active.Remove(m.Name);seen.Add(m.Name);result.Add(m);
        }
        foreach(var m in modules)Visit(m);return result;
    }
    static async Task Wait(int port,string name,bool running)
    {
        var deadline=DateTime.UtcNow.AddSeconds(15);
        do{if((await DvProtocol.CallAsync(port,9,"/mainloop/"+name+"/","isRunning"))[0].Value==(running?"true":"false"))return;await Task.Delay(100);}while(DateTime.UtcNow<deadline);
        throw new TimeoutException("模块未完成"+(running?"启动":"停止")+"："+name);
    }
    public static async Task Restore(int port,XDocument input,string folder,Action<string>? progress=null)
    {
        Directory.CreateDirectory(folder);var target=Portable(input);var validation=Configuration.Validate(target,Native.Cameras());if(validation.Any(f=>f.Level=="错误"))throw new InvalidDataException(string.Join("；",validation.Where(f=>f.Level=="错误").Select(f=>f.Message)));
        var ordered=Order(target);var before=await DvProtocol.SnapshotAsync(port);if(Configuration.RecordingStatus(before)!="未发现活动录制")throw new InvalidOperationException("检测到活动或待启动录制，请先在 DV 中停止录制，再恢复项目。");
        foreach(var m in ordered)if(!before.Descendants("node").Any(n=>(string?)n.Attribute("path")=="/system/modules/"+m.Library+"/"))throw new InvalidDataException("本机未提供项目需要的模块："+m.Library+"；当前结构未改动。");
        before.Save(Path.Combine(folder,"before-restore.xml"));target.Save(Path.Combine(folder,"portable-project.xml"));
        Storage.WriteJson(Path.Combine(folder,"restore-state.json"),new{state="in-progress",time=DateTimeOffset.Now});
        var skipped=new List<string>();var applied=new List<(string Path,string Key,string Value)>();
        try
        {
            progress?.Invoke("正在停止旧结构并保存恢复前快照…");
            foreach(var m in Order(before).AsEnumerable().Reverse()){await DvProtocol.CallAsync(port,10,m.Path,"running","false");await Wait(port,m.Name,false);}
            foreach(var m in Order(before).AsEnumerable().Reverse())await DvProtocol.CallAsync(port,12,m.Name);
            progress?.Invoke("正在重建全部模块、参数与连线…");
            foreach(var m in ordered){await DvProtocol.CallAsync(port,11,m.Name,m.Library);await Task.Delay(40);}
            // Hardware-dependent bias/crop options only exist after the first camera initialization.
            foreach(var m in ordered.Where(m=>m.Camera))
            {
                await DvProtocol.CallAsync(port,10,m.Path,"serialNumber",m.Get("serialNumber"));await DvProtocol.CallAsync(port,10,m.Path,"running","true");await Wait(port,m.Name,true);await DvProtocol.CallAsync(port,10,m.Path,"running","false");await Wait(port,m.Name,false);
            }
            var definitions=await DvProtocol.CallAsync(port,17);var metadata=definitions.Where(x=>x.Action==19).ToDictionary(x=>(x.Node,x.Key));
            foreach(var m in ordered)
            foreach(var node in m.Element.DescendantsAndSelf("node"))
            foreach(var attr in node.Elements("attr"))
            {
                string key=(string?)attr.Attribute("key")??"",path=(string?)node.Attribute("path")??"";
                if(key is "moduleLibrary" or "running" or "from")continue;
                if(!path.StartsWith(m.Path,StringComparison.Ordinal))throw new InvalidDataException("模块参数路径不一致："+path);
                bool known=metadata.TryGetValue((path,key),out var definition);
                if(known&&(definition!.Flags&3)!=0){skipped.Add(path+key+"（运行时字段）");continue;}
                if(!known&&key!="_guiSupport")throw new InvalidDataException("本版模块未提供参数："+path+key+"。请核对项目与模块版本；恢复未完成。");
                sbyte type=known?definition!.Type:(sbyte)5;
                await DvProtocol.CallAsync(port,10,path,key,attr.Value,type:type,flags:known?0:4);applied.Add((path,key,attr.Value));
            }
            foreach(var m in ordered)foreach(var node in m.Element.Descendants("node"))foreach(var a in node.Elements("attr").Where(a=>(string?)a.Attribute("key")=="from"))
            {string path=(string)node.Attribute("path")!;await DvProtocol.CallAsync(port,10,path,"from",a.Value);applied.Add((path,"from",a.Value));}
            var check=await DvProtocol.CallAsync(port,17);var values=check.Where(x=>x.Action==19).ToDictionary(x=>(x.Node,x.Key),x=>x.Value);
            foreach(var a in applied)
            {
                bool same=values.TryGetValue((a.Path,a.Key),out string? actual)&&actual==a.Value;
                if(!same&&metadata.TryGetValue((a.Path,a.Key),out var definition)&&definition.Type is 3 or 4&&double.TryParse(actual,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double x)&&double.TryParse(a.Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double y))same=double.IsFinite(x)&&double.IsFinite(y)&&Math.Abs(x-y)<=(definition.Type==3?1e-6:1e-12)*Math.Max(1,Math.Abs(y));
                if(!same)throw new InvalidDataException("参数恢复核对不一致："+a.Path+a.Key);
            }
            progress?.Invoke("结构与参数核对通过，正在按输入依赖顺序启动；录制保持停止…");
            foreach(var m in ordered.Where(m=>!m.Writer&&m.Get("running")=="true")){await DvProtocol.CallAsync(port,10,m.Path,"running","true");await Wait(port,m.Name,true);}
            var after=await DvProtocol.SnapshotAsync(port);after.Save(Path.Combine(folder,"after-restore.xml"));
            var actualModules=Configuration.Modules(after);if(actualModules.Count!=ordered.Count||ordered.Any(m=>!actualModules.Any(x=>x.Name==m.Name&&x.Library==m.Library)))throw new InvalidDataException("模块集合核对失败");
            Storage.WriteJson(Path.Combine(folder,"restore-state.json"),new{state=skipped.Count==0?"verified":"verified-with-notes",time=DateTimeOffset.Now,modules=ordered.Count,verifiedAttributes=applied.Count,skipped});progress?.Invoke("项目已恢复并核对："+ordered.Count+" 个模块，"+applied.Count+" 项参数/连线。"+(skipped.Count>0?"有 "+skipped.Count+" 个非持久字段未应用，详见恢复记录。":""));
        }
        catch(Exception ex)
        {
            try{var partial=await DvProtocol.SnapshotAsync(port);foreach(var m in Configuration.Modules(partial)){await DvProtocol.CallAsync(port,10,m.Path,"running","false");}partial.Save(Path.Combine(folder,"failed-restore.xml"));}catch{}
            Storage.WriteJson(Path.Combine(folder,"restore-state.json"),new{state="failed-stopped",error=ex.ToString(),skipped});throw;
        }
    }
}
