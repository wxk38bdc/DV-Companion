using System.Xml.Linq;
namespace DVCompanion;

internal static class SaveCycleTests
{
    public static async Task Run(string output,string executable)
    {
        string folder=Storage.NewFolder("save-cycles"),project=Path.Combine(folder,"latest.xml");var results=new List<object>();
        string seed="""
            <dv version="2.0"><node name="mainloop" path="/mainloop/">
              <node name="source"><attr key="moduleLibrary" type="string">dv_dvsnoisefilter</attr><attr key="running" type="bool">false</attr></node>
              <node name="old_view"><attr key="moduleLibrary" type="string">dv_accumulator</attr><attr key="running" type="bool">false</attr><node name="inputs"><node name="events"><attr key="from" type="string">source[events]</attr></node></node></node>
            </node></dv>
            """;
        for(int round=1;round<=2;round++)
        {
            using var job=new Native.OwnedJob();string run=Path.Combine(folder,"round"+round);Directory.CreateDirectory(run);
            var reserve=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);reserve.Start();int port=((System.Net.IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
            var p=job.Launch(executable,["-i","127.0.0.1","-p",port.ToString(),"-l",Path.Combine(run,"runtime.log"),"--console","0"],@"C:\Program Files\DV\runtime",true,new Dictionary<string,string>{{"DV_MODULES_PATH",Path.Combine(Path.GetDirectoryName(executable)!,"dv_modules")+@"|C:\Program Files\DV\runtime\dv_modules"}});
            string error="";bool stopped=false;int count=0;
            try
            {
                var end=DateTime.UtcNow.AddSeconds(15);while(p.Alive&&!Native.ListeningPorts(p.Pid).Contains(port)&&DateTime.UtcNow<end)await Task.Delay(100);Diagnostics.RegisterRuntime(p,port);
                Task<XDocument> Read()=>Diagnostics.Snapshot(p);
                async Task Put(string name,string key,string value,sbyte type=-1,int flags=0)=>_ = await DvProtocol.CallAsync(port,10,"/mainloop/"+name+"/",key,value,type:type,flags:flags);
                async Task Check(string expected,string position)
                {
                    var snapshot=await Read();var modules=Configuration.Modules(snapshot);count=modules.Count;
                    if(count!=3||modules.Any(m=>m.Name=="old_view")||!modules.Any(m=>m.Name=="new_source"))throw new Exception("新增/删除模块未恢复");
                    var view=modules.Single(m=>m.Name=="new_view");
                    if(view.Get("accumulationTime")!=expected||!view.Get("_guiSupport").Contains(position)||modules.Single(m=>m.Name=="source").Get("hotPixelEnable")!="true")throw new Exception("参数或界面布局未恢复");
                    if(!view.Element.Descendants("attr").Any(a=>(string?)a.Attribute("key")=="from"&&a.Value=="new_source[events]"))throw new Exception("新连线未恢复");
                    snapshot.Save(Path.Combine(run,"verified-"+expected+".xml"));
                }
                if(round==1)
                {
                    await ProjectRestore.Restore(port,Configuration.Parse(seed),Path.Combine(run,"initial"));
                    await ProjectSave.Current(project,Read);
                    await ProjectRestore.Restore(port,Configuration.Parse(File.ReadAllText(project)),Path.Combine(run,"load-first"));
                    // These public-protocol mutations stand in for edits made in DV's Structure UI.
                    await DvProtocol.CallAsync(port,12,"old_view");
                    await DvProtocol.CallAsync(port,11,"new_source","dv_dvsnoisefilter");
                    await DvProtocol.CallAsync(port,11,"new_view","dv_accumulator");
                    await Put("new_view","accumulationTime","75");
                    await Put("new_view","_guiSupport","{\"boxX\":725,\"boxY\":250}",5,4);
                    await Put("source","hotPixelEnable","true");
                    await DvProtocol.CallAsync(port,10,"/mainloop/new_view/inputs/events/","from","new_source[events]");
                    await ProjectSave.Current(project,Read);
                    await ProjectRestore.Restore(port,Configuration.Parse(File.ReadAllText(project)),Path.Combine(run,"reload-edited"));
                    await Check("75","725");
                    await Put("new_view","accumulationTime","91");
                    await ProjectSave.Current(project,Read);
                    await ProjectRestore.Restore(port,Configuration.Parse(File.ReadAllText(project)),Path.Combine(run,"reload-edited-again"));
                    await Check("91","725");
                }
                else
                {
                    await ProjectRestore.Restore(port,Configuration.Parse(File.ReadAllText(project)),Path.Combine(run,"load-after-restart"));
                    await Check("91","725");
                    await Put("new_view","accumulationTime","123");
                    await Put("new_view","_guiSupport","{\"boxX\":860,\"boxY\":350}",5,4);
                    await ProjectSave.Current(project,Read);
                    await ProjectRestore.Restore(port,Configuration.Parse(File.ReadAllText(project)),Path.Combine(run,"reload-after-restart"));
                    await Check("123","860");
                }
                await DvProtocol.CallAsync(port,10,"/system/","running","false");end=DateTime.UtcNow.AddSeconds(15);while(p.Alive&&DateTime.UtcNow<end)await Task.Delay(100);stopped=!p.Alive;
                end=DateTime.UtcNow.AddSeconds(3);while(job.Members().Count>0&&DateTime.UtcNow<end)await Task.Delay(100);
            }
            catch(Exception ex){error=ex.ToString();try{(await Diagnostics.Snapshot(p)).Save(Path.Combine(run,"failure.xml"));}catch{}}
            int remaining=job.Members().Count;results.Add(new{round,pass=error.Length==0&&stopped&&remaining==0,modules=count,stopped,remaining,error,folder=run});
        }
        bool passed=results.All(x=>(bool)x.GetType().GetProperty("pass")!.GetValue(x)!);
        Storage.WriteJson(output,new{time=DateTimeOffset.Now,passed,method="Real backend CFGA edits, save, restore, edit again; no cameras or GUI processes started",project,results});Environment.ExitCode=passed?0:1;
    }
}
