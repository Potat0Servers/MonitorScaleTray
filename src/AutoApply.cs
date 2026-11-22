using System;
using System.Collections.Generic;
using System.Linq;

namespace MonitorScaleTray {
    // UI-thread scheduler; native calls are injected so event handling can be tested without changing displays.
    internal sealed class AutoApply {
        readonly Func<List<MonitorInfo>> enumerate;
        readonly Func<Configuration> configuration;
        readonly Action<List<MonitorInfo>,string> apply;
        readonly Func<DateTime> clock;
        readonly Action<Exception> report;
        DateTime nextCheck=DateTime.MaxValue, lastAttempt=DateTime.MinValue;
        int checksLeft;
        string lastFingerprint="";
        bool running;
        public AutoApply(Func<List<MonitorInfo>> enumerate,Func<Configuration> configuration,Action<List<MonitorInfo>,string> apply,Func<DateTime> clock,Action<Exception> report) {
            this.enumerate=enumerate; this.configuration=configuration; this.apply=apply; this.clock=clock; this.report=report;
        }
        public void Trigger(string reason,bool startup=false,bool distinctEvent=false) {
            Check(reason,startup,false,distinctEvent);
        }
        void Followups() {
            if(nextCheck==DateTime.MaxValue) {checksLeft=2; nextCheck=clock().AddSeconds(2);}
        }
        public void Tick() {
            if(clock()<nextCheck) return;
            checksLeft--; nextCheck=checksLeft>0?clock().AddSeconds(4):DateTime.MaxValue;
            Check("事件后复查",false,true,false);
        }
        public static bool NeedsApply(Configuration config,List<MonitorInfo> monitors) {
            return monitors.Any(m=> {
                string key=Store.Match(config,m,monitors);
                return key!=null && (m.Error!=null || m.Current!=config.displays[key]);
            });
        }
        void Check(string reason,bool startup,bool followup,bool distinctEvent) {
            if(running) return;
            running=true;
            try {
                var monitors=enumerate(); var config=configuration();
                string fingerprint=String.Join("|",monitors.Select(m=>m.DevicePath+":"+m.Source+":"+m.Current+":"+m.Error).OrderBy(s=>s))+
                    "#"+String.Join("|",config.displays.OrderBy(p=>p.Key).Select(p=>p.Key+"="+p.Value))+"#"+
                    String.Join("|",config.devicePaths.OrderBy(p=>p.Key).Select(p=>p.Key+"="+p.Value));
                // Ignore duplicate notifications emitted by our own setter; timed checks can still retry failures.
                if(!startup && !distinctEvent && !followup && fingerprint==lastFingerprint && clock()-lastAttempt<TimeSpan.FromSeconds(10)) return;
                if(!followup) Followups();
                if(!startup && !NeedsApply(config,monitors)) return;
                lastFingerprint=fingerprint; lastAttempt=clock();
                apply(monitors,reason);
            } catch(Exception ex) {if(!followup) Followups(); report(ex);}
            finally {running=false;}
        }
    }
}
