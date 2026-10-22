using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace DVCompanion;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if(args.Length>0)
        {
            try
            {
                if(args[0]=="--child-sleep"){Thread.Sleep(120000);return;}
                if(args[0]=="--render-preview"){ApplicationConfiguration.Initialize();using var form=new MainForm();form.RenderPreviews(args[1]);return;}
                if(args[0]=="--child-parent")
                {
                    var child=Process.Start(new ProcessStartInfo(Environment.ProcessPath!){ArgumentList={"--child-sleep"},UseShellExecute=false,CreateNoWindow=true})!;
                    File.WriteAllText(args[1],child.Id.ToString());Thread.Sleep(120000);return;
                }
                string output=args.Length>1?Path.GetFullPath(args[1]):Path.Combine(AppContext.BaseDirectory,"checks.json");
                Storage.Root=Path.Combine(Path.GetDirectoryName(output)!,"test-data");
                if(args[0]=="--self-test"){Tests.Run(output).GetAwaiter().GetResult();return;}
                if(args[0]=="--inspect"){Storage.WriteJson(output,new{devices=Native.Cameras(),processes=Native.DvProcesses(@"C:\Program Files\DV")});return;}
                if(args[0]=="--integration"){Tests.Integration(output,args.Length>2?args[2]:@"C:\Program Files\DV").GetAwaiter().GetResult();return;}
                if(args[0]=="--backend-regression"){Tests.BackendRegression(output,args[2]).GetAwaiter().GetResult();return;}
                if(args[0]=="--camera-test"){TestFlows.Cameras(output,args[2]).GetAwaiter().GetResult();return;}
                if(args[0]=="--gui-smoke"){TestFlows.Gui(output).GetAwaiter().GetResult();return;}
                if(args[0]=="--restore-test"){RestoreTests.Run(output,args[2]).GetAwaiter().GetResult();return;}
                if(args[0]=="--save-cycle-test"){SaveCycleTests.Run(output,args[2]).GetAwaiter().GetResult();return;}
                if(args[0]=="--probe-existing")
                {
                    var list=new List<object>();foreach(var p in Native.DvProcesses(@"C:\Program Files\DV").Where(p=>p.Name=="dv-runtime.exe"))
                    {try{var d=Diagnostics.Snapshot(p).GetAwaiter().GetResult();string file=Path.Combine(Path.GetDirectoryName(output)!,"existing-"+p.Pid+".xml");d.Save(file);list.Add(new{p.Pid,state=Configuration.RecordingStatus(d),file});}catch(Exception ex){list.Add(new{p.Pid,error=ex.Message});}}Storage.WriteJson(output,list);return;
                }
            }
            catch(Exception ex){string output=args.Length>1?args[1]:Path.Combine(AppContext.BaseDirectory,"fatal.json");Storage.WriteJson(output,new{error=ex.ToString()});Environment.ExitCode=1;return;}
        }
        ApplicationConfiguration.Initialize();
        bool created;using var singleton=new Mutex(true,"Local\\DVCompanion-Manager",out created);if(!created){MessageBox.Show("DV Companion 已在运行。请使用已打开的管理器。","DV Companion");return;}
        try{Directory.CreateDirectory(Storage.Root);Application.Run(new MainForm());}
        catch(Exception ex){try{Storage.WriteJson(Path.Combine(Storage.Root,"manager-fatal.json"),new{time=DateTimeOffset.Now,error=ex.ToString()});}catch{}MessageBox.Show("管理器遇到错误："+ex.Message,"DV Companion");}
    }
}

