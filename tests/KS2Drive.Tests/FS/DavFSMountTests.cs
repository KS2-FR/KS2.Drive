using Fsp;
using KS2Drive.Config;
using KS2Drive.FS;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Security.AccessControl;
using static KS2Drive.Tests.FS.DavFSTestHelpers;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace KS2Drive.Tests.FS
{
    /// <summary>
    /// DavFS constructor, Init, volume callbacks, Unmounted, unsupported callbacks, authentication notification and log helpers.
    /// </summary>
    [TestClass]
    public class DavFSMountTests
    {
        [TestCleanup]
        public void Cleanup()
        {
            WebDavClient2.TestHttpMessageHandler = null;
        }

        #region Constructor

        [TestMethod]
        public void Constructor_TestsTheConnectionByListingTheLibraryRoot()
        {
            var Server = new FakeWebDavServer();
            var Fs = CreateFileSystem(Server, DavFsContext.TestConfiguration());

            Assert.IsNotNull(Fs);
            Assert.AreEqual(1, Server.CountRequests("PROPFIND"));
            Assert.AreEqual("PROPFIND /dav", Server.Requests.Single());
        }

        [TestMethod]
        public void Constructor_InvalidCredentials_ThrowsInvalidCredentials()
        {
            var Server = new FakeWebDavServer { RejectCredentials = true };

            var Ex = Assert.ThrowsException<Exception>(() => { CreateFileSystem(Server, DavFsContext.TestConfiguration()); });
            Assert.AreEqual("Cannot connect to server : Invalid credentials", Ex.Message);
        }

        [TestMethod]
        public void Constructor_NetworkFailure_ThrowsCannotConnect()
        {
            var Server = new FakeWebDavServer { NetworkFailure = (M, P) => true };

            var Ex = Assert.ThrowsException<Exception>(() => { CreateFileSystem(Server, DavFsContext.TestConfiguration()); });
            StringAssert.StartsWith(Ex.Message, "Cannot connect to server : ");
            Assert.IsFalse(Ex.Message.Contains("Invalid credentials"), Ex.Message);
        }

        [DataTestMethod]
        [DataRow(404)]
        [DataRow(500)]
        [DataRow(403)]
        public void Constructor_OtherServerError_ThrowsCannotConnectWithoutInvalidCredentials(int Code)
        {
            var Server = new FakeWebDavServer { ForcedStatus = AnswerAll("PROPFIND", Code) };

            var Ex = Assert.ThrowsException<Exception>(() => { CreateFileSystem(Server, DavFsContext.TestConfiguration()); });
            StringAssert.StartsWith(Ex.Message, "Cannot connect to server : ");
            Assert.IsFalse(Ex.Message.Contains("Invalid credentials"), Ex.Message);
        }

        [TestMethod]
        public void Constructor_ServerUrlWithTrailingSlash_MapsTheLibraryToTheDriveRoot()
        {
            var Server = new FakeWebDavServer();
            Server.AddFile("/dav/a.txt", "abc");
            var Config = DavFsContext.TestConfiguration();
            Config.ServerURL = FakeWebDavServer.LibraryUrl + "/";
            var Fs = CreateFileSystem(Server, Config);

            Int32 Status = Fs.Open("\\a.txt", 0, 0, out Object Node, out Object Desc, out FileInfo Info, out String Normalized);

            Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
            Assert.AreEqual("/dav/a.txt", ((FileNode)Node).RepositoryPath);
            Assert.AreEqual(3UL, Info.FileSize);
        }

        #endregion

        #region Init

        [DataTestMethod]
        [DataRow(-1)]
        [DataRow(0)]
        [DataRow(1000)]
        public void Init_SetsTheHostParameters(int KernelCacheModeValue)
        {
            var Server = new FakeWebDavServer();
            var Config = DavFsContext.TestConfiguration();
            Config.KernelCacheMode = KernelCacheModeValue;
            var Fs = CreateFileSystem(Server, Config);

            using (var Host = new FileSystemHost(Fs))
            {
                UInt64 Before = (UInt64)DateTime.Now.ToFileTimeUtc();
                Int32 Status = Fs.Init(Host);
                UInt64 After = (UInt64)DateTime.Now.ToFileTimeUtc();

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                Assert.AreEqual(unchecked((UInt32)KernelCacheModeValue), Host.FileInfoTimeout);
                Assert.AreEqual("davFS", Host.FileSystemName);
                Assert.AreEqual((UInt16)4096, Host.SectorSize);
                Assert.AreEqual((UInt16)1, Host.SectorsPerAllocationUnit);
                Assert.IsTrue(Host.VolumeCreationTime >= Before && Host.VolumeCreationTime <= After, "VolumeCreationTime");
                Assert.AreEqual((UInt32)(Host.VolumeCreationTime / (10000 * 1000)), Host.VolumeSerialNumber);
                Assert.IsFalse(Host.CaseSensitiveSearch);
                Assert.IsTrue(Host.CasePreservedNames);
                Assert.IsTrue(Host.UnicodeOnDisk);
                Assert.IsFalse(Host.PersistentAcls);
                Assert.IsFalse(Host.ReparsePoints);
                Assert.IsFalse(Host.ReparsePointsAccessCheck);
                Assert.IsFalse(Host.NamedStreams);
                Assert.IsTrue(Host.PostCleanupWhenModifiedOnly);
                Assert.IsTrue(Host.FlushAndPurgeOnCleanup);
                Assert.IsTrue(String.IsNullOrEmpty(Host.Prefix), "No prefix when not mounted as a network drive");
            }
        }

        [TestMethod]
        public void Init_MountAsNetworkDrive_SetsTheUncPrefix()
        {
            var Server = new FakeWebDavServer();
            var Config = DavFsContext.TestConfiguration();
            Config.MountAsNetworkDrive = true;
            var Fs = CreateFileSystem(Server, Config);

            using (var Host = new FileSystemHost(Fs))
            {
                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Fs.Init(Host));
                Assert.AreEqual(@"\fake.local\dav", Host.Prefix);
            }
        }

        #endregion

        #region Volume

        [TestMethod]
        public void GetVolumeInfo_ReportsTheTheoreticalMaximumSizeAndAnEmptyLabel()
        {
            using (var Ctx = DavFsContext.Create())
            {
                int RequestsBefore = Ctx.Server.Requests.Count;

                Int32 Status = Ctx.Fs.GetVolumeInfo(out VolumeInfo Info);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                Assert.AreEqual(MaxFileNodes * MaxFileSize, Info.TotalSize);
                Assert.AreEqual(MaxFileNodes * MaxFileSize, Info.FreeSize);
                Assert.AreEqual("", GetVolumeLabel(Info));
                Assert.AreEqual(RequestsBefore, Ctx.Server.Requests.Count, "No server call");
            }
        }

        [TestMethod]
        public void SetVolumeLabel_KeepsTheLabelInMemory()
        {
            using (var Ctx = DavFsContext.Create())
            {
                int RequestsBefore = Ctx.Server.Requests.Count;

                Int32 Status = Ctx.Fs.SetVolumeLabel("Documents", out VolumeInfo Info);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                Assert.AreEqual("Documents", GetVolumeLabel(Info));
                Assert.AreEqual(MaxFileNodes * MaxFileSize, Info.TotalSize);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.GetVolumeInfo(out VolumeInfo Info2));
                Assert.AreEqual("Documents", GetVolumeLabel(Info2));
                Assert.AreEqual(RequestsBefore, Ctx.Server.Requests.Count, "The label is not sent to the server");
            }
        }

        [TestMethod]
        public void Unmounted_ClearsTheCache()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/a.txt", "abc")))
            {
                Ctx.Open("\\a.txt");
                Assert.IsTrue(GetCachedNodes(Ctx.Fs).ContainsKey("\\a.txt"));

                Ctx.Fs.Unmounted(null);

                Assert.IsNull(GetCachedNodes(Ctx.Fs));
            }
        }

        #endregion

        #region Not supported callbacks

        [TestMethod]
        public void CanDelete_AlwaysAllowsTheDeletionWithoutServerCall()
        {
            using (var Ctx = DavFsContext.Create(s => { s.AddFile("/dav/a.txt", "abc"); s.AddFolder("/dav/folder"); s.AddFile("/dav/folder/b.txt", "b"); }))
            {
                var File = Ctx.Open("\\a.txt");
                var Folder = Ctx.Open("\\folder");
                int RequestsBefore = Ctx.Server.Requests.Count;

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.CanDelete(File, null, "\\a.txt"));
                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.CanDelete(Folder, null, "\\folder"));
                Assert.AreEqual(RequestsBefore, Ctx.Server.Requests.Count);
                Assert.IsTrue(Ctx.Server.Exists("/dav/a.txt"), "Nothing is deleted before Cleanup");
            }
        }

        [TestMethod]
        public void SetSecurity_IsNotSupported()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/a.txt", "abc")))
            {
                var Node = Ctx.Open("\\a.txt");
                byte[] SecurityBefore = Node.FileSecurity;

                Int32 Status = Ctx.Fs.SetSecurity(Node, null, AccessControlSections.Access, new byte[] { 1, 2, 3 });

                Assert.AreEqual(FileSystemBase.STATUS_INVALID_DEVICE_REQUEST, Status);
                CollectionAssert.AreEqual(SecurityBefore, Node.FileSecurity);
            }
        }

        [TestMethod]
        public void GetStreamEntry_ReturnsNoStream()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/a.txt", "abc")))
            {
                var Node = Ctx.Open("\\a.txt");
                Object Context = null;

                bool Result = Ctx.Fs.GetStreamEntry(Node, null, ref Context, out String StreamName, out UInt64 StreamSize, out UInt64 StreamAllocationSize);

                Assert.IsFalse(Result);
                Assert.IsNull(StreamName);
                Assert.AreEqual(0UL, StreamSize);
                Assert.AreEqual(0UL, StreamAllocationSize);
            }
        }

        [TestMethod]
        public void ReparsePointCallbacks_AreNotSupported()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/a.txt", "abc")))
            {
                var Node = Ctx.Open("\\a.txt");
                byte[] ReparseData = null;

                Assert.AreEqual(FileSystemBase.STATUS_INVALID_DEVICE_REQUEST, Ctx.Fs.GetReparsePointByName("\\a.txt", false, ref ReparseData));
                Assert.IsNull(ReparseData);
                Assert.AreEqual(FileSystemBase.STATUS_INVALID_DEVICE_REQUEST, Ctx.Fs.GetReparsePoint(Node, null, "\\a.txt", ref ReparseData));
                Assert.IsNull(ReparseData);
                Assert.AreEqual(FileSystemBase.STATUS_INVALID_DEVICE_REQUEST, Ctx.Fs.SetReparsePoint(Node, null, "\\a.txt", new byte[16]));
                Assert.AreEqual(FileSystemBase.STATUS_INVALID_DEVICE_REQUEST, Ctx.Fs.DeleteReparsePoint(Node, null, "\\a.txt", new byte[16]));
                Assert.AreEqual(0u, Node.FileInfo.FileAttributes & (UInt32)System.IO.FileAttributes.ReparsePoint);
            }
        }

        #endregion

        #region Authentication failure notification

        [TestMethod]
        public void AuthenticationFailure_IsNotifiedOncePerMount_EvenWithSeveral401()
        {
            using (var Ctx = DavFsContext.Create(s => { s.AddFile("/dav/a.txt", "abc"); s.AddFolder("/dav/folder"); }))
            {
                var Node = Ctx.Open("\\a.txt");
                var Folder = Ctx.Open("\\folder");
                Ctx.Server.RejectCredentials = true;

                byte[] Sd = null;
                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, Ctx.Fs.GetSecurityByName("\\x.txt", out UInt32 Attributes, ref Sd));
                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, Ctx.TryOpen("\\y.txt", out FileNode Missing));
                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Ctx.TryCreate("\\z.txt", false, out FileNode Created));
                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Ctx.Read(Node, 0, 10, out byte[] Data));
                Assert.AreEqual(0, Ctx.List(Folder).Count);

                Assert.AreEqual(1, Ctx.AuthenticationFailures);
            }
        }

        [TestMethod]
        public void AuthenticationFailure_IsNotifiedAgainOnANewMount()
        {
            using (var Ctx = DavFsContext.Create())
            {
                Ctx.Server.RejectCredentials = true;
                Ctx.TryOpen("\\a.txt", out FileNode N1);
                Assert.AreEqual(1, Ctx.AuthenticationFailures);
            }

            using (var Ctx = DavFsContext.Create())
            {
                Ctx.Server.RejectCredentials = true;
                Ctx.TryOpen("\\a.txt", out FileNode N2);
                Assert.AreEqual(1, Ctx.AuthenticationFailures);
            }
        }

        [TestMethod]
        public void AuthenticationFailure_WithoutSubscriber_DoesNotThrow()
        {
            var Server = new FakeWebDavServer();
            var Fs = CreateFileSystem(Server, DavFsContext.TestConfiguration());
            Server.RejectCredentials = true;

            Int32 Status = Fs.Open("\\a.txt", 0, 0, out Object Node, out Object Desc, out FileInfo Info, out String Normalized);

            Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, Status);
        }

        [TestMethod]
        public void NonAuthenticationErrors_DoNotNotifyAnAuthenticationFailure()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/a.txt", "abc")))
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Server.ForcedStatus = (M, P) => (System.Net.HttpStatusCode?)System.Net.HttpStatusCode.InternalServerError;

                Ctx.Read(Node, 0, 10, out byte[] Data);
                Ctx.Fs.Cleanup(Node, null, "\\a.txt", FileSystemBase.CleanupDelete);
                Ctx.TryCreate("\\new.txt", false, out FileNode Created);

                Assert.AreEqual(0, Ctx.AuthenticationFailures);
            }
        }

        #endregion

        #region Trace / Debug helpers

        [TestMethod]
        public void DebugHelpers_LogTheStartAndEndOfAnOperationWithTheSameOperationId()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/a.txt", "abc")))
            using (var Capture = new NLogCapture())
            {
                Ctx.Open("\\a.txt");

                var Messages = Capture.Messages;
                String Start = Messages.SingleOrDefault(x => x.StartsWith("Debug|") && x.Contains("|Open|Start|\\a.txt"));
                Assert.IsNotNull(Start, String.Join(Environment.NewLine, Messages));
                String OperationId = Start.Split('|')[1];
                Assert.IsTrue(Guid.TryParse(OperationId, out Guid Parsed), OperationId);

                String End = Messages.SingleOrDefault(x => x.StartsWith("Debug|" + OperationId + "|Open|End|"));
                Assert.IsNotNull(End, String.Join(Environment.NewLine, Messages));
                StringAssert.Contains(End, "STATUS_SUCCESS - From Repository");
            }
        }

        [TestMethod]
        public void TraceHelpers_LogTheFileNameThenTheNodeAsJson()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/a.txt", "abc")))
            using (var Capture = new NLogCapture())
            {
                byte[] Sd = null;
                Ctx.Fs.GetSecurityByName("\\a.txt", out UInt32 Attributes, ref Sd);
                Ctx.Fs.GetSecurityByName("\\a.txt", out Attributes, ref Sd);
                var Node = Ctx.Open("\\a.txt");
                Ctx.Read(Node, 0, 3, out byte[] Data); //Loads FileData, which must not be serialized
                Ctx.Fs.GetFileInfo(Node, null, out FileInfo Info);

                var Messages = Capture.Messages;
                Assert.AreEqual(2, Messages.Count(x => x.StartsWith("Trace|") && x.Contains("|GetSecurityByName|Start|\\a.txt")), String.Join(Environment.NewLine, Messages));
                Assert.IsTrue(Messages.Any(x => x.StartsWith("Trace|") && x.Contains("|GetSecurityByName|End|STATUS_SUCCESS - From Repository")));
                Assert.IsTrue(Messages.Any(x => x.StartsWith("Trace|") && x.Contains("|GetSecurityByName|End|STATUS_SUCCESS - From Cache")));

                String FileInfoStart = Messages.SingleOrDefault(x => x.StartsWith("Trace|") && x.Contains("|GetFileInfo|Start|{"));
                Assert.IsNotNull(FileInfoStart, String.Join(Environment.NewLine, Messages));
                StringAssert.Contains(FileInfoStart, "\"LocalPath\":\"\\\\a.txt\"");
                Assert.IsFalse(FileInfoStart.Contains("FileData"), "The content is not logged");
                Assert.IsTrue(Messages.Any(x => x.StartsWith("Trace|") && x.Contains("|GetFileInfo|End|STATUS_SUCCESS")));
            }
        }

        #endregion
    }
}
