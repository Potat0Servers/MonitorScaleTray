using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MonitorScaleTray {
    internal sealed class MonitorInfo {
        public string Name, DevicePath, Source, Error;
        public Native.Luid Adapter;
        public uint SourceId;
        public int Current;
        public int[] Supported = new int[0];
    }
    internal static class Native {
        public static readonly int[] Scales = {100,125,150,175,200,225,250,300,350,400,450,500};
        [StructLayout(LayoutKind.Sequential)] public struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] public struct Header { public int Type; public uint Size; public Luid Adapter; public uint Id; }
        [StructLayout(LayoutKind.Sequential)] public struct SourceInfo { public Luid Adapter; public uint Id, Mode, Status; }
        [StructLayout(LayoutKind.Sequential)] public struct Rational { public uint Numerator, Denominator; }
        [StructLayout(LayoutKind.Sequential)] public struct TargetInfo {
            public Luid Adapter; public uint Id, Mode, Technology, Rotation, Scaling; public Rational Refresh;
            public uint Scanline; public int Available; public uint Status;
        }
        [StructLayout(LayoutKind.Sequential)] public struct DisplayPath { public SourceInfo Source; public TargetInfo Target; public uint Flags; }
        [StructLayout(LayoutKind.Explicit, Size=64)] public struct ModeInfo { [FieldOffset(0)] public uint Type; }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct TargetName {
            public Header Header; public uint Flags, Technology; public ushort Manufacturer, Product; public uint Connector;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=64)] public string Friendly;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Path;
        }
        [StructLayout(LayoutKind.Sequential)] public struct DpiGet { public Header Header; public int Min, Current, Max; }
        [StructLayout(LayoutKind.Sequential)] public struct DpiSet { public Header Header; public int Relative; }
        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] DisplayPath[] pathArray, ref uint modes, [Out] ModeInfo[] modeArray, IntPtr topology);
        [DllImport("user32.dll", EntryPoint="DisplayConfigGetDeviceInfo")] static extern int GetName(ref TargetName value);
        [DllImport("user32.dll", EntryPoint="DisplayConfigGetDeviceInfo")] static extern int GetDpi(ref DpiGet value);
        [DllImport("user32.dll", EntryPoint="DisplayConfigSetDeviceInfo")] static extern int SetDpi(ref DpiSet value);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        static Header MakeHeader<T>(int type, Luid adapter, uint id) { return new Header {Type=type, Size=(uint)Marshal.SizeOf(typeof(T)), Adapter=adapter, Id=id}; }
        static void Check(int result) { if (result != 0) throw new Win32Exception(result); }
        public static int ScaleIndex(DpiGet dpi) {
            if (dpi.Current == 1234568) throw new InvalidOperationException("Windows 自定义缩放已启用，请在系统显示设置中关闭自定义缩放并重新登录，再应用逐屏配置。");
            long basis = -(long)dpi.Min, last = basis + dpi.Max, current = basis + dpi.Current;
            if (dpi.Min > 0 || dpi.Max < 0 || basis >= Scales.Length || last >= Scales.Length || current < 0 || current > last)
                throw new InvalidOperationException("系统缩放范围无法识别（min="+dpi.Min+", current="+dpi.Current+", max="+dpi.Max+"）。");
            return (int)basis;
        }
        static DpiGet ReadDpi(MonitorInfo monitor) {
            var request = new DpiGet { Header=MakeHeader<DpiGet>(-3, monitor.Adapter, monitor.SourceId) };
            Check(GetDpi(ref request)); ScaleIndex(request); return request;
        }
        public static List<MonitorInfo> Enumerate() {
            for (int attempt=0; attempt<5; attempt++) {
                uint count, modeCount; Check(GetDisplayConfigBufferSizes(2, out count, out modeCount));
                var paths = new DisplayPath[count]; var modes = new ModeInfo[modeCount];
                int result=QueryDisplayConfig(2, ref count, paths, ref modeCount, modes, IntPtr.Zero);
                if (result == 122) continue;
                Check(result); var monitors=new List<MonitorInfo>();
                for (int i=0; i<count; i++) {
                    var path=paths[i]; var name=new TargetName {Header=MakeHeader<TargetName>(2,path.Target.Adapter,path.Target.Id)};
                    Check(GetName(ref name));
                    var monitor=new MonitorInfo {Name=String.IsNullOrWhiteSpace(name.Friendly) ? "显示器 " + path.Target.Id : name.Friendly.Trim(),
                        DevicePath=name.Path, Adapter=path.Source.Adapter, SourceId=path.Source.Id,
                        Source=path.Source.Adapter.High + ":" + path.Source.Adapter.Low + ":" + path.Source.Id};
                    try { var dpi=ReadDpi(monitor); int basis=ScaleIndex(dpi); monitor.Current=Scales[basis+dpi.Current]; monitor.Supported=Scales.Take(basis+dpi.Max+1).ToArray(); }
                    catch(Exception ex) {
                        monitor.Error=ex.Message;
                        using(var desktop=Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop")) {
                            if(desktop!=null && Convert.ToInt32(desktop.GetValue("Win8DpiScaling",0))==1) monitor.Current=(int)Math.Round(Convert.ToInt32(desktop.GetValue("LogPixels",96))*100.0/96.0);
                        }
                    }
                    monitors.Add(monitor);
                }
                return monitors;
            }
            throw new InvalidOperationException("显示器正在切换，请稍后刷新。");
        }
        public static string Apply(MonitorInfo monitor, int percent, bool force=false) {
            var dpi=ReadDpi(monitor); int basis=ScaleIndex(dpi), index=Array.IndexOf(Scales,percent);
            if (index < 0 || index > basis+dpi.Max) throw new InvalidOperationException("该显示器不支持 " + percent + "%（支持：" + String.Join(", ",monitor.Supported) + "）。");
            if (!force && index == basis+dpi.Current) return "已符合 " + percent + "%";
            var request=new DpiSet { Header=MakeHeader<DpiSet>(-4,monitor.Adapter,monitor.SourceId), Relative=index-basis };
            Check(SetDpi(ref request));
            var actual=ReadDpi(monitor);
            if (Scales[ScaleIndex(actual)+actual.Current] != percent) throw new InvalidOperationException("系统未确认目标缩放值，请检查自定义缩放设置。");
            return "已应用 " + percent + "%";
        }
    }
}
