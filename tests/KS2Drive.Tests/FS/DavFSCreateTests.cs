using Fsp;
using KS2Drive.FS;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net;
using static KS2Drive.Tests.FS.DavFSTestHelpers;
using FileInfo = Fsp.Interop.FileInfo;

namespace KS2Drive.Tests.FS
{
    /// <summary>
    /// Create (and GetCreatedFileNode) and Overwrite.
    /// </summary>
    [TestClass]
    public class DavFSCreateTests
    {
        private const UInt32 DirectoryAttribute = (UInt32)System.IO.FileAttributes.Directory;
        private const UInt32 ArchiveAttribute = (UInt32)System.IO.FileAttributes.Archive;
        private const UInt32 HiddenAttribute = (UInt32)System.IO.FileAttributes.Hidden;
        private const UInt32 NormalAttribute = (UInt32)System.IO.FileAttributes.Normal;

        private static DavFsContext CreateContext(FlushMode Flush = FlushMode.FlushAtCleanup)
        {
            return DavFsContext.Create(s =>
            {
                s.AddFile("/dav/a.txt", "abcdef");
                s.AddFolder("/dav/folder");
            }, Flush);
        }

        #region Create

        [TestMethod]
        public void Create_File_IsCreatedOnTheServerByItsFirstUpload()
        {
            using (var Ctx = CreateContext())
            {
                Int32 Status = Ctx.Fs.Create("\\new.txt", 0, 0, NormalAttribute, null, 0, out Object Node0, out Object Desc, out FileInfo Info, out String Normalized);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                var Node = (FileNode)Node0;
                Assert.IsNull(Desc);
                Assert.AreEqual("\\new.txt", Normalized);
                Assert.AreEqual(0UL, Info.FileSize);
                Assert.AreEqual(0u, Info.FileAttributes & DirectoryAttribute);
                Assert.AreEqual("\\new.txt", Node.LocalPath);
                Assert.AreEqual("/dav/new.txt", Node.RepositoryPath);
                Assert.AreEqual(1, Node.OpenCount);
                Assert.IsTrue(Node.HasUnflushedData, "The new file must be uploaded at cleanup even if nothing is written");
                Assert.IsTrue(Node.IsNewOnServer);

                //No empty file on the server: it is created by the first upload
                Assert.IsFalse(Ctx.Server.Exists("/dav/new.txt"));
                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));

                Ctx.CleanupAndClose(Node, "\new.txt", DavFsContext.ModifiedFlags);
                Assert.IsTrue(Ctx.Server.Exists("/dav/new.txt"));
                Assert.IsFalse(Ctx.Server.IsFolder("/dav/new.txt"));
                Assert.AreEqual(0, Ctx.Server.GetContent("/dav/new.txt").Length);
                Assert.AreEqual(1, Ctx.Server.CountRequests("PUT"));
                Assert.IsFalse(Node.IsNewOnServer);