internal static class Tests
{
    public static async Task Run(string output)
    {
        var results=new List<object>();int failures=0;
        async Task Test(string name,Func<Task> test){try{await test();results.Add(new{name,pass=true});}catch(Exception ex){failures++;results.Add(new{name,pass=false,error=ex.Message});}}
        Task Sync(Action a){a();return Task.CompletedTask;}
        void Assert(bool condition,string why){if(!condition)throw new Exception(why);}
        string Xml(string a,string b="")=>"<dv version=\"2.0\"><node name=\"\" path=\"/\"><node name=\"mainloop\" path=\"/mainloop/\">"+a+b+"</node></node></dv>";
        string Camera(string name,string serial,string sync="",bool secondary=false)=>"<node name=\""+name+"\" path=\"/mainloop/"+name+"/\"><attr key=\"moduleLibrary\" type=\"string\">dv_davis</attr><attr key=\"serialNumber\" type=\"string\">"+serial+"</attr><attr key=\"syncWith\" type=\"string\">"+sync+"</attr>"+(secondary?"<node name=\"sourceInfo\" path=\"/mainloop/"+name+"/sourceInfo/\"><attr key=\"deviceIsMaster\" type=\"bool\">false</attr></node>":"")+"</node>";
        List<Finding> Check(string xml)=>Configuration.Validate(Configuration.Parse(xml));
        await SaveTests.Run(Test,Assert);
        await Test("FlatBuffers 请求含中文与空格可往返",()=>Sync(()=>{var msg=DvProtocol.Decode(DvProtocol.Encode(10,"/system/config/","saveFile",@"C:\测 试\a.xml"));Assert(msg.Action==10&&msg.Value==@"C:\测 试\a.xml"&&msg.Type==-1,"协议字段错误");}));
        await Test("拒绝损坏协议数据",()=>Sync(()=>{try{DvProtocol.Decode([1,2,3]);throw new Exception("未拒绝损坏数据");}catch(InvalidDataException){}}));
        await Test("配置导入协议保留类型与标志",()=>Sync(()=>{var m=DvProtocol.Decode(DvProtocol.Encode(10,"/mainloop/a/","_guiSupport","{}",5,4));Assert(m.Type==5&&m.Flags==4,"导入类型或标志丢失");}));
        await Test("项目保存去除后台系统树与运行字段",()=>Sync(()=>{var d=Configuration.Parse(Xml(Camera("left","10000001")));d.Root!.Add(new XElement("node",new XAttribute("name","system"),new XAttribute("path","/system/")));var p=ProjectRestore.Portable(d);Assert(p.Root!.Elements("node").Count()==1&&p.Root.Element("node")!.Attribute("name")!.Value=="mainloop","系统树被保存为项目");}));
        await Test("项目从运行快照固定真实相机身份",()=>Sync(()=>{var d=Configuration.Parse(Xml(Camera("left","")));var m=Configuration.Modules(d).Single();m.Element.Add(new XElement("node",new XAttribute("name","sourceInfo"),new XElement("attr",new XAttribute("key","serialNumber"),new XAttribute("type","string"),"10000001")));Assert(Configuration.Modules(ProjectRestore.Portable(d)).Single().Get("serialNumber")=="10000001","相机身份丢失");}));
        await Test("项目恢复阻止模块连线循环",()=>Sync(()=>{var d=Configuration.Parse(Xml(Camera("left","10000001"),Camera("right","10000002")));foreach(var m in Configuration.Modules(d))m.Element.Add(new XElement("node",new XAttribute("name","inputs"),new XElement("node",new XAttribute("name","events"),new XElement("attr",new XAttribute("key","from"),new XAttribute("type","string"),(m.Name=="left"?"right":"left")+"[events]"))));try{ProjectRestore.Order(d);throw new Exception("连线循环未被拒绝");}catch(InvalidDataException){}}));
        await Test("允许有效的双机配置",()=>Sync(()=>Assert(!Check(Xml(Camera("left","10000001","right"),Camera("right","10000002","",true))).Any(f=>f.Level=="错误"),"有效配置被拒绝")));
        await Test("阻止重复相机序列号",()=>Sync(()=>Assert(Check(Xml(Camera("left","10000001"),Camera("right","10000001"))).Any(f=>f.Code=="DUP_SERIAL"),"未检测重复设备")));
        await Test("阻止双机未固定身份",()=>Sync(()=>Assert(Check(Xml(Camera("left",""),Camera("right",""))).Count(f=>f.Code=="NO_SERIAL"&&f.Level=="错误")==2,"未固定身份未被阻止")));
        await Test("阻止不存在的同步模块",()=>Sync(()=>Assert(Check(Xml(Camera("left","10000001","absent"))).Any(f=>f.Code=="MISSING_SYNC_MODULE"),"未检测缺失引用")));
        await Test("阻止同步循环",()=>Sync(()=>Assert(Check(Xml(Camera("left","10000001","right"),Camera("right","10000002","left"))).Any(f=>f.Code=="SYNC_CYCLE"),"未检测循环")));
        await Test("阻止从机设置 syncWith",()=>Sync(()=>Assert(Check(Xml(Camera("left","10000001","right",true),Camera("right","10000002"))).Any(f=>f.Code=="SECONDARY_SYNC"),"未检测从机错误")));
        await Test("拒绝非 DV XML",()=>Sync(()=>Assert(Check("<hello />").Any(f=>f.Code=="SCHEMA"),"未拒绝错误 XML")));
        await Test("拒绝伪装 mainloop 的非 DV 根节点",()=>Sync(()=>Assert(Check(Xml(Camera("left","10000001")).Replace("<dv version=\"2.0\">","<hello>").Replace("</dv>","</hello>")).Any(f=>f.Code=="SCHEMA"),"未核验 DV 根节点")));
        await Test("阻止序列号前后空白造成打开失败",()=>Sync(()=>Assert(Check(Xml(Camera("left"," 10000001 "))).Any(f=>f.Code=="SERIAL_WHITESPACE"),"未检测序列号空白")));
        await Test("阻止 XML 外部实体",()=>Sync(()=>{try{Configuration.Parse("<!DOCTYPE dv [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><dv>&x;</dv>");throw new Exception("外部实体未被拒绝");}catch(System.Xml.XmlException){}}));
        await Test("离线与现场设备检查分开",()=>Sync(()=>{var d=Configuration.Parse(Xml(Camera("left","10000001")));Assert(!Configuration.Validate(d).Any(f=>f.Code=="MISSING_CAMERA")&&Configuration.Validate(d,[]).Any(f=>f.Code=="MISSING_CAMERA"),"未正确区分离线检查");}));
        await Test("原子保存保留原文件备份",()=>Sync(()=>{string p=Path.Combine(Storage.NewFolder("atomic"),"project.xml");string old=Xml(Camera("left","10000001")),next=Xml(Camera("right","10000002"));File.WriteAllText(p,old);string b=Configuration.SaveAtomic(p,next);Assert(File.ReadAllText(p)==next&&File.ReadAllText(b)==old,"备份或保存错误");}));
        await Test("无效保存不会覆盖原文件",()=>Sync(()=>{string p=Path.Combine(Storage.NewFolder("atomic"),"project.xml");File.WriteAllText(p,"original");try{Configuration.SaveAtomic(p,"<hello/>");}catch(InvalidDataException){}Assert(File.ReadAllText(p)=="original","原文件被损坏");}));
        await Test("记录正在写文件的模块",()=>Sync(()=>{var d=Configuration.Parse(Xml("<node name=\"writer\" path=\"/mainloop/writer/\"><attr key=\"moduleLibrary\" type=\"string\">dv_output_file</attr><attr key=\"isRunning\" type=\"bool\">true</attr></node>"));Assert(Configuration.RecordingStatus(d)=="正在写文件","未检测录制");}));
        await Test("录制启动过程中不视为空闲",()=>Sync(()=>{var d=Configuration.Parse(Xml("<node name=\"writer\" path=\"/mainloop/writer/\"><attr key=\"moduleLibrary\" type=\"string\">dv_output_file</attr><attr key=\"running\" type=\"bool\">true</attr><attr key=\"isRunning\" type=\"bool\">false</attr></node>"));Assert(Configuration.RecordingStatus(d)=="状态待确认","启动中的录制被视为空闲");}));
        await Test("不把传输取消判为线材故障",()=>Sync(()=>Assert(ErrorAdvice.Explain("LIBUSB_TRANSFER_CANCELLED").Contains("正常停止"),"错误分类不正确")));
        await Test("进程身份拒绝 PID 重用",()=>Sync(()=>{var p=ProcessIdentity.Read(Environment.ProcessId)!;Assert(p.Alive&&!new ProcessIdentity(p.Pid,p.StartedUtcTicks+1,p.Path,0).Alive,"进程身份校验失效");}));
        await Test("Job 对象包含子进程且退出后无残留",async()=>
        {
            using var job=new Native.OwnedJob();string pidFile=Path.Combine(Storage.NewFolder("job"),"child.pid");var parent=job.Launch(Environment.ProcessPath!,["--child-parent",pidFile],AppContext.BaseDirectory,true);var end=DateTime.UtcNow.AddSeconds(8);while(!File.Exists(pidFile)&&DateTime.UtcNow<end)await Task.Delay(100);Assert(File.Exists(pidFile),"测试子进程未启动");int childPid=int.Parse(File.ReadAllText(pidFile));var child=ProcessIdentity.Read(childPid)!;Assert(job.Members().Any(p=>p.Pid==childPid),"子进程未继承 Job");job.ForceEnd();end=DateTime.UtcNow.AddSeconds(4);while((parent.Alive||child.Alive)&&DateTime.UtcNow<end)await Task.Delay(50);Assert(!parent.Alive&&!child.Alive,"测试进程残留");
        });
        await Test("无响应服务的请求有超时",async()=>
        {
            var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();try{int port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;var accept=listener.AcceptTcpClientAsync();var watch=Stopwatch.StartNew();try{await DvProtocol.CallAsync(port,9,"/system/","running",timeoutMs:350);throw new Exception("未超时");}catch(TimeoutException){Assert(watch.ElapsedMilliseconds<2500,"超时未及时返回");}using var accepted=await accept;}finally{listener.Stop();}
        });
        Storage.WriteJson(output,new{time=DateTimeOffset.Now,total=results.Count,failures,results});Environment.ExitCode=failures==0?0:1;
    }
    public static async Task Integration(string output,string dvRoot)
    {
        var rounds=new List<object>();for(int round=1;round<=3;round++)
        {
            using var job=new Native.OwnedJob();string folder=Storage.NewFolder("runtime");string config=Path.Combine(folder,"empty.xml");File.WriteAllText(config,"<dv version=\"2.0\"><node name=\"\" path=\"/\"><node name=\"mainloop\" path=\"/mainloop/\"/></node></dv>");
            ProcessIdentity? p=null;try
            {
                var reservation=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);reservation.Start();int freePort=((System.Net.IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
                p=job.Launch(Path.Combine(dvRoot,"runtime","dv-runtime.exe"),["-i","127.0.0.1","-p",freePort.ToString(),"-l",Path.Combine(folder,"runtime.log"),"--console","0","-c",config],Path.Combine(dvRoot,"runtime"),true);
                var end=DateTime.UtcNow.AddSeconds(20);List<int> ports=[];while(p.Alive&&DateTime.UtcNow<end){ports=Native.ListeningPorts(p.Pid);if(ports.Count==1)break;await Task.Delay(200);}if(ports.Count!=1)throw new Exception("后台未建立配置端口");
                string running=(await DvProtocol.CallAsync(ports[0],9,"/system/","running"))[0].Value;if(running!="true")throw new Exception("后台未报告运行中");
                var doc=await DvProtocol.SnapshotAsync(ports[0]);doc.Save(Path.Combine(folder,"snapshot.xml"));
                if(Configuration.Modules(doc).Count!=0)throw new Exception("空白配置意外加载了相机或其他模块");
                await DvProtocol.CallAsync(ports[0],10,"/system/","running","false");end=DateTime.UtcNow.AddSeconds(20);while(p.Alive&&DateTime.UtcNow<end)await Task.Delay(100);if(p.Alive)throw new Exception("后台正常退出超时");
                rounds.Add(new{round,pass=true,pid=p.Pid,port=ports[0],folder,remaining=job.Members().Count});
            }
            catch(Exception ex){rounds.Add(new{round,pass=false,error=ex.ToString(),folder});}
        }
        Storage.WriteJson(output,new{time=DateTimeOffset.Now,dvRoot,rounds});
        Environment.ExitCode=rounds.Any(r=>r.GetType().GetProperty("pass")?.GetValue(r) is false)?1:0;
    }
    public static async Task BackendRegression(string output,string executable)
    {
        using var job=new Native.OwnedJob();string folder=Storage.NewFolder("regression");string original=@"C:\Program Files\DV\runtime";
        var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();int port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var p=job.Launch(executable,["-i","127.0.0.1","-p",port.ToString(),"-l",Path.Combine(folder,"runtime.log"),"--console","0"],original,true);
        var end=DateTime.UtcNow.AddSeconds(15);while(p.Alive&&DateTime.UtcNow<end&&!Native.ListeningPorts(p.Pid).Contains(port))await Task.Delay(100);
        if(!p.Alive)throw new Exception("测试后台提前退出；退出码或缺失 DLL 需要检查。");
        var baseline=await DvProtocol.CallAsync(port,9,"/system/","running");
        bool healthyAfterReset=false;
        using(var reset=new System.Net.Sockets.TcpClient()){await reset.ConnectAsync(System.Net.IPAddress.Loopback,port);await Task.Delay(150);reset.LingerState=new System.Net.Sockets.LingerOption(true,0);}
        await Task.Delay(200);
        try{healthyAfterReset=(await DvProtocol.CallAsync(port,9,"/system/","running",timeoutMs:1500))[0].Value=="true";}catch{}
        string injection="";bool healthy=false,stopped=false;
        try
        {
            using var client=new System.Net.Sockets.TcpClient();await client.ConnectAsync(System.Net.IPAddress.Loopback,port);
            byte[] payload=DvProtocol.Encode(10,"/system/logger/","logLevel","BAD_UTF8_MARKER");byte[] marker=Encoding.ASCII.GetBytes("BAD_UTF8_MARKER");int offset=-1;for(int i=0;i<=payload.Length-marker.Length;i++)if(payload.AsSpan(i,marker.Length).SequenceEqual(marker)){offset=i;break;}if(offset<0)throw new Exception("注入标记不存在");payload[offset]=255;
            using var timeout=new CancellationTokenSource(1200);var stream=client.GetStream();await stream.WriteAsync(BitConverter.GetBytes(payload.Length),timeout.Token);await stream.WriteAsync(payload,timeout.Token);byte[] prefix=new byte[4];await stream.ReadExactlyAsync(prefix,timeout.Token);int length=BitConverter.ToInt32(prefix);if(length<8||length>8192)throw new Exception("异常响应长度");byte[] data=new byte[length];await stream.ReadExactlyAsync(data,timeout.Token);injection=DvProtocol.Decode(data).Value;
        }catch(Exception ex){injection=ex.GetType().Name+": "+ex.Message;}
        try{healthy=(await DvProtocol.CallAsync(port,9,"/system/","running",timeoutMs:1500))[0].Value=="true";}catch{}
        if(!healthy&&p.Alive)
        {
            var psi=new ProcessStartInfo(@"C:\msys64\mingw64\bin\gdb.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string arg in new[]{"--batch","-ex","set pagination off","-ex","set debuginfod enabled off","-ex","set print thread-events off","-ex","thread apply all bt 22","-ex","detach","-p",p.Pid.ToString()})psi.ArgumentList.Add(arg);
            using var debugger=Process.Start(psi)!;var stdout=debugger.StandardOutput.ReadToEndAsync();var stderr=debugger.StandardError.ReadToEndAsync();using var timeout=new CancellationTokenSource(15000);try{await debugger.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){debugger.Kill();}
            File.WriteAllText(Path.Combine(folder,"stack.txt"),(await stdout)+(await stderr));
        }
        if(healthy){await DvProtocol.CallAsync(port,10,"/system/","running","false");end=DateTime.UtcNow.AddSeconds(8);while(p.Alive&&DateTime.UtcNow<end)await Task.Delay(100);stopped=!p.Alive;}
        Storage.WriteJson(output,new{time=DateTimeOffset.Now,executable,pid=p.Pid,port,baseline=baseline[0].Value,healthyAfterReset,injection,healthyAfterMalformedLog=healthy,gracefulStop=stopped,folder});
    }
}
