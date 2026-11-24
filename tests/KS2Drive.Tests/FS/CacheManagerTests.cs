using KS2Drive.FS;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using KS2Drive.WebDav;

namespace KS2Drive.Tests.FS
{
    [TestClass]
    public class CacheManagerTests
    {
        private FakeWebDavServer Server;
        private CacheManager Cm;
        private WebDavClient Client;

        [TestInitialize]
        public void Setup()
        {
            Server = new FakeWebDavServer();
            Client = new WebDavClient(WebDavClient.CreateHttpClient(Server), FakeWebDavServer.ServerUrl);
            FileNode.Init(FakeWebDavServer.LibraryPath, WebDAVMode.WebDAV);
            Cm = null;
        }

        [TestCleanup]
        public void Cleanup()
        {
            //Let background refreshes started by the test finish before the next test (the cache lock is static)
            if (Cm != null) WaitUntil(() => RunningRefreshCount(Cm) == 0);
        }

        #region Helpers

        private static FileNode MakeFile(String RepositoryPath, long Length = 3)
        {
            return new FileNode(new WebDavItem
            {
                Path = RepositoryPath,
                DisplayName = RepositoryPath.Substring(RepositoryPath.LastIndexOf('/') + 1),
                ETag = "\"etag\"",
                ContentLength = Length,
                IsCollection = false,
                CreationDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                LastModified = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        }

        private static FileNode MakeFolder(String RepositoryPath)
        {
            return new FileNode(new WebDavItem
            {
                Path = RepositoryPath + "/",
                DisplayName = RepositoryPath.Substring(RepositoryPath.LastIndexOf('/') + 1),
                IsCollection = true
            });
        }

        private static FileNode MakeRoot()
        {
            return new FileNode(new WebDavItem { Path = "/dav/", DisplayName = "", IsCollection = true });
        }

        private static bool IsDirectory(FileNode Node) => (Node.FileInfo.FileAttributes & (UInt32)System.IO.FileAttributes.Directory) != 0;

        private FileNode Add(FileNode Node)
        {
            Cm.Lock();
            try { return Cm.AddFileNodeNoLock(Node); }
            finally { Cm.Unlock(); }
        }

        private FileNode Cached(String LocalPath)
        {
            Cm.Lock();
            try { return Cm.GetFileNodeNoLock(LocalPath).node; }
            finally { Cm.Unlock(); }
        }

        private static T GetPrivateField<T>(object Target, String Name)
        {
            return (T)Target.GetType().GetField(Name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Target);
        }

        private static object InvokePrivate(object Target, String Name, params object[] Args)
        {
            try
            {
                return Target.GetType().GetMethod(Name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(Target, Args);
            }
            catch (TargetInvocationException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private static Dictionary<String, DateTime> MissingFileCache(CacheManager Cm) => GetPrivateField<Dictionary<String, DateTime>>(Cm, "MissingFileCache");

        private static HashSet<String> RunningRefreshSet(CacheManager Cm) => GetPrivateField<HashSet<String>>(Cm, "RunningRefreshActionList");

        private static int RunningRefreshCount(CacheManager Cm)
        {
            object LockObject = GetPrivateField<object>(Cm, "RunningRefreshActionListLock");
            lock (LockObject) return RunningRefreshSet(Cm).Count;
        }

        private static object StaticCacheLock() => typeof(CacheManager).GetField("CacheLock", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

        private List<Tuple<String, FileNode>> BuildListing(FileNode Folder, IEnumerable<Tuple<String, FileNode>> Children, String Marker)
        {
            return (List<Tuple<String, FileNode>>)InvokePrivate(Cm, "BuildDirectoryListing", Folder, Children, Marker);
        }

        private static bool WaitUntil(Func<bool> Condition, int TimeoutMs = 10000)
        {
            var Sw = Stopwatch.StartNew();
            while (Sw.ElapsedMilliseconds < TimeoutMs)
            {
                if (Condition()) return true;
                Thread.Sleep(20);
            }
            return Condition();
        }

        private static List<String> Names(List<Tuple<String, FileNode>> Listing) => Listing.Select(x => x.Item1).ToList();

        private int CountRequests(String Request)
        {
            lock (Server.Requests) return Server.Requests.ToArray().Count(x => x == Request);
        }

        private void SetupFolderOnServer()
        {
            Server.AddFolder("/dav/folder");
            Server.AddFile("/dav/folder/b.txt", "bbb");
            Server.AddFile("/dav/folder/A.txt", "a");
            Server.AddFolder("/dav/folder/sub");
            Server.AddFile("/dav/folder/sub/deep.txt", "deep");
        }

        #endregion

        #region Constructor / GetFileNodeNoLock / AddFileNodeNoLock

        [TestMethod]
        public void Constructor_StoresModeAndPreloadingFlag()
        {
            Cm = new CacheManager(CacheMode.Disabled, true, Client);
            Assert.AreEqual(CacheMode.Disabled, GetPrivateField<CacheMode>(Cm, "_mode"));
            Assert.IsTrue(GetPrivateField<bool>(Cm, "_PreLoadFoldersInCache"));

            var Cm2 = new CacheManager(CacheMode.Enabled, false, Client);
            Assert.AreEqual(CacheMode.Enabled, GetPrivateField<CacheMode>(Cm2, "_mode"));
            Assert.IsFalse(GetPrivateField<bool>(Cm2, "_PreLoadFoldersInCache"));
            Assert.AreEqual(0, RunningRefreshCount(Cm2));
        }

        [TestMethod]
        public void GetFileNodeNoLock_UnknownPath_ReturnsNullAndUnknown()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Result = Cm.GetFileNodeNoLock("\\unknown.txt");
            Assert.IsNull(Result.node);
            Assert.IsFalse(Result.IsNonExistent);
        }

        [TestMethod]
        public void GetFileNodeNoLock_CachedNode_ReturnsIt()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Node = MakeFile("/dav/a.txt");
            Add(Node);
            var Result = Cm.GetFileNodeNoLock("\\a.txt");
            Assert.AreSame(Node, Result.node);
            Assert.IsFalse(Result.IsNonExistent);
        }

        [TestMethod]
        public void GetFileNodeNoLock_IsCaseInsensitive()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Node = MakeFile("/dav/Folder/File.TXT");
            Add(Node);
            Assert.AreSame(Node, Cm.GetFileNodeNoLock("\\folder\\file.txt").node);
        }

        [TestMethod]
        public void GetFileNodeNoLock_KnownMissingWithinDuration_ReturnsNonExistent()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            Cm.AddMissingFileNoLock("\\missing.txt");
            var Result = Cm.GetFileNodeNoLock("\\missing.txt");
            Assert.IsNull(Result.node);
            Assert.IsTrue(Result.IsNonExistent);
            Assert.IsTrue(Cm.GetFileNodeNoLock("\\MISSING.TXT").IsNonExistent, "missing list is case-insensitive");
        }

        [TestMethod]
        public void GetFileNodeNoLock_KnownMissingStillValidJustBeforeExpiry()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            Cm.AddMissingFileNoLock("\\missing.txt");
            MissingFileCache(Cm)["\\missing.txt"] = DateTime.Now.AddSeconds(-13);
            Assert.IsTrue(Cm.GetFileNodeNoLock("\\missing.txt").IsNonExistent);
        }

        [TestMethod]
        public void GetFileNodeNoLock_KnownMissingAfterExpiry_ReturnsUnknown()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            Cm.AddMissingFileNoLock("\\missing.txt");
            MissingFileCache(Cm)["\\missing.txt"] = DateTime.Now.AddSeconds(-16);
            var Result = Cm.GetFileNodeNoLock("\\missing.txt");
            Assert.IsNull(Result.node);
            Assert.IsFalse(Result.IsNonExistent);
        }

