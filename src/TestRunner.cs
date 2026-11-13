using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
namespace PixelHdd {
    static class TestRunner {
        static readonly List<object> Checks=new List<object>();
        static void Check(string name,bool pass,object detail){Checks.Add(new {name,pass,detail,at=DateTimeOffset.Now.ToString("o")});Store.Save("test-progress.json",Checks);if(!pass)throw new Exception("Test failed: "+name);}
        static void Main(string[] args) {if(args.Contains("--sleep")){Thread.Sleep(5000);return;}if(args.Contains("--fail")){Environment.Exit(7);return;}if(args.Contains("--supplemental")||args.Contains("--leave-off")){Supplemental(args);return;}Run(new Controller(),args);}
        static void Supplemental(string[] args){
            var c=new Controller();bool leaving=false;
            try{
                c.Initialize();
                if(args.Contains("--leave-off")){
                    c.SetMode("关闭",true);Check("actual_process_exit_offline",Native.IsOffline(c.State.DiskNumber)&&c.ReadPower(c.State.DiskNumber)=="关闭",c.State.Mode);leaving=true;
                    Store.Save("exit-offline.json",new {passed=true,checks=Checks,at=DateTimeOffset.Now.ToString("o")});return;
                }
                Check("actual_process_reopen_offline",c.State.Offline&&c.State.Mode=="关闭记录",c.State.Mode);
                c.SetMode("开启",true);Thread.Sleep(5000);
                c.TestBeforeStandby=()=>{throw new IOException("Intentional pre-standby fault for rollback test");};bool failure=false;
                try{c.SetMode("关闭",true);}catch(IOException){failure=true;}finally{c.TestBeforeStandby=null;}
                Check("pre_standby_failure_rollback",failure&&!Native.IsOffline(c.State.DiskNumber)&&Directory.Exists("I:\\"),c.State.Mode);
                var pending=typeof(Controller).GetField("transitionPending",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);pending.SetValue(c,1);bool rejected=false;
                try{c.SetMode("开启",true);}catch(IOException){rejected=true;}finally{pending.SetValue(c,0);}
                Check("duplicate_transition_rejected",rejected,"No disk command sent");
                c.Sample();Check("final_online",!Native.IsOffline(c.State.DiskNumber)&&Directory.Exists("I:\\"),c.State.Mode);
                Store.Save("supplemental.json",new {passed=true,checks=Checks,at=DateTimeOffset.Now.ToString("o")});
            }catch(Exception ex){Store.Save("supplemental.json",new {passed=false,error=ex.ToString(),checks=Checks});}
            finally{if(!leaving)try{c.SetMode("开启",true);}catch(Exception ex){Store.Log("supplemental-restore-failure",ex.ToString());}}
        }
        public static void Run(Controller c,string[] args) {
            bool restored=false;
            try {
                c.Initialize();Check("identity",c.Identify().Id==Controller.TargetId,c.Identify());
                Check("temperature",c.State.Temperature.HasValue,new {c.State.Temperature});
                if(args.Contains("--probe-only"))return;
                var contentsBefore=Directory.GetFileSystemEntries("I:\\").Select(Path.GetFileName).OrderBy(s=>s,StringComparer.OrdinalIgnoreCase).ToArray();
                var before=Controller.ReadCounters(c.State.DiskNumber);
                string temporary="I:\\PixelHdd-test-"+Guid.NewGuid().ToString("N")+".tmp";
                try {
                    byte[] block=new byte[1024*1024];new Random(213).NextBytes(block);
                    using(var f=new FileStream(temporary,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,block.Length,FileOptions.WriteThrough)){
                        for(int i=0;i<32;i++)f.Write(block,0,block.Length);f.Flush(true);
                        bool denied=false;try {using(var locked=Native.LockVolume()){} }catch(IOException){denied=true;}
                        Check("open_file_lock_refused",denied,new {temporary});
                    }
                    using(var f=new FileStream(temporary,FileMode.Open,FileAccess.Read,FileShare.Read,block.Length,FileOptions.SequentialScan)){while(f.Read(block,0,block.Length)>0){}}
                    var after=Controller.ReadCounters(c.State.DiskNumber);
                    Check("counters_monotonic",after.Item1>=before.Item1 && after.Item2>=before.Item2,new {readDelta=after.Item1-before.Item1,writeDelta=after.Item2-before.Item2});
                } finally{if(File.Exists(temporary))File.Delete(temporary);}
                Thread.Sleep(5000);
                c.SetMode("待机",true);Check("idle_b",c.State.Mode=="待机",c.State.Mode);
                Thread.Sleep(2000);Check("idle_query_does_not_load_heads",c.ReadPower(c.State.DiskNumber)=="待机",c.State.Mode);
                c.SetMode("开启",true);Check("idle_to_active",!c.Identify().Offline && Directory.Exists("I:\\"),c.State.Mode);
                Thread.Sleep(5000);
                c.SetMode("关闭",true);Check("standby_offline",Native.IsOffline(c.State.DiskNumber) && c.State.Mode=="关闭",new {c.State.Mode,c.State.Offline});
                var newController=new Controller();newController.Initialize();Check("reopen_offline_recovery",newController.State.Offline,newController.State.Mode);
                var stop=Stopwatch.StartNew();int samples=0;
                while(stop.Elapsed.TotalSeconds<610){
                    c.Sample();samples++;
                    if(File.Exists(Store.PathOf("cancel-test")))throw new OperationCanceledException("测试被取消");
                    if(samples%30==0){string mode=c.ReadPower(c.State.DiskNumber);Check("standby_hold_"+samples,mode=="关闭" && Native.IsOffline(c.State.DiskNumber),new {elapsed=stop.Elapsed.TotalSeconds,mode});}
                    Thread.Sleep(1000);
                }
                Check("ten_minute_hold",true,new {elapsed=stop.Elapsed.TotalSeconds,samples});
                c.SetMode("开启",true);restored=true;
                var contentsAfter=Directory.GetFileSystemEntries("I:\\").Select(Path.GetFileName).OrderBy(s=>s,StringComparer.OrdinalIgnoreCase).ToArray();
                Check("root_metadata_names_preserved",contentsBefore.SequenceEqual(contentsAfter),new {before=contentsBefore.Length,after=contentsAfter.Length});
                Check("restored_online",!c.Identify().Offline && Directory.Exists("I:\\"),c.State.Mode);
                bool oldAuto=c.Preferences.AutoStart;
                try {
                    c.SetAutoStart(true);
                    string task=c.Tool(Path.Combine(Environment.SystemDirectory,"schtasks.exe"),"/Query /TN PixelHdd-EXOS-ZRT0E133 /XML",20);
                    Check("autostart_task_registered",task.Contains("--tray") && task.Contains("HighestAvailable"),"Task XML verified");
                } finally {if(!oldAuto)c.SetAutoStart(false);}
                string tester=Path.Combine(Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath),"TestRunner.exe");
                bool timeout=false;try{c.Tool(tester,"--sleep",1);}catch(TimeoutException){timeout=true;}Check("command_timeout",timeout,"Own child process only");
                bool failure=false;try{c.Tool(tester,"--fail",5);}catch(IOException){failure=true;}Check("command_failure",failure,"Own child process only");
                var pending=typeof(Controller).GetField("transitionPending",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
                if(pending!=null){
                    pending.SetValue(c,1);bool rejected=false;
                    try{c.SetMode("开启",true);}catch(IOException){rejected=true;}finally{pending.SetValue(c,0);}
                    Check("duplicate_transition_rejected",rejected,"Rejected before any disk command");
                }
                c.Preferences.OffValidated=true;Store.Save("preferences.json",c.Preferences);
                Store.Save("acceptance.json",new {passed=true,checks=Checks,completed=DateTimeOffset.Now.ToString("o")});
            }catch(Exception ex){Store.Save("acceptance.json",new {passed=false,error=ex.ToString(),checks=Checks});}
            finally{
                if(!args.Contains("--probe-only") && !restored){try{c.SetMode("开启",true);Store.Log("test-finally-restored",true);}catch(Exception ex){Store.Log("test-restore-failure",ex.ToString());}}
                Store.Save("test-exit.json",new {completed=DateTimeOffset.Now.ToString("o"),restored});
            }
        }
    }
}
