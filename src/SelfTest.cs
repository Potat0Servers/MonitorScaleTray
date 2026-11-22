using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace MonitorScaleTray {
    internal static class SelfTest {
        static void Assert(bool value,string label) {if(!value) throw new Exception(label);}
        static void CheckAutomaticApplication() {
            var config=new Configuration(); config.displays.Add("A",150); config.displays.Add("B",200);
            var a=new MonitorInfo {Name="A",DevicePath="a",Source="1",Current=150};
            var b=new MonitorInfo {Name="B",DevicePath="b",Source="2",Current=200};
            var monitors=new List<MonitorInfo>{a,b};
            DateTime now=new DateTime(2026,1,1); int writes=0, failures=0; bool readFailure=false,repair=true;
            AutoApply automatic=null;
            automatic=new AutoApply(()=> {if(readFailure) throw new IOException("display switching"); return monitors;},()=>config,(snapshot,reason)=> {
                writes++; Assert(Object.ReferenceEquals(snapshot,monitors),"Whole active configuration snapshot supplied");
                if(repair) {a.Current=150; b.Current=200;}
                automatic.Trigger("setter notification"); // Native setter can emit nested notifications.
            },()=>now,ex=>failures++);
            automatic.Trigger("startup",true); Assert(writes==1,"Startup applies immediately even when already compliant");
            now=now.AddSeconds(20); automatic.Trigger("display"); Assert(writes==1,"Compliant event causes no write");
            b.Current=125; automatic.Trigger("display"); Assert(writes==2 && b.Current==200,"One mismatch immediately reapplies current configuration");
            automatic.Trigger("self notification"); Assert(writes==2,"Successful setter notifications do not loop");
            now=now.AddSeconds(20); a.Current=100; automatic.Trigger("unlock",false,true); Assert(writes==3 && a.Current==150,"Unlock corrects mismatch immediately");
            a.Current=100; automatic.Trigger("resume",false,true); Assert(writes==4,"Distinct resume is not lost to event deduplication");
            now=now.AddSeconds(20); automatic.Tick(); now=now.AddSeconds(4); automatic.Tick();
            Assert(writes==4,"Stable followups do not rewrite");
            repair=false; a.Current=100; now=now.AddSeconds(20); automatic.Trigger("display"); int first=writes;
            automatic.Trigger("display"); Assert(writes==first,"Duplicate failed notifications coalesce");
            now=now.AddSeconds(2); automatic.Tick(); now=now.AddSeconds(4); automatic.Tick();
            Assert(writes==first+2,"At most two timed retries per event burst");
            automatic.Trigger("self notification"); now=now.AddSeconds(20); automatic.Tick();
            Assert(writes==first+2,"Setter notification does not restart exhausted retries");
            repair=true; readFailure=true; automatic.Trigger("unlock",false,true); Assert(failures==1,"Temporary enumeration failure reported");
            readFailure=false; now=now.AddSeconds(2); automatic.Tick(); Assert(writes==first+3 && a.Current==150,"Wake-up query failure recovers on followup");
            now=now.AddSeconds(4); automatic.Tick();
            monitors.Clear(); now=now.AddSeconds(20); automatic.Trigger("display");
            Assert(writes==first+3,"No active monitors do not cause writes");
            b.Current=100; monitors.Add(b); now=now.AddSeconds(2); automatic.Tick();
            Assert(writes==first+4 && b.Current==200,"External-only display appearing after switch is corrected");
            monitors.Add(new MonitorInfo {Name="Unconfigured",DevicePath="new",Current=100});
            Assert(!AutoApply.NeedsApply(config,monitors),"Unconfigured and offline displays ignored");
            b.Error="unavailable"; Assert(AutoApply.NeedsApply(config,monitors),"Unknown scaling is not mistaken for compliance");
        }
        public static int Run(string output) {
            string folder=Path.Combine(Path.GetTempPath(),"MonitorScaleTray-tests-"+Guid.NewGuid().ToString("N"));
            try {
                CheckAutomaticApplication();
                foreach(bool systemDark in new[]{false,true}) {
                    Assert(Theme.ResolveDark("auto",systemDark)==systemDark,"Automatic theme follows system");
                    Assert(!Theme.ResolveDark("light",systemDark),"Explicit light overrides system");
                    Assert(Theme.ResolveDark("dark",systemDark),"Explicit dark overrides system");
                }
                string appearance=Path.Combine(folder,"appearance.json");
                Theme.SavePreference(appearance,"dark"); Theme.SavePreference(appearance,"auto");
                Assert(File.ReadAllText(appearance).Contains("auto") && File.ReadAllText(appearance+".bak").Contains("dark"),"Theme persistence and backup");
                bool invalidMode=false; try {Theme.SavePreference(appearance,"invalid");} catch(ArgumentException) {invalidMode=true;}
                Assert(invalidMode && File.ReadAllText(appearance).Contains("auto"),"Invalid theme preserves saved preference");
                Assert(Marshal.SizeOf(typeof(Native.DisplayPath))==72,"Display path ABI");
                Assert(Marshal.SizeOf(typeof(Native.TargetName))==420,"Target name ABI");
                Assert(Marshal.SizeOf(typeof(Native.DpiGet))==32,"DPI get ABI");
                Assert(Marshal.SizeOf(typeof(Native.DpiSet))==24,"DPI set ABI");
                Assert(Marshal.SizeOf(typeof(Native.ModeInfo))==64,"Mode ABI");
                Assert(Native.ScaleIndex(new Native.DpiGet {Min=-2,Current=1,Max=5})==2,"Relative DPI mapping");
                bool rejected=false; try {Native.ScaleIndex(new Native.DpiGet {Min=Int32.MinValue,Max=1});} catch(InvalidOperationException) {rejected=true;}
                Assert(rejected,"Unknown DPI range rejected");
                var config=new Configuration(); config.displays.Add("测试显示器",150); config.devicePaths.Add("测试显示器","device-a");
                string path=Path.Combine(folder,"config.json"); Store.Save(config,path); Assert(Store.Read(path).displays["测试显示器"]==150,"Unicode JSON roundtrip");
                config.displays["测试显示器"]=175; Store.Save(config,path); Assert(Store.Read(path+".bak").displays["测试显示器"]==150,"Atomic backup");
                var a=new MonitorInfo {Name="同型号",DevicePath="device-a"}; var b=new MonitorInfo {Name="同型号",DevicePath="device-b"};
                var connected=new List<MonitorInfo> {a,b}; Assert(Store.Match(config,a,connected)=="测试显示器","Bound identity survives name changes");
                Assert(Store.Match(config,b,connected)==null,"Different same-model monitor isolated");
                config.displays.Add("同型号",125); Assert(Store.Match(config,b,connected)==null,"Ambiguous manual name rejected");
                connected.Remove(a); Assert(Store.Match(config,b,connected)=="同型号","External-only manual matching");
                config.displays.Add("invalid",133); rejected=false; try {config.Validate();} catch(InvalidDataException) {rejected=true;}
                Assert(rejected,"Unsupported percentage rejected");
                File.WriteAllText(path,"{broken"); rejected=false; try {Store.Read(path);} catch {rejected=true;}
                Assert(rejected && File.ReadAllText(path)=="{broken","Corrupt config preserved");
                File.WriteAllText(output,"PASS: immediate startup/display/unlock/resume application; compliant-event no-op; duplicate/reentrant notification suppression; bounded retries; transient query recovery; delayed external-only display; unmatched/offline exclusion; unreadable scale handling; light/dark overrides; automatic theme; preference persistence; native ABI; DPI mapping; JSON/backup; hardware binding; invalid config preservation.\r\n"); return 0;
            } catch(Exception ex) {File.WriteAllText(output,ex.ToString()); return 1;}
            finally {if(Directory.Exists(folder)) Directory.Delete(folder,true);}
        }
    }
}
