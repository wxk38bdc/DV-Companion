using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace DVCompanion;

// Wire layout follows upstream dv_config_action_data.fbs (Apache-2.0).
// Only loopback endpoints discovered for the selected process are accepted.
public record WireMessage(byte Action, string Node, string Key, string Value, sbyte Type,int Flags=0);
public record BatchWriteStatus(string Node,string Key,string Value,bool Confirmed,string Error,double AcknowledgedMs);
public record BatchWriteResult(bool Dispatched,double ElapsedMs,IReadOnlyList<BatchWriteStatus> Writes,string TransportError)
{
    public bool Success=>Dispatched&&TransportError.Length==0&&Writes.Count>0&&Writes.All(w=>w.Confirmed);
}
public static class DvProtocol
{
    public static async Task<BatchWriteResult> PutBatchAsync(int port,IReadOnlyList<WireMessage> writes,int timeoutMs=6000)
    {
        if(port is <1 or >65535||writes.Count is <1 or >64||writes.Any(w=>w.Action!=10))throw new ArgumentException("批量写入参数无效");
        using var payload=new MemoryStream();foreach(var write in writes){var bytes=Encode(10,write.Node,write.Key,write.Value,write.Type,write.Flags);payload.Write(BitConverter.GetBytes(bytes.Length));payload.Write(bytes);}
        if(payload.Length>2_000_000)throw new InvalidDataException("批量请求过大");
        using var client=new TcpClient(AddressFamily.InterNetwork){NoDelay=true};using var timeout=new CancellationTokenSource(timeoutMs);
        var watch=System.Diagnostics.Stopwatch.StartNew();var results=new List<BatchWriteStatus>();bool dispatched=false;string error="";
        try
        {
            await client.ConnectAsync(IPAddress.Loopback,port,timeout.Token);var stream=client.GetStream();
            // One buffered write queues all commands without waiting for individual round trips.
            // The DV server still applies each PUT separately; this is not an atomic barrier.
            dispatched=true;await stream.WriteAsync(payload.ToArray(),timeout.Token);
            int total=0;
            foreach(var write in writes)
            {
                byte[] size=new byte[4];await stream.ReadExactlyAsync(size,timeout.Token);int n=BitConverter.ToInt32(size);
                if(n<8||n>8_000_000||(total+=n)>24_000_000)throw new InvalidDataException("批量响应超过限制");
                byte[] data=new byte[n];await stream.ReadExactlyAsync(data,timeout.Token);var reply=Decode(data);
                bool confirmed=reply.Action==10;results.Add(new(write.Node,write.Key,write.Value,confirmed,confirmed?"":reply.Action==0?reply.Value:"DV 响应类型不符",watch.Elapsed.TotalMilliseconds));
            }
        }
        catch(Exception ex)when(ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException or InvalidDataException)
        {error=ex is OperationCanceledException?"批量请求超时；部分指令可能已执行，必须读取后台核对。":ex.Message;}
        foreach(var write in writes.Skip(results.Count))results.Add(new(write.Node,write.Key,write.Value,false,"未获得确认："+error,watch.Elapsed.TotalMilliseconds));
        return new(dispatched,watch.Elapsed.TotalMilliseconds,results,error);
    }
    static void U16(byte[] b,int p,int v)=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p,2),(ushort)v);
    static void I32(byte[] b,int p,int v)=>BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(p,4),v);
    static int I32(byte[] b,int p) { Check(b,p,4); return BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p,4)); }
    static void Check(byte[] b,int p,int n) { if(p<0 || n<0 || p>b.Length-n) throw new InvalidDataException("DV 协议数据越界"); }
    public static byte[] Encode(byte action,string node="",string key="",string value="",sbyte type=-1,int flags=0)
    {
        var bytes=new List<byte>(new byte[88]);
        var offsets=new Dictionary<int,int>();
        foreach(var pair in new[]{(slot:4,text:node),(slot:5,text:key),(slot:7,text:value)})
        {
            while(bytes.Count%4!=0) bytes.Add(0);
            offsets[pair.slot]=bytes.Count;
            var s=Encoding.UTF8.GetBytes(pair.text);
            bytes.AddRange(BitConverter.GetBytes(s.Length)); bytes.AddRange(s); bytes.Add(0);
        }
        var b=bytes.ToArray(); I32(b,0,40); Encoding.ASCII.GetBytes("CFGA").CopyTo(b,4);
        U16(b,8,26); U16(b,10,48); I32(b,40,32); b[44]=action; b[64]=255;
        b[64]=unchecked((byte)type);I32(b,76,flags);
        var fields=new Dictionary<int,int>{{0,4},{4,16},{5,20},{6,24},{7,28},{9,36}};
        foreach(var f in fields) U16(b,12+f.Key*2,f.Value);
        foreach(var f in offsets){int p=40+fields[f.Key];I32(b,p,f.Value-p);}
        return b;
    }
    public static WireMessage Decode(byte[] b)
    {
        Check(b,0,8); if(Encoding.ASCII.GetString(b,4,4)!="CFGA") throw new InvalidDataException("不是 DV 配置协议");
        int t=I32(b,0); Check(b,t,4);int v=checked(t-I32(b,t));Check(b,v,4);
        int len=BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(v,2));Check(b,v,len);
        int Field(int n){int p=v+4+n*2;if(p+2>v+len)return 0;int off=BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p,2));if(off==0)return 0;int at=checked(t+off);Check(b,at,1);return at;}
        string Str(int n){int p=Field(n);if(p==0)return "";int s=checked(p+I32(b,p));int size=I32(b,s);Check(b,s+4,size);if(size>4_000_000)throw new InvalidDataException("DV 文本过大");return Encoding.UTF8.GetString(b,s+4,size);}
        int a=Field(0),ty=Field(6);
        int fl=Field(9);return new(a==0?(byte)0:b[a],Str(4),Str(5),Str(7),ty==0?(sbyte)-1:unchecked((sbyte)b[ty]),fl==0?0:I32(b,fl));
    }
    public static async Task<List<WireMessage>> CallAsync(int port,byte action,string node="",string key="",string value="",int timeoutMs=3500,sbyte type=-1,int flags=0)
    {
        if(port is <1 or >65535)throw new ArgumentOutOfRangeException(nameof(port));
        using var cts=new CancellationTokenSource(timeoutMs);using var client=new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback,port,cts.Token);
            var stream=client.GetStream();var payload=Encode(action,node,key,value,type,flags);var prefix=BitConverter.GetBytes(payload.Length);
            await stream.WriteAsync(prefix,cts.Token);await stream.WriteAsync(payload,cts.Token);
            var result=new List<WireMessage>();int total=0;
            for(int i=0;i<15000;i++)
            {
                var size=new byte[4];await stream.ReadExactlyAsync(size,cts.Token);int n=BitConverter.ToInt32(size);
                if(n<8 || n>8_000_000 || (total+=n)>24_000_000)throw new InvalidDataException("DV 响应超过限制");
                var b=new byte[n];await stream.ReadExactlyAsync(b,cts.Token);var msg=Decode(b);
                if(msg.Action==0)throw new InvalidOperationException("DV 返回错误："+msg.Value);
                result.Add(msg);if(action!=17 || msg.Action==17)return result;
            }
            throw new InvalidDataException("DV 配置节点超过限制");
        }
        catch(OperationCanceledException){throw new TimeoutException("DV 后台未在规定时间内响应。请先保存诊断，再尝试恢复。");}
        catch(IOException)when(action==10&&node=="/system/"&&key=="running"&&value=="false"){return [];}
    }
    public static async Task<XDocument> SnapshotAsync(int port,int timeoutMs=6000)
    {
        var replies=await CallAsync(port,17,timeoutMs:timeoutMs);
        var root=new XElement("dv",new XAttribute("version","2.0"));
        var nodes=new Dictionary<string,XElement>();
        XElement Node(string path)
        {
            if(nodes.TryGetValue(path,out var existing))return existing;
            string name=path.Trim('/').Split('/').LastOrDefault()??"";
            var elem=new XElement("node",new XAttribute("name",name),new XAttribute("path",path));nodes[path]=elem;
            if(path=="/")root.Add(elem);
            else{string trimmed=path.TrimEnd('/');int i=trimmed.LastIndexOf('/');Node(i<=0?"/":trimmed[..(i+1)]).Add(elem);}
            return elem;
        }
        string[] types=["bool","int","long","float","double","string"];
        foreach(var m in replies)
        {
            if(m.Action==18)Node(m.Node);
            else if(m.Action==19)Node(m.Node).Add(new XElement("attr",new XAttribute("key",m.Key),new XAttribute("type",m.Type>=0&&m.Type<types.Length?types[m.Type]:"string"),m.Value));
        }
        return new XDocument(root);
    }
}
