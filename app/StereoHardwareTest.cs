using System.Diagnostics;
using System.Xml.Linq;
namespace DVCompanion;

internal static class StereoHardwareTest
{
    public static async Task Run(string output,string executable)
    {
        if(Process.GetProcessesByName("dv-runtime").Length>0||Native.DvProcesses(@"C:\Program Files\DV").Count>0)throw new InvalidOperationException("已有 DV 进程，未启动硬件测试。");
        var devices=Native.Cameras().OrderBy(c=>c.Serial).ToArray();if(devices.Length!=2)throw new InvalidOperationException("需要恰好两台 Windows 可见相机。");
        string folder=Storage.NewFolder("stereo-hardware");var results=new List<object>();string error="";bool stopped=false;int remaining;
        using(var job=new Native.OwnedJob())
        {
            var reserve=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);reserve.Start();int port=((System.Net.IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
            string runtime=@"C:\Program Files\DV\runtime";
            var p=job.Launch(executable,["-i","127.0.0.1","-p",port.ToString(),"-l",Path.Combine(folder,"runtime.log"),"--console","0"],runtime,true,new Dictionary<string,string>{{"DV_MODULES_PATH",Path.Combine(Path.GetDirectoryName(executable)!,"dv_modules")+"|"+Path.Combine(runtime,"dv_modules")}});
            async Task Put(string path,string key,string value)=>_ = await DvProtocol.CallAsync(port,10,path,key,value);
            Task<XDocument> Read()=>DvProtocol.SnapshotAsync(port);
            try
            {
                var end=DateTime.UtcNow.AddSeconds(15);while(p.Alive&&!Native.ListeningPorts(p.Pid).Contains(port)&&DateTime.UtcNow<end)await Task.Delay(100);
                if(!p.Alive||!Native.ListeningPorts(p.Pid).Contains(port))throw new Exception("测试后台未就绪");
                for(int i=0;i<2;i++)
                {
                    string name=i==0?"left":"right",path="/mainloop/"+name+"/";
                    await DvProtocol.CallAsync(port,11,name,"dv_davis");await Put(path,"serialNumber",devices[i].Serial);await Put(path,"running","true");
                    end=DateTime.UtcNow.AddSeconds(15);while((await DvProtocol.CallAsync(port,9,path,"isRunning"))[0].Value!="true"&&DateTime.UtcNow<end)await Task.Delay(200);
                    await DvProtocol.CallAsync(port,11,"writer_"+name,"dv_output_file");
                }
                var baseline=await Read();baseline.Save(Path.Combine(folder,"initial.xml"));var initial=StereoSync.Inspect(baseline);
                results.Add(new{test="actual role, identity and idle writer detection",pass=initial.CanSync,reason=initial.BlockReason});
                if(!initial.CanSync)throw new Exception(initial.BlockReason);
                for(int round=1;round<=2;round++)
                {
                    var result=await StereoSync.Execute(Read,Put,Path.Combine(folder,"sync"+round));
                    results.Add(new{test="fresh timestamp synchronization round "+round,pass=result.CanSync&&result.Cameras.All(c=>c.Status=="SYNC_OK")&&result.Master.Offset==result.Secondary.Offset,master=result.Master.Name,secondary=result.Secondary.Name,offset=result.Master.Offset});
                }
                var final=await Read();final.Save(Path.Combine(folder,"final.xml"));
                var writers=Configuration.Modules(final).Where(m=>m.Writer).ToArray();
                results.Add(new{test="sync never starts writers",pass=writers.Length==2&&writers.All(m=>m.Get("running")=="false"&&m.Get("isRunning")=="false")});
                var saved=await ProjectSave.Current(Path.Combine(folder,"saved-project.xml"),Read);var portable=Configuration.Modules(Configuration.Parse(saved.Xml));
                var master=StereoSync.Inspect(final).Master;results.Add(new{test="existing save retains syncWith and resets transient action",pass=portable.Single(m=>m.Name==master.Name).Get("syncWith")==StereoSync.Inspect(final).Secondary.Name&&portable.Where(m=>m.Camera).All(m=>m.Get("syncTime")=="false")});
            }
            catch(Exception ex){error=ex.ToString();try{(await Read()).Save(Path.Combine(folder,"failure.xml"));}catch{}}
            finally
            {
                if(p.Alive)try{await DvProtocol.CallAsync(port,10,"/system/","running","false");var end=DateTime.UtcNow.AddSeconds(15);while(p.Alive&&DateTime.UtcNow<end)await Task.Delay(100);}catch{}
                stopped=!p.Alive;var deadline=DateTime.UtcNow.AddSeconds(2);while(job.Members().Count>0&&DateTime.UtcNow<deadline)await Task.Delay(100);remaining=job.Members().Count;
            }
        }
        bool pass=error.Length==0&&stopped&&remaining==0&&results.All(r=>(bool)r.GetType().GetProperty("pass")!.GetValue(r)!);
        Storage.WriteJson(output,new{time=DateTimeOffset.Now,pass,folder,results,error,stopped,remaining,recordingStarted=false});Environment.ExitCode=pass?0:1;
    }
}
