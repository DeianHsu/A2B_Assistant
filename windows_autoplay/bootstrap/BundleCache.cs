using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace A2BAutoplay.Bootstrap
{
    public sealed class PreparedBundle
    {
        public string Directory;
        public bool Reused;
    }

    public static class BundleCache
    {
        public static PreparedBundle Prepare(Stream bundle, string cacheBase, string identity)
        {
            cacheBase = Path.GetFullPath(cacheBase);
            string destination = ChildPath(cacheBase, identity);
            System.IO.Directory.CreateDirectory(cacheBase);
            using (var gate = new Mutex(false, "Local\\A2BAutoplay.Bundle." + identity))
            {
                bool owned;
                try { owned = gate.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) throw new IOException("另一进程正在准备资源，请稍后再试。");
                try
                {
                    string payload = ChildPath(destination, "A2B-Autoplay");
                    if (Ready(destination, payload, identity))
                        return new PreparedBundle { Directory = payload, Reused = true };
                    string staging = ChildPath(cacheBase, identity + ".preparing-" + Guid.NewGuid().ToString("N"));
                    System.IO.Directory.CreateDirectory(staging);
                    Extract(bundle, staging);
                    string stagedPayload = ChildPath(staging, "A2B-Autoplay");
                    if (!HasRuntime(stagedPayload)) throw new InvalidDataException("内置运行资源不完整。");
                    File.WriteAllText(ChildPath(staging, ".ready"), identity);
                    if (System.IO.Directory.Exists(destination))
                    {
                        string previous = ChildPath(cacheBase, identity + ".incomplete-" + Guid.NewGuid().ToString("N"));
                        // Both resolved paths are inside this application's runtime cache.
                        System.IO.Directory.Move(destination, previous);
                    }
                    // The extracted staging directory and final directory share the same cache root.
                    System.IO.Directory.Move(staging, destination);
                    return new PreparedBundle { Directory = payload, Reused = false };
                }
                finally { gate.ReleaseMutex(); }
            }
        }

        private static bool Ready(string destination, string payload, string identity)
        {
            string marker = ChildPath(destination, ".ready");
            return File.Exists(marker) && File.ReadAllText(marker) == identity && HasRuntime(payload);
        }
        private static bool HasRuntime(string payload)
        {
            return File.Exists(Path.Combine(payload, "A2B-Autoplay.exe"))
                && File.Exists(Path.Combine(payload, "runtime", "python.exe"));
        }
        private static string ChildPath(string parent, string relative)
        {
            string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string child = Path.GetFullPath(Path.Combine(root, relative));
            if (!child.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("资源路径超出应用缓存目录。");
            return child;
        }
        private static void Extract(Stream bundle, string staging)
        {
            if (bundle == null) throw new FileNotFoundException("未找到内置运行资源。");
            using (var archive = new ZipArchive(bundle, ZipArchiveMode.Read, true))
            {
                foreach (var entry in archive.Entries)
                {
                    string target = ChildPath(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    if (entry.FullName.EndsWith("/")) { System.IO.Directory.CreateDirectory(target); continue; }
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var source = entry.Open())
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write)) source.CopyTo(output);
                }
            }
        }
    }
}
