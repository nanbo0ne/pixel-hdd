using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace PixelHdd {
    static class Store {
        public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixelHdd");
        static readonly object LogGate = new object();
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        public static string PathOf(string name) { Directory.CreateDirectory(Root); return Path.Combine(Root, name); }
        public static void Save(string name, object value) {
            lock(LogGate) {
                string path = PathOf(name), tmp = path + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(value), new UTF8Encoding(false));
                if(File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            }
        }
        public static T Read<T>(string name) where T:new() {
            try { return Json.Deserialize<T>(File.ReadAllText(PathOf(name), Encoding.UTF8)); } catch { return new T(); }
        }
        public static void Log(string action, object detail) {
            lock(LogGate) {
                string path=PathOf("events.jsonl");
                if(File.Exists(path) && new FileInfo(path).Length>8*1024*1024){string previous=path+".previous";if(File.Exists(previous))File.Delete(previous);File.Move(path,previous);}
                File.AppendAllText(path, Json.Serialize(new {at=DateTimeOffset.Now.ToString("o"),action,detail})+Environment.NewLine,Encoding.UTF8);
            }
        }
        public static string Extract(string resource, string file) {
            byte[] bytes;
            using(Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)) {
                if(stream == null) throw new IOException("缺少内嵌资源 " + resource);
                using(var memory = new MemoryStream()) { stream.CopyTo(memory); bytes=memory.ToArray(); }
            }
            string path=PathOf("bin-26.03.2\\"+file); Directory.CreateDirectory(Path.GetDirectoryName(path));
            using(var hash=SHA256.Create()) {
                string wanted=BitConverter.ToString(hash.ComputeHash(bytes));
                if(!File.Exists(path) || BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path)))!=wanted) {
                    File.WriteAllBytes(path+".new",bytes);
                    if(File.Exists(path)) File.Replace(path+".new",path,null); else File.Move(path+".new",path);
                }
            }
            return path;
        }
    }
    public class Preferences { public bool OffValidated=false; public bool AutoStart=false; public int X=-1; public int Y=-1; public string DeviceSerial=""; }
    public class RecoveryRecord { public string device=""; public string requested=""; public string stage=""; public bool offline; public string at=""; public int? temperature; public string temperatureAt=""; }
    public class CounterSession { public string Boot=""; public string Device=""; public string FirstSeen=""; public ulong RawRead; public ulong RawWrite; public ulong ReadOffset; public ulong WriteOffset; public bool Reset; }
    public class DiskIdentity { public int Number; public bool Offline; public string Serial; public string Id; public long Size; }
    public class DiskStats {
        public ulong Read; public ulong Written; public long Timestamp; public double ReadRate; public double WriteRate;
        public bool Available; public bool Reset; public string Origin="Windows 磁盘会话累计";
    }
    public class Status {
        public string Mode="识别中"; public string Detail="正在连接硬盘"; public bool Offline; public int DiskNumber;
        public int? Temperature; public DateTime TemperatureAt; public DateTime ConfirmedAt; public bool Busy;
        public DiskStats Stats=new DiskStats();
    }
    static class Native {
        static SafeFileHandle powerHandle;
        static int powerNumber=-1;
        public static void PreparePowerHandle(int number){if(powerHandle==null || powerHandle.IsInvalid || powerNumber!=number){if(powerHandle!=null)powerHandle.Dispose();powerHandle=Open(@"\\.\PhysicalDrive"+number);powerNumber=number;}}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern SafeFileHandle CreateFile(string p,uint a,uint s,IntPtr sec,uint disposition,uint flags,IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)]
        static extern bool DeviceIoControl(SafeFileHandle h,uint code,IntPtr input,int size,IntPtr output,int outputSize,out int returned,IntPtr overlapped);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,int m,IntPtr w,IntPtr l);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        public static SafeFileHandle Open(string path) {
            var handle=CreateFile(path,0xC0000000,3,IntPtr.Zero,3,0,IntPtr.Zero);
            if(handle.IsInvalid) { int error=Marshal.GetLastWin32Error();handle.Dispose();throw new System.ComponentModel.Win32Exception(error,"无法取得磁盘访问权限"); }
            return handle;
        }
        public static void Control(SafeFileHandle handle,uint code) {
            int returned;if(!DeviceIoControl(handle,code,IntPtr.Zero,0,IntPtr.Zero,0,out returned,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public static string CurrentVolumePath=@"\\?\Volume{b42cd3d9-9162-4faa-8936-9bae7f2d1f2f}";
        public static SafeFileHandle LockVolume() { return LockVolume(CurrentVolumePath,"该卷"); }
        public static SafeFileHandle LockVolume(string volumePath,string root) {
            if(string.IsNullOrWhiteSpace(volumePath)) volumePath=CurrentVolumePath;
            var h=Open(volumePath.TrimEnd('\\'));
            try { Control(h,0x90018);return h; } catch(Exception ex) {h.Dispose();throw new IOException((root.Length>0?root:"该卷")+" 仍被占用，无法安全关闭。请结束正在使用的任务。"+ex.Message,ex);}
        }
        public static void Offline(int number,bool offline) {
            using(var h=Open(@"\\.\PhysicalDrive"+number)) {
                IntPtr memory=Marshal.AllocHGlobal(40);
                try {
                    Marshal.Copy(new byte[40],0,memory,40);Marshal.WriteInt32(memory,40);
                    // Persistent offline attribute survives an application crash; recovery remains available.
                    Marshal.WriteByte(memory,4,1);Marshal.WriteInt64(memory,8,offline?1:0);Marshal.WriteInt64(memory,16,1);
                    int returned;
                    if(!DeviceIoControl(h,0x7C0F4,memory,40,IntPtr.Zero,0,out returned,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"磁盘联机状态设置失败");
                } finally { Marshal.FreeHGlobal(memory); }
            }
        }
        static readonly object AtaGate=new object();
        // Register layout follows Seagate opensea-transport ata_SF_EPC_Go_To_Power_Condition.
        // No timer or persistent feature settings are modified.
        public static byte[] Ata(int number,byte command,byte feature=0,byte count=0,byte low=0) {
            lock(AtaGate) {
                PreparePowerHandle(number);IntPtr data=Marshal.AllocHGlobal(48);
                try {
                    Marshal.Copy(new byte[48],0,data,48);Marshal.WriteInt16(data,48);Marshal.WriteInt32(data,12,45);
                    Marshal.WriteByte(data,40,feature);Marshal.WriteByte(data,41,count);Marshal.WriteByte(data,42,low);
                    Marshal.WriteByte(data,45,0xE0);Marshal.WriteByte(data,46,command);
                    int returned;
                    if(!DeviceIoControl(powerHandle,0x4D02C,data,48,data,48,out returned,IntPtr.Zero))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"ATA 指令失败");
                    byte[] regs=new byte[8];Marshal.Copy(IntPtr.Add(data,40),regs,0,8);
                    if((regs[6]&0x21)!=0)throw new IOException("ATA 指令返回错误: "+BitConverter.ToString(regs));
                    return regs;
                }finally{Marshal.FreeHGlobal(data);}
            }
        }
        public static byte[] CheckPower(int number){return Ata(number,0xE5);}
        public static void TransitionPower(int number,string mode) {
            byte[] result;
            if(mode=="active")result=Ata(number,0xE1); // IDLE IMMEDIATE wakes spindle; does not change EPC timers.
            else if(mode=="idle_b")result=Ata(number,0xEF,0x4A,0x82,1);
            else if(mode=="standby_z")result=Ata(number,0xEF,0x4A,0,1);
            else throw new ArgumentException("Unsupported power mode");
            Store.Log("native-transition",new {mode,registers=BitConverter.ToString(result)});
        }
        public static bool IsOffline(int number) {
            PreparePowerHandle(number);IntPtr p=Marshal.AllocHGlobal(16);
            try {int returned;if(!DeviceIoControl(powerHandle,0x700F0,IntPtr.Zero,0,p,16,out returned,IntPtr.Zero))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return (Marshal.ReadInt64(p,8)&1)!=0;}finally{Marshal.FreeHGlobal(p);}
        }
        public static Tuple<ulong,ulong> Counters(int number) {
            PreparePowerHandle(number);IntPtr p=Marshal.AllocHGlobal(88);
            try {int returned;if(!DeviceIoControl(powerHandle,0x70020,IntPtr.Zero,0,p,88,out returned,IntPtr.Zero))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return Tuple.Create(unchecked((ulong)Marshal.ReadInt64(p,0)),unchecked((ulong)Marshal.ReadInt64(p,8)));}finally{Marshal.FreeHGlobal(p);}
        }
    }
    sealed class DiskTarget {
        public int Number; public string Serial="", Id="", Model="";
        public long Size; public bool Offline; public ushort BusType;
        public string Root="", VolumePath="";
        public long PartOffset, PartSize;
        public string Label { get { return "磁盘 "+Number+"  ·  "+(Model.Length>0?Model:"未知型号")+"  ·  "+Controller.Human(Size); } }
    }
    sealed class Controller {
        public const string LegacyId="5000C50074445A8C";
        public const string TargetId=LegacyId;
        public const string TargetSerial=LegacySerial;
        public const long TargetSize=LegacySize;
        public const string LegacySerial="ZRT0E133";
        public const long LegacySize=12000138625024L;
        public readonly DiskTarget Target;
        public readonly Status State=new Status();
        public readonly object Gate=new object();
        public Preferences Preferences;
        readonly string powerTool,smartTool;
        DateTime lastTemp=DateTime.MinValue,lastPower=DateTime.MinValue;
        DateTime lastCounterSave=DateTime.MinValue;
        CounterSession counterSession=new CounterSession();
        string chosenMode="开启";
        int transitionPending;
        internal Action TestBeforeStandby;
        bool deferredOffline;
        public Controller():this(null){}
        public Controller(DiskTarget target) {
            Preferences=Store.Read<Preferences>("preferences.json");
            powerTool=Store.Extract("PowerControl","openSeaChest_PowerControl.exe");
            smartTool=Store.Extract("Smart","openSeaChest_SMART.exe");
            Store.Extract("License","Seagate-LICENSE.txt");
            Target=target??Resolve();
        }
        // ATA power control works over the Windows storage stack for fixed ATA/SATA/SAS disks.
        // Removable, virtual and the boot/system disk are never offered.
        public static List<DiskTarget> Enumerate() {
            var list=new List<DiskTarget>();
            using(var search=new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage","SELECT * FROM MSFT_Disk")) {
                foreach(ManagementObject disk in search.Get()) using(disk) {
                    try {
                        if(Convert.ToBoolean(disk["IsBoot"])||Convert.ToBoolean(disk["IsSystem"])) continue;
                        ushort bus=disk["BusType"]==null?(ushort)0:Convert.ToUInt16(disk["BusType"]);
                        // ATA pass-through only has meaning on SCSI/ATA/SATA/SAS/RAID members;
                        // USB, SD, virtual, NVMe and UFS buses are excluded.
                        if(bus!=1&&bus!=3&&bus!=8&&bus!=10&&bus!=11) continue;
                        list.Add(new DiskTarget {
                            Number=Convert.ToInt32(disk["Number"]),
                            Serial=(Convert.ToString(disk["SerialNumber"])??"").Trim(),
                            Id=(Convert.ToString(disk["UniqueId"])??"").Trim(),
                            Model=(Convert.ToString(disk["FriendlyName"])??"").Trim(),
                            Size=Convert.ToInt64(disk["Size"]),
                            Offline=Convert.ToBoolean(disk["IsOffline"]),
                            BusType=bus
                        });
                    } catch(Exception ex) { Store.Log("enumerate-skip",ex.Message); }
                }
            }
            foreach(var target in list) {
                try {
                    int count=0; string volume="", root="";
                    using(var parts=new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage","SELECT * FROM MSFT_Partition WHERE DiskNumber="+target.Number)) {
                        foreach(ManagementObject part in parts.Get()) using(part) {
                            count++;
                            var paths=part["AccessPaths"] as string[];
                            if(paths!=null) foreach(var path in paths) {
                                if(path.StartsWith(@"\\?\Volume{",StringComparison.OrdinalIgnoreCase)) volume=path;
                                else if(path.Length>=2 && path[1]==':') root=path.Substring(0,3);
                            }
                        }
                    }
                    if(count==1) { target.VolumePath=volume; target.Root=root; }
                } catch(Exception ex) { Store.Log("enumerate-volume",ex.Message); }
            }
            list.Sort((a,b)=>a.Number.CompareTo(b.Number));
            return list;
        }
        static DiskTarget Resolve() {
            List<DiskTarget> all;
            try { all=Enumerate(); } catch(Exception ex) { Store.Log("enumerate-failed",ex.Message); return null; }
            if(all.Count==0) return null;
            string wanted=Store.Read<Preferences>("preferences.json").DeviceSerial;
            foreach(var target in all) if(target.Serial==wanted) return target;
            return all[0];
        }
        public static string Human(double n){string[] units={"B","KB","MB","GB","TB"};int i=0;while(n>=1024&&i<4){n/=1024;i++;}return n.ToString(i<2?"0":"0.00")+" "+units[i];}
        public DiskIdentity Identify() {
            if(Target==null) throw new IOException("没有找到可控制的硬盘");
            using(var search=new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage","SELECT * FROM MSFT_Disk WHERE Number="+Target.Number)) {
                var matches=new List<DiskIdentity>();
                foreach(ManagementObject disk in search.Get()) using(disk) {
                    if(Convert.ToInt32(disk["Number"])!=Target.Number) continue;
                    string serial=Convert.ToString(disk["SerialNumber"]).Trim();
                    string id=Convert.ToString(disk["UniqueId"]).Trim();
                    long size=Convert.ToInt64(disk["Size"]);
                    if(Convert.ToBoolean(disk["IsBoot"])||Convert.ToBoolean(disk["IsSystem"])) throw new IOException("系统盘受保护，已阻止控制");
                    if(Target.Serial.Length>0 && serial!=Target.Serial) throw new IOException("硬盘序列号与绑定记录不一致，已阻止操作");
                    if(Target.Id.Length>0 && id!=Target.Id) throw new IOException("硬盘身份与绑定记录不一致，已阻止操作");
                    if(Target.Size!=0 && size!=Target.Size) throw new IOException("硬盘容量与绑定记录不一致，已阻止操作");
                    matches.Add(new DiskIdentity { Number=Target.Number, Offline=Convert.ToBoolean(disk["IsOffline"]), Serial=serial, Id=id, Size=size });
                }
                if(matches.Count!=1) throw new IOException("找不到绑定的硬盘，或出现多个匹配设备");
                var result=matches[0];
                if(Target.Root.Length>2) {
                    using(var pages=new ManagementObjectSearcher("SELECT Name FROM Win32_PageFileUsage")) foreach(ManagementObject page in pages.Get()) using(page)
                        if(Convert.ToString(page["Name"]).StartsWith(Target.Root.Substring(0,2),StringComparison.OrdinalIgnoreCase)) throw new IOException("该卷含分页文件，已阻止控制");
                }
                using(var parts=new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage","SELECT * FROM MSFT_Partition WHERE DiskNumber="+result.Number)) {
                    int count=0; long offset=0, size=0;
                    foreach(ManagementObject part in parts.Get()) using(part) {
                        count++; offset=Convert.ToInt64(part["Offset"]); size=Convert.ToInt64(part["Size"]);
                    }
                    if(count!=1) throw new IOException("分区布局变化，已阻止操作");
                    if(Target.PartOffset==0 && Target.PartSize==0) { Target.PartOffset=offset; Target.PartSize=size; }
                    else if(offset!=Target.PartOffset || size!=Target.PartSize) throw new IOException("分区布局变化，已阻止操作");
                }
                State.DiskNumber=result.Number;State.Offline=result.Offline;return result;
            }
        }
        public string Tool(string executable,string args,int seconds) {
            var info=new ProcessStartInfo(executable,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Store.Root};
            using(var p=Process.Start(info)) {
                Task<string> stdout=p.StandardOutput.ReadToEndAsync(),stderr=p.StandardError.ReadToEndAsync();
                if(!p.WaitForExit(seconds*1000)) {
                    try {p.Kill();p.WaitForExit(3000);}catch{}
                    Store.Log("tool-timeout",new {args});throw new TimeoutException("硬盘指令超时，状态需重新确认");
                }
                string result=stdout.Result+stderr.Result;
                Store.Log("tool",new {tool=Path.GetFileName(executable),args,exit=p.ExitCode,output=result});
                if(p.ExitCode!=0) throw new IOException("硬盘指令失败（"+p.ExitCode+"），请查看日志");
                return result;
            }
        }
        public string Power(int number,string arguments) {return Tool(powerTool,"-d PD"+number+" "+arguments,45);}
        public string ReadPower(int number) {
            byte[] regs=Native.CheckPower(number);Store.Log("ata-check-power",new {registers=BitConverter.ToString(regs)});string state;
            if(regs[1]==0)state="关闭";
            else if(regs[1]==0x82)state="待机";
            else if(regs[1]==0x80 || regs[1]==0xFF || regs[1]==0x81 || regs[1]==0x83 || regs[1]==0x40 || regs[1]==0x41)state="开启";
            else throw new IOException("电源状态返回未知代码 "+regs[1]);
            State.ConfirmedAt=DateTime.Now;lastPower=DateTime.Now;State.Mode=state;return state;
        }
        public void Sample() {
            if(!Monitor.TryEnter(Gate)) return;
            try {
                if(Target==null){State.Stats.Available=false;State.Detail="没有找到可控制的硬盘";return;}
                var stats=State.Stats;bool found=false;
                if(deferredOffline){stats.Available=false;stats.ReadRate=stats.WriteRate=0;SaveLive();return;}
                {
                        var counters=Native.Counters(State.DiskNumber);
                        ulong read=counters.Item1,written=counters.Item2;long now=Stopwatch.GetTimestamp();
                        double dt=stats.Timestamp==0?0:(double)(now-stats.Timestamp)/Stopwatch.Frequency;
                        bool reset=read<counterSession.RawRead || written<counterSession.RawWrite;
                        if(reset){counterSession.ReadOffset+=counterSession.RawRead;counterSession.WriteOffset+=counterSession.RawWrite;counterSession.Reset=true;Store.Log("counter-reset",new {read,written});}
                        stats.ReadRate=dt>0&&!reset?(read-counterSession.RawRead)/dt:0;
                        stats.WriteRate=dt>0&&!reset?(written-counterSession.RawWrite)/dt:0;
                        counterSession.RawRead=read;counterSession.RawWrite=written;
                        stats.Read=read+counterSession.ReadOffset;stats.Written=written+counterSession.WriteOffset;stats.Timestamp=now;stats.Available=true;found=true;
                        stats.Origin=counterSession.Reset?"计数重置已衔接 · 重置间隔可能有缺口":"系统计数启用起点未知 · 首次观测 "+counterSession.FirstSeen;
                        if((DateTime.Now-lastCounterSave).TotalSeconds>=15){SaveCounters();lastCounterSave=DateTime.Now;}
                }
                if(!found){stats.Available=false;stats.ReadRate=stats.WriteRate=0;}
                if((DateTime.Now-lastPower).TotalSeconds>=30) {
                    ReadPower(State.DiskNumber);
                    State.Offline=Native.IsOffline(State.DiskNumber);
                    if(State.Offline && State.Mode!="关闭")State.Detail="访问已隔离，但检测到外部设备唤醒";
                }
                if(!State.Offline && chosenMode=="开启") {
                    if(State.Mode=="开启" && (DateTime.Now-lastTemp).TotalSeconds>=60) ReadTemperature();
                }
                SaveLive();
            } catch(Exception ex){State.Stats.Available=false;State.Stats.ReadRate=State.Stats.WriteRate=0;State.Detail="监控暂不可用 · 最后确认 "+State.ConfirmedAt.ToString("HH:mm:ss")+" · "+ex.Message;Store.Log("sample-error",ex.Message);} finally {Monitor.Exit(Gate);}
        }
        string CountersFile{get{return "counters-"+(Target==null?"none":Target.Serial)+".json";}}
        string RecoveryFile{get{return "recovery-"+(Target==null?"none":Target.Serial)+".json";}}
        void SaveCounters(){Store.Save(CountersFile,counterSession);}
        void SaveLive(){var stats=State.Stats;Store.Save("live-status.json",new {at=DateTimeOffset.Now.ToString("o"),device=Target==null?"":Target.Serial,State.Mode,State.Offline,State.DiskNumber,State.Temperature,temperatureAt=State.TemperatureAt.ToString("o"),confirmedAt=State.ConfirmedAt.ToString("o"),readBytes=stats.Read,writtenBytes=stats.Written,readRate=stats.ReadRate,writeRate=stats.WriteRate,stats.Available,chosenMode,deferredOffline});}
        public void ReadTemperature() {
            lastTemp=DateTime.Now;
            try {
                string output=Tool(smartTool,"-d PD"+State.DiskNumber+" --smartAttributes hybrid",25);
                foreach(string line in output.Split('\n')) {
                    if(line.IndexOf("Temperature",StringComparison.OrdinalIgnoreCase)<0) continue;
                    var match=System.Text.RegularExpressions.Regex.Match(line,@"\b194\s+Temperature\s+\S+\s+\d+\s+\d+\s+\S+\s+(\d+)\s*\(");
                    if(match.Success) {
                        int value=Int32.Parse(match.Groups[1].Value);
                        if(value>0 && value<100){State.Temperature=value;State.TemperatureAt=DateTime.Now;return;}
                    }
                }
                State.Detail="温度数据解析未通过，已保留最近有效值";
            }catch(Exception ex){Store.Log("temperature-error",ex.Message);}
        }
        public void Initialize() {
            lock(Gate) {
                if(Target==null){State.Mode="无设备";State.Detail="没有找到可控制的硬盘";return;}
                Preferences.DeviceSerial=Target.Serial;Store.Save("preferences.json",Preferences);
                string boot="";using(var os=new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"))foreach(ManagementObject row in os.Get())using(row){boot=Convert.ToString(row["LastBootUpTime"]);}
                var saved=Store.Read<CounterSession>(CountersFile);
                if(saved.Boot.Length==0 && File.Exists(Store.PathOf("counters.json"))) {
                    var legacy=Store.Read<CounterSession>("counters.json");
                    if(legacy.Device==LegacyId || legacy.Device==Target.Id || Target.Serial==LegacySerial) saved=legacy;
                }
                counterSession=saved.Boot==boot&&(saved.Device==Target.Id||saved.Device=="")?saved:new CounterSession{Boot=boot};
                counterSession.Device=Target.Id;if(string.IsNullOrEmpty(counterSession.FirstSeen))counterSession.FirstSeen=DateTime.Now.ToString("MM-dd HH:mm");
                var recovery=Store.Read<RecoveryRecord>(RecoveryFile);
                if(recovery.device.Length==0 && File.Exists(Store.PathOf("recovery.json"))) {
                    var legacy=Store.Read<RecoveryRecord>("recovery.json");
                    if(legacy.device==Target.Id || Target.Serial==LegacySerial) recovery=legacy;
                }
                if(recovery.device==Target.Id&&recovery.requested=="关闭"&&recovery.stage=="stable"&&recovery.offline){
                    deferredOffline=true;chosenMode="关闭";State.Mode="关闭记录";State.Offline=true;State.DiskNumber=-1;
                    DateTimeOffset at;if(DateTimeOffset.TryParse(recovery.at,out at))State.ConfirmedAt=at.LocalDateTime;
                    State.Temperature=recovery.temperature;if(DateTimeOffset.TryParse(recovery.temperatureAt,out at))State.TemperatureAt=at.LocalDateTime;
                    State.Stats.Read=counterSession.RawRead+counterSession.ReadOffset;State.Stats.Written=counterSession.RawWrite+counterSession.WriteOffset;
                    State.Stats.Origin="上次记录 · "+State.ConfirmedAt.ToString("MM-dd HH:mm:ss")+" · 本次未查询设备";
                    State.Detail="上次已关闭 · 本次未重新查询 · 点开启恢复";return;
                }
                var disk=Identify();chosenMode=disk.Offline?"关闭":"开启";
                if(disk.Offline){if(ReadPower(disk.Number)!="关闭"){Native.TransitionPower(disk.Number,"standby_z");Thread.Sleep(2000);ReadPower(disk.Number);}State.Detail="磁盘已脱机，可点开启恢复";}
                else {ReadPower(disk.Number);ReadTemperature();State.Detail="就绪 · "+Target.Model;}
            }
        }
        public void SetMode(string mode,bool test=false) {
            if(Interlocked.CompareExchange(ref transitionPending,1,0)!=0)throw new IOException("状态切换进行中，请勿重复操作");
            try {
            lock(Gate) {
                if(State.Busy) throw new IOException("状态切换进行中");
                if(Target==null) throw new IOException("没有找到可控制的硬盘");
                State.Busy=true;
                bool rollbackAttempted=false;
                try {
                    var disk=Identify();deferredOffline=false;Store.Save(RecoveryFile,new {device=Target.Id,requested=mode,stage="begin",at=DateTimeOffset.Now.ToString("o")});
                    SaveCounters();
                    State.Detail="正在"+(mode=="关闭"?"安全关闭":mode=="待机"?"进入待机":"唤醒");
                    if(mode=="开启") {
                        var until=DateTime.UtcNow.AddSeconds(60);
                        Native.TransitionPower(disk.Number,"active");
                        if(disk.Offline) Native.Offline(disk.Number,false);
                        bool ready=false;
                        while(DateTime.UtcNow<until) {
                            var current=Identify();
                            if(!current.Offline && (Target.Root.Length<2 || Directory.Exists(Target.Root))) {ready=true;break;}
                            Thread.Sleep(1000);
                        }
                        if(!ready)throw new TimeoutException("60 秒内未恢复卷访问，请重试开启并查看日志");
                        chosenMode="开启";ReadPower(disk.Number);ReadTemperature();State.Detail="已恢复访问";
                    } else if(mode=="待机") {
                        if(disk.Offline) throw new IOException("请先开启硬盘，再选择待机");
                        Native.TransitionPower(disk.Number,"idle_b");
                        chosenMode="待机";string actual="";
                        for(int attempt=0;attempt<8;attempt++){Thread.Sleep(1000);actual=ReadPower(disk.Number);if(actual=="待机")break;}
                        if(actual!="待机")throw new IOException("硬盘未保持 Idle_b，可能有程序正在访问");
                        State.Detail="磁头已卸载 · 盘片继续旋转";
                    } else if(mode=="关闭") {
                        if(!Preferences.OffValidated && !test)throw new IOException("关闭模式正在等待本机往返验证");
                        if(disk.Offline){if(ReadPower(disk.Number)!="关闭"){Native.TransitionPower(disk.Number,"standby_z");if(ReadPower(disk.Number)!="关闭")throw new IOException("未确认停转");}chosenMode="关闭";State.Detail="主轴已停转 · 仅手动开启恢复";Store.Save(RecoveryFile,new {device=Target.Id,requested=mode,stage="stable",offline=true,at=DateTimeOffset.Now.ToString("o"),temperature=State.Temperature,temperatureAt=State.TemperatureAt.ToString("o")});return;}
                        var before=ReadCounters(disk.Number);Thread.Sleep(3000);var after=ReadCounters(disk.Number);
                        if(before.Item1!=after.Item1 || before.Item2!=after.Item2)throw new IOException("该盘仍有读写，请等待任务结束后重试");
                        using(var locked=Native.LockVolume(Target.VolumePath,Target.Root)) {
                            Store.Save(RecoveryFile,new {device=Target.Id,requested=mode,stage="locked"});
                            try {
                                Native.Offline(disk.Number,true);State.Offline=true;
                                if(TestBeforeStandby!=null)TestBeforeStandby();
                                Native.TransitionPower(disk.Number,"standby_z");
                                Thread.Sleep(2000);
                                if(ReadPower(disk.Number)!="关闭")throw new IOException("未确认主轴停转");
                            } catch {
                                try {Native.TransitionPower(disk.Number,"active");Native.Offline(disk.Number,false);State.Offline=false;chosenMode="开启";rollbackAttempted=true;}catch(Exception rollback){Store.Log("rollback-error",rollback.Message);}
                                throw;
                            }
                        }
                        if(!Native.IsOffline(disk.Number))throw new IOException("访问隔离未保持，请重新确认状态");
                        chosenMode="关闭";State.Detail="主轴已停转 · 仅手动开启恢复";
                    } else throw new ArgumentException("未知模式");
                    Store.Save(RecoveryFile,new {device=Target.Id,requested=mode,stage="stable",offline=State.Offline,at=DateTimeOffset.Now.ToString("o"),temperature=State.Temperature,temperatureAt=State.TemperatureAt.ToString("o")});
                    Store.Log("transition-complete",new {device=Target.Serial,requested=mode,actual=State.Mode,State.Offline});
                } catch(Exception ex){
                    if(rollbackAttempted){
                        var until=DateTime.UtcNow.AddSeconds(60);while(DateTime.UtcNow<until&&(Target.Root.Length<2||!Directory.Exists(Target.Root)))Thread.Sleep(500);
                        if(Target.Root.Length<2||Directory.Exists(Target.Root)){ReadPower(State.DiskNumber);Store.Save(RecoveryFile,new {device=Target.Id,requested="开启",stage="stable",offline=false,at=DateTimeOffset.Now.ToString("o")});}
                        else Store.Log("rollback-volume-timeout","Online requested but volume not ready");
                    }
                    State.Detail=ex.Message;Store.Log("transition-error",new {mode,error=ex.ToString()});throw;
                }
                finally{State.Busy=false;}
            }
            } finally {Interlocked.Exchange(ref transitionPending,0);}
        }
        public static Tuple<ulong,ulong> ReadCounters(int number) {
            return Native.Counters(number);
        }
        public void Probe() {
            Initialize();Store.Save("probe.json",new {device=Target==null?"":Target.Serial,identity=Target==null?null:Identify(),mode=State.Mode,temperature=State.Temperature,stats=Target==null?null:ReadCounters(State.DiskNumber)});
            File.WriteAllText(Store.PathOf("smart-help.txt"),Tool(smartTool,"--help",30),Encoding.UTF8);
        }
        public void SetAutoStart(bool enabled) {
            string task="PixelHdd-"+((Target==null||Target.Serial.Length==0)?LegacySerial:Target.Serial);
            if(enabled) {
                string escaped=System.Security.SecurityElement.Escape(Application.ExecutablePath);
                string user=System.Security.SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
                string xml="<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Triggers><LogonTrigger><Enabled>true</Enabled><UserId>"+user+"</UserId></LogonTrigger></Triggers><Principals><Principal id=\"Author\"><UserId>"+user+"</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings><Actions Context=\"Author\"><Exec><Command>"+escaped+"</Command><Arguments>--tray</Arguments></Exec></Actions></Task>";
                string file=Store.PathOf("startup-task.xml");File.WriteAllText(file,xml,Encoding.Unicode);
                Tool(Path.Combine(Environment.SystemDirectory,"schtasks.exe"),"/Create /TN \""+task+"\" /XML \""+file+"\" /F",20);
            } else {
                Tool(Path.Combine(Environment.SystemDirectory,"schtasks.exe"),"/Delete /TN \""+task+"\" /F",20);
            }
            Preferences.AutoStart=enabled;Store.Save("preferences.json",Preferences);
        }
    }
}