        [TestMethod]
        public void GetFileNodeNoLock_DisabledMode_AlwaysUnknown()
        {
            Cm = new CacheManager(CacheMode.Disabled, false, Client);
            Cm.AddMissingFileNoLock("\\missing.txt");
            Cm.AddFileNodeNoLock(MakeFile("/dav/a.txt"));
            var Missing = Cm.GetFileNodeNoLock("\\missing.txt");
            var Added = Cm.GetFileNodeNoLock("\\a.txt");
            Assert.IsNull(Missing.node);
            Assert.IsFalse(Missing.IsNonExistent);
            Assert.IsNull(Added.node);
            Assert.IsFalse(Added.IsNonExistent);
        }

        [TestMethod]
        public void AddFileNodeNoLock_NewNode_ReturnsItAndRaisesCacheRefreshed()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            int Events = 0;
            Cm.CacheRefreshed += (s, e) => { Events++; Assert.AreSame(Cm, s); };
            var Node = MakeFile("/dav/a.txt");
            Assert.AreSame(Node, Add(Node));
            Assert.AreEqual(1, Events);
        }

        [TestMethod]
        public void AddFileNodeNoLock_AlreadyCached_ReturnsCachedInstanceCaseInsensitive()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var First = MakeFile("/dav/folder/a.txt");
            Add(First);
            int Events = 0;
            Cm.CacheRefreshed += (s, e) => Events++;

