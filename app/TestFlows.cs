using System.Diagnostics;
using Microsoft.Win32;
namespace DVCompanion;

internal static class TestFlows
{
    static async Task WaitState(int port,string name,bool state,int seconds=12)
    {
        var end=DateTime.UtcNow.AddSeconds(seconds);
        while(DateTime.UtcNow<end){if((await DvProtocol.CallAsync(port,9,"/mainloop/"+name+"/","isRunning"))[0].Value==(state?"true":"false"))return;await Task.Delay(200);}
        throw new TimeoutException(name+" isRunning != "+state);
    }
    public static async Task Cameras(string output,string executable)
    {
        string primarySerial=Environment.GetEnvironmentVariable("DV_TEST_PRIMARY_SERIAL")??throw new InvalidOperationException("Set DV_TEST_PRIMARY_SERIAL before a hardware test.");
        string secondarySerial=Environment.GetEnvironmentVariable("DV_TEST_SECONDARY_SERIAL")??throw new InvalidOperationException("Set DV_TEST_SECONDARY_SERIAL before a hardware test.");
        if(primarySerial==secondarySerial)throw new InvalidOperationException("Two distinct camera serial numbers are required.");
        using var job=new Native.OwnedJob();string folder=Storage.NewFolder("cameras"),original=@"C:\Program Files\DV\runtime";
        var reserve=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);reserve.Start();int port=((System.Net.IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
        string overlay=Path.Combine(Path.GetDirectoryName(executable)!,"dv_modules");
        var p=job.Launch(executable,["-i","127.0.0.1","-p",port.ToString(),"-l",Path.Combine(folder,"runtime.log"),"--console","0"],original,true,new Dictionary<string,string>{{"DV_MODULES_PATH",overlay+"|"+Path.Combine(original,"dv_modules")}});
        var results=new List<object>();bool stopped=false;string error="";
        try
        {
            var end=DateTime.UtcNow.AddSeconds(15);while(p.Alive&&!Native.ListeningPorts(p.Pid).Contains(port)&&DateTime.UtcNow<end)await Task.Delay(100);
            (await DvProtocol.SnapshotAsync(port)).Save(Path.Combine(folder,"initial.xml"));
            foreach(var item in new[]{("left",primarySerial),("right",secondarySerial)})
            {
                await DvProtocol.CallAsync(port,11,item.Item1,"dv_davis");
                await DvProtocol.CallAsync(port,10,"/mainloop/"+item.Item1+"/","serialNumber",item.Item2);
                await DvProtocol.CallAsync(port,11,"writer_"+item.Item1,"dv_output_file");
                await DvProtocol.CallAsync(port,10,"/mainloop/writer_"+item.Item1+"/","directory",folder);
                await DvProtocol.CallAsync(port,10,"/mainloop/writer_"+item.Item1+"/inputs/output0/","from",item.Item1+"[events]");
            }
            // A missing serial must fail clearly without losing the control service, then allow retry.
            await DvProtocol.CallAsync(port,10,"/mainloop/left/","serialNumber","99999999");
            await DvProtocol.CallAsync(port,10,"/mainloop/left/","running","true");await Task.Delay(1700);
            var missing=await DvProtocol.SnapshotAsync(port);missing.Save(Path.Combine(folder,"missing-camera.xml"));
            string log=Diagnostics.Tail(Path.Combine(folder,"runtime.log"));
            bool explicitError=log.Contains("No available DAVIS camera matches serial: 99999999");
            results.Add(new{test="missing serial reports reason and backend stays responsive",pass=explicitError,detail=log.Split('\n').LastOrDefault(l=>l.Contains("99999999"))});
            await DvProtocol.CallAsync(port,10,"/mainloop/left/","running","false");await WaitState(port,"left",false);
            await DvProtocol.CallAsync(port,10,"/mainloop/left/","serialNumber",primarySerial);
            for(int round=1;round<=3;round++)
            {
                foreach(string name in new[]{"left","right"}){await DvProtocol.CallAsync(port,10,"/mainloop/"+name+"/","running","true");await DvProtocol.CallAsync(port,10,"/system/devices/","updateAvailableDevices","true");await WaitState(port,name,true);}
                if(round==1)
                {
                    await DvProtocol.CallAsync(port,10,"/mainloop/left/","syncWith","right");await DvProtocol.CallAsync(port,10,"/mainloop/left/","syncTime","true");
                    var until=DateTime.UtcNow.AddSeconds(12);string left="",right="";
                    do{await Task.Delay(200);left=(await DvProtocol.CallAsync(port,9,"/mainloop/left/sourceInfo/","syncStatus"))[0].Value;right=(await DvProtocol.CallAsync(port,9,"/mainloop/right/sourceInfo/","syncStatus"))[0].Value;}while((left!="SYNC_OK"||right!="SYNC_OK")&&DateTime.UtcNow<until);
                    results.Add(new{test="hardware timestamp reset synchronization",pass=left=="SYNC_OK"&&right=="SYNC_OK",left,right});
                }
                foreach(string name in new[]{"left","right"}){await DvProtocol.CallAsync(port,10,"/mainloop/writer_"+name+"/","prefix","round"+round+"_"+name);await DvProtocol.CallAsync(port,10,"/mainloop/writer_"+name+"/","running","true");await WaitState(port,"writer_"+name,true);}
                await Task.Delay(2400);var snapshot=await DvProtocol.SnapshotAsync(port);snapshot.Save(Path.Combine(folder,"round"+round+"-running.xml"));
                foreach(string name in new[]{"left","right"}){await DvProtocol.CallAsync(port,10,"/mainloop/writer_"+name+"/","running","false");await WaitState(port,"writer_"+name,false);}
                foreach(string name in new[]{"left","right"}){await DvProtocol.CallAsync(port,10,"/mainloop/"+name+"/","running","false");await WaitState(port,name,false);}
                results.Add(new{test="two cameras record and stop round "+round,pass=true,files=Directory.GetFiles(folder,"round"+round+"*.aedat4").Select(f=>new{file=f,bytes=new FileInfo(f).Length})});
            }
            await DvProtocol.CallAsync(port,10,"/system/","running","false");end=DateTime.UtcNow.AddSeconds(15);while(p.Alive&&DateTime.UtcNow<end)await Task.Delay(100);stopped=!p.Alive;
        }
        catch(Exception ex){error=ex.ToString();try{(await DvProtocol.SnapshotAsync(port)).Save(Path.Combine(folder,"failure.xml"));}catch{}try{Native.WriteMiniDump(p,Path.Combine(folder,"failure.dmp"));}catch{}}
        Storage.WriteJson(output,new{time=DateTimeOffset.Now,executable,folder,results,error,gracefulStop=stopped,remaining=job.Members().Count});
    }
    static Dictionary<string,object?> Prefs(){using var key=Registry.CurrentUser.OpenSubKey(@"Software\JavaSoft\Prefs\inivation\dv");return new[]{"manage_local_instances","default_credentials"}.ToDictionary(k=>k,k=>key?.GetValue(k));}
    public static async Task Gui(string output)
    {
        var before=Prefs();string error="";bool responsive=false,closed=false,backendStopped=false;int remaining=-1;string folder="";
        using(var session=new Session(@"C:\Program Files\DV",true))
        {
            folder=session.Folder;
            try
            {
                await session.StartGui("<dv version=\"2.0\"><node name=\"\" path=\"/\"><node name=\"mainloop\" path=\"/mainloop/\"/></node></dv>");
                var end=DateTime.UtcNow.AddSeconds(30);while(session.Gui!.Alive&&DateTime.UtcNow<end){using var g=Process.GetProcessById(session.Gui.Pid);if(g.MainWindowHandle!=0&&g.Responding){responsive=true;break;}await Task.Delay(300);}
                await Task.Delay(20000);foreach(var rt in session.Runtimes())(await session.Snapshot(rt)).Save(Path.Combine(folder,"gui-connected.xml"));
                using(var g=Process.GetProcessById(session.Gui!.Pid)){session.Note("GUI title="+g.MainWindowTitle+" handle="+g.MainWindowHandle);session.Note("CloseMainWindow result="+g.CloseMainWindow());}end=DateTime.UtcNow.AddSeconds(180);while(session.Gui.Alive&&DateTime.UtcNow<end)await Task.Delay(200);closed=!session.Gui.Alive;
                backendStopped=true;foreach(var rt in session.Runtimes().ToList())backendStopped &= await session.ShutdownRuntime(rt);remaining=session.Job.Members().Count;
                await Diagnostics.Capture(session.DvRoot,session);
            }
            catch(Exception ex){error=ex.ToString();await Diagnostics.Capture(session.DvRoot,session);}
        }
        var after=Prefs();Storage.WriteJson(output,new{time=DateTimeOffset.Now,folder,responsive,closed,backendStopped,remaining,preferencesRestored=before.All(k=>Equals(after[k.Key],k.Value)),error});
    }
}
