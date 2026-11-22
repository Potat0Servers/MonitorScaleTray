using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MonitorScaleTray {
    public sealed class Configuration {
        public Dictionary<string,int> displays {get;set;}
        public Dictionary<string,string> devicePaths {get;set;}
        public Configuration() { displays=new Dictionary<string,int>(); devicePaths=new Dictionary<string,string>(); }
        public void Validate() {
            if(displays==null || devicePaths==null) throw new InvalidDataException("配置缺少 displays 或 devicePaths。");
            var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var pair in displays) {
                if(String.IsNullOrWhiteSpace(pair.Key) || pair.Key != pair.Key.Trim() || !keys.Add(pair.Key)) throw new InvalidDataException("显示器名称不能为空、重复或带首尾空格。");
                if(!Native.Scales.Contains(pair.Value)) throw new InvalidDataException(pair.Key + " 的缩放值不在标准档位内。");
            }
            var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var pair in devicePaths) {
                if(!displays.ContainsKey(pair.Key) || String.IsNullOrWhiteSpace(pair.Value) || !paths.Add(pair.Value)) throw new InvalidDataException("设备绑定无效或重复："+pair.Key);
            }
        }
    }
    internal static class Store {
        public static readonly string Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonitorScaleTray");
        public static readonly string FilePath=Path.Combine(Folder,"config.json");
        public static Configuration Read(string path) {
            if(!File.Exists(path)) return new Configuration();
            var config=new JavaScriptSerializer().Deserialize<Configuration>(File.ReadAllText(path,Encoding.UTF8));
            if(config==null) throw new InvalidDataException("配置为空。");
            config.Validate(); return config;
        }
        public static void Save(Configuration config, string path) {
            config.Validate(); Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp=path+".tmp";
            string json=new JavaScriptSerializer().Serialize(config);
            File.WriteAllText(temp,json, new UTF8Encoding(false));
            if(File.Exists(path)) File.Replace(temp,path,path+".bak"); else File.Move(temp,path);
        }
        public static void Log(string message) {
            try { Directory.CreateDirectory(Folder); string path=Path.Combine(Folder,"activity.log");
                if(File.Exists(path) && new FileInfo(path).Length>1048576) File.Delete(path);
                File.AppendAllText(path,DateTime.Now.ToString("s")+" "+message+Environment.NewLine,Encoding.UTF8);
            } catch { }
        }
        public static string Match(Configuration config, MonitorInfo monitor, List<MonitorInfo> connected) {
            var bound=config.devicePaths.FirstOrDefault(p=>String.Equals(p.Value,monitor.DevicePath,StringComparison.OrdinalIgnoreCase));
            if(bound.Key!=null && config.displays.ContainsKey(bound.Key)) return bound.Key;
            // Manual names are only resolved when exactly one active monitor has this name.
            if(connected.Count(m=>String.Equals(m.Name,monitor.Name,StringComparison.OrdinalIgnoreCase))!=1) return null;
            return config.displays.Keys.FirstOrDefault(k=>!config.devicePaths.ContainsKey(k) && String.Equals(k,monitor.Name,StringComparison.OrdinalIgnoreCase));
        }
    }
}
