using Microsoft.Win32;
using System.Text.Json;

namespace DVCompanion;

// Only the official external-runtime preferences are leased. Other GUI settings stay untouched.
public sealed class PreferencesLease:IDisposable
{
    const string KeyPath=@"Software\JavaSoft\Prefs\inivation\dv";
    public record Entry(string Name,string? Before,bool Existed,string Applied);
    public record Journal(string State,List<Entry> Entries);
    readonly string journal;List<Entry> entries=[];bool restored;
    public PreferencesLease(string sessionFolder,int port)
    {
        journal=Path.Combine(sessionFolder,"preferences-lease.json");using var key=Registry.CurrentUser.CreateSubKey(KeyPath,true);
        foreach(var pair in new Dictionary<string,string>{{"manage_local_instances","false"},{"default_credentials",JsonSerializer.Serialize(new{address="127.0.0.1",port})}})
        {
            bool exists=key.GetValueNames().Contains(pair.Key);var old=key.GetValue(pair.Key);
            if(old!=null&&old is not string)throw new InvalidDataException("DV 偏好值类型不符合预期，未修改："+pair.Key);
            entries.Add(new(pair.Key,old as string,exists,pair.Value));
        }
        Storage.WriteJson(journal,new Journal("pending",entries));
        try{foreach(var e in entries)key.SetValue(e.Name,e.Applied,RegistryValueKind.String);key.Flush();}catch{Dispose();throw;}
    }
    static List<string> Restore(string file,Journal data)
    {
        var notes=new List<string>();using var key=Registry.CurrentUser.CreateSubKey(KeyPath,true);
        foreach(var e in data.Entries)
        {
            if(e.Name is not("manage_local_instances" or "default_credentials"))throw new InvalidDataException("偏好恢复记录包含未允许字段");
            if(!Equals(key.GetValue(e.Name),e.Applied)){notes.Add("保留会话期间另行更改的设置："+e.Name);continue;}
            if(e.Existed)key.SetValue(e.Name,e.Before??"",RegistryValueKind.String);else key.DeleteValue(e.Name,false);
        }
        key.Flush();Storage.WriteJson(file,new Journal("restored",data.Entries));return notes;
    }
    public static List<string> RecoverPending()
    {
        var notes=new List<string>();string dir=Path.Combine(Storage.Root,"sessions");if(!Directory.Exists(dir))return notes;
        foreach(string file in Directory.EnumerateFiles(dir,"preferences-lease.json",SearchOption.AllDirectories))
        {
            if(new FileInfo(file).Length>16384){notes.Add("跳过异常恢复记录："+file);continue;}
            try{var data=JsonSerializer.Deserialize<Journal>(File.ReadAllText(file));if(data?.State=="pending"){notes.AddRange(Restore(file,data));notes.Add("已恢复上次异常退出前的 DV 连接偏好。");}}catch(Exception ex){notes.Add("偏好恢复失败："+ex.Message);}
        }
        return notes;
    }
    public void Dispose(){if(restored)return;Restore(journal,new Journal("pending",entries));restored=true;}
}