                var L = LastLog(Ctx, "Create");
                Assert.IsNotNull(L);
                Assert.AreEqual("STATUS_SUCCESS", L.Result);
                Assert.AreEqual("\\new.txt", L.File);
                Assert.AreEqual(Node.ObjectId, L.Object);
            }
        }

        [TestMethod]
        public void Create_FileThenWrite_UploadsTheContentAtCleanup()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Create("\\new.txt");
                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Write(Node, 0, "hello"));
                Assert.AreEqual(0, Ctx.Server.CountRequests("GET"), "Nothing to download for a new file");

                Ctx.CleanupAndClose(Node, "\\new.txt", DavFsContext.ModifiedFlags);

                Assert.AreEqual("hello", Ctx.Server.GetText("/dav/new.txt"));
            }
        }

        [TestMethod]
        public void Create_FileInASubFolder_CreatesItInThatFolder()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Create("\\folder\\new.txt");

                Assert.AreEqual("/dav/folder/new.txt", Node.RepositoryPath);
                Ctx.CleanupAndClose(Node, "\folder\new.txt");
                Assert.IsTrue(Ctx.Server.Exists("/dav/folder/new.txt"));
            }
        }

        [TestMethod]
        public void Create_Folder_CreatesTheFolderOnTheServer()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Create("\\newfolder", true);

                Assert.AreNotEqual(0u, Node.FileInfo.FileAttributes & DirectoryAttribute);
                Assert.AreEqual("\\newfolder", Node.LocalPath);
                Assert.AreEqual(1, Node.OpenCount);
                Assert.IsFalse(Node.HasUnflushedData);
                Assert.IsTrue(Ctx.Server.IsFolder("/dav/newfolder"));
                Assert.AreEqual(1, Ctx.Server.CountRequests("MKCOL"));
                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));
                Assert.AreEqual("STATUS_SUCCESS", LastLog(Ctx, "Create").Result);

                Ctx.CleanupAndClose(Node, "\\newfolder");
                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"), "Nothing to upload for a folder");
            }
        }

        [TestMethod]
        public void Create_NodeIsCached_AndReplacesAKnownMissingEntry()
        {
            using (var Ctx = CreateContext())
            {
                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, Ctx.TryOpen("\\new.txt", out FileNode Missing));

                var Created = Ctx.Create("\\new.txt");
                var Opened = Ctx.Open("\\NEW.TXT");

                Assert.AreSame(Created, Opened);
                Assert.AreEqual(2, Created.OpenCount);
            }
        }

        [TestMethod]
        public void Create_ExistingFile_ReturnsCollisionWithoutTouchingIt()
        {
            using (var Ctx = CreateContext())
            {
                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_COLLISION, Ctx.TryCreate("\\a.txt", false, out FileNode Node));

                Assert.IsNull(Node);
                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));
                Assert.AreEqual("abcdef", Ctx.Server.GetText("/dav/a.txt"));
            }
        }

        [TestMethod]
        public void Create_ExistingFolder_ReturnsCollision()
        {
            using (var Ctx = CreateContext())
            {
                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_COLLISION, Ctx.TryCreate("\\folder", true, out FileNode Node));
                Assert.AreEqual(0, Ctx.Server.CountRequests("MKCOL"));
            }
        }

        [TestMethod]
        public void Create_ExistingFileWithAnotherCase_ReturnsCollision()
        {
            using (var Ctx = CreateContext())
            {
                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_COLLISION, Ctx.TryCreate("\\A.TXT", false, out FileNode Node));
                Assert.AreEqual("abcdef", Ctx.Server.GetText("/dav/a.txt"));
            }
        }

        [DataTestMethod]
        [DataRow(true)] //A file is created on the server by its first upload, see Create_File_IsCreatedOnTheServerByItsFirstUpload
        public void Create_Conflict_ReturnsAccessDenied(bool IsDirectory)
        {
            using (var Ctx = CreateContext())
            {
                //The parent folder does not exist: the server answers 409
                Assert.AreEqual(FileSystemBase.STATUS_ACCESS_DENIED, Ctx.TryCreate("\\missing\\new", IsDirectory, out FileNode Node));

                Assert.IsNull(Node);
                Assert.AreEqual("STATUS_ACCESS_DENIED", LastLog(Ctx, "Create").Result);
                Assert.IsFalse(Ctx.Server.Exists("/dav/missing/new"));
            }
        }

        [TestMethod]
        public void Create_NetworkFailureWhileCheckingExistence_ReturnsNetworkUnreachable()
        {
            using (var Ctx = CreateContext())
            {
                Ctx.Server.NetworkFailure = NetworkDown("PROPFIND");

                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Ctx.TryCreate("\\new.txt", false, out FileNode Node));

                Assert.AreEqual("STATUS_NETWORK_UNREACHABLE", LastLog(Ctx, "Create").Result);
                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));
            }
        }

        [DataTestMethod]
        [DataRow(true, "MKCOL")] //Only folders are created right away
        public void Create_NetworkFailureWhileCreating_ReturnsNetworkUnreachable(bool IsDirectory, String Method)
        {
            using (var Ctx = CreateContext())
            {
                Ctx.Server.NetworkFailure = NetworkDown(Method);

                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Ctx.TryCreate("\\new", IsDirectory, out FileNode Node));

                Assert.AreEqual("STATUS_NETWORK_UNREACHABLE", LastLog(Ctx, "Create").Result);
                Assert.IsFalse(Ctx.Server.Exists("/dav/new"));
            }
        }

        [TestMethod]
        public void Create_ServerErrorWhileCheckingExistence_ReturnsCannotMake()
        {
            using (var Ctx = CreateContext())
            {
                Ctx.Server.ForcedStatus = AnswerAll("PROPFIND", 500);

                Assert.AreEqual(FileSystemBase.STATUS_CANNOT_MAKE, Ctx.TryCreate("\\new.txt", false, out FileNode Node));

                Assert.AreEqual("STATUS_CANNOT_MAKE", LastLog(Ctx, "Create").Result);
                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));
            }
        }

        [DataTestMethod]
        [DataRow(true, "MKCOL")] //Only folders are created right away
        public void Create_ServerRefusesTheCreation_ReturnsCannotMake(bool IsDirectory, String Method)
        {
            using (var Ctx = CreateContext())
            {
                Ctx.Server.ForcedStatus = AnswerAll(Method, 403);

                Assert.AreEqual(FileSystemBase.STATUS_CANNOT_MAKE, Ctx.TryCreate("\\new", IsDirectory, out FileNode Node));

                Assert.AreEqual("STATUS_CANNOT_MAKE", LastLog(Ctx, "Create").Result);
                Assert.AreEqual(0, Ctx.AuthenticationFailures);
            }
        }

        [TestMethod]
        public void Create_401WhileCheckingExistence_ReturnsNetworkUnreachableAndNotifies()
        {
            using (var Ctx = CreateContext())
            {
                Ctx.Server.RejectCredentials = true;

                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Ctx.TryCreate("\\new.txt", false, out FileNode Node));
                Assert.AreEqual(1, Ctx.AuthenticationFailures);
            }
        }

        [DataTestMethod]
        [DataRow(true, "MKCOL")] //Only folders are created right away
        public void Create_401WhileCreating_ReturnsNetworkUnreachableAndNotifies(bool IsDirectory, String Method)
        {
            using (var Ctx = CreateContext())
            {
                Ctx.Server.ForcedStatus = AnswerAll(Method, 401);

                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Ctx.TryCreate("\\new", IsDirectory, out FileNode Node));
                Assert.AreEqual(1, Ctx.AuthenticationFailures);
            }
        }

        [TestMethod]
        public void Create_DoesNotReadTheCreatedElementBack()
        {
            //WinFSP holds a lock on the parent folder during Create: the created element is built locally instead of being
            //read back from the server (one request less while Explorer cannot list the folder)
            using (var Ctx = CreateContext())
            {
                Ctx.Server.Requests.Clear(); //Connection test of the mount
                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.TryCreate("\\new.txt", false, out FileNode Node));

                //Only the check that the name is free: no upload (see IsNewOnServer) and no read-back
                CollectionAssert.AreEqual(new[] { "PROPFIND /dav/new.txt" }, Ctx.Server.Requests, String.Join(", ", Ctx.Server.Requests));
                Assert.AreEqual("\\new.txt", Node.LocalPath);
                Assert.AreEqual("new.txt", Node.Name);
                Assert.AreEqual(0UL, Node.FileInfo.FileSize);
                Assert.AreEqual(0, Ctx.AuthenticationFailures);
            }
        }

        [TestMethod]
        public void Create_FileNotFoundAfterCreation_BuildsTheNodeLocally()
        {
            using (var Ctx = CreateContext())
            {
                //The server does not (yet) return the created element
                Ctx.Server.ForcedStatus = Answer("PROPFIND", "/dav/new.txt", 404);

                Int32 Status = Ctx.TryCreate("\\new.txt", false, out FileNode Node);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                Assert.AreEqual("\\new.txt", Node.LocalPath);
                Assert.AreEqual("/dav/new.txt", Node.RepositoryPath);
                Assert.AreEqual("new.txt", Node.Name);
                Assert.AreEqual(NormalAttribute, Node.FileInfo.FileAttributes);
                Assert.AreEqual(0UL, Node.FileInfo.FileSize);
                Assert.AreEqual(1, Node.OpenCount);
                Assert.IsTrue(Node.HasUnflushedData);

                //The node is usable and cached
                Assert.AreSame(Node, Ctx.Open("\\new.txt"));
                Ctx.Write(Node, 0, "data");
                Ctx.CleanupAndClose(Node, "\\new.txt", DavFsContext.ModifiedFlags);
                Assert.AreEqual("data", Ctx.Server.GetText("/dav/new.txt"));
            }
        }

        [TestMethod]
        public void Create_FolderNotFoundAfterCreation_BuildsADirectoryNodeLocally()
        {
            using (var Ctx = CreateContext())
            {
                Ctx.Server.ForcedStatus = Answer("PROPFIND", "/dav/v1.2", 404);

                Int32 Status = Ctx.TryCreate("\\v1.2", true, out FileNode Node);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                Assert.AreEqual("\\v1.2", Node.LocalPath);
                Assert.AreEqual(DirectoryAttribute, Node.FileInfo.FileAttributes);
                Assert.IsTrue(Ctx.Server.IsFolder("/dav/v1.2"));
            }
        }

        [TestMethod]
        public void Create_ErrorWhileReadingTheCreatedElementBack_BuildsTheNodeLocally()
        {
            using (var Ctx = CreateContext())
            {
                var Server = Ctx.Server;
                Server.ForcedStatus = (M, P) => M == "PROPFIND" && P == "/dav/new.txt" ? (HttpStatusCode?)(Server.CountRequests("PUT") > 0 ? HttpStatusCode.InternalServerError : HttpStatusCode.NotFound) : null;

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.TryCreate("\\new.txt", false, out FileNode Node));
                Assert.AreEqual("\\new.txt", Node.LocalPath);
                Assert.AreEqual(NormalAttribute, Node.FileInfo.FileAttributes);
            }
        }

        #endregion

        #region Overwrite

        [TestMethod]
        public void Overwrite_EmptiesTheFileWithoutDownloadingIt()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Int32 Status = Ctx.Fs.Overwrite(Node, null, 0, false, 0, out FileInfo Info);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                Assert.AreEqual(0UL, Info.FileSize);
                Assert.AreEqual(0UL, Node.FileInfo.FileSize);
                Assert.AreNotEqual(0u, Info.FileAttributes & ArchiveAttribute);
                Assert.IsTrue(Node.HasUnflushedData);
                Assert.AreEqual(0, Ctx.Server.CountRequests("GET"));
                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"), "Nothing is sent before cleanup");
                Assert.AreEqual("abcdef", Ctx.Server.GetText("/dav/a.txt"));
            }
        }

        [TestMethod]
        public void Overwrite_ThenCleanupWithoutWrite_UploadsAnEmptyFile()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Fs.Overwrite(Node, null, 0, false, 0, out FileInfo Info);

                Ctx.CleanupAndClose(Node, "\\a.txt");

                Assert.AreEqual(1, Ctx.Server.CountRequests("PUT"));
                Assert.AreEqual(0, Ctx.Server.GetContent("/dav/a.txt").Length);
            }
        }

        [TestMethod]
        public void Overwrite_OfAReadFile_ThenCleanup_UploadsAnEmptyFile()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Assert.AreEqual("abcdef", Ctx.ReadAll(Node));
                Ctx.Fs.Overwrite(Node, null, 0, false, 0, out FileInfo Info);

                Assert.AreEqual("", Ctx.ReadAll(Node));
                Ctx.CleanupAndClose(Node, "\\a.txt", DavFsContext.ModifiedFlags);

                Assert.AreEqual(0, Ctx.Server.GetContent("/dav/a.txt").Length);
            }
        }

        [TestMethod]
        public void Overwrite_ThenWrite_ReplacesTheWholeContent()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Fs.Overwrite(Node, null, 0, false, 0, out FileInfo Info);
                Ctx.Write(Node, 0, "xy");

                Ctx.CleanupAndClose(Node, "\\a.txt", DavFsContext.ModifiedFlags);

                Assert.AreEqual("xy", Ctx.Server.GetText("/dav/a.txt"));
                Assert.AreEqual(0, Ctx.Server.CountRequests("GET"));
            }
        }

        [TestMethod]
        public void Overwrite_WithAllocationSize_AllocatesAnEmptyBuffer()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Overwrite(Node, null, 0, false, 8192, out FileInfo Info));

                Assert.AreEqual(0UL, Info.FileSize);
                Assert.AreEqual(8192UL, Info.AllocationSize);
                Assert.AreEqual(8192, Node.FileData.Length);
            }
        }

        [TestMethod]
        public void Overwrite_ReplaceFileAttributes_ReplacesThemAndAddsArchive()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Ctx.Fs.Overwrite(Node, null, HiddenAttribute, true, 0, out FileInfo Info);

                Assert.AreEqual(HiddenAttribute | ArchiveAttribute, Info.FileAttributes);
            }
        }

        [TestMethod]
        public void Overwrite_WithoutReplaceFileAttributes_MergesThemAndAddsArchive()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                UInt32 Before = Node.FileInfo.FileAttributes;

                Ctx.Fs.Overwrite(Node, null, HiddenAttribute, false, 0, out FileInfo Info);

                Assert.AreEqual(Before | HiddenAttribute | ArchiveAttribute, Info.FileAttributes);
            }
        }

        [TestMethod]
        public void Overwrite_UpdatesTheTimestamps()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                UInt64 Before = (UInt64)DateTime.Now.ToFileTimeUtc();

                Ctx.Fs.Overwrite(Node, null, 0, false, 0, out FileInfo Info);

                Assert.IsTrue(Info.LastWriteTime >= Before);
                Assert.IsTrue(Info.ChangeTime >= Before);
                Assert.IsTrue(Info.LastAccessTime >= Before);
            }
        }

        [TestMethod]
        public void Overwrite_AllocationSizeAbove2GB_ReturnsDiskFull()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Int32 Status = Ctx.Fs.Overwrite(Node, null, 0, false, ThreeGigabytes, out FileInfo Info);

                Assert.AreEqual(FileSystemBase.STATUS_DISK_FULL, Status);
            }
        }

        #endregion
    }
}
