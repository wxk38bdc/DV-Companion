using System.Xml.Linq;
namespace DVCompanion;

internal static class SaveTests
{
    public static async Task Run(Func<string,Func<Task>,Task> test,Action<bool,string> assert)
    {
        XDocument Fixture()=>Configuration.Parse("""
            <dv version="2.0"><node name="mainloop" path="/mainloop/">
              <node name="left"><attr key="moduleLibrary" type="string">dv_davis</attr><attr key="serialNumber" type="string">10000001</attr><attr key="syncWith" type="string">right</attr><attr key="_guiSupport" type="string">{"x":10,"y":20}</attr></node>
              <node name="right"><attr key="moduleLibrary" type="string">dv_davis</attr><attr key="serialNumber" type="string">10000002</attr><attr key="syncWith" type="string"></attr></node>
              <node name="writer"><attr key="moduleLibrary" type="string">dv_output_file</attr><attr key="running" type="bool">true</attr><node name="inputs"><node name="output0"><attr key="from" type="string">left[events]</attr></node></node></node>
            </node></dv>
            """);
        await test("连续保存重新读取当前结构并保留同步关系与布局",async()=>
        {
            string target=Path.Combine(Storage.NewFolder("save-current"),"project.xml");
            var live=Fixture();int reads=0;
            Task<XDocument> Read(){reads++;return Task.FromResult(new XDocument(live));}
            await ProjectSave.Current(target,Read);
            string first=File.ReadAllText(target);
            var modules=Configuration.Modules(live);
            modules.Single(m=>m.Name=="left").Element.Elements("attr").Single(a=>(string?)a.Attribute("key")=="_guiSupport").Value="{\"x\":410,\"y\":220}";
            modules.Single(m=>m.Name=="writer").Element.Remove();
            live.Root!.Element("node")!.Add(Configuration.Parse("""
                <dv version="2.0"><node name="mainloop"><node name="new_filter"><attr key="moduleLibrary" type="string">dv_dvsnoisefilter</attr><node name="inputs"><node name="events"><attr key="from" type="string">right[events]</attr></node></node></node></node></dv>
                """).Root!.Element("node")!.Element("node"));
            var result=await ProjectSave.Current(target,Read);
            var saved=Configuration.Modules(Configuration.Parse(File.ReadAllText(target)));
            assert(reads==2&&saved.Any(m=>m.Name=="new_filter")&&!saved.Any(m=>m.Name=="writer"),"保存使用了旧结构");
            assert(saved.Single(m=>m.Name=="left").Get("syncWith")=="right"&&saved.Single(m=>m.Name=="left").Get("_guiSupport").Contains("410"),"同步关系或布局丢失");
            assert(saved.Single(m=>m.Name=="new_filter").Element.Descendants("attr").Any(a=>(string?)a.Attribute("key")=="from"&&a.Value=="right[events]"),"新连线丢失");
            assert(File.ReadAllText(result.Backup)==first&&File.ReadAllText(target)!=first,"覆盖备份或连续保存错误");
            assert(Configuration.Modules(live).Single(m=>m.Name=="left").Get("_guiSupport").Contains("410"),"保存修改了当前结构");
        });
        await test("读取当前 DV 失败时不覆盖文件也不创建旧副本",async()=>
        {
            string folder=Storage.NewFolder("save-failure"),target=Path.Combine(folder,"existing.xml"),absent=Path.Combine(folder,"new.xml");File.WriteAllText(target,"original");
            foreach(string path in new[]{target,absent})
            {
                try{await ProjectSave.Current(path,()=>Task.FromException<XDocument>(new TimeoutException("read failed")));throw new Exception("读取失败仍保存成功");}
                catch(TimeoutException){}
            }
            assert(File.ReadAllText(target)=="original"&&!File.Exists(absent)&&Directory.GetFiles(folder).Length==1,"失败时覆盖或创建了文件");
        });
        await test("当前结构无效时保留原文件",async()=>
        {
            string target=Path.Combine(Storage.NewFolder("save-invalid"),"existing.xml");File.WriteAllText(target,"original");
            var invalid=Fixture();Configuration.Modules(invalid).Single(m=>m.Name=="left").Element.Elements("attr").Single(a=>(string?)a.Attribute("key")=="syncWith").Value="missing";
            try{await ProjectSave.Current(target,()=>Task.FromResult(invalid));throw new Exception("无效配置被保存");}catch(InvalidDataException){}
            assert(File.ReadAllText(target)=="original","无效配置覆盖了原文件");
        });
        await test("保存当前结构只在文件副本中停止录制",async()=>
        {
            var live=Fixture();string target=Path.Combine(Storage.NewFolder("save-recording"),"project.xml");await ProjectSave.Current(target,()=>Task.FromResult(live));
            assert(Configuration.Modules(live).Single(m=>m.Writer).Get("running")=="true"&&Configuration.Modules(Configuration.Parse(File.ReadAllText(target))).Single(m=>m.Writer).Get("running")=="false","录制配置副本处理不正确");
        });
    }
}
