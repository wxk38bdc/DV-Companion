using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DVCompanion;

public record ProcessIdentity(int Pid,long StartedUtcTicks,string Path,int ParentPid)
{
    public bool Alive
    {
        get {try{using var p=Process.GetProcessById(Pid);return p.StartTime.ToUniversalTime().Ticks==StartedUtcTicks && string.Equals(Native.ProcessPath(Pid),Path,StringComparison.OrdinalIgnoreCase);}catch{return false;}}
    }
    public string Name=>System.IO.Path.GetFileName(Path);
    public static ProcessIdentity? Read(int pid,int parent=0)
    {try{using var p=Process.GetProcessById(pid);string path=Native.ProcessPath(pid);return string.IsNullOrEmpty(path)?null:new(pid,p.StartTime.ToUniversalTime().Ticks,path,parent);}catch{return null;}}
}
public record CameraDevice(string Name,string Serial,string InstanceId,uint Problem,bool Started)
{
    public string State=>Started && Problem==0?"Windows 已识别":"设备异常 / 错误码 "+Problem;
}
public static class Native
{
    public const uint CREATE_SUSPENDED=4,CREATE_NO_WINDOW=0x08000000;
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr h,int flags,StringBuilder name,ref int size);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    public static string ProcessPath(int pid)
    {
        var h=OpenProcess(0x1000,false,pid);if(h==IntPtr.Zero)return "";
        try{var b=new StringBuilder(32768);int n=b.Capacity;return QueryFullProcessImageName(h,0,b,ref n)?b.ToString():"";}finally{CloseHandle(h);}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct ProcessEntry
    {public uint Size,Usage,Pid;public UIntPtr Heap;public uint Module,Threads,Parent;public int Priority;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Exe;}
    [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern bool Process32FirstW(IntPtr snap,ref ProcessEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern bool Process32NextW(IntPtr snap,ref ProcessEntry entry);
    public static List<ProcessIdentity> DvProcesses(string root)
    {
        var list=new List<ProcessIdentity>();var h=CreateToolhelp32Snapshot(2,0);if(h==new IntPtr(-1))throw new Win32Exception();
        try{var e=new ProcessEntry{Size=(uint)Marshal.SizeOf<ProcessEntry>()};if(Process32FirstW(h,ref e))do{
            if(e.Exe.Equals("dv-runtime.exe",StringComparison.OrdinalIgnoreCase)||e.Exe.Equals("javaw.exe",StringComparison.OrdinalIgnoreCase)||e.Exe.Equals("java.exe",StringComparison.OrdinalIgnoreCase))
            {var p=ProcessIdentity.Read((int)e.Pid,(int)e.Parent);if(p!=null && IsUnder(p.Path,root))list.Add(p);}
        }while(Process32NextW(h,ref e));}finally{CloseHandle(h);}return list;
    }
    public static bool IsUnder(string file,string root)=>System.IO.Path.GetFullPath(file).StartsWith(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root))+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    [StructLayout(LayoutKind.Sequential)]struct DevInfo{public uint Size;public Guid Class;public uint DevInst;public UIntPtr Reserved;}
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr SetupDiGetClassDevsW(IntPtr cls,string? enumerator,IntPtr parent,uint flags);
    [DllImport("setupapi.dll",SetLastError=true)]static extern bool SetupDiEnumDeviceInfo(IntPtr set,uint index,ref DevInfo data);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set,ref DevInfo data,StringBuilder id,int size,out int required);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode)]static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set,ref DevInfo data,uint prop,out uint type,byte[] buffer,uint size,out uint required);
    [DllImport("setupapi.dll")]static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")]static extern uint CM_Get_DevNode_Status(out uint status,out uint problem,uint instance,uint flags);
    public static List<CameraDevice> Cameras()
    {
        var list=new List<CameraDevice>();var h=SetupDiGetClassDevsW(IntPtr.Zero,"USB",IntPtr.Zero,6);if(h==new IntPtr(-1))throw new Win32Exception();
        try{for(uint i=0;;i++){var d=new DevInfo{Size=(uint)Marshal.SizeOf<DevInfo>()};if(!SetupDiEnumDeviceInfo(h,i,ref d))break;var b=new StringBuilder(2048);if(!SetupDiGetDeviceInstanceIdW(h,ref d,b,b.Capacity,out _))continue;string id=b.ToString();if(!id.Contains("VID_152A",StringComparison.OrdinalIgnoreCase))continue;
            var buf=new byte[4096];string name="iniVation 相机";if(SetupDiGetDeviceRegistryPropertyW(h,ref d,12,out _,buf,(uint)buf.Length,out _)||SetupDiGetDeviceRegistryPropertyW(h,ref d,0,out _,buf,(uint)buf.Length,out _))name=Encoding.Unicode.GetString(buf).TrimEnd('\0');
            uint result=CM_Get_DevNode_Status(out uint state,out uint problem,d.DevInst,0);list.Add(new(name,id.Split('\\').Last(),id,result==0?problem:result,result==0&&(state&8)!=0));
        }}finally{SetupDiDestroyDeviceInfoList(h);}return list.GroupBy(x=>x.InstanceId,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();
    }
    [DllImport("iphlpapi.dll")]static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool order,int family,int tableClass,uint reserved);
    public static List<int> ListeningPorts(int pid)
    {
        int size=0;GetExtendedTcpTable(IntPtr.Zero,ref size,false,2,5,0);if(size<=0||size>16_000_000)return [];
        IntPtr ptr=Marshal.AllocHGlobal(size);try{if(GetExtendedTcpTable(ptr,ref size,false,2,5,0)!=0)return [];int count=Marshal.ReadInt32(ptr);var list=new List<int>();
            for(int i=0;i<count;i++){int o=4+i*24;if(o+24>size)break;int state=Marshal.ReadInt32(ptr,o),owner=Marshal.ReadInt32(ptr,o+20);uint ip=unchecked((uint)Marshal.ReadInt32(ptr,o+4));if(owner==pid&&state==2 && ip==0x0100007f){int p=o+8;list.Add((Marshal.ReadByte(ptr,p)<<8)|Marshal.ReadByte(ptr,p+1));}}return list.Distinct().ToList();
        }finally{Marshal.FreeHGlobal(ptr);}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]public struct StartupInfo
    {public uint cb;public string? reserved,desktop,title;public uint x,y,cx,cy,xChars,yChars,fill,flags;public ushort show,cbReserved;public IntPtr reserved2,input,output,error;}
    [StructLayout(LayoutKind.Sequential)]public struct ProcessInfo{public IntPtr Process,Thread;public uint Pid,Tid;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]public static extern bool CreateProcessW(string app,StringBuilder cmd,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr environment,string cwd,ref StartupInfo si,out ProcessInfo pi);
    [DllImport("kernel32.dll",SetLastError=true)]public static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll",SetLastError=true)]public static extern bool TerminateProcess(IntPtr process,uint code);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateJobObjectW(IntPtr attr,string? name);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetInformationJobObject(IntPtr job,int cls,IntPtr data,uint len);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool QueryInformationJobObject(IntPtr job,int cls,IntPtr data,uint len,out uint ret);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateJobObject(IntPtr job,uint code);
    [StructLayout(LayoutKind.Sequential)]struct BasicLimit{public long a,b;public uint flags;public UIntPtr min,max;public uint active;public UIntPtr affinity;public uint priority,scheduling;}
    [StructLayout(LayoutKind.Sequential)]struct IoCounters{public ulong a,b,c,d,e,f;}
    [StructLayout(LayoutKind.Sequential)]struct ExtendedLimit{public BasicLimit Basic;public IoCounters Io;public UIntPtr processMemory,jobMemory,peakProcess,peakJob;}
    public sealed class OwnedJob:IDisposable
    {
        IntPtr handle;
        public OwnedJob()
        {
            handle=CreateJobObjectW(IntPtr.Zero,null);if(handle==IntPtr.Zero)throw new Win32Exception();
            var limit=new ExtendedLimit{Basic=new BasicLimit{flags=0x2000}};int size=Marshal.SizeOf<ExtendedLimit>();var p=Marshal.AllocHGlobal(size);
            try{Marshal.StructureToPtr(limit,p,false);if(!SetInformationJobObject(handle,9,p,(uint)size))throw new Win32Exception();}catch{CloseHandle(handle);handle=IntPtr.Zero;throw;}finally{Marshal.FreeHGlobal(p);}
        }
        public ProcessIdentity Launch(string exe,IEnumerable<string> args,string cwd,bool hidden,Dictionary<string,string>? overrides=null)
        {
            if(handle==IntPtr.Zero)throw new ObjectDisposedException(nameof(OwnedJob));
            var si=new StartupInfo{cb=(uint)Marshal.SizeOf<StartupInfo>()};
            var cmd=new StringBuilder(string.Join(" ",new[]{exe}.Concat(args).Select(Quote)));
            IntPtr environment=IntPtr.Zero;
            if(overrides!=null){var values=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);foreach(System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())values[(string)e.Key]=(string)e.Value!;foreach(var e in overrides)values[e.Key]=e.Value;environment=Marshal.StringToHGlobalUni(string.Join('\0',values.Select(e=>e.Key+"="+e.Value))+"\0\0");}
            ProcessInfo pi;try{if(!CreateProcessW(exe,cmd,IntPtr.Zero,IntPtr.Zero,false,CREATE_SUSPENDED|0x400|(hidden?CREATE_NO_WINDOW:0),environment,cwd,ref si,out pi))throw new Win32Exception();}finally{if(environment!=IntPtr.Zero)Marshal.FreeHGlobal(environment);}
            try{if(!AssignProcessToJobObject(handle,pi.Process))throw new Win32Exception();if(ResumeThread(pi.Thread)==uint.MaxValue)throw new Win32Exception();return ProcessIdentity.Read((int)pi.Pid)??throw new InvalidOperationException("启动进程已退出");}
            catch{TerminateProcess(pi.Process,1);throw;}finally{CloseHandle(pi.Thread);CloseHandle(pi.Process);}
        }
        public List<ProcessIdentity> Members()
        {
            if(handle==IntPtr.Zero)return [];const int size=65536;var p=Marshal.AllocHGlobal(size);
            try{if(!QueryInformationJobObject(handle,3,p,size,out _))throw new Win32Exception();int count=Marshal.ReadInt32(p,4);var list=new List<ProcessIdentity>();for(int i=0;i<Math.Min(count,(size-8)/IntPtr.Size);i++){int pid=(int)Marshal.ReadIntPtr(p,8+i*IntPtr.Size);var id=ProcessIdentity.Read(pid);if(id!=null)list.Add(id);}return list;}finally{Marshal.FreeHGlobal(p);}
        }
        public void ForceEnd(){if(handle!=IntPtr.Zero&&!TerminateJobObject(handle,1))throw new Win32Exception();}
        public void Dispose(){if(handle!=IntPtr.Zero){CloseHandle(handle);handle=IntPtr.Zero;}}
    }
    public static string Quote(string value)
    {
        if(value.Length>0 && !value.Any(c=>char.IsWhiteSpace(c)||c=='"'))return value;
        var b=new StringBuilder("\"");int slashes=0;foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='"')b.Append('\\',slashes*2+1).Append('"');else b.Append('\\',slashes).Append(c);slashes=0;}return b.Append('\\',slashes*2).Append('"').ToString();
    }
    [DllImport("dbghelp.dll",SetLastError=true)]static extern bool MiniDumpWriteDump(IntPtr process,uint pid,IntPtr file,uint type,IntPtr exception,IntPtr user,IntPtr callback);
    public static void WriteMiniDump(ProcessIdentity p,string file)
    {
        if(!p.Alive)throw new InvalidOperationException("进程身份已改变");var h=OpenProcess(0x450,false,p.Pid);if(h==IntPtr.Zero)throw new Win32Exception();
        try{using var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None);if(!MiniDumpWriteDump(h,(uint)p.Pid,stream.SafeFileHandle.DangerousGetHandle(),0,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero))throw new Win32Exception();}finally{CloseHandle(h);}
    }
}
