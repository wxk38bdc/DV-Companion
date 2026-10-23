using System.Diagnostics;
using System.Xml.Linq;
namespace DVCompanion;

internal static class RecordingHardwareTest
{
    public static async Task Run(string output,string executable,string project)
    {
        if(Process.GetProcessesByName("dv-runtime").Length>0||Native.DvProcesses(@"C:\Program Files\DV").Count>0)throw new InvalidOperationException("已有 DV 进程，未进行录制测试。");
        string folder=Storage.NewFolder("recording-hardware"),data=Path.Combine(folder,"recorded-files");Directory.CreateDirectory(data);string error="";bool stopped=false;int remaining=0;var results=new List<object>();
        var xml=ProjectRestore.Portable(Configuration.Parse(File.ReadAllText(project)));
        // User's original project stays untouched; only the isolated test copy has a new output directory.
        foreach(var module in Configuration.Modules(xml).Where(m=>m.Writer))module.Element.Elements("attr").Single(a=>(string?)a.Attribute("key")=="directory").Value=data;
        foreach(var module in Configuration.Modules(xml).Where(m=>m.Name.StartsWith("_visualizer_")).ToArray())module.Element.Remove();
        xml.Save(Path.Combine(folder,"test-project.xml"));var names=Configuration.Modules(xml).Where(m=>m.Library=="dv_output_file").Select(m=>m.Name).ToArray();
        if(names.Length!=3||!new[]{"output_top","output_bottom","output_stereo"}.All(names.Contains))throw new InvalidDataException("测试项目不符合三录制模块样本。");
        using(var job=new Native.OwnedJob())
        {
            var reserve=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);reserve.Start();int port=((System.Net.IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();string runtime=@"C:\Program Files\DV\runtime";
            var p=job.Launch(executable,["-i","127.0.0.1","-p",port.ToString(),"-l",Path.Combine(folder,"runtime.log"),"--console","0"],runtime,true,new Dictionary<string,string>{{"DV_MODULES_PATH",Path.Combine(Path.GetDirectoryName(executable)!,"dv_modules")+"|"+Path.Combine(runtime,"dv_modules")}});
            Task<XDocument> Read()=>DvProtocol.SnapshotAsync(port);Task<BatchWriteResult> Batch(IReadOnlyList<WireMessage> writes)=>DvProtocol.PutBatchAsync(port,writes);
            async Task Start(string label,string[] selection)
            {
                var operation=await RecordingControl.Start(Read,Batch,selection,Path.Combine(folder,label));results.Add(new{test=label,pass=RecordingControl.Inspect(operation.Snapshot).Where(m=>selection.Contains(m.Name)).All(m=>m.Active=="true"),batchAcknowledgedMs=operation.Batch.ElapsedMs,selected=selection});
            }
            async Task Stop(string label,string[] selection)
            {
                var operation=await RecordingControl.Stop(Read,Batch,selection,Path.Combine(folder,label));results.Add(new{test=label,pass=RecordingControl.Inspect(operation.Snapshot).Where(m=>selection.Contains(m.Name)).All(m=>m.Idle),selected=selection});
            }
            try
            {
                var end=DateTime.UtcNow.AddSeconds(15);while(p.Alive&&!Native.ListeningPorts(p.Pid).Contains(port)&&DateTime.UtcNow<end)await Task.Delay(100);
                if(!p.Alive)throw new Exception("测试后台未就绪");
                await ProjectRestore.Restore(port,xml,Path.Combine(folder,"restore"));
                await StereoSync.Execute(Read,async(path,key,value)=>_ = await DvProtocol.CallAsync(port,10,path,key,value),Path.Combine(folder,"synchronize"));
                await Start("start-pair",["output_top","output_bottom"]);await Task.Delay(1800);
                var pair=await Read();pair.Save(Path.Combine(folder,"pair-running.xml"));results.Add(new{test="mixed output remains stopped when not selected",pass=RecordingControl.Inspect(pair).Single(m=>m.Name=="output_stereo").Idle});
                await Stop("stop-top-only",["output_top"]);var single=await Read();results.Add(new{test="bottom continues when top stopped",pass=RecordingControl.Inspect(single).Single(m=>m.Name=="output_bottom").Active=="true"});
                await Start("restart-top-while-bottom-recording",["output_top"]);await Task.Delay(700);await Stop("stop-pair",["output_top","output_bottom"]);
                await Start("start-all-three",names);await Task.Delay(1600);await Stop("stop-all-three",names);
                await Start("start-mixed-only",["output_stereo"]);await Task.Delay(700);await Stop("stop-mixed-only",["output_stereo"]);
                var final=await Read();final.Save(Path.Combine(folder,"final.xml"));
                results.Add(new{test="all writers idle, cameras keep running",pass=RecordingControl.Inspect(final).All(m=>m.Idle)&&Configuration.Modules(final).Where(m=>m.Camera).All(m=>m.Get("isRunning")=="true")});
                var files=Directory.GetFiles(data,"*.aedat4");results.Add(new{test="seven unique nonempty AEDAT4 files retained",pass=files.Length==7&&files.All(f=>new FileInfo(f).Length>1000),files=files.Select(f=>new{file=f,bytes=new FileInfo(f).Length})});
            }
            catch(Exception ex){error=ex.ToString();try{(await Read()).Save(Path.Combine(folder,"failure.xml"));}catch{}}
            finally
            {
                if(p.Alive)try{await DvProtocol.CallAsync(port,10,"/system/","running","false");var end=DateTime.UtcNow.AddSeconds(15);while(p.Alive&&DateTime.UtcNow<end)await Task.Delay(100);}catch{}
                stopped=!p.Alive;var deadline=DateTime.UtcNow.AddSeconds(2);while(job.Members().Count>0&&DateTime.UtcNow<deadline)await Task.Delay(100);remaining=job.Members().Count;
            }
        }
        bool pass=error.Length==0&&stopped&&remaining==0&&results.All(r=>(bool)r.GetType().GetProperty("pass")!.GetValue(r)!);
        Storage.WriteJson(output,new{time=DateTimeOffset.Now,pass,project,folder,data,results,error,stopped,remaining});Environment.ExitCode=pass?0:1;
    }
}
