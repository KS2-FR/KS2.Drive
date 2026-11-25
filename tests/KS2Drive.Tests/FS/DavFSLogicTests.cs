using Fsp;
using KS2Drive.Config;
using KS2Drive.FS;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Net;

namespace KS2Drive.Tests.FS
{
    /// <summary>
    /// File system operations aligned on NTFS semantics:
    /// (1) a non-empty directory cannot be deleted (a WebDAV DELETE on a collection is recursive);
    /// (2) a rename with ReplaceIfExists replaces a file atomically (MOVE with "Overwrite: T"), never a directory;
    /// (4) a created file exists on the server only from its first upload, which never replaces a file created meanwhile;
    /// (5) FlushAtWrite is no longer applied.
    /// </summary>
    [TestClass]
    public class DavFSLogicTests
    {
        #region (1) CanDelete

        [TestMethod]
        public void CanDelete_File_IsAllowedWithoutRequest()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Server.Requests.Clear();
                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.CanDelete(Node, null, "\\a.txt"));
                Assert.AreEqual(0, Ctx.Server.Requests.Count);
            }
        }

        [TestMethod]
        public void CanDelete_EmptyDirectory_IsAllowed()
        {
            using (var Ctx = CreateContext())
            {
                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.CanDelete(Ctx.Open("\\empty"), null, "\\empty"));
            }
        }

        [TestMethod]
        public void CanDelete_NonEmptyDirectory_IsRefused()
        {
            using (var Ctx = CreateContext())
            {
                Assert.AreEqual(FileSystemBase.STATUS_DIRECTORY_NOT_EMPTY, Ctx.Fs.CanDelete(Ctx.Open("\\full"), null, "\\full"));
            }
        }

        [TestMethod]
        public void CanDelete_DirectoryListedEmpty_ButFilledByAnotherClientMeanwhile_IsRefused()
        {
            //The server is asked, not the cache: the DELETE would remove the colleague's file
            using (var Ctx = CreateContext())
            {
                var Folder = Ctx.Open("\\empty");
                CollectionAssert.AreEqual(new String[0], Ctx.List(Folder).Where(x => x != "." && x != "..").ToArray());
                Ctx.Server.AddFile("/dav/empty/colleague.docx", "work");

                Assert.AreEqual(FileSystemBase.STATUS_DIRECTORY_NOT_EMPTY, Ctx.Fs.CanDelete(Folder, null, "\\empty"));
            }
        }

        [TestMethod]
        public void CanDelete_DirectoryWithAFileNotUploadedYet_IsRefused()
        {
            using (var Ctx = CreateContext())
            {
                var Folder = Ctx.Open("\\empty");
                Ctx.Create("\\empty\\new.txt");
                Assert.AreEqual(FileSystemBase.STATUS_DIRECTORY_NOT_EMPTY, Ctx.Fs.CanDelete(Folder, null, "\\empty"));
            }
        }

        [TestMethod]
        public void CanDelete_DirectoryAlreadyDeletedOnTheServer_IsAllowed()
        {
            using (var Ctx = CreateContext())
            {
                var Folder = Ctx.Open("\\full");
                Ctx.Server.Remove("/dav/full");
                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.CanDelete(Folder, null, "\\full"));
            }
        }

        [TestMethod]
        public void CanDelete_DirectoryThatCannotBeChecked_IsRefused()
        {
            using (var Ctx = CreateContext())
            {
                var Folder = Ctx.Open("\\empty");
                Ctx.Server.ForcedStatus = (M, P) => (HttpStatusCode?)HttpStatusCode.InternalServerError;
                Assert.AreEqual(FileSystemBase.STATUS_ACCESS_DENIED, Ctx.Fs.CanDelete(Folder, null, "\\empty"));
                Ctx.Server.ForcedStatus = null;
                Ctx.Server.NetworkFailure = (M, P) => true;
                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Ctx.Fs.CanDelete(Folder, null, "\\empty"));
            }
        }

        [TestMethod]
        public void CanDelete_401_IsRefusedAndNotified()
        {
            using (var Ctx = CreateContext())
            {
                var Folder = Ctx.Open("\\empty");
                Ctx.Server.RejectCredentials = true;
                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Ctx.Fs.CanDelete(Folder, null, "\\empty"));
                Assert.AreEqual(1, Ctx.AuthenticationFailures);
            }
        }

        #endregion

        #region (2) Rename with ReplaceIfExists

        [DataTestMethod]
        [DataRow(WebDAVMode.WebDAV)]
        [DataRow(WebDAVMode.AOS)]
        public void Rename_ReplaceExistingFile_IsASingleAtomicMove(WebDAVMode Mode)
        {
            //What editors do to save safely: write a temporary file, then rename it over the original
            using (var Ctx = CreateContext(Mode))
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Open("\\b.txt");
                Ctx.Server.Requests.Clear();

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Rename(Node, null, "\\a.txt", "\\b.txt", true));

                Assert.AreEqual("abc", Ctx.Server.GetText("/dav/b.txt"));
                Assert.IsFalse(Ctx.Server.Exists("/dav/a.txt"));
                Assert.AreEqual(0, Ctx.Server.CountRequests("DELETE"), "No separate DELETE: the replacement is atomic");
                Assert.AreEqual("/dav/a.txt -> /dav/b.txt (Overwrite: T)", Ctx.Server.MoveRequests.Single());
                Assert.AreEqual("\\b.txt", Node.LocalPath);
                Assert.AreSame(Node, Ctx.Open("\\b.txt"), "The renamed node replaces the old one in the cache");
            }
        }

        [TestMethod]
        public void Rename_ExistingTarget_WithoutReplace_IsACollision()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_COLLISION, Ctx.Fs.Rename(Node, null, "\\a.txt", "\\b.txt", false));
                Assert.AreEqual("bbbb", Ctx.Server.GetText("/dav/b.txt"));
                Assert.AreEqual(0, Ctx.Server.CountRequests("MOVE"));
            }
        }

        [TestMethod]
        public void Rename_FileOverADirectory_IsRefused_AndTheDirectoryIsKept()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Assert.AreEqual(FileSystemBase.STATUS_ACCESS_DENIED, Ctx.Fs.Rename(Node, null, "\\a.txt", "\\full", true));
                Assert.IsTrue(Ctx.Server.Exists("/dav/full/x.txt"));
                Assert.AreEqual(0, Ctx.Server.CountRequests("MOVE"));
            }
        }

        [TestMethod]
        public void Rename_DirectoryOverAFile_IsRefused()
        {
            using (var Ctx = CreateContext())
            {
                var Folder = Ctx.Open("\\full");
                Assert.AreEqual(FileSystemBase.STATUS_ACCESS_DENIED, Ctx.Fs.Rename(Folder, null, "\\full", "\\a.txt", true));
                Assert.AreEqual("abc", Ctx.Server.GetText("/dav/a.txt"));
                Assert.IsTrue(Ctx.Server.Exists("/dav/full/x.txt"));
            }
        }

        [TestMethod]
        public void Rename_ReplaceTargetCreatedAfterTheListing_IsReplacedAsRequested()
        {
            //The listing says the target is free (no check), the server answers 412 to "Overwrite: F": replace as requested
            using (var Ctx = CreateContext())
            {
                Ctx.List(Ctx.Open("\\"));
                var Node = Ctx.Open("\\a.txt");
                Ctx.Server.AddFile("/dav/late.txt", "late");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Rename(Node, null, "\\a.txt", "\\late.txt", true));

                Assert.AreEqual("abc", Ctx.Server.GetText("/dav/late.txt"));
                CollectionAssert.AreEqual(new[] { "/dav/a.txt -> /dav/late.txt (Overwrite: T)" }, Ctx.Server.MoveRequests, String.Join(", ", Ctx.Server.MoveRequests));
            }
        }

        [TestMethod]
        public void Rename_TargetCreatedAfterTheListing_WithoutReplace_IsACollision()
        {
            using (var Ctx = CreateContext())
            {
                Ctx.List(Ctx.Open("\\"));
                var Node = Ctx.Open("\\a.txt");
                Ctx.Server.AddFile("/dav/late.txt", "late");

                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_COLLISION, Ctx.Fs.Rename(Node, null, "\\a.txt", "\\late.txt", false));
                Assert.AreEqual("late", Ctx.Server.GetText("/dav/late.txt"));
            }
        }

        #endregion

        #region (4) File created on the drive: on the server from its first upload

        [TestMethod]
        public void NewFile_FirstUpload_NeverReplacesAFileCreatedMeanwhile()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Create("\\report.docx");
                Ctx.Write(Node, 0, "mine");
                Ctx.Server.AddFile("/dav/report.docx", "colleague's report"); //Created meanwhile by another client
                try
                {
                    Ctx.CleanupAndClose(Node, "\\report.docx", DavFsContext.ModifiedFlags);

                    Assert.AreEqual("colleague's report", Ctx.Server.GetText("/dav/report.docx"));
                    var Failure = Ctx.Log.Last(x => x.Result == "STATUS_FAILED");
                    Assert.IsTrue(File.Exists(Failure.LocalTemporaryPath), "My content is kept in a recovery copy");
                    Assert.AreEqual("mine", File.ReadAllText(Failure.LocalTemporaryPath));
                }
                finally
                {
                    foreach (var Entry in Ctx.Log.Where(x => !String.IsNullOrEmpty(x.LocalTemporaryPath))) File.Delete(Entry.LocalTemporaryPath);
                }
            }
        }

        [TestMethod]
        public void NewFile_LaterUploads_ReplaceItsOwnPreviousVersion()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Create("\\new.txt");
                Ctx.Write(Node, 0, "v1");
                Ctx.CleanupAndClose(Node, "\\new.txt", DavFsContext.ModifiedFlags);

                var Again = Ctx.Open("\\new.txt");
                Ctx.Write(Again, 0, "v2");
                Ctx.CleanupAndClose(Again, "\\new.txt", DavFsContext.ModifiedFlags);

                Assert.AreEqual("v2", Ctx.Server.GetText("/dav/new.txt"));
            }
        }

        [TestMethod]
        public void NewFile_DeletedBeforeItsFirstUpload_SendsNoRequest()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Create("\\tmp.txt");
                Ctx.Server.Requests.Clear();

                Ctx.CleanupAndClose(Node, "\\tmp.txt", 1); //CleanupDelete

                Assert.AreEqual(0, Ctx.Server.Requests.Count, String.Join(", ", Ctx.Server.Requests));
                Assert.IsFalse(Ctx.Server.Exists("/dav/tmp.txt"));
                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, Ctx.TryOpen("\\tmp.txt", out FileNode Gone));
            }
        }

        [TestMethod]
        public void NewFile_RenamedBeforeItsFirstUpload_EndsUpUnderTheNewName()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Create("\\~WRD0001.tmp");
                Ctx.Write(Node, 0, "content");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Rename(Node, null, "\\~WRD0001.tmp", "\\doc.docx", false));
                Ctx.CleanupAndClose(Node, "\\doc.docx", DavFsContext.ModifiedFlags);

                Assert.AreEqual("content", Ctx.Server.GetText("/dav/doc.docx"));
                Assert.IsFalse(Ctx.Server.Exists("/dav/~WRD0001.tmp"));
            }
        }

        [TestMethod]
        public void NewFile_IsListed_AndSurvivesARefreshOfItsFolder()
        {
            using (var Ctx = CreateContext())
            {
                var Folder = Ctx.Open("\\empty");
                Ctx.Create("\\empty\\new.txt");

                CollectionAssert.Contains(Ctx.List(Folder), "new.txt", "Listed although not on the server yet");

                Folder.LastRefresh = DateTime.Now.AddMinutes(-1); //Expired: the next listing refreshes it from the server
                Ctx.List(Folder);
                System.Threading.Thread.Sleep(500); //Background refresh
                CollectionAssert.Contains(Ctx.List(Folder), "new.txt");
                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.TryOpen("\\empty\\new.txt", out FileNode Node));
            }
        }

        #endregion

        #region (5) FlushAtWrite no longer applied

        [TestMethod]
        public void FlushAtWriteConfiguration_DoesNotUploadOnEachWrite()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/a.txt", "abc"), FlushMode.FlushAtWrite))
            {
                var Node = Ctx.Open("\\a.txt");
                for (int i = 0; i < 10; i++) Ctx.Write(Node, (UInt64)i, "x");
                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));

                Ctx.CleanupAndClose(Node, "\\a.txt", DavFsContext.ModifiedFlags);
                Assert.AreEqual(1, Ctx.Server.CountRequests("PUT"));
                Assert.AreEqual("xxxxxxxxxx", Ctx.Server.GetText("/dav/a.txt"));
            }
        }

        #endregion

        #region Private methods

        private static DavFsContext CreateContext(WebDAVMode Mode = WebDAVMode.WebDAV)
        {
            return DavFsContext.Create(s =>
            {
                s.AddFile("/dav/a.txt", "abc");
                s.AddFile("/dav/b.txt", "bbbb");
                s.AddFolder("/dav/empty");
                s.AddFolder("/dav/full");
                s.AddFile("/dav/full/x.txt", "x");
            }, FlushMode.FlushAtCleanup, Mode);
        }

        #endregion
    }
}
