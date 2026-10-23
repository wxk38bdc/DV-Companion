using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
namespace DVCompanion;

internal static class RecordingTests
{
    internal static XDocument Fixture(string directory)
    {
        var doc=StereoTests.Fixture();var modules=Configuration.Modules(doc);modules.Single(m=>m.Writer).Element.Remove();
        XElement Attr(string key,string value,string type="string")=>new("attr",new XAttribute("key",key),new XAttribute("type",type),value);
        foreach(string name in new[]{"left","right"})
        {
            var camera=Configuration.Modules(doc).Single(m=>m.Name==name);camera.Element.Add(new XElement("node",new XAttribute("name","outputs"),new[]{"events","frames","imu","triggers"}.Select(s=>new XElement("node",new XAttribute("name",s)))));
            StereoTests.Set(doc,name,"syncStatus","SYNC_OK",true);StereoTests.Set(doc,name,"tsOffset","200",true);
        }
        StereoTests.Set(doc,"left","syncWith","right");
        var loop=doc.Descendants("node").Single(n=>(string?)n.Attribute("name")=="mainloop");
        foreach(var (name,prefix,refs) in new[]{("output_top","top_camera",new[]{"right[events]","right[frames]","right[imu]","right[triggers]"}),("output_bottom","bottom_camera",new[]{"left[events]","left[frames]","left[imu]","left[triggers]"}),("output_stereo","stereo",new[]{"left[events]","left[frames]","right[events]","right[frames]"})})
            loop.Add(new XElement("node",new XAttribute("name",name),new XAttribute("path","/mainloop/"+name+"/"),Attr("moduleLibrary","dv_output_file"),Attr("running","false","bool"),Attr("isRunning","false","bool"),Attr("directory",directory),Attr("prefix",prefix),Attr("compression","LZ4"),Attr("timeout","0","long"),Attr("file",""),Attr("writtenDataSize","0","long"),new XElement("node",new XAttribute("name","inputs"),refs.Select((r,i)=>new XElement("node",new XAttribute("name","output"+i),Attr("from",r))))));
        return doc;
    }
    sealed class Backend
    {
        public XDocument Doc;public List<WireMessage> Writes=[];public string FailStart="",FailStop="";public bool LostStartAck;public int Reads;
        public Backend(){Doc=Fixture(Storage.NewFolder("recording-fake-files"));}
        public Task<XDocument> Read(){Reads++;return Task.FromResult(new XDocument(Doc));}
        public Task<BatchWriteResult> Batch(IReadOnlyList<WireMessage> writes)
        {
            bool lost=LostStartAck&&writes.Any(w=>w.Key=="running"&&w.Value=="true");
            foreach(var write in writes)
            {
                Writes.Add(write);var module=Configuration.Modules(Doc).Single(m=>m.Path==write.Node);if(write.Key=="running"&&write.Value=="false"&&module.Name==FailStop)continue;
                module.Element.Elements("attr").Single(a=>(string?)a.Attribute("key")==write.Key).Value=write.Value;
                if(write.Key=="running")
                {
                    bool active=write.Value=="true"&&module.Name!=FailStart;StereoTests.Set(Doc,module.Name,"isRunning",active?"true":"false");
                    if(write.Value=="true"&&!active)StereoTests.Set(Doc,module.Name,"running","false");
                    if(active){module=Configuration.Modules(Doc).Single(m=>m.Name==module.Name);string path=Path.Combine(module.Get("directory"),module.Get("prefix")+"-2026_10_23_12_00_00.aedat4");File.WriteAllText(path,"#!AER-DAT4.0\r\nmock-packet");StereoTests.Set(Doc,module.Name,"file",path);}
                }
            }
            return Task.FromResult(new BatchWriteResult(true,1,writes.Select(w=>new BatchWriteStatus(w.Node,w.Key,w.Value,!lost,lost?"ack lost":"",1)).ToArray(),lost?"ack lost":""));
        }
    }
    public static async Task Run(Func<string,Func<Task>,Task> test,Action<bool,string> assert)
    {
        await test("录制识别任意模块名和四路输入",()=>
        {
            var doc=Fixture(Storage.NewFolder("recording-plan"));var rows=RecordingControl.Inspect(doc);assert(rows.Count==3&&rows.Single(m=>m.Name=="output_stereo").Inputs.Contains("output3 ← right[frames]"),"三模块或连线识别错误");
            var plan=RecordingControl.Plan(doc,["output_stereo"]);assert(plan.Cameras.Count==2,"混合文件的双机来源未识别");return Task.CompletedTask;
        });
        async Task Refuse(string name,Action<Backend> change)
        {
            await test(name,async()=>{var b=new Backend();change(b);try{await RecordingControl.Start(b.Read,b.Batch,["output_top","output_bottom"],Storage.NewFolder("recording-refuse"),probe:_=>{});throw new Exception("未拒绝错误状态");}catch(InvalidOperationException){}assert(b.Writes.Count==0,"检查失败仍写入后台");});
        }
        await Refuse("录制拒绝已经启动的所选模块",b=>StereoTests.Set(b.Doc,"output_top","running","true"));
        await Refuse("录制拒绝未知写盘状态",b=>Configuration.Modules(b.Doc).Single(m=>m.Name=="output_bottom").Element.Elements("attr").Single(a=>(string?)a.Attribute("key")=="isRunning").Remove());
        await Refuse("录制拒绝未采集的数据源",b=>StereoTests.Set(b.Doc,"right","isRunning","false"));
        await Refuse("双目录制拒绝未同步的相机",b=>StereoTests.Set(b.Doc,"right","syncStatus","NO_SYNC",true));
        await Refuse("录制拒绝未发布的输出端口",b=>Configuration.Modules(b.Doc).Single(m=>m.Name=="right").Element.Elements("node").Single(n=>(string?)n.Attribute("name")=="outputs").Elements("node").Single(n=>(string?)n.Attribute("name")=="frames").Remove());
        await Refuse("录制拒绝缺失必需输入",b=>Configuration.Modules(b.Doc).Single(m=>m.Name=="output_top").Element.Descendants("node").Single(n=>(string?)n.Attribute("name")=="output0").Element("attr")!.Value="");
        await Refuse("录制拒绝无效文件前缀",b=>StereoTests.Set(b.Doc,"output_top","prefix","bad/name"));
        await test("目录写权限失败不启动录制",async()=>
        {
            var b=new Backend();try{await RecordingControl.Start(b.Read,b.Batch,["output_top"],Storage.NewFolder("recording-probe"),probe:_=>throw new IOException("denied"));throw new Exception("目录失败仍启动");}catch(InvalidOperationException){}assert(b.Writes.Count==0,"目录失败仍写入配置");
        });
        await test("检查期间出现外部录制请求不再下发启动",async()=>
        {
            var b=new Backend();int reads=0;Task<XDocument> Read(){if(++reads==2)StereoTests.Set(b.Doc,"output_top","running","true");return b.Read();}
            try{await RecordingControl.Start(Read,b.Batch,["output_top","output_bottom"],Storage.NewFolder("recording-start-race"),probe:_=>{});throw new Exception("未拒绝外部启动");}catch(InvalidOperationException){}
            assert(b.Writes.Count==0,"预检查期间发生外部启动仍写入");
        });
        await test("文件名准备后连线变化不触发录制",async()=>
        {
            var b=new Backend();int reads=0;Task<XDocument> Read(){if(++reads==3)Configuration.Modules(b.Doc).Single(m=>m.Name=="output_top").Element.Descendants("attr").Single(a=>(string?)a.Attribute("key")=="from"&&a.Value=="right[events]").Value="left[events]";return b.Read();}
            try{await RecordingControl.Start(Read,b.Batch,["output_top","output_bottom"],Storage.NewFolder("recording-graph-race"),probe:_=>{});throw new Exception("连线变化仍启动");}catch(InvalidOperationException){}
            assert(b.Writes.All(w=>w.Key!="running"),"启动前没有再次核验连线");
        });
        await test("联合启动及单独停止不影响其他模块",async()=>
        {
            var b=new Backend();var start=await RecordingControl.Start(b.Read,b.Batch,["output_top","output_bottom"],Storage.NewFolder("recording-start"),probe:_=>{});
            assert(RecordingControl.Inspect(start.Snapshot).Count(m=>m.Active=="true")==2,"未全部启动");
            await RecordingControl.Stop(b.Read,b.Batch,["output_top"],Storage.NewFolder("recording-stop"));var rows=RecordingControl.Inspect(b.Doc);
            assert(rows.Single(m=>m.Name=="output_top").Idle&&rows.Single(m=>m.Name=="output_bottom").Active=="true"&&rows.Single(m=>m.Name=="output_stereo").Idle,"停止影响未选择模块");
        });
        await test("部分启动失败停止本组且保留文件与未选录制",async()=>
        {
            var b=new Backend{FailStart="output_top"};StereoTests.Set(b.Doc,"output_stereo","running","true");StereoTests.Set(b.Doc,"output_stereo","isRunning","true");
            string folder=Storage.NewFolder("recording-partial");try{await RecordingControl.Start(b.Read,b.Batch,["output_top","output_bottom"],folder,probe:_=>{});throw new Exception("部分启动误报成功");}catch(InvalidOperationException){}
            var rows=RecordingControl.Inspect(b.Doc);assert(rows.Where(m=>m.Name!="output_stereo").All(m=>m.Idle)&&rows.Single(m=>m.Name=="output_stereo").Active=="true","失败清理影响非本组");
            assert(File.Exists(rows.Single(m=>m.Name=="output_bottom").File)&&File.ReadAllText(Path.Combine(folder,"recording-result.json")).Contains("failed-stopped"),"删除了文件或错误报告");
        });
        await test("启动回执丢失也核对停止本组",async()=>
        {
            var b=new Backend{LostStartAck=true};string folder=Storage.NewFolder("recording-lost-ack");try{await RecordingControl.Start(b.Read,b.Batch,["output_top","output_bottom"],folder,probe:_=>{});throw new Exception("回执丢失误报成功");}catch(InvalidOperationException){}
            assert(RecordingControl.Inspect(b.Doc).All(m=>m.Idle)&&b.Writes.Count(w=>w.Key=="running"&&w.Value=="false")==2,"未清理可能启动的模块");
        });
        await test("停止超时不假报完成也不结束进程",async()=>
        {
            var b=new Backend();await RecordingControl.Start(b.Read,b.Batch,["output_top"],Storage.NewFolder("recording-stop-failure-start"),probe:_=>{});b.FailStop="output_top";string folder=Storage.NewFolder("recording-stop-failure");
            try{await RecordingControl.Stop(b.Read,b.Batch,["output_top"],folder,timeoutMs:250);throw new Exception("停止超时误报成功");}catch(InvalidOperationException){}
            assert(File.ReadAllText(Path.Combine(folder,"recording-result.json")).Contains("unconfirmed")&&RecordingControl.Inspect(b.Doc).Single(m=>m.Name=="output_top").Active=="true","停止失败被隐藏");
        });
        await test("快速重录唯一文件名不叠加前缀且保留旧文件",async()=>
        {
            var b=new Backend();await RecordingControl.Start(b.Read,b.Batch,["output_top"],Storage.NewFolder("recording-name1"),probe:_=>{});string first=RecordingControl.Inspect(b.Doc).Single(m=>m.Name=="output_top").File;
            await RecordingControl.Stop(b.Read,b.Batch,["output_top"],Storage.NewFolder("recording-name-stop"));await RecordingControl.Start(b.Read,b.Batch,["output_top"],Storage.NewFolder("recording-name2"),probe:_=>{});var row=RecordingControl.Inspect(b.Doc).Single(m=>m.Name=="output_top");
            assert(first!=row.File&&File.Exists(first)&&File.Exists(row.File)&&RecordingControl.BasePrefix(row.Prefix)=="top_camera"&&row.Prefix.Split("__DVC_").Length==2,"文件名冲突或前缀累加");
        });
        await test("单相机录制不强制双目同步",async()=>
        {
            var b=new Backend();StereoTests.Set(b.Doc,"right","syncStatus","NO_SYNC",true);await RecordingControl.Start(b.Read,b.Batch,["output_top"],Storage.NewFolder("recording-mono"),probe:_=>{});assert(RecordingControl.Inspect(b.Doc).Single(m=>m.Name=="output_top").Active=="true","单机被错误阻止");
        });
        await test("多相机可显式不校验同步且不重置时钟",async()=>
        {
            var b=new Backend();StereoTests.Set(b.Doc,"right","syncStatus","NO_SYNC",true);await RecordingControl.Start(b.Read,b.Batch,["output_top","output_bottom"],Storage.NewFolder("recording-unsynced"),requireSync:false,probe:_=>{});assert(b.Writes.All(w=>w.Key is "prefix" or "running"),"修改了时钟或连线");
        });
        await test("录制源可经中间处理模块追溯相机",()=>
        {
            var b=new Backend();var left=Configuration.Modules(b.Doc).Single(m=>m.Name=="left");var filter=new XElement(left.Element);filter.SetAttributeValue("name","renamed_filter");filter.SetAttributeValue("path","/mainloop/renamed_filter/");filter.Elements("attr").Single(a=>(string?)a.Attribute("key")=="moduleLibrary").Value="dv_filter";
            filter.Add(new XElement("node",new XAttribute("name","inputs"),new XElement("node",new XAttribute("name","events"),new XElement("attr",new XAttribute("key","from"),new XAttribute("type","string"),"left[events]"))));left.Element.Parent!.Add(filter);
            Configuration.Modules(b.Doc).Single(m=>m.Name=="output_bottom").Element.Descendants("attr").Single(a=>(string?)a.Attribute("key")=="from"&&a.Value=="left[events]").Value="renamed_filter[events]";
            assert(RecordingControl.Plan(b.Doc,["output_bottom"]).Cameras.SequenceEqual(new[]{"left"}),"中间模块没有追溯到相机");return Task.CompletedTask;
        });
        await test("录制目录探测不会遗留临时文件",()=>{string folder=Storage.NewFolder("recording-directory");RecordingControl.ProbeDirectory(folder);assert(Directory.GetFiles(folder).Length==0,"写权限探测遗留文件");return Task.CompletedTask;});
        await test("批量请求在读取回执前已发送全部命令",async()=>
        {
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();try
            {
                int port=((IPEndPoint)listener.LocalEndpoint).Port;
                var server=Task.Run(async()=>
                {
                    using var client=await listener.AcceptTcpClientAsync();using var timeout=new CancellationTokenSource(4000);var stream=client.GetStream();var names=new List<string>();
                    for(int i=0;i<2;i++){byte[] p=new byte[4];await stream.ReadExactlyAsync(p,timeout.Token);byte[] data=new byte[BitConverter.ToInt32(p)];await stream.ReadExactlyAsync(data,timeout.Token);names.Add(DvProtocol.Decode(data).Node);}
                    for(int i=0;i<2;i++){var ack=DvProtocol.Encode(i==0?(byte)10:(byte)0,value:i==0?"":"rejected");await stream.WriteAsync(BitConverter.GetBytes(ack.Length),timeout.Token);await stream.WriteAsync(ack,timeout.Token);}return names;
                });
                var result=await DvProtocol.PutBatchAsync(port,[new(10,"/mainloop/one/","running","true",0),new(10,"/mainloop/two/","running","true",0)]);var names=await server;
                assert(result.Dispatched&&!result.Success&&result.Writes[0].Confirmed&&!result.Writes[1].Confirmed&&names.SequenceEqual(new[]{"/mainloop/one/","/mainloop/two/"}),"批量传输或部分失败处理错误");
            }finally{listener.Stop();}
        });
    }
}
