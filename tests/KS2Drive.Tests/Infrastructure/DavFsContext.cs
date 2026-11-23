using Fsp;
using KS2Drive.Config;
using KS2Drive.FS;
using KS2Drive.Log;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using FileInfo = Fsp.Interop.FileInfo;

namespace KS2Drive.Tests.Infrastructure
{
    /// <summary>
    /// A <see cref="DavFS"/> connected to a <see cref="FakeWebDavServer"/>, with helpers to call the WinFSP callbacks
    /// the way WinFSP does (open, read, write, cleanup, close...). Dispose it at the end of each test.
    /// </summary>
    internal sealed class DavFsContext : IDisposable
    {
        public FakeWebDavServer Server { get; }
        public DavFS Fs { get; }
        public Configuration Config { get; }
        public List<LogListItem> Log { get; } = new List<LogListItem>();
        public int AuthenticationFailures { get; private set; }

        private DavFsContext(FakeWebDavServer Server, Configuration Config)
        {
            this.Server = Server;
            this.Config = Config;
            WebDavClient2.TestHttpMessageHandler = Server;
            Fs = new DavFS(Config);
            Fs.RepositoryActionPerformed += (s, e) => { lock (Log) Log.Add(e); };
            Fs.RepositoryAuthenticationFailed += (s, e) => AuthenticationFailures++;
        }

        /// <summary>
        /// Creates the context. <paramref name="Setup"/> can fill the fake server before the file system is created
        /// (the DavFS constructor lists the library root to check the connection).
        /// </summary>
        public static DavFsContext Create(Action<FakeWebDavServer> Setup = null, FlushMode Flush = FlushMode.FlushAtCleanup, WebDAVMode Mode = WebDAVMode.WebDAV, bool PreLoading = false)
        {
            var Server = new FakeWebDavServer();
            Setup?.Invoke(Server);
            var Config = TestConfiguration(Flush, Mode, PreLoading);
            return new DavFsContext(Server, Config);
        }

        public static Configuration TestConfiguration(FlushMode Flush = FlushMode.FlushAtCleanup, WebDAVMode Mode = WebDAVMode.WebDAV, bool PreLoading = false)
        {
            return new Configuration
            {
                ServerURL = FakeWebDavServer.LibraryUrl,
                ServerLogin = "user",
                ServerPassword = "password",
                ServerType = (int)Mode,
                FlushMode = (int)Flush,
                KernelCacheMode = (int)KernelCacheMode.MetaDataOnly,
                PreLoading = PreLoading,
                DriveLetter = "Z"
            };
        }

        public void Dispose()
        {
            WebDavClient2.TestHttpMessageHandler = null;
        }

        /// <summary>Opens an existing element and asserts success.</summary>
        public FileNode Open(String FileName)
        {
            Int32 Status = Fs.Open(FileName, 0, 0, out Object Node, out Object Desc, out FileInfo Info, out String Normalized);
            Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status, $"Open {FileName}");
            Assert.IsNotNull(Node);
            return (FileNode)Node;
        }

        public Int32 TryOpen(String FileName, out FileNode Node)
        {
            Int32 Status = Fs.Open(FileName, 0, 0, out Object Node0, out Object Desc, out FileInfo Info, out String Normalized);
            Node = (FileNode)Node0;
            return Status;
        }

        /// <summary>Creates a file or folder and asserts success.</summary>
        public FileNode Create(String FileName, bool IsDirectory = false)
        {
            Int32 Status = TryCreate(FileName, IsDirectory, out FileNode Node);
            Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status, $"Create {FileName}");
            return Node;
        }

        public Int32 TryCreate(String FileName, bool IsDirectory, out FileNode Node)
        {
            UInt32 Attributes = IsDirectory ? (UInt32)System.IO.FileAttributes.Directory : (UInt32)System.IO.FileAttributes.Normal;
            Int32 Status = Fs.Create(FileName, 0, 0, Attributes, null, 0, out Object Node0, out Object Desc, out FileInfo Info, out String Normalized);
            Node = (FileNode)Node0;
            return Status;
        }

        public Int32 Write(FileNode Node, UInt64 Offset, String Text, bool WriteToEndOfFile = false, bool ConstrainedIo = false) => Write(Node, Offset, Encoding.UTF8.GetBytes(Text), WriteToEndOfFile, ConstrainedIo, out _);

        public Int32 Write(FileNode Node, UInt64 Offset, byte[] Data, bool WriteToEndOfFile, bool ConstrainedIo, out UInt32 BytesTransferred)
        {
            IntPtr Buffer = Marshal.AllocHGlobal(Math.Max(1, Data.Length));
            try
            {
                Marshal.Copy(Data, 0, Buffer, Data.Length);
                return Fs.Write(Node, null, Buffer, Offset, (UInt32)Data.Length, WriteToEndOfFile, ConstrainedIo, out BytesTransferred, out FileInfo Info);
            }
            finally
            {
                Marshal.FreeHGlobal(Buffer);
            }
        }

        public Int32 Read(FileNode Node, UInt64 Offset, UInt32 Length, out byte[] Data)
        {
            IntPtr Buffer = Marshal.AllocHGlobal((int)Math.Max(1, Length));
            try
            {
                Int32 Status = Fs.Read(Node, null, Buffer, Offset, Length, out UInt32 BytesTransferred);
                Data = new byte[BytesTransferred];
                Marshal.Copy(Buffer, Data, 0, (int)BytesTransferred);
                return Status;
            }
            finally
            {
                Marshal.FreeHGlobal(Buffer);
            }
        }

        /// <summary>Reads the whole file (up to 1 MB) as UTF-8 text.</summary>
        public String ReadAll(FileNode Node)
        {
            Int32 Status = Read(Node, 0, 1024 * 1024, out byte[] Data);
            if (Status == FileSystemBase.STATUS_END_OF_FILE) return "";
            Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status, "Read");
            return Encoding.UTF8.GetString(Data);
        }

        /// <summary>Enumerates a directory like WinFSP does and returns the entry names.</summary>
        public List<String> List(FileNode Folder, String Marker = null)
        {
            var Names = new List<String>();
            Object Context = null;
            while (Fs.ReadDirectoryEntry(Folder, null, null, Marker, ref Context, out String FileName, out FileInfo Info)) Names.Add(FileName);
            return Names;
        }

        /// <summary>Last handle released: Cleanup (with the given flags) then Close, like WinFSP does.</summary>
        public void CleanupAndClose(FileNode Node, String FileName, UInt32 Flags = 0)
        {
            Fs.Cleanup(Node, null, FileName, Flags);
            Fs.Close(Node, null);
        }

        public const UInt32 ModifiedFlags = 16 | 64; //CleanupSetArchiveBit | CleanupSetLastWriteTime
    }
}
