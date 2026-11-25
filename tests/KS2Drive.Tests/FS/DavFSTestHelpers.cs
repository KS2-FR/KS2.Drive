using KS2Drive.Config;
using KS2Drive.FS;
using KS2Drive.Log;
using KS2Drive.Tests.Infrastructure;
using NLog;
using NLog.Config;
using NLog.Targets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace KS2Drive.Tests.FS
{
    /// <summary>
    /// Helpers shared by the DavFS test classes.
    /// </summary>
    internal static class DavFSTestHelpers
    {
        /// <summary>A size larger than the largest file DavFS can hold in memory (about 2 GB).</summary>
        public const UInt64 ThreeGigabytes = 3UL * 1024 * 1024 * 1024;

        /// <summary>Maximum file size of DavFS (largest byte array .NET can allocate).</summary>
        public const UInt64 MaxFileSize = 0x7FFFFFC7;

        /// <summary>Maximum number of file nodes of DavFS.</summary>
        public const UInt64 MaxFileNodes = 500000;

        /// <summary>
        /// Creates a DavFS directly (without <see cref="DavFsContext"/>), e.g. to test the constructor or a specific configuration.
        /// </summary>
        public static DavFS CreateFileSystem(FakeWebDavServer Server, Configuration Config)
        {
            return new DavFS(Config, Server);
        }

        /// <summary>Answers <paramref name="Code"/> to the requests matching the method and (decoded) path.</summary>
        public static Func<String, String, HttpStatusCode?> Answer(String Method, String Path, int Code)
        {
            return (M, P) => M == Method && String.Equals(P, Path, StringComparison.OrdinalIgnoreCase) ? (HttpStatusCode?)(HttpStatusCode)Code : null;
        }

        /// <summary>Answers <paramref name="Code"/> to every request with the given method.</summary>
        public static Func<String, String, HttpStatusCode?> AnswerAll(String Method, int Code)
        {
            return (M, P) => M == Method ? (HttpStatusCode?)(HttpStatusCode)Code : null;
        }

        /// <summary>Simulates a transport failure for the requests with the given method (and path, if not null).</summary>
        public static Func<String, String, bool> NetworkDown(String Method, String Path = null)
        {
            return (M, P) => M == Method && (Path == null || String.Equals(P, Path, StringComparison.OrdinalIgnoreCase));
        }

        public static byte[] Bytes(String Text) => Encoding.UTF8.GetBytes(Text);

        /// <summary>Last activity log entry with the given method, or null.</summary>
        public static LogListItem LastLog(DavFsContext Ctx, String Method)
        {
            lock (Ctx.Log) return Ctx.Log.LastOrDefault(x => x.Method == Method);
        }

        /// <summary>Deletes the recovery copies referenced by the activity log or by the given nodes.</summary>
        public static void DeleteRecoveryCopies(DavFsContext Ctx, params FileNode[] Nodes)
        {
            var Paths = new List<String>();
            if (Ctx != null)
            {
                lock (Ctx.Log) Paths.AddRange(Ctx.Log.Select(x => x.LocalTemporaryPath));
            }
            if (Nodes != null) Paths.AddRange(Nodes.Where(x => x != null).Select(x => x.TemporaryLocalCopyPath));

            foreach (var Path in Paths.Where(x => !String.IsNullOrEmpty(x)).Distinct())
            {
                try
                {
                    if (System.IO.File.Exists(Path)) System.IO.File.Delete(Path);
                }
                catch
                {
                    //Best effort
                }
            }
        }

        /// <summary>Reads the volume label stored in a WinFSP VolumeInfo (its label fields are not public).</summary>
        public static String GetVolumeLabel(VolumeInfo Info)
        {
            int Size = Marshal.SizeOf(typeof(VolumeInfo));
            IntPtr Pointer = Marshal.AllocHGlobal(Size);
            try
            {
                Marshal.StructureToPtr(Info, Pointer, false);
                int LengthOffset = (int)Marshal.OffsetOf(typeof(VolumeInfo), "VolumeLabelLength");
                int LabelOffset = (int)Marshal.OffsetOf(typeof(VolumeInfo), "VolumeLabel");
                int LengthInBytes = Marshal.ReadInt16(Pointer, LengthOffset) & 0xFFFF;
                var Chars = new char[LengthInBytes / 2];
                for (int i = 0; i < Chars.Length; i++) Chars[i] = (char)Marshal.ReadInt16(Pointer, LabelOffset + 2 * i);
                return new String(Chars);
            }
            finally
            {
                Marshal.FreeHGlobal(Pointer);
            }
        }

        /// <summary>Returns the node dictionary of the DavFS cache (null once the cache has been cleared).</summary>
        public static Dictionary<String, FileNode> GetCachedNodes(DavFS Fs)
        {
            var Cache = (CacheManager)typeof(DavFS).GetField("Cache", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Fs);
            return (Dictionary<String, FileNode>)typeof(CacheManager).GetField("FileNodeCache", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Cache);
        }
    }

    /// <summary>
    /// Captures every NLog message (Trace and above) as "Level|message" while it is alive, then restores the previous configuration.
    /// </summary>
    internal sealed class NLogCapture : IDisposable
    {
        private readonly LoggingConfiguration Previous;
        private readonly MemoryTarget Target;

        public List<String> Messages => Target.Logs.ToList();

        public NLogCapture()
        {
            Previous = LogManager.Configuration;
            Target = new MemoryTarget { Name = "DavFSTestCapture", Layout = "${level}|${message}" };
            var Config = new LoggingConfiguration();
            Config.AddTarget("DavFSTestCapture", Target);
            Config.LoggingRules.Add(new LoggingRule("*", LogLevel.Trace, Target));
            LogManager.Configuration = Config;
        }

        public void Dispose()
        {
            LogManager.Configuration = Previous ?? new LoggingConfiguration();
        }
    }
}
