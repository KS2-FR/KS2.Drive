using Fsp;
using KS2Drive.FS;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Net;

namespace KS2Drive.Tests.FS
{
    /// <summary>
    /// Fewer requests to the server, without losing data:
    /// (1) names absent from a fresh folder listing are answered without request; (3) the content of a closed file is reused by a
    /// new open for a short time; (5) a Flush right after an upload is deferred. Each test also checks the safety limits.
    /// </summary>
    [TestClass]
    public class DavFSPerformanceTests
    {
        private static Int32 GetSecurityByName(DavFsContext Ctx, String FileName)
        {
            Byte[] Descriptor = null;
            return Ctx.Fs.GetSecurityByName(FileName, out UInt32 Attributes, ref Descriptor);
        }

        private static DavFsContext CreateListedContext()
        {
            var Ctx = DavFsContext.Create(s => { s.AddFile("/dav/doc.docx", "content"); s.AddFolder("/dav/sub"); });
            Ctx.List(Ctx.Open("\\")); //Explorer shows the folder: its listing is cached
            Ctx.Server.Requests.Clear();
            return Ctx;
        }

        #region (1) Names absent from a fresh folder listing

        [TestMethod]
        public void ProbeOfANameAbsentFromAFreshListing_SendsNoRequest()
        {
            using (var Ctx = CreateListedContext())
            {
                foreach (String Name in new[] { "\\~WRD0001.tmp", "\\~$doc.docx", "\\desktop.ini.bak", "\\Thumbs.db" })
                {
                    Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, GetSecurityByName(Ctx, Name), Name);
                    Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, Ctx.TryOpen(Name, out FileNode Node), Name);
                }
                Assert.AreEqual(0, Ctx.Server.Requests.Count, String.Join(", ", Ctx.Server.Requests));
            }
        }

        [TestMethod]
        public void ProbeInAFolderNeverListed_StillAsksTheServer()
        {
            using (var Ctx = CreateListedContext())
            {
                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, GetSecurityByName(Ctx, "\\sub\\x.txt"));
                Assert.IsTrue(Ctx.Server.CountRequests("PROPFIND") > 0);
            }
        }

        [TestMethod]
        public void ProbeAfterTheListingExpired_StillAsksTheServer()
        {
            using (var Ctx = CreateListedContext())
            {
                Ctx.Open("\\").LastRefresh = DateTime.Now.AddMinutes(-1);
                Ctx.Server.AddFile("/dav/new.txt", "created by someone else");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, GetSecurityByName(Ctx, "\\new.txt"));
            }
        }

        [TestMethod]
        public void CreateAfterAProbeAnsweredByTheListing_StillChecksTheServer_SoNothingIsOverwritten()
        {
            //The listing may be up to 15 s old: Create uploads with a PUT, which would replace a file created meanwhile
            using (var Ctx = CreateListedContext())
            {
                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, GetSecurityByName(Ctx, "\\report.docx"));
                Ctx.Server.AddFile("/dav/report.docx", "someone else's report");

                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_COLLISION, Ctx.TryCreate("\\report.docx", false, out FileNode Node));
                Assert.AreEqual("someone else's report", Ctx.Server.GetText("/dav/report.docx"));
            }
        }

        [TestMethod]
        public void CreateOfAFreeName_ChecksTheServerWithASingleRequest()
        {
            using (var Ctx = CreateListedContext())
            {
                Ctx.Create("\\~WRD0001.tmp");
                CollectionAssert.AreEqual(new[] { "PROPFIND /dav/~WRD0001.tmp", "PUT /dav/~WRD0001.tmp" }, Ctx.Server.Requests);
            }
        }

        [TestMethod]
        public void RenameToANameAbsentFromAFreshListing_SendsOnlyTheMove()
        {
            using (var Ctx = CreateListedContext())
            {
                var Node = Ctx.Open("\\doc.docx");
                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Rename(Node, null, "\\doc.docx", "\\~WRL0002.tmp", true));
                CollectionAssert.AreEqual(new[] { "MOVE /dav/doc.docx" }, Ctx.Server.Requests);
                Assert.AreEqual("/dav/doc.docx -> /dav/~WRL0002.tmp (Overwrite: F)", Ctx.Server.MoveRequests.Last());
            }
        }

        [TestMethod]
        public void ExistsOnServer_FileFolderMissingAndErrors()
        {
            using (var Ctx = DavFsContext.Create(s => { s.AddFile("/dav/a.txt", "a"); s.AddFolder("/dav/v1.2"); }))
            {
                var Proxy = new WebDavClient2();
                Assert.IsTrue(Proxy.ExistsOnServer("\\a.txt"));
                Assert.IsTrue(Proxy.ExistsOnServer("\\v1.2"));
                Assert.IsFalse(Proxy.ExistsOnServer("\\missing"));
                Ctx.Server.ForcedStatus = (M, P) => P == "/dav/moved" ? (HttpStatusCode?)HttpStatusCode.MovedPermanently : null;
                Assert.IsTrue(Proxy.ExistsOnServer("\\moved"), "A redirection (folder URL without slash) means the name is taken");
                Ctx.Server.ForcedStatus = (M, P) => (HttpStatusCode?)HttpStatusCode.InternalServerError;
                Assert.ThrowsException<WebDAVClient.Helpers.WebDAVException>(() => Proxy.ExistsOnServer("\\a.txt"));
            }
        }

        #endregion

        #region (3) Content of a closed file reused by a new open

        private static FileNode ReadAndClose(DavFsContext Ctx, String FileName)
        {
            var Node = Ctx.Open(FileName);
            Ctx.ReadAll(Node);
            Ctx.CleanupAndClose(Node, FileName);
            return Node;
        }

        [TestMethod]
        public void ReopenRightAfterClose_DoesNotDownloadAgain()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/doc.docx", "content")))
            {
                ReadAndClose(Ctx, "\\doc.docx");
                ReadAndClose(Ctx, "\\doc.docx");
                var Node = Ctx.Open("\\doc.docx");
                Assert.AreEqual("content", Ctx.ReadAll(Node));

                Assert.AreEqual(1, Ctx.Server.CountRequests("GET"));
            }
        }

        [TestMethod]
        public void ReopenAfterTheRetentionDelay_DownloadsAgain()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/doc.docx", "v1")))
            {
                var Node = ReadAndClose(Ctx, "\\doc.docx");
                Node.ContentRetainedAt = DateTime.Now.AddMinutes(-1);
                Ctx.Server.AddFile("/dav/doc.docx", "v2");

                var Reopened = Ctx.Open("\\doc.docx");
                Assert.AreEqual("v2", Ctx.ReadAll(Reopened));
                Assert.AreEqual(2, Ctx.Server.CountRequests("GET"));
            }
        }

        [TestMethod]
        public void ReopenAfterTheFileChangedOnTheServer_DownloadsAgain()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/doc.docx", "v1")))
            {
                var Node = ReadAndClose(Ctx, "\\doc.docx");
                Ctx.Server.AddFile("/dav/doc.docx", "version 2");
                //What a refresh of the folder does: the node gets the server metadata
                Node.FileInfo.FileSize = (UInt64)"version 2".Length;

                var Reopened = Ctx.Open("\\doc.docx");
                Assert.AreEqual("version 2", Ctx.ReadAll(Reopened));
            }
        }

        [TestMethod]
        public void ContentWithChangesThatCouldNotBeUploaded_IsNotKept()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/doc.docx", "v1")))
            {
                var Node = Ctx.Open("\\doc.docx");
                Ctx.Write(Node, 0, "v2");
                Ctx.Server.ForcedStatus = (M, P) => M == "PUT" ? (HttpStatusCode?)HttpStatusCode.InternalServerError : null;
                Ctx.CleanupAndClose(Node, "\\doc.docx", DavFsContext.ModifiedFlags);
                Ctx.Server.ForcedStatus = null;
                try
                {
                    Assert.IsNull(Node.FileData, "Unsaved changes stay only in the recovery copy");
                    Assert.AreEqual("v1", Ctx.ReadAll(Ctx.Open("\\doc.docx")), "The server content is read again");
                }
                finally
                {
                    if (!String.IsNullOrEmpty(Node.TemporaryLocalCopyPath)) System.IO.File.Delete(Node.TemporaryLocalCopyPath);
                }
            }
        }

        [TestMethod]
        public void SavedContent_IsReusedAfterClose()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/doc.docx", "v1")))
            {
                var Node = Ctx.Open("\\doc.docx");
                Ctx.Write(Node, 0, "v2");
                Ctx.CleanupAndClose(Node, "\\doc.docx", DavFsContext.ModifiedFlags);
                Assert.AreEqual("v2", Ctx.Server.GetText("/dav/doc.docx"));
                Int32 GetsBefore = Ctx.Server.CountRequests("GET");

                Assert.AreEqual("v2", Ctx.ReadAll(Ctx.Open("\\doc.docx")));
                Assert.AreEqual(GetsBefore, Ctx.Server.CountRequests("GET"));
            }
        }

        [TestMethod]
        public void ClosedContents_AreReleasedWhenTheyExpire()
        {
            using (var Ctx = DavFsContext.Create(s => { s.AddFile("/dav/old.txt", "old"); s.AddFile("/dav/new.txt", "new"); }))
            {
                var Old = ReadAndClose(Ctx, "\\old.txt");
                Old.ContentRetainedAt = DateTime.Now.AddMinutes(-1);
                ReadAndClose(Ctx, "\\new.txt"); //Retaining another content purges the expired ones

                Assert.IsNull(Old.FileData);
            }
        }

        [TestMethod]
        public void ClosedContents_StayWithinTheMemoryBudget()
        {
            //Budget: 64 MB. Three closed files of 30 MB: the oldest one is released
            byte[] Big = new byte[30 * 1024 * 1024];
            using (var Ctx = DavFsContext.Create(s => { s.AddFile("/dav/1.bin", Big); s.AddFile("/dav/2.bin", Big); s.AddFile("/dav/3.bin", Big); }))
            {
                var First = ReadAndClose(Ctx, "\\1.bin");
                var Second = ReadAndClose(Ctx, "\\2.bin");
                var Third = ReadAndClose(Ctx, "\\3.bin");

                Assert.IsNull(First.FileData);
                Assert.IsNotNull(Second.FileData);
                Assert.IsNotNull(Third.FileData);
            }
        }

        #endregion

        #region (5) Flush right after an upload

        [TestMethod]
        public void FlushRightAfterTheCreation_IsDeferredToCleanup()
        {
            using (var Ctx = DavFsContext.Create())
            {
                var Node = Ctx.Create("\\new.docx");
                Ctx.Write(Node, 0, "content");
                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Flush(Node, null, out Fsp.Interop.FileInfo Info));
                Assert.AreEqual(1, Ctx.Server.CountRequests("PUT"), "Only the empty file of the creation");

                Ctx.CleanupAndClose(Node, "\\new.docx", DavFsContext.ModifiedFlags);
                Assert.AreEqual("content", Ctx.Server.GetText("/dav/new.docx"));
                Assert.AreEqual(2, Ctx.Server.CountRequests("PUT"));
            }
        }

        [TestMethod]
        public void FlushLongAfterTheLastUpload_Uploads()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/doc.docx", "v1")))
            {
                var Node = Ctx.Open("\\doc.docx");
                Ctx.Write(Node, 0, "v2");
                Node.LastUploadAt = DateTime.Now.AddMinutes(-1);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Flush(Node, null, out Fsp.Interop.FileInfo Info));
                Assert.AreEqual("v2", Ctx.Server.GetText("/dav/doc.docx"));
            }
        }

        [TestMethod]
        public void DeferredFlush_ChangesAreUploadedAtCloseEvenWithoutCleanup()
        {
            using (var Ctx = DavFsContext.Create())
            {
                var Node = Ctx.Create("\\new.docx");
                Ctx.Write(Node, 0, "content");
                Ctx.Fs.Flush(Node, null, out Fsp.Interop.FileInfo Info);
                Ctx.Fs.Close(Node, null);

                Assert.AreEqual("content", Ctx.Server.GetText("/dav/new.docx"));
            }
        }

        #endregion
    }
}
