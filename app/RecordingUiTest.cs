using System.Diagnostics;
using System.Xml.Linq;
namespace DVCompanion;

internal static class RecordingUiTest
{
    public static void Run(string output,string executable,string project)
    {
        if(Process.GetProcessesByName("dv-runtime").Length>0||Native.DvProcesses(@"C:\Program Files\DV").Count>0)throw new InvalidOperationException("已有 DV，未启动 UI 验证。");
        string folder=Storage.NewFolder("recording-ui"),data=Path.Combine(folder,"files");Directory.CreateDirectory(data);using var job=new Native.OwnedJob();
        var reserve=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);reserve.Start();int port=((System.Net.IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
        string runtime=@"C:\Program Files\DV\runtime";var p=job.Launch(executable,["-i","127.0.0.1","-p",port.ToString(),"-l",Path.Combine(folder,"runtime.log"),"--console","0"],runtime,true,new Dictionary<string,string>{{"DV_MODULES_PATH",Path.Combine(Path.GetDirectoryName(executable)!,"dv_modules")+"|"+Path.Combine(runtime,"dv_modules")}});
        async Task Prepare()
        {
            var end=DateTime.UtcNow.AddSeconds(15);while(p.Alive&&!Native.ListeningPorts(p.Pid).Contains(port)&&DateTime.UtcNow<end)await Task.Delay(100);Diagnostics.RegisterRuntime(p,port);
            var xml=ProjectRestore.Portable(Configuration.Parse(File.ReadAllText(project)));
            foreach(var module in Configuration.Modules(xml).Where(m=>m.Writer))module.Element.Elements("attr").Single(a=>(string?)a.Attribute("key")=="directory").Value=data;
            foreach(var module in Configuration.Modules(xml).Where(m=>m.Name.StartsWith("_visualizer_")).ToArray())module.Element.Remove();
            await ProjectRestore.Restore(port,xml,Path.Combine(folder,"restore"));
            await StereoSync.Execute(()=>DvProtocol.SnapshotAsync(port),async(path,key,value)=>_ = await DvProtocol.CallAsync(port,10,path,key,value),Path.Combine(folder,"sync"));
            (await DvProtocol.SnapshotAsync(port)).Save(Path.Combine(folder,"ready.xml"));
        }
        bool stopped=false;string error="";
        try{Prepare().GetAwaiter().GetResult();Storage.WriteJson(output,new{state="ready",folder,data,pid=p.Pid});ApplicationConfiguration.Initialize();using var form=new MainForm();form.UseRecordingTestBackend(p);Application.Run(form);}
        catch(Exception ex){error=ex.ToString();throw;}
        finally
        {
            try{if(p.Alive){DvProtocol.CallAsync(port,10,"/system/","running","false").GetAwaiter().GetResult();var end=DateTime.UtcNow.AddSeconds(15);while(p.Alive&&DateTime.UtcNow<end)Thread.Sleep(100);}stopped=!p.Alive;}catch(Exception ex){error+="\n"+ex.Message;}
            var deadline=DateTime.UtcNow.AddSeconds(2);while(job.Members().Count>0&&DateTime.UtcNow<deadline)Thread.Sleep(50);
            Storage.WriteJson(output,new{state="closed",folder,data,pid=p.Pid,stopped,remaining=job.Members().Count,error,files=Directory.GetFiles(data,"*.aedat4").Select(f=>new{file=f,bytes=new FileInfo(f).Length})});
        }
    }
}