            var Second = MakeFile("/dav/FOLDER/A.TXT");
            Assert.AreSame(First, Add(Second));
            Assert.AreSame(First, Cached("\\folder\\a.txt"));
            Assert.AreEqual(0, Events, "no event when the node was already cached");
        }

        [TestMethod]
        public void AddFileNodeNoLock_RemovesThePathFromTheMissingList()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            Cm.AddMissingFileNoLock("\\a.txt");
            var Node = MakeFile("/dav/a.txt");
            Add(Node);
            Assert.IsFalse(MissingFileCache(Cm).ContainsKey("\\a.txt"));
            var Result = Cm.GetFileNodeNoLock("\\a.txt");
            Assert.AreSame(Node, Result.node);
            Assert.IsFalse(Result.IsNonExistent);
        }

        [TestMethod]
        public void AddFileNodeNoLock_DisabledMode_ReturnsNodeWithoutCachingOrEvent()
        {
            Cm = new CacheManager(CacheMode.Disabled, false, Client);
            int Events = 0;
            Cm.CacheRefreshed += (s, e) => Events++;
            var Node = MakeFile("/dav/a.txt");
            Assert.AreSame(Node, Cm.AddFileNodeNoLock(Node));
            var Other = MakeFile("/dav/a.txt");
            Assert.AreSame(Other, Cm.AddFileNodeNoLock(Other), "disabled cache never returns a previously added node");
            Assert.AreEqual(0, Events);
        }

        #endregion

        #region AddMissingFileNoLock

        [TestMethod]
        public void AddMissingFileNoLock_RetimestampsAnExpiredEntry()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            Cm.AddMissingFileNoLock("\\x");
            MissingFileCache(Cm)["\\x"] = DateTime.Now.AddSeconds(-60);
            Assert.IsFalse(Cm.GetFileNodeNoLock("\\x").IsNonExistent);
            Cm.AddMissingFileNoLock("\\x");
            Assert.IsTrue(Cm.GetFileNodeNoLock("\\x").IsNonExistent);
        }

        [TestMethod]
        public void AddMissingFileNoLock_PurgesExpiredEntriesAboveThreshold()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Missing = MissingFileCache(Cm);
            DateTime Old = DateTime.Now.AddSeconds(-60);
            for (int i = 0; i < 999; i++) Missing["\\old" + i] = Old;
            Missing["\\fresh"] = DateTime.Now;
            Assert.AreEqual(1000, Missing.Count);

            Cm.AddMissingFileNoLock("\\new");

            Assert.AreEqual(2, Missing.Count);
            Assert.IsTrue(Missing.ContainsKey("\\fresh"));
            Assert.IsTrue(Missing.ContainsKey("\\new"));
        }

        [TestMethod]
        public void AddMissingFileNoLock_DoesNotPurgeBelowThreshold()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Missing = MissingFileCache(Cm);
            DateTime Old = DateTime.Now.AddSeconds(-60);
            for (int i = 0; i < 998; i++) Missing["\\old" + i] = Old;

            Cm.AddMissingFileNoLock("\\new");

            Assert.AreEqual(999, Missing.Count);
        }

        [TestMethod]
        public void AddMissingFileNoLock_DisabledMode_DoesNothing()
        {
            Cm = new CacheManager(CacheMode.Disabled, false, Client);
            Cm.AddMissingFileNoLock("\\x");
            Assert.AreEqual(0, MissingFileCache(Cm).Count);
        }

        #endregion

        #region Rename / Delete / Invalidate

        [TestMethod]
        public void RenameFileNodeKey_MovesTheEntryWithoutChangingTheNode()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Node = MakeFile("/dav/a.txt");
            Add(Node);
            int Events = 0;
            Cm.CacheRefreshed += (s, e) => Events++;

            Cm.RenameFileNodeKey("\\a.txt", "\\b.txt");

            Assert.IsNull(Cached("\\a.txt"));
            Assert.AreSame(Node, Cached("\\b.txt"));
            Assert.AreEqual("\\a.txt", Node.LocalPath, "only the key is changed");
            Assert.AreEqual(1, Events);
        }

        [TestMethod]
        public void RenameFileNodeKey_IsCaseInsensitiveOnThePreviousKey()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Node = MakeFile("/dav/a.txt");
            Add(Node);
            Cm.RenameFileNodeKey("\\A.TXT", "\\b.txt");
            Assert.IsNull(Cached("\\a.txt"));
            Assert.AreSame(Node, Cached("\\b.txt"));
        }

        [TestMethod]
        public void RenameFileNodeKey_DisabledMode_DoesNothing()
        {
            Cm = new CacheManager(CacheMode.Disabled, false, Client);
            int Events = 0;
            Cm.CacheRefreshed += (s, e) => Events++;
            Cm.RenameFileNodeKey("\\a.txt", "\\b.txt"); //Would throw (missing key) if the cache was used
            Assert.AreEqual(0, Events);
        }

        [TestMethod]
        public void RenameFolderSubElements_UpdatesDescendantsPathsAndKeys()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/old"));
            var A = Add(MakeFile("/dav/old/a.txt"));
            var Sub = Add(MakeFolder("/dav/old/sub"));
            var Deep = Add(MakeFile("/dav/old/sub/b.txt"));
            var Sibling = Add(MakeFile("/dav/oldish/c.txt"));
            int Events = 0;
            Cm.CacheRefreshed += (s, e) => Events++;

            Cm.RenameFolderSubElements("\\old", "\\new");

            Assert.AreSame(A, Cached("\\new\\a.txt"));
            Assert.AreEqual("\\new\\a.txt", A.LocalPath);
            Assert.AreEqual("/dav/new/a.txt", A.RepositoryPath);
            Assert.AreSame(Sub, Cached("\\new\\sub"));
            Assert.AreEqual("/dav/new/sub", Sub.RepositoryPath);
            Assert.AreSame(Deep, Cached("\\new\\sub\\b.txt"));
            Assert.AreEqual("\\new\\sub\\b.txt", Deep.LocalPath);
            Assert.AreEqual("/dav/new/sub/b.txt", Deep.RepositoryPath);
            Assert.IsNull(Cached("\\old\\a.txt"));
            Assert.IsNull(Cached("\\old\\sub\\b.txt"));

            Assert.AreSame(Folder, Cached("\\old"), "the folder's own entry is handled by RenameFileNodeKey");
            Assert.AreSame(Sibling, Cached("\\oldish\\c.txt"));
            Assert.AreEqual("\\oldish\\c.txt", Sibling.LocalPath);
            Assert.AreEqual(1, Events);
        }

        [TestMethod]
        public void RenameFolderSubElements_IsCaseInsensitive()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var A = Add(MakeFile("/dav/Old/a.txt"));
            Cm.RenameFolderSubElements("\\OLD", "\\new");
            Assert.AreSame(A, Cached("\\new\\a.txt"));
            Assert.AreEqual("\\new\\a.txt", A.LocalPath);
        }

        [TestMethod]
        public void RenameFolderSubElements_DisabledMode_DoesNothing()
        {
            Cm = new CacheManager(CacheMode.Disabled, false, Client);
            var A = MakeFile("/dav/old/a.txt");
            Cm.RenameFolderSubElements("\\old", "\\new");
            Assert.AreEqual("\\old\\a.txt", A.LocalPath);
        }

        [TestMethod]
        public void DeleteFileNode_File_RemovesOnlyThatEntry()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var A = Add(MakeFile("/dav/a.txt"));
            var B = Add(MakeFile("/dav/a.txt2"));
            int Events = 0;
            Cm.CacheRefreshed += (s, e) => Events++;

            Cm.DeleteFileNode(A);

            Assert.IsNull(Cached("\\a.txt"));
            Assert.AreSame(B, Cached("\\a.txt2"));
            Assert.AreEqual(1, Events);
        }

        [TestMethod]
        public void DeleteFileNode_Folder_RemovesDescendantsButNotSiblings()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));
            Add(MakeFile("/dav/folder/a.txt"));
            Add(MakeFolder("/dav/folder/sub"));
            Add(MakeFile("/dav/folder/sub/b.txt"));
            var Sibling = Add(MakeFile("/dav/folderX/c.txt"));
            Assert.IsTrue(IsDirectory(Folder));

            Cm.DeleteFileNode(Folder);

            Assert.IsNull(Cached("\\folder"));
            Assert.IsNull(Cached("\\folder\\a.txt"));
            Assert.IsNull(Cached("\\folder\\sub"));
            Assert.IsNull(Cached("\\folder\\sub\\b.txt"));
            Assert.AreSame(Sibling, Cached("\\folderX\\c.txt"));
        }

        [TestMethod]
        public void DeleteFileNode_Folder_IsCaseInsensitive()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            Add(MakeFolder("/dav/folder"));
            Add(MakeFile("/dav/folder/a.txt"));

            Cm.DeleteFileNode(MakeFolder("/dav/FOLDER"));

            Assert.IsNull(Cached("\\folder"));
            Assert.IsNull(Cached("\\folder\\a.txt"));
        }

        [TestMethod]
        public void DeleteFileNode_DisabledMode_DoesNotRaiseEvent()
        {
            Cm = new CacheManager(CacheMode.Disabled, false, Client);
            int Events = 0;
            Cm.CacheRefreshed += (s, e) => Events++;
            Cm.DeleteFileNode(MakeFolder("/dav/folder"));
            Assert.AreEqual(0, Events);
        }

        [TestMethod]
        public void DeleteFileEntry_RemovesOnlyTheKey()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            Add(MakeFolder("/dav/folder"));
            var Child = Add(MakeFile("/dav/folder/a.txt"));
            int Events = 0;
            Cm.CacheRefreshed += (s, e) => Events++;

            Cm.DeleteFileEntry("\\FOLDER");

            Assert.IsNull(Cached("\\folder"));
            Assert.AreSame(Child, Cached("\\folder\\a.txt"), "descendants are not removed");
            Assert.AreEqual(1, Events);
        }

        [TestMethod]
        public void DeleteFileEntry_UnknownKey_DoesNotThrow()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            Cm.DeleteFileEntry("\\nothing");
            Assert.IsNull(Cached("\\nothing"));
        }

        [TestMethod]
        public void DeleteFileEntry_DisabledMode_DoesNotRaiseEvent()
        {
            Cm = new CacheManager(CacheMode.Disabled, false, Client);
            int Events = 0;
            Cm.CacheRefreshed += (s, e) => Events++;
            Cm.DeleteFileEntry("\\a.txt");
            Assert.AreEqual(0, Events);
        }

        [TestMethod]
        public void InvalidateFileNode_ElementAtRoot_MarksRootAsNotParsed()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Root = Add(MakeRoot());
            Assert.AreEqual("\\", Root.LocalPath);
            Root.IsParsed = true;
            var A = Add(MakeFile("/dav/a.txt"));

            Cm.InvalidateFileNode(A);

            Assert.IsNull(Cached("\\a.txt"));
            Assert.IsFalse(Root.IsParsed);
        }

        [TestMethod]
        public void InvalidateFileNode_NestedElement_MarksParentAsNotParsed()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Root = Add(MakeRoot());
            Root.IsParsed = true;
            var Folder = Add(MakeFolder("/dav/folder"));
            Folder.IsParsed = true;
            var A = Add(MakeFile("/dav/folder/a.txt"));

            Cm.InvalidateFileNode(A);

            Assert.IsNull(Cached("\\folder\\a.txt"));
            Assert.IsFalse(Folder.IsParsed);
            Assert.IsTrue(Root.IsParsed, "only the direct parent is invalidated");
        }

        [TestMethod]
        public void InvalidateFileNode_ParentNotCached_RemovesNodeWithoutError()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var A = Add(MakeFile("/dav/folder/a.txt"));
            Cm.InvalidateFileNode(A);
            Assert.IsNull(Cached("\\folder\\a.txt"));
        }

        [TestMethod]
        public void InvalidateFileNode_DisabledMode_DoesNothing()
        {
            Cm = new CacheManager(CacheMode.Disabled, false, Client);
            var Folder = MakeFolder("/dav/folder");
            Folder.IsParsed = true;
            Cm.InvalidateFileNode(MakeFile("/dav/folder/a.txt"));
            Assert.IsTrue(Folder.IsParsed);
        }

        #endregion

        #region Clear / Lock / Unlock

        [TestMethod]
        public void Clear_ReleasesTheNodeCache()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Cache = GetPrivateField<Dictionary<String, FileNode>>(Cm, "FileNodeCache");
            Add(MakeFile("/dav/a.txt"));

            Cm.Clear();

            Assert.AreEqual(0, Cache.Count, "the dictionary is emptied");
            Assert.IsNull(GetPrivateField<Dictionary<String, FileNode>>(Cm, "FileNodeCache"), "the dictionary is released");
        }

        [TestMethod]
        public void LockAndUnlock_AcquireAndReleaseTheCacheLock()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            object CacheLock = StaticCacheLock();

            Cm.Lock();
            try
            {
                Assert.IsTrue(Monitor.IsEntered(CacheLock));
                bool OtherThreadGotLock = Task.Run(() =>
                {
                    bool Taken = Monitor.TryEnter(CacheLock);
                    if (Taken) Monitor.Exit(CacheLock);
                    return Taken;
                }).Result;
                Assert.IsFalse(OtherThreadGotLock, "another thread cannot take the lock while it is held");
            }
            finally
            {
                Cm.Unlock();
            }

            Assert.IsFalse(Monitor.IsEntered(CacheLock));
            var Blocked = Task.Run(() => Cm.DeleteFileEntry("\\x"));
            Assert.IsTrue(Blocked.Wait(5000), "operations taking the lock can run once it is released");
        }

        [TestMethod]
        public void Lock_BlocksLockingOperationsOfOtherThreads()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            Add(MakeFile("/dav/a.txt"));
            Task Rename;
            Cm.Lock();
            try
            {
                Rename = Task.Run(() => Cm.RenameFileNodeKey("\\a.txt", "\\b.txt"));
                Assert.IsFalse(Rename.Wait(200), "the rename waits for the lock");
            }
            finally
            {
                Cm.Unlock();
            }
            Assert.IsTrue(Rename.Wait(5000));
            Assert.IsNotNull(Cached("\\b.txt"));
        }

        #endregion

        #region BuildDirectoryListing

        private List<Tuple<String, FileNode>> SampleChildren(out FileNode Folder)
        {
            Folder = MakeFolder("/dav/folder");
            return new List<Tuple<String, FileNode>>
            {
                new Tuple<String, FileNode>("c.txt", MakeFile("/dav/folder/c.txt")),
                new Tuple<String, FileNode>("A.txt", MakeFile("/dav/folder/A.txt")),
                new Tuple<String, FileNode>("b.txt", MakeFile("/dav/folder/b.txt")),
                new Tuple<String, FileNode>(".", Folder),
                new Tuple<String, FileNode>("..", Folder),
            };
        }

        [TestMethod]
        public void BuildDirectoryListing_SubFolder_DotEntriesFirstThenSortedChildren()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Children = SampleChildren(out FileNode Folder);

            var Listing = BuildListing(Folder, Children, null);

            CollectionAssert.AreEqual(new[] { ".", "..", "A.txt", "b.txt", "c.txt" }, Names(Listing));
            Assert.AreSame(Folder, Listing[0].Item2);
            Assert.AreSame(Folder, Listing[1].Item2, "'..' is the current folder when the parent is not cached");
        }

        [TestMethod]
        public void BuildDirectoryListing_ParentCached_DotDotIsTheParent()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Root = Add(MakeRoot());
            var Parent = Add(MakeFolder("/dav/parent"));
            var Folder = MakeFolder("/dav/parent/folder");
            var Top = MakeFolder("/dav/top");

            var Listing = BuildListing(Folder, new List<Tuple<String, FileNode>>(), "");
            Assert.AreSame(Parent, Listing.Single(x => x.Item1 == "..").Item2);

            var TopListing = BuildListing(Top, new List<Tuple<String, FileNode>>(), null);
            Assert.AreSame(Root, TopListing.Single(x => x.Item1 == "..").Item2, "the parent of a root element is the root folder");
        }

        [TestMethod]
        public void BuildDirectoryListing_Root_HasNoDotEntries()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Root = MakeRoot();
            var Children = new List<Tuple<String, FileNode>>
            {
                new Tuple<String, FileNode>("z", MakeFile("/dav/z")),
                new Tuple<String, FileNode>(".", Root),
                new Tuple<String, FileNode>("a", MakeFile("/dav/a")),
            };
            CollectionAssert.AreEqual(new[] { "a", "z" }, Names(BuildListing(Root, Children, null)));
        }

        [TestMethod]
        public void BuildDirectoryListing_MarkerDot_SkipsDot()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Children = SampleChildren(out FileNode Folder);
            CollectionAssert.AreEqual(new[] { "..", "A.txt", "b.txt", "c.txt" }, Names(BuildListing(Folder, Children, ".")));
        }

        [TestMethod]
        public void BuildDirectoryListing_MarkerDotDot_SkipsBothDotEntries()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Children = SampleChildren(out FileNode Folder);
            CollectionAssert.AreEqual(new[] { "A.txt", "b.txt", "c.txt" }, Names(BuildListing(Folder, Children, "..")));
        }

        [DataTestMethod]
        [DataRow("A.txt", "b.txt|c.txt")]
        [DataRow("B.TXT", "c.txt")]
        [DataRow("b.txt0", "c.txt")]
        [DataRow("bb", "c.txt")]
        [DataRow("0", "A.txt|b.txt|c.txt")]
        [DataRow("c.txt", "")]
        [DataRow("zzz", "")]
        public void BuildDirectoryListing_NameMarker_ResumesAfterMarkerEvenIfMissing(String Marker, String ExpectedNames)
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Children = SampleChildren(out FileNode Folder);
            var Expected = ExpectedNames.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            CollectionAssert.AreEqual(Expected, Names(BuildListing(Folder, Children, Marker)));
        }

        [TestMethod]
        public void BuildDirectoryListing_AfterClear_DotDotIsCurrentFolder()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            Cm.Clear();
            var Folder = MakeFolder("/dav/folder");
            var Listing = BuildListing(Folder, new List<Tuple<String, FileNode>>(), null);
            CollectionAssert.AreEqual(new[] { ".", ".." }, Names(Listing));
            Assert.AreSame(Folder, Listing[1].Item2);
        }

        #endregion

        #region GetFolderContent

        [TestMethod]
        public void GetFolderContent_UnparsedFolder_ListsFromServerAndCaches()
        {
            SetupFolderOnServer();
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));
            Assert.IsFalse(Folder.IsParsed);

            var Result = Cm.GetFolderContent(Folder, null);

            Assert.IsTrue(Result.Success);
            Assert.IsNull(Result.ErrorMessage);
            CollectionAssert.AreEqual(new[] { ".", "..", "A.txt", "b.txt", "sub" }, Names(Result.Content));
            Assert.IsTrue(Folder.IsParsed);
            Assert.IsTrue((DateTime.Now - Folder.LastRefresh).TotalSeconds < 5);
            Assert.AreEqual(1, CountRequests("PROPFIND /dav/folder"));

            //Children are cached and the listing returns the cached instances
            foreach (var Entry in Result.Content.Where(x => x.Item1 != "." && x.Item1 != ".."))
            {
                Assert.AreSame(Entry.Item2, Cached(Entry.Item2.LocalPath), Entry.Item1);
            }
            Assert.AreEqual(3UL, Cached("\\folder\\b.txt").FileInfo.FileSize);
            Assert.IsTrue(IsDirectory(Cached("\\folder\\sub")));
            Assert.IsFalse(IsDirectory(Cached("\\folder\\a.txt")));
            Assert.IsNull(Cached("\\folder\\sub\\deep.txt"), "only direct children are listed");
            Assert.AreEqual(0, RunningRefreshCount(Cm), "no background refresh is needed for a fresh listing");
        }

        [TestMethod]
        public void GetFolderContent_UnparsedFolder_ReturnsAlreadyCachedInstances()
        {
            SetupFolderOnServer();
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));
            var KnownNode = Add(MakeFile("/dav/folder/b.txt", 3));

            var Result = Cm.GetFolderContent(Folder, null);

            Assert.IsTrue(Result.Success);
            Assert.AreSame(KnownNode, Result.Content.Single(x => x.Item1 == "b.txt").Item2);
            Assert.AreSame(KnownNode, Cached("\\folder\\b.txt"));
        }

        [TestMethod]
        public void GetFolderContent_Root_HasNoDotEntries()
        {
            Server.AddFile("/dav/z.txt", "z");
            Server.AddFolder("/dav/a");
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Root = Add(MakeRoot());

            var Result = Cm.GetFolderContent(Root, null);

            Assert.IsTrue(Result.Success);
            CollectionAssert.AreEqual(new[] { "a", "z.txt" }, Names(Result.Content));
        }

        [TestMethod]
        public void GetFolderContent_DotDotRefersToCachedParent()
        {
            SetupFolderOnServer();
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Root = Add(MakeRoot());
            var Folder = Add(MakeFolder("/dav/folder"));

            var Result = Cm.GetFolderContent(Folder, null);

            Assert.AreSame(Folder, Result.Content[0].Item2);
            Assert.AreSame(Root, Result.Content[1].Item2);
        }

        [TestMethod]
        public void GetFolderContent_ParsedFolder_ServedFromCacheWithoutServerRequest()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));
            Folder.IsParsed = true;
            Folder.LastRefresh = DateTime.Now;
            var B = Add(MakeFile("/dav/folder/b.txt"));
            var A = Add(MakeFile("/dav/folder/a.txt"));
            Add(MakeFolder("/dav/folder/sub"));
            Add(MakeFile("/dav/folder/sub/deep.txt"));
            Add(MakeFile("/dav/folderX/other.txt"));
            Add(MakeFile("/dav/root.txt"));

            var Result = Cm.GetFolderContent(Folder, null);

            Assert.IsTrue(Result.Success);
            CollectionAssert.AreEqual(new[] { ".", "..", "a.txt", "b.txt", "sub" }, Names(Result.Content));
            Assert.AreSame(A, Result.Content[2].Item2);
            Assert.AreSame(B, Result.Content[3].Item2);
            Assert.AreEqual(0, Server.Requests.Count, "no request to the server");
            Assert.AreEqual(0, RunningRefreshCount(Cm), "cache is fresh: no background refresh");
        }

        [TestMethod]
        public void GetFolderContent_ParsedRoot_ServedFromCache()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Root = Add(MakeRoot());
            Root.IsParsed = true;
            Root.LastRefresh = DateTime.Now;
            Add(MakeFile("/dav/a.txt"));
            Add(MakeFolder("/dav/f"));
            Add(MakeFile("/dav/f/inner.txt"));

            var Result = Cm.GetFolderContent(Root, null);

            CollectionAssert.AreEqual(new[] { "a.txt", "f" }, Names(Result.Content));
            Assert.AreEqual(0, Server.Requests.Count);
        }

        [TestMethod]
        public void GetFolderContent_SecondCall_DoesNotQueryTheServerAgain()
        {
            SetupFolderOnServer();
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));
            Cm.GetFolderContent(Folder, null);
            Server.AddFile("/dav/folder/new.txt", "n");

            var Result = Cm.GetFolderContent(Folder, null);

            Assert.IsTrue(Result.Success);
            CollectionAssert.AreEqual(new[] { ".", "..", "A.txt", "b.txt", "sub" }, Names(Result.Content));
            Assert.AreEqual(1, CountRequests("PROPFIND /dav/folder"));
        }

        [TestMethod]
        public void GetFolderContent_ExpiredCache_ServesCacheAndRefreshesInBackground()
        {
            SetupFolderOnServer();
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));
            Cm.GetFolderContent(Folder, null);

            Server.AddFile("/dav/folder/new.txt", "n");
            Client.Delete("/dav/folder/b.txt", false).GetAwaiter().GetResult();
            Folder.LastRefresh = DateTime.Now.AddSeconds(-60);

            var Stale = Cm.GetFolderContent(Folder, null);

            Assert.IsTrue(Stale.Success);
            CollectionAssert.AreEqual(new[] { ".", "..", "A.txt", "b.txt", "sub" }, Names(Stale.Content), "the stale content is returned immediately");

            Assert.IsTrue(WaitUntil(() => Cached("\\folder\\new.txt") != null && Cached("\\folder\\b.txt") == null && RunningRefreshCount(Cm) == 0), "background refresh did not complete");
            Assert.IsTrue((DateTime.Now - Folder.LastRefresh).TotalSeconds < 10);

            var Fresh = Cm.GetFolderContent(Folder, null);
            CollectionAssert.AreEqual(new[] { ".", "..", "A.txt", "new.txt", "sub" }, Names(Fresh.Content));
        }

        [TestMethod]
        public void GetFolderContent_WithMarker_ReturnsOnlyFollowingEntries()
        {
            SetupFolderOnServer();
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));

            var FromServer = Cm.GetFolderContent(Folder, "a.txt");
            CollectionAssert.AreEqual(new[] { "b.txt", "sub" }, Names(FromServer.Content));

            var FromCache = Cm.GetFolderContent(Folder, "b.txt");
            CollectionAssert.AreEqual(new[] { "sub" }, Names(FromCache.Content));

            var AfterDotDot = Cm.GetFolderContent(Folder, "..");
            CollectionAssert.AreEqual(new[] { "A.txt", "b.txt", "sub" }, Names(AfterDotDot.Content));

            var AfterDot = Cm.GetFolderContent(Folder, ".");
            CollectionAssert.AreEqual(new[] { "..", "A.txt", "b.txt", "sub" }, Names(AfterDot.Content));

            var DeletedMarker = Cm.GetFolderContent(Folder, "az");
            CollectionAssert.AreEqual(new[] { "b.txt", "sub" }, Names(DeletedMarker.Content));

            var Last = Cm.GetFolderContent(Folder, "sub");
            Assert.AreEqual(0, Last.Content.Count);
        }

        [TestMethod]
        public void GetFolderContent_Unauthorized_Returns401()
        {
            SetupFolderOnServer();
            Server.RejectCredentials = true;
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));

            var Result = Cm.GetFolderContent(Folder, null);

            Assert.IsFalse(Result.Success);
            Assert.IsNull(Result.Content);
            Assert.AreEqual("401", Result.ErrorMessage);
            Assert.IsFalse(Folder.IsParsed);
        }

        [TestMethod]
        public void GetFolderContent_ServerError_ReturnsFailureWithMessage()
        {
            SetupFolderOnServer();
            Server.ForcedStatus = (m, p) => HttpStatusCode.InternalServerError;
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));

            var Result = Cm.GetFolderContent(Folder, null);

            Assert.IsFalse(Result.Success);
            Assert.IsNull(Result.Content);
            Assert.IsFalse(String.IsNullOrEmpty(Result.ErrorMessage));
            Assert.AreNotEqual("401", Result.ErrorMessage);
            Assert.IsFalse(Folder.IsParsed);
            Assert.IsNull(Cached("\\folder\\b.txt"));
        }

        [TestMethod]
        public void GetFolderContent_NetworkFailure_ReturnsFailureWithMessage()
        {
            SetupFolderOnServer();
            Server.NetworkFailure = (m, p) => true;
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));

            var Result = Cm.GetFolderContent(Folder, null);

            Assert.IsFalse(Result.Success);
            Assert.IsNull(Result.Content);
            Assert.IsFalse(String.IsNullOrEmpty(Result.ErrorMessage));
            Assert.AreNotEqual("401", Result.ErrorMessage);
        }

        [TestMethod]
        public void GetFolderContent_DisabledCache_AlwaysListsFromServer()
        {
            SetupFolderOnServer();
            Cm = new CacheManager(CacheMode.Disabled, true, Client);
            var Folder = MakeFolder("/dav/folder");

            var First = Cm.GetFolderContent(Folder, null);
            Server.AddFile("/dav/folder/new.txt", "n");
            var Second = Cm.GetFolderContent(Folder, "b.txt");

            Assert.IsTrue(First.Success);
            CollectionAssert.AreEqual(new[] { ".", "..", "A.txt", "b.txt", "sub" }, Names(First.Content));
            Assert.AreSame(Folder, First.Content[1].Item2, "'..' is the current folder when nothing is cached");
            CollectionAssert.AreEqual(new[] { "new.txt", "sub" }, Names(Second.Content));
            Assert.AreEqual(2, CountRequests("PROPFIND /dav/folder"));
            Assert.IsFalse(Folder.IsParsed);
            Assert.AreEqual(0, RunningRefreshCount(Cm), "no pre-loading when the cache is disabled");
        }

        [TestMethod]
        public void GetFolderContent_DisabledCache_Unauthorized_Returns401()
        {
            Server.AddFolder("/dav/folder");
            Server.RejectCredentials = true;
            Cm = new CacheManager(CacheMode.Disabled, false, Client);

            var Result = Cm.GetFolderContent(MakeFolder("/dav/folder"), null);

            Assert.IsFalse(Result.Success);
            Assert.IsNull(Result.Content);
            Assert.AreEqual("401", Result.ErrorMessage);
        }

        [TestMethod]
        public void GetFolderContent_PreLoading_ListsUnparsedSubFoldersInBackground()
        {
            Server.AddFolder("/dav/f1");
            Server.AddFile("/dav/f1/x.txt", "x");
            Server.AddFolder("/dav/f2");
            Server.AddFile("/dav/file.txt", "f");
            Cm = new CacheManager(CacheMode.Enabled, true, Client);
            var Root = Add(MakeRoot());

            var Result = Cm.GetFolderContent(Root, null);

            Assert.IsTrue(Result.Success);
            var F1 = Result.Content.Single(x => x.Item1 == "f1").Item2;
            var F2 = Result.Content.Single(x => x.Item1 == "f2").Item2;
            Assert.IsTrue(WaitUntil(() => F1.IsParsed && F2.IsParsed && Cached("\\f1\\x.txt") != null && RunningRefreshCount(Cm) == 0), "sub-folders were not pre-loaded");
            Assert.AreEqual(0, CountRequests("PROPFIND /dav/file.txt"), "files are not pre-loaded");
            Assert.IsTrue(Root.IsParsed);
        }

        [TestMethod]
        public void GetFolderContent_WithoutPreLoading_DoesNotListSubFolders()
        {
            Server.AddFolder("/dav/f1");
            Server.AddFile("/dav/f1/x.txt", "x");
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Root = Add(MakeRoot());

            var Result = Cm.GetFolderContent(Root, null);

            Assert.AreEqual(0, RunningRefreshCount(Cm), "no refresh task is queued");
            Assert.IsFalse(Result.Content.Single(x => x.Item1 == "f1").Item2.IsParsed);
            Assert.AreEqual(0, CountRequests("PROPFIND /dav/f1"));
        }

        [TestMethod]
        public void GetFolderContent_PreLoading_SkipsAlreadyParsedSubFolders()
        {
            Cm = new CacheManager(CacheMode.Enabled, true, Client);
            var Root = Add(MakeRoot());
            Root.IsParsed = true;
            Root.LastRefresh = DateTime.Now;
            var F1 = Add(MakeFolder("/dav/f1"));
            F1.IsParsed = true;

            Cm.GetFolderContent(Root, null);

            Assert.AreEqual(0, RunningRefreshCount(Cm));
            Assert.AreEqual(0, Server.Requests.Count);
        }

        #endregion

        #region Background refresh (InternalRefreshFolderCacheContent / RefreshFolderCacheContent / AddRefreshTask)

        [TestMethod]
        public void Refresh_UnparsedFolder_AddsChildrenAndMarksParsed()
        {
            SetupFolderOnServer();
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));
            Folder.LastRefresh = DateTime.Now.AddDays(-1);
            int Events = 0;
            Cm.CacheRefreshed += (s, e) => Interlocked.Increment(ref Events);

            InvokePrivate(Cm, "InternalRefreshFolderCacheContent", Folder);

            Assert.IsTrue(Folder.IsParsed);
            Assert.IsTrue((DateTime.Now - Folder.LastRefresh).TotalSeconds < 5);
            Assert.IsNotNull(Cached("\\folder\\a.txt"));
            Assert.IsNotNull(Cached("\\folder\\b.txt"));
            Assert.IsNotNull(Cached("\\folder\\sub"));
            Assert.IsNull(Cached("\\folder\\sub\\deep.txt"));
            Assert.AreEqual(3, Events);
        }

        [TestMethod]
        public void Refresh_UpdatesFileInfoOfNodesWithoutUnflushedData()
        {
            Server.AddFolder("/dav/folder");
            Server.AddFile("/dav/folder/a.txt", "123456789");
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));
            var A = Add(MakeFile("/dav/folder/a.txt", 2));
            Assert.AreEqual(2UL, A.FileInfo.FileSize);

            InvokePrivate(Cm, "InternalRefreshFolderCacheContent", Folder);

            Assert.AreSame(A, Cached("\\folder\\a.txt"), "the cached instance is kept");
            Assert.AreEqual(9UL, A.FileInfo.FileSize);
        }

        [TestMethod]
        public void Refresh_KeepsFileInfoOfNodesWithUnflushedData()
        {
            Server.AddFolder("/dav/folder");
            Server.AddFile("/dav/folder/a.txt", "123456789");
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));
            var A = Add(MakeFile("/dav/folder/a.txt", 2));
            A.HasUnflushedData = true;

            InvokePrivate(Cm, "InternalRefreshFolderCacheContent", Folder);

            Assert.AreSame(A, Cached("\\folder\\a.txt"));
            Assert.AreEqual(2UL, A.FileInfo.FileSize);
        }

        [TestMethod]
        public void Refresh_RemovesNodesDeletedOnServer()
        {
            Server.AddFolder("/dav/folder");
            Server.AddFile("/dav/folder/kept.txt", "k");
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));
            var Kept = Add(MakeFile("/dav/folder/kept.txt", 1));
            Add(MakeFile("/dav/folder/gone.txt"));
            Add(MakeFolder("/dav/folder/gonedir"));
            Add(MakeFile("/dav/folder/gonedir/child.txt"));
            var Sibling = Add(MakeFile("/dav/other/x.txt"));

            InvokePrivate(Cm, "InternalRefreshFolderCacheContent", Folder);

            Assert.AreSame(Kept, Cached("\\folder\\kept.txt"));
            Assert.AreSame(Folder, Cached("\\folder"), "the refreshed folder itself is kept");
            Assert.IsNull(Cached("\\folder\\gone.txt"));
            Assert.IsNull(Cached("\\folder\\gonedir"));
            Assert.IsNull(Cached("\\folder\\gonedir\\child.txt"), "descendants of a deleted folder are removed too");
            Assert.AreSame(Sibling, Cached("\\other\\x.txt"));
        }

        [TestMethod]
        public void Refresh_Root_RemovesDeletedRootElementsAndKeepsRoot()
        {
            Server.AddFile("/dav/kept.txt", "k");
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Root = Add(MakeRoot());
            Add(MakeFile("/dav/gone.txt"));
            var Deep = Add(MakeFile("/dav/folder/deep.txt"));

            InvokePrivate(Cm, "InternalRefreshFolderCacheContent", Root);

            Assert.AreSame(Root, Cached("\\"));
            Assert.IsNotNull(Cached("\\kept.txt"));
            Assert.IsNull(Cached("\\gone.txt"));
            Assert.AreSame(Deep, Cached("\\folder\\deep.txt"), "only direct children are compared with the server listing");
            Assert.IsTrue(Root.IsParsed);
        }

        [TestMethod]
        public void Refresh_ServerFailure_LeavesCacheUntouchedAndDoesNotThrow()
        {
            Server.AddFolder("/dav/folder");
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));
            var A = Add(MakeFile("/dav/folder/a.txt"));
            Server.NetworkFailure = (m, p) => true;

            InvokePrivate(Cm, "InternalRefreshFolderCacheContent", Folder);

            Assert.AreSame(A, Cached("\\folder\\a.txt"));
            Assert.IsFalse(Folder.IsParsed);

            Server.NetworkFailure = null;
            Server.RejectCredentials = true;
            InvokePrivate(Cm, "InternalRefreshFolderCacheContent", Folder);
            Assert.AreSame(A, Cached("\\folder\\a.txt"));
        }

        [TestMethod]
        public void Refresh_ExceptionDuringUpdate_IsSwallowedAndReleasesLockAndRunningEntry()
        {
            Server.AddFolder("/dav/folder");
            Server.AddFile("/dav/folder/new.txt", "n");
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));
            Cm.CacheRefreshed += (s, e) => throw new InvalidOperationException("handler failure");
            object RunningLock = GetPrivateField<object>(Cm, "RunningRefreshActionListLock");
            lock (RunningLock) RunningRefreshSet(Cm).Add(Folder.LocalPath);

            InvokePrivate(Cm, "InternalRefreshFolderCacheContent", Folder); //Must not throw

            Assert.AreEqual(0, RunningRefreshCount(Cm));
            Assert.IsFalse(Monitor.IsEntered(StaticCacheLock()));
            Assert.IsTrue(Task.Run(() => { Cm.Lock(); Cm.Unlock(); }).Wait(5000), "the cache lock was released");
        }

        [TestMethod]
        public void Refresh_AfterClear_DoesNotThrow()
        {
            Server.AddFolder("/dav/folder");
            Server.AddFile("/dav/folder/a.txt", "a");
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = MakeFolder("/dav/folder");
            Cm.Clear();

            InvokePrivate(Cm, "InternalRefreshFolderCacheContent", Folder);

            Assert.IsTrue(Folder.IsParsed);
            Assert.AreEqual(0, RunningRefreshCount(Cm));
        }

        [TestMethod]
        public void InternalRefresh_RemovesTheRunningEntryAfterwards()
        {
            Server.AddFolder("/dav/folder");
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));
            object RunningLock = GetPrivateField<object>(Cm, "RunningRefreshActionListLock");
            lock (RunningLock) RunningRefreshSet(Cm).Add("\\FOLDER");

            InvokePrivate(Cm, "InternalRefreshFolderCacheContent", Folder);

            Assert.AreEqual(0, RunningRefreshCount(Cm), "the entry is removed (case-insensitively) once the refresh is over");
        }

        [TestMethod]
        public void AddRefreshTask_DoesNotQueueTheSameFolderTwice()
        {
            Server.AddFolder("/dav/folder");
            Server.AddFile("/dav/folder/a.txt", "a");
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = Add(MakeFolder("/dav/folder"));

            Cm.Lock(); //The refresh cannot update the cache (nor complete) while the lock is held
            try
            {
                InvokePrivate(Cm, "AddRefreshTask", Folder);
                InvokePrivate(Cm, "AddRefreshTask", MakeFolder("/dav/FOLDER"));
                Assert.AreEqual(1, RunningRefreshCount(Cm));
            }
            finally
            {
                Cm.Unlock();
            }

            Assert.IsTrue(WaitUntil(() => RunningRefreshCount(Cm) == 0 && Folder.IsParsed));
            Assert.AreEqual(1, CountRequests("PROPFIND /dav/folder"));
            Assert.IsNotNull(Cached("\\folder\\a.txt"));

            //Once finished, the folder can be refreshed again
            InvokePrivate(Cm, "AddRefreshTask", Folder);
            Assert.IsTrue(WaitUntil(() => RunningRefreshCount(Cm) == 0 && CountRequests("PROPFIND /dav/folder") == 2));
        }

        [TestMethod]
        public void RemoveRefreshTask_RemovesThePath()
        {
            Cm = new CacheManager(CacheMode.Enabled, false, Client);
            var Folder = MakeFolder("/dav/folder");
            object RunningLock = GetPrivateField<object>(Cm, "RunningRefreshActionListLock");
            lock (RunningLock) RunningRefreshSet(Cm).Add(Folder.LocalPath);

            InvokePrivate(Cm, "RemoveRefreshTask", Folder);

            Assert.AreEqual(0, RunningRefreshCount(Cm));
        }

        #endregion
    }
}
