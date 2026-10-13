using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace A2BAutoplay.Bootstrap
{
    internal static class SingleFile
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool prepareOnly = args.Contains("--prepare-only");
            try
            {
                if (prepareOnly)
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
                    Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
                }
                if (!prepareOnly)
                {
                    Mutex existing;
                    if (Mutex.TryOpenExisting("Local\\A2BAutoplay", out existing))
                    {
                        existing.Dispose();
                        MessageBox.Show("程序已经运行，请使用已打开的窗口。", "A=B 自动验证");
                        return 0;
                    }
                }
                string home = Environment.GetEnvironmentVariable("A2B_AUTOPLAY_HOME");
                if (String.IsNullOrWhiteSpace(home))
                    home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "A2B-Autoplay");
                home = Path.GetFullPath(home);
                string data = Path.Combine(home, "data");
                Directory.CreateDirectory(data);
                PreparedBundle result;
                using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("A2BAutoplay.Bundle"))
                    result = BundleCache.Prepare(resource, Path.Combine(home, "runtime"), BundleInfo.Identity);
                if (prepareOnly)
                {
                    Console.WriteLine(new JavaScriptSerializer().Serialize(new
                    {
                        resources = result.Directory, data = data, reused = result.Reused, version = BundleInfo.Version
                    }));
                    return 0;
                }
                var info = new ProcessStartInfo(Path.Combine(result.Directory, "A2B-Autoplay.exe"))
                {
                    Arguments = String.Join(" ", args.Select(Quote)),
                    WorkingDirectory = result.Directory,
                    UseShellExecute = false
                };
                info.EnvironmentVariables["A2B_AUTOPLAY_DATA_DIRECTORY"] = data;
                Process.Start(info);
                return 0;
            }
            catch (Exception error)
            {
                if (prepareOnly) Console.Error.WriteLine(error.Message);
                else MessageBox.Show("无法准备或启动程序：\n" + error.Message, "A=B 自动验证", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); }
                else { result.Append('\\', slashes); result.Append(character); }
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }
    }
}
