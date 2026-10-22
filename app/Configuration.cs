using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DVCompanion;

public record Finding(string Level,string Code,string Message);
public record ModuleConfig(string Name,string Path,string Library,Dictionary<string,string> Values,XElement Element)
{
    public string Get(string key)=>Values.GetValueOrDefault(key,"");
    public bool Camera=>Library.Contains("davis",StringComparison.OrdinalIgnoreCase)||Library.Contains("dvxplorer",StringComparison.OrdinalIgnoreCase)||Library.Contains("dvs128",StringComparison.OrdinalIgnoreCase);
    public bool Writer=>Library.Contains("output_file",StringComparison.OrdinalIgnoreCase)||Library.Contains("video_output",StringComparison.OrdinalIgnoreCase)||Library.Contains("image_output",StringComparison.OrdinalIgnoreCase);
}
public static class Configuration
{
    public static XDocument Parse(string text)
    {
        if(Encoding.UTF8.GetByteCount(text)>8_000_000)throw new InvalidDataException("配置文件超过 8 MB 限制");
        var settings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=8_000_000};
        using var sr=new StringReader(text);using var reader=XmlReader.Create(sr,settings);return XDocument.Load(reader,LoadOptions.SetLineInfo);
    }
    public static List<ModuleConfig> Modules(XDocument doc)
    {
        if(doc.Root?.Name!="dv"||(string?)doc.Root.Attribute("version")!="2.0")throw new InvalidDataException("请选择 version=2.0 的 DV XML 配置。");
        var loops=doc.Descendants("node").Where(n=>(string?)n.Attribute("path")=="/mainloop/"||(string?)n.Attribute("name")=="mainloop").ToList();
        if(loops.Count!=1)throw new InvalidDataException("必须包含唯一的 mainloop 配置节点；请选择 DV 项目 XML 或导出的后台配置。");
        return loops[0].Elements("node").Select(n=>new ModuleConfig((string?)n.Attribute("name")??"",(string?)n.Attribute("path")??"/mainloop/"+(string?)n.Attribute("name")+"/",n.Elements("attr").FirstOrDefault(a=>(string?)a.Attribute("key")=="moduleLibrary")?.Value??"",n.Elements("attr").GroupBy(a=>(string?)a.Attribute("key")??"").ToDictionary(g=>g.Key,g=>g.First().Value),n)).ToList();
    }
    public static List<Finding> Validate(XDocument doc,IReadOnlyCollection<CameraDevice>? devices=null)
    {
        var f=new List<Finding>();List<ModuleConfig> modules;
        try{modules=Modules(doc);}catch(Exception ex){return [new("错误","SCHEMA",ex.Message)];}
        foreach(var dup in doc.Descendants("node").GroupBy(n=>(string?)n.Attribute("path")).Where(g=>!string.IsNullOrEmpty(g.Key)&&g.Count()>1))f.Add(new("错误","DUP_PATH","重复节点路径："+dup.Key));
        foreach(var dup in modules.GroupBy(m=>m.Name,StringComparer.Ordinal).Where(g=>g.Count()>1))f.Add(new("错误","DUP_MODULE","重复模块名称："+dup.Key));
        foreach(var m in modules)
        {
            if(m.Name.Length==0||m.Name.Contains('/'))f.Add(new("错误","MODULE_NAME","模块名称为空或包含斜杠。"));
            if(m.Library.Length==0)f.Add(new("错误","MODULE_LIBRARY",m.Name+" 缺少 moduleLibrary。"));
            foreach(var a in m.Element.Elements("attr").GroupBy(x=>(string?)x.Attribute("key")??"").Where(g=>g.Count()>1))f.Add(new("错误","DUP_ATTR",m.Name+" 存在重复字段 "+a.Key));
        }
        var cameras=modules.Where(m=>m.Camera).ToList();var byName=modules.GroupBy(m=>m.Name).ToDictionary(g=>g.Key,g=>g.First());
        foreach(var dup in cameras.Where(m=>m.Get("serialNumber").Trim().Length>0).GroupBy(m=>m.Get("serialNumber").Trim(),StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()>1))f.Add(new("错误","DUP_SERIAL","多个模块指向同一台相机 "+dup.Key+"："+string.Join("、",dup.Select(m=>m.Name))));
        foreach(var m in cameras)
        {
            string serial=m.Get("serialNumber").Trim();
            if(serial!=m.Get("serialNumber"))f.Add(new("错误","SERIAL_WHITESPACE",m.Name+" 的相机序列号前后含有空白，请先移除。"));
            if(serial.Length==0)f.Add(new(cameras.Count>1?"错误":"提醒","NO_SERIAL",m.Name+" 未固定序列号"+(cameras.Count>1?"，双机可能选中同一设备。":"，将由 DV 选择可用相机。")));
            else if(devices!=null&&!devices.Any(d=>d.Serial.Equals(serial,StringComparison.OrdinalIgnoreCase)))f.Add(new("错误","MISSING_CAMERA",m.Name+" 对应的相机 "+serial+" 当前未被 Windows 检测到。"));
            var info=m.Element.Elements("node").FirstOrDefault(n=>(string?)n.Attribute("name")=="sourceInfo");
            string role=info?.Elements("attr").FirstOrDefault(a=>(string?)a.Attribute("key")=="deviceIsMaster")?.Value??"";
            string[] targets=m.Get("syncWith").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
            if(role=="false"&&targets.Length>0)f.Add(new("错误","SECONDARY_SYNC",m.Name+" 为从相机，syncWith 应为空。"));
            foreach(string target in targets)
            {
                if(target==m.Name)f.Add(new("错误","SELF_SYNC",m.Name+" 不能与自身同步。"));
                else if(!byName.TryGetValue(target,out var other))f.Add(new("错误","MISSING_SYNC_MODULE",m.Name+" 引用了不存在的同步模块 "+target+"。"));
                else if(!other.Camera)f.Add(new("错误","SYNC_NOT_CAMERA",target+" 不是相机采集模块。"));
                else if(other.Element.Elements("node").Where(n=>(string?)n.Attribute("name")=="sourceInfo").Elements("attr").Any(a=>(string?)a.Attribute("key")=="deviceIsMaster"&&a.Value=="true"))f.Add(new("错误","SYNC_TARGET_MASTER",target+" 当前为主时钟设备，不能作为从机同步目标。"));
            }
        }
        var visited=new HashSet<string>();var stack=new HashSet<string>();
        bool Cycle(string name){if(stack.Contains(name))return true;if(!visited.Add(name))return false;stack.Add(name);if(byName.TryGetValue(name,out var m))foreach(var s in m.Get("syncWith").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))if(byName.ContainsKey(s)&&Cycle(s))return true;stack.Remove(name);return false;}
        if(cameras.Any(m=>Cycle(m.Name)))f.Add(new("错误","SYNC_CYCLE","同步模块之间存在循环引用。"));
        if(cameras.Count>1)f.Add(new("提醒","PHYSICAL_SYNC","双机配置已做静态检查；同步线、实际主从角色和时间戳质量仍需连接后验证。"));
        foreach(var writer in modules.Where(m=>m.Writer))
        {
            var parent=writer.Get("directory");if(parent.Length>0&&!Directory.Exists(parent))f.Add(new("提醒","OUTPUT_DIRECTORY",writer.Name+" 的输出目录不存在："+parent));
        }
        if(modules.Count==0)f.Add(new("提醒","EMPTY_PROJECT","项目没有处理模块，可作为空白测试配置。"));
        if(!f.Any(x=>x.Level=="错误"))f.Insert(0,new("通过","VALID","配置结构检查通过，共 "+modules.Count+" 个模块、"+cameras.Count+" 个相机模块。"));
        return f;
    }
    public static string RecordingStatus(XDocument doc)
    {
        var writers=Modules(doc).Where(m=>m.Writer).ToList();
        if(writers.Any(m=>m.Get("isRunning")=="true"))return "正在写文件";
        if(writers.Any(m=>m.Get("running")=="true"))return "状态待确认";
        return "未发现活动录制";
    }
    public static string SaveAtomic(string target,string text,IReadOnlyCollection<CameraDevice>? devices=null)
    {
        var doc=Parse(text);var checks=Validate(doc,devices);if(checks.Any(x=>x.Level=="错误"))throw new InvalidDataException(string.Join(Environment.NewLine,checks.Where(x=>x.Level=="错误").Select(x=>x.Message)));
        target=System.IO.Path.GetFullPath(target);string folder=System.IO.Path.GetDirectoryName(target)!;Directory.CreateDirectory(folder);
        string tmp=target+".new-"+Guid.NewGuid().ToString("N");string backup="";
        try{using(var f=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){var data=new UTF8Encoding(false).GetBytes(text);f.Write(data);f.Flush(true);}
            if(File.Exists(target)){backup=target+".backup-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");File.Replace(tmp,target,backup);}else File.Move(tmp,target);
            return backup;
        }finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
}

