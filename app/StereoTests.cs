using System.Xml.Linq;
namespace DVCompanion;

internal static class StereoTests
{
    internal static XDocument Fixture()
    {
        var loop=new XElement("node",new XAttribute("name","mainloop"),new XAttribute("path","/mainloop/"));
        XElement Attr(string key,string value,string type="string")=>new("attr",new XAttribute("key",key),new XAttribute("type",type),value);
        foreach(var (name,serial,master) in new[]{("left","10000001",true),("right","10000002",false)})
            loop.Add(new XElement("node",new XAttribute("name",name),new XAttribute("path","/mainloop/"+name+"/"),Attr("moduleLibrary","dv_davis"),Attr("serialNumber",serial),Attr("running","true","bool"),Attr("isRunning","true","bool"),Attr("syncTime","false","bool"),Attr("syncWith",""),new XElement("node",new XAttribute("name","sourceInfo"),Attr("serialNumber",serial),Attr("deviceIsMaster",master?"true":"false","bool"),Attr("syncStatus","NO_SYNC"),Attr("tsOffset",master?"100":"101","long"))));
        loop.Add(new XElement("node",new XAttribute("name","writer"),new XAttribute("path","/mainloop/writer/"),Attr("moduleLibrary","dv_output_file"),Attr("running","false","bool"),Attr("isRunning","false","bool")));
        return new(new XElement("dv",new XAttribute("version","2.0"),loop));
    }
    internal static void Set(XDocument doc,string module,string key,string value,bool info=false)
    {
        var node=Configuration.Modules(doc).Single(m=>m.Name==module).Element;if(info)node=node.Elements("node").Single(n=>(string?)n.Attribute("name")=="sourceInfo");
        node.Elements("attr").Single(a=>(string?)a.Attribute("key")==key).Value=value;
    }
    public static async Task Run(Func<string,Func<Task>,Task> test,Action<bool,string> assert)
    {
        await test("双机状态只依据实际设备身份与后台采集状态",()=>
        {
            var state=StereoSync.Inspect(Fixture());assert(state.CanSync&&state.Master.Name=="left"&&state.Secondary.Serial=="10000002","未识别主从");return Task.CompletedTask;
        });
        async Task Refuse(string name,Action<XDocument> change)
        {
            await test(name,async()=>
            {
                var doc=Fixture();change(doc);int writes=0;
                try{await StereoSync.Execute(()=>Task.FromResult(doc),(p,k,v)=>{writes++;return Task.CompletedTask;},Storage.NewFolder("stereo-refuse"));throw new Exception("应拒绝但执行成功");}catch(InvalidOperationException){}
                assert(writes==0,"拒绝条件仍写入后台");
            });
        }
        await Refuse("双机同步拒绝正在录制",d=>Set(d,"writer","isRunning","true"));
        await Refuse("双机同步拒绝录制启动中",d=>Set(d,"writer","running","true"));
        await Refuse("双机同步拒绝缺失录制状态",d=>Configuration.Modules(d).Single(m=>m.Writer).Element.Elements("attr").Single(a=>(string?)a.Attribute("key")=="isRunning").Remove());
        await Refuse("双机同步拒绝未成功打开的相机",d=>Set(d,"right","isRunning","false"));
        await Refuse("双机同步拒绝两台都是主机",d=>Set(d,"right","deviceIsMaster","true",true));
        await Refuse("双机同步拒绝从机填写同步目标",d=>Set(d,"right","syncWith","left"));
        await Refuse("双机同步不覆盖已有冲突同步目标",d=>Set(d,"left","syncWith","missing"));
        await Refuse("双机同步拒绝配置与真实设备不匹配",d=>Set(d,"left","serialNumber","10000099"));
        await Refuse("双机同步拒绝重复真实相机身份",d=>Set(d,"right","serialNumber","10000001",true));
        await Refuse("双机同步拒绝缺失角色",d=>Set(d,"right","deviceIsMaster","",true));
        await Refuse("双机同步拒绝模块数量不为二",d=>Configuration.Modules(d).Single(m=>m.Name=="right").Element.Remove());
        await test("同步前再次读取阻止新启动录制",async()=>
        {
            int reads=0,writes=0;var doc=Fixture();
            try{await StereoSync.Execute(()=>{if(++reads==2)Set(doc,"writer","running","true");return Task.FromResult(new XDocument(doc));},(p,k,v)=>{writes++;return Task.CompletedTask;},Storage.NewFolder("stereo-race"));throw new Exception("未拒绝新启动录制");}catch(InvalidOperationException){}
            assert(reads==2&&writes==0,"检查未阻止写入");
        });
        await test("自动补全同步目标并核验本次偏移更新",async()=>
        {
            var doc=Fixture();var writes=new List<string>();
            await StereoSync.Execute(()=>Task.FromResult(new XDocument(doc)),(path,key,value)=>
            {
                writes.Add(path+key+"="+value);
                if(key=="syncWith")Set(doc,"left",key,value);
                if(key=="syncTime")foreach(string name in new[]{"left","right"}){Set(doc,name,"syncStatus","SYNC_OK",true);Set(doc,name,"tsOffset","200",true);}
                return Task.CompletedTask;
            },Storage.NewFolder("stereo-success"));
            assert(writes.SequenceEqual(new[]{"/mainloop/left/syncWith=right","/mainloop/left/syncTime=true"}),"写入了预期之外的字段");
            assert(Configuration.Modules(doc).Single(m=>m.Writer).Get("running")=="false","修改了录制状态");
        });
        await test("旧 SYNC_OK 不冒充本次同步成功并保存诊断",async()=>
        {
            var doc=Fixture();Set(doc,"left","syncWith","right");foreach(string name in new[]{"left","right"}){Set(doc,name,"syncStatus","SYNC_OK",true);Set(doc,name,"tsOffset","200",true);}
            string folder=Storage.NewFolder("stereo-stale");int writes=0;
            try{await StereoSync.Execute(()=>Task.FromResult(new XDocument(doc)),(p,k,v)=>{writes++;return Task.CompletedTask;},folder,timeoutMs:300);throw new Exception("旧状态被当成成功");}catch(TimeoutException){}
            assert(writes==1&&File.ReadAllText(Path.Combine(folder,"sync-result.json")).Contains("unconfirmed")&&File.Exists(Path.Combine(folder,"before.xml"))&&File.Exists(Path.Combine(folder,"after.xml")),"失败记录不完整");
        });
        await test("双机同步断连失败不会假报成功",async()=>
        {
            string folder=Storage.NewFolder("stereo-disconnect");
            try{await StereoSync.Execute(()=>Task.FromResult(Fixture()),(p,k,v)=>Task.FromException(new IOException("disconnected")),folder);throw new Exception("写入失败被忽略");}catch(IOException){}
            assert(File.ReadAllText(Path.Combine(folder,"sync-result.json")).Contains("blocked"),"缺少失败记录");
        });
        await test("本次同步仅一台更新不能通过",async()=>
        {
            var doc=Fixture();Set(doc,"left","syncWith","right");
            try{await StereoSync.Execute(()=>Task.FromResult(new XDocument(doc)),(p,k,v)=>{Set(doc,"left","syncStatus","SYNC_OK",true);Set(doc,"left","tsOffset","200",true);return Task.CompletedTask;},Storage.NewFolder("stereo-half"),timeoutMs:300);throw new Exception("单机成功误报双机完成");}catch(TimeoutException){}
        });
        await test("保存双机项目保留同步目标并清除瞬时同步操作",()=>
        {
            var doc=Fixture();Set(doc,"left","syncWith","right");Set(doc,"left","syncTime","true");var portable=ProjectRestore.Portable(doc);var master=Configuration.Modules(portable).Single(m=>m.Name=="left");
            assert(master.Get("syncWith")=="right"&&master.Get("syncTime")=="false","同步关系与瞬时动作处理错误");return Task.CompletedTask;
        });
    }
}
