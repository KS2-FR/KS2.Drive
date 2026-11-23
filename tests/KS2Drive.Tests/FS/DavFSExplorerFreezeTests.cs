using Fsp;
using KS2Drive.FS;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace KS2Drive.Tests.FS
{
    /// <summary>
    /// Regression tests for "Explorer freezes while Office saves a document on an AOS drive".
    /// Causes: (1) the global cache lock was held during requests to the server, so every operation (e.g. Explorer listing a folder)
    /// waited for each request of another operation; (2) Create and Rename made 3 to 4 round trips while WinFSP holds a lock on the
    /// parent folder / the volume, although the cache already knew the names were free (Office probes its temporary names first).
    /// Part (2) as seen by Explorer depends on the WinFSP driver and is only covered by the Word harness; here the request counts are checked.
    /// </summary>
    [TestClass]
    public class DavFSExplorerFreezeTests
    {
        private static readonly TimeSpan NotBlocked = TimeSpan.FromSeconds(2);

        private static Int32 GetSecurityByName(DavFsContext Ctx, String FileName)
        {
            Byte[] Descriptor = null;
            return Ctx.Fs.GetSecurityByName(FileName, out UInt32 Attributes, ref Descriptor);
        }

        /// <summary>
        /// Starts <paramref name="SlowOperation"/> (whose request to the server is held), then checks that a cached operation
        /// completes while that request is still pending.
        /// </summary>
        private static void AssertCachedOperationIsNotBlocked(DavFsContext Ctx, Func<String, String, bool> Hold, Action SlowOperation, Func<Int32> CachedOperation)
        {
            Ctx.Server.HoldRequest = Hold;
            var Slow = Task.Run(SlowOperation);
            try
            {
                Assert.IsTrue(Ctx.Server.RequestHeld.Wait(TimeSpan.FromSeconds(10)), "The slow operation did not reach the server");

                var Cached = Task.Run(CachedOperation);
                Assert.IsTrue(Cached.Wait(NotBlocked), "A cached operation waited for a request of another operation (cache lock held during the request)");
                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Cached.Result);
            }
            finally
            {
                Ctx.Server.ReleaseHeldRequests.Set();
                Assert.IsTrue(Slow.Wait(TimeSpan.FromSeconds(10)), "The slow operation did not complete once released");
            }
        }

        [TestMethod]
        public void Open_DoesNotHoldTheCacheLockDuringTheServerRequest()
        {
            using (var Ctx = DavFsContext.Create(s => { s.AddFile("/dav/cached.txt", "a"); s.AddFile("/dav/slow.txt", "b"); }))
            {
                Ctx.Open("\\cached.txt");
                AssertCachedOperationIsNotBlocked(Ctx,
                    (Method, Path) => Method == "PROPFIND" && Path == "/dav/slow.txt",
                    () => Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.TryOpen("\\slow.txt", out FileNode Node)),
                    () => GetSecurityByName(Ctx, "\\cached.txt"));
            }
        }

        [TestMethod]
        public void GetSecurityByName_DoesNotHoldTheCacheLockDuringTheServerRequest()
        {
            using (var Ctx = DavFsContext.Create(s => { s.AddFile("/dav/cached.txt", "a"); s.AddFile("/dav/slow.txt", "b"); }))
            {
                Ctx.Open("\\cached.txt");
                AssertCachedOperationIsNotBlocked(Ctx,
                    (Method, Path) => Method == "PROPFIND" && Path == "/dav/slow.txt",
                    () => Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, GetSecurityByName(Ctx, "\\slow.txt")),
                    () => Ctx.TryOpen("\\cached.txt", out FileNode Node));
            }
        }

        [TestMethod]
        public void FirstListingOfAFolder_DoesNotHoldTheCacheLockDuringTheServerRequest()
        {
            using (var Ctx = DavFsContext.Create(s => { s.AddFile("/dav/cached.txt", "a"); s.AddFolder("/dav/sub"); s.AddFile("/dav/sub/x.txt", "x"); }))
            {
                Ctx.Open("\\cached.txt");
                var Sub = Ctx.Open("\\sub");
                AssertCachedOperationIsNotBlocked(Ctx,
                    (Method, Path) => Method == "PROPFIND" && Path == "/dav/sub",
                    () => CollectionAssert.Contains(Ctx.List(Sub), "x.txt"),
                    () => GetSecurityByName(Ctx, "\\cached.txt"));
            }
        }

        [TestMethod]
        public void ConcurrentLookupsOfTheSameFile_ShareOneFileNode()
        {
            //The server request is no longer made under the cache lock: two lookups can race, they must still end with one node
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/a.txt", "a")))
            {
                var Nodes = Enumerable.Range(0, 8).AsParallel().Select(i => { Ctx.TryOpen(i % 2 == 0 ? "\\a.txt" : "\\A.TXT", out FileNode Node); return Node; }).ToList();
                Assert.AreEqual(1, Nodes.Distinct().Count());
                Assert.AreEqual(8, Nodes[0].OpenCount);
            }
        }

        [TestMethod]
        public void Create_OfANameKnownToBeMissing_SendsOnlyThePut()
        {
            using (var Ctx = DavFsContext.Create())
            {
                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, GetSecurityByName(Ctx, "\\~WRD0001.tmp")); //What Office does first
                Ctx.Server.Requests.Clear();

                var Node = Ctx.Create("\\~WRD0001.tmp");

                CollectionAssert.AreEqual(new[] { "PUT /dav/~WRD0001.tmp" }, Ctx.Server.Requests);
                Assert.AreEqual("\\~WRD0001.tmp", Node.LocalPath);
                Assert.IsTrue(Ctx.Server.Exists("/dav/~WRD0001.tmp"));
            }
        }

        [TestMethod]
        public void Create_OfAnUnknownName_StillChecksTheServer()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/existing.txt", "keep me")))
            {
                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_COLLISION, Ctx.TryCreate("\\existing.txt", false, out FileNode Node));
                Assert.AreEqual("keep me", Ctx.Server.GetText("/dav/existing.txt"));
                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));
            }
        }

        [TestMethod]
        public void Rename_ToANameKnownToBeMissing_SendsOnlyTheMoveWithOverwriteF()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/~WRD0001.tmp", "new content")))
            {
                var Node = Ctx.Open("\\~WRD0001.tmp");
                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, GetSecurityByName(Ctx, "\\doc.docx"));
                Ctx.Server.Requests.Clear();

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Rename(Node, null, "\\~WRD0001.tmp", "\\doc.docx", true));

                CollectionAssert.AreEqual(new[] { "MOVE /dav/~WRD0001.tmp" }, Ctx.Server.Requests);
                Assert.AreEqual("/dav/~WRD0001.tmp -> /dav/doc.docx (Overwrite: F)", Ctx.Server.MoveRequests.Last());
                Assert.AreEqual("new content", Ctx.Server.GetText("/dav/doc.docx"));
            }
        }

        [TestMethod]
        public void Rename_TargetCreatedByAnotherClientMeanwhile_IsACollisionAndNothingIsOverwritten()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/a.txt", "mine")))
            {
                var Node = Ctx.Open("\\a.txt");
                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, GetSecurityByName(Ctx, "\\b.txt")); //Cached as missing
                Ctx.Server.AddFile("/dav/b.txt", "someone else's"); //Created by another client after the probe

                Int32 Status = Ctx.Fs.Rename(Node, null, "\\a.txt", "\\b.txt", false);

                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_COLLISION, Status);
                Assert.AreEqual("someone else's", Ctx.Server.GetText("/dav/b.txt"));
                Assert.AreEqual("mine", Ctx.Server.GetText("/dav/a.txt"));
                Assert.AreEqual("\\a.txt", Node.LocalPath);
            }
        }

        [TestMethod]
        public void Rename_RemembersTheOldNameAsMissing()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/doc.docx", "x")))
            {
                var Node = Ctx.Open("\\doc.docx");
                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Rename(Node, null, "\\doc.docx", "\\~WRL0002.tmp", false));
                Ctx.Server.Requests.Clear();

                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, GetSecurityByName(Ctx, "\\doc.docx"));
                Assert.AreEqual(0, Ctx.Server.Requests.Count, "The old name is known to be missing: no request");
            }
        }

        [TestMethod]
        public void OfficeSaveSequence_UsesFewRequests()
        {
            //Word saving an existing document: probe temp name, create it, write, rename original away, rename temp to original, delete old
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/doc.docx", "v1")))
            {
                Ctx.CleanupAndClose(Ctx.Open("\\doc.docx"), "\\doc.docx");
                Ctx.Server.Requests.Clear();

                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, GetSecurityByName(Ctx, "\\~WRD0001.tmp"));
                var Temp = Ctx.Create("\\~WRD0001.tmp");
                Ctx.Write(Temp, 0, "v2");
                Ctx.CleanupAndClose(Temp, "\\~WRD0001.tmp", DavFsContext.ModifiedFlags);
                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, GetSecurityByName(Ctx, "\\~WRL0002.tmp"));
                var Original = Ctx.Open("\\doc.docx");
                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Rename(Original, null, "\\doc.docx", "\\~WRL0002.tmp", true));
                var TempAgain = Ctx.Open("\\~WRD0001.tmp");
                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Rename(TempAgain, null, "\\~WRD0001.tmp", "\\doc.docx", true));
                Ctx.CleanupAndClose(Original, "\\~WRL0002.tmp", 1); //CleanupDelete
                Ctx.Fs.Close(TempAgain, null);

                Assert.AreEqual("v2", Ctx.Server.GetText("/dav/doc.docx"));
                Assert.IsFalse(Ctx.Server.Exists("/dav/~WRL0002.tmp"));
                Assert.IsFalse(Ctx.Server.Exists("/dav/~WRD0001.tmp"));
                //Before the fix: 2 PROPFIND per probe, 2 more + 1 read-back in Create, 2 more before each MOVE
                Assert.AreEqual(4, Ctx.Server.CountRequests("PROPFIND"), String.Join(", ", Ctx.Server.Requests));
                Assert.AreEqual(2, Ctx.Server.CountRequests("MOVE"));
            }
        }
    }
}
