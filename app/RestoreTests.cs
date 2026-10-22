namespace DVCompanion;
internal static class RestoreTests
{
    public static async Task Run(string output,string input)
    {
        var results=new List<object>();var original=Configuration.Parse(File.ReadAllText(input));
        var expectedCameras=Configuration.Modules(ProjectRestore.Portable(original)).Where(m=>m.Camera).ToList();
        for(int round=1;round<=3;round++)
        {
            string folder=Storage.NewFolder("restore-tests");using var job=new Native.OwnedJob();string error="";bool stopped=false;int restored=0;
            var reserve=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);reserve.Start();int port=((System.Net.IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
            var p=job.Launch(Session.PatchedExecutable,["-i","127.0.0.1","-p",port.ToString(),"-l",Path.Combine(folder,"runtime.log"),"--console","0"],@"C:\Program Files\DV\runtime",true,new Dictionary<string,string>{{"DV_MODULES_PATH",Path.Combine(Path.GetDirectoryName(Session.PatchedExecutable)!,"dv_modules")+@"|C:\Program Files\DV\runtime\dv_modules"}});
            try
            {
                var end=DateTime.UtcNow.AddSeconds(15);while(p.Alive&&!Native.ListeningPorts(p.Pid).Contains(port)&&DateTime.UtcNow<end)await Task.Delay(100);
                Diagnostics.RegisterRuntime(p,port);
                await ProjectRestore.Restore(port,original,Path.Combine(folder,"load-original"));
                var saved=ProjectRestore.Portable(await DvProtocol.SnapshotAsync(port));saved.Save(Path.Combine(folder,"saved-again.xml"));
                if(saved.Root!.Elements("node").Single().Attribute("name")?.Value!="mainloop"||saved.Descendants("node").Any(n=>(string?)n.Attribute("path")=="/system/")||saved.Descendants("attr").Any(a=>(string?)a.Attribute("key")=="isRunning"))throw new Exception("便携项目包含运行时数据");
                await ProjectRestore.Restore(port,saved,Path.Combine(folder,"reload-saved"));
                var after=await DvProtocol.SnapshotAsync(port);restored=Configuration.Modules(after).Count;
                if(restored!=Configuration.Modules(original).Count)throw new Exception("模块数量不一致");
                foreach(var expected in expectedCameras)
                {
                    var camera=Configuration.Modules(after).Single(m=>m.Name==expected.Name);
                    if(camera.Get("serialNumber")!=expected.Get("serialNumber")||camera.Get("isRunning")!=expected.Get("running"))throw new Exception("相机身份或运行状态错误");
                }
                await DvProtocol.CallAsync(port,10,"/system/","running","false");end=DateTime.UtcNow.AddSeconds(15);while(p.Alive&&DateTime.UtcNow<end)await Task.Delay(100);stopped=!p.Alive;
                end=DateTime.UtcNow.AddSeconds(3);while(job.Members().Count>0&&DateTime.UtcNow<end)await Task.Delay(100);
            }
            catch(Exception ex){error=ex.ToString();try{(await DvProtocol.SnapshotAsync(port)).Save(Path.Combine(folder,"failure.xml"));}catch{}}
            results.Add(new{round,restored,pass=error.Length==0&&stopped,error,stopped,remaining=job.Members().Count,folder});
        }
        Storage.WriteJson(output,new{time=DateTimeOffset.Now,input,results});
    }
}