public static class ErrorAdvice
{
    public static string Explain(string text)
    {
        if(text.Contains("No available DAVIS camera matches serial"))return "未找到可用的 DAVIS 相机：核对配置序列号、USB 连接和其他 DV 后台的占用。修正后可以停止模块并重新启动，无需先重启电脑。";
        if(text.Contains("vector::_M_range_check"))return "采集初始化失败：没有得到可选设备。先检查序列号和占用，再重新发现相机。";
        if(text.Contains("could not open device")&&text.Contains("[5]"))return "设备打开被拒绝：可能被其他实例占用。检查残留后台；暂不能确定是权限问题。";
        if(text.Contains("LIBUSB_TRANSFER_NO_DEVICE"))return "USB 设备已断开或暂时消失；若并未拔插，请交叉检查接口和数据线。";
        if(text.Contains("LIBUSB_TRANSFER_STALL"))return "USB 传输停滞。停止采集后尝试重新连接，避免录制中反复重启。";
        if(text.Contains("syncWith")||text.Contains("Secondary camera module"))return "双机同步配置不一致，请使用配置预检查核对模块名称和主从角色。";
        if(text.Contains("libbacktrace"))return "后台符号解析或模块卸载相关故障，需要转储确认实际调用位置。";
        if(text.Contains("LIBUSB_TRANSFER_CANCELLED"))return "传输已取消，正常停止采集时也可能出现。";
        return "请结合故障前的操作与诊断记录判断；暂未识别到明确原因。";
    }
}
