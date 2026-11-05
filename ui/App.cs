using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;

namespace PhoneVPNDashboard
{
    public class Program : Application
    {
        [STAThread]
        public static void Main()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                CrashLog.Write(e.ExceptionObject != null ? e.ExceptionObject.ToString() : "Unknown exception");

            try
            {
                Program app = new Program();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                app.DispatcherUnhandledException += (s, e) =>
                {
                    CrashLog.Write(e.Exception != null ? e.Exception.ToString() : "Unknown dispatcher exception");
                    e.Handled = true;
                };
                app.Run(new DashboardWindow());
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex.ToString());
                MessageBox.Show("Phone-Tether-Tunnel Dashboard could not start:\n\n" + ex.Message +
                    "\n\nDetails: " + CrashLog.PathOnDisk, "Phone-Tether-Tunnel", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    internal static class CrashLog
    {
        public static string PathOnDisk
        {
            get { return Path.Combine(Prefs.DataDir, "crash.log"); }
        }

        public static void Write(string text)
        {
            try
            {
                Directory.CreateDirectory(Prefs.DataDir);
                File.AppendAllText(PathOnDisk, string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}\r\n\r\n", DateTime.Now, text));
            }
            catch { }
        }
    }

    /// <summary>Dashboard preferences (selected platform, folder overrides, board selection).</summary>
    internal class Prefs
    {
        public string Platform;
        public Dictionary<string, string> Folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> Selected = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public static string DataDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneTetherTunnel");
            }
        }

        private static string FilePath
        {
            get { return Path.Combine(DataDir, "dashboard.json"); }
        }

        public static Prefs Load()
        {
            Prefs p = new Prefs();
            Dictionary<string, object> d = Json.ReadFile(FilePath);
            if (d == null) return p;
            p.Platform = Json.Str(d, "platform");
            Dictionary<string, object> folders = Json.Obj(d, "folders");
            if (folders != null)
            {
                foreach (KeyValuePair<string, object> kv in folders)
                {
                    if (kv.Value is string) p.Folders[kv.Key] = (string)kv.Value;
                }
            }
            Dictionary<string, object> sel = Json.Obj(d, "selected");
            if (sel != null)
            {
                foreach (KeyValuePair<string, object> kv in sel)
                {
                    List<string> names = new List<string>();
                    object[] arr = kv.Value as object[];
                    if (arr != null)
                    {
                        foreach (object o in arr) if (o is string) names.Add((string)o);
                    }
                    p.Selected[kv.Key] = names;
                }
            }
            return p;
        }

        public void Save()
        {
            try
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["platform"] = Platform;
                Dictionary<string, object> folders = new Dictionary<string, object>();
                foreach (KeyValuePair<string, string> kv in Folders) folders[kv.Key] = kv.Value;
                d["folders"] = folders;
                Dictionary<string, object> sel = new Dictionary<string, object>();
                foreach (KeyValuePair<string, List<string>> kv in Selected) sel[kv.Key] = kv.Value;
                d["selected"] = sel;
                Directory.CreateDirectory(DataDir);
                Json.WriteFileAtomic(FilePath, Json.Stringify(d));
            }
            catch (Exception ex)
            {
                CrashLog.Write("Prefs save failed: " + ex.Message);
            }
        }
    }
}
