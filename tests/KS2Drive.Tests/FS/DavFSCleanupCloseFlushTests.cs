using Fsp;
using KS2Drive.FS;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using static KS2Drive.Tests.FS.DavFSTestHelpers;
using FileInfo = Fsp.Interop.FileInfo;

namespace KS2Drive.Tests.FS
{
    /// <summary>
    /// Cleanup, Close, Flush and UploadPendingChanges, including the data-safety scenarios (recovery copies).
    /// </summary>
    [TestClass]
    public class DavFSCleanupCloseFlushTests
    {
        #region Cleanup - delete

        [TestMethod]
        public void Cleanup_DeleteFlag_DeletesTheFileOnTheServer()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Ctx.Fs.Cleanup(Node, null, "\\a.txt", FileSystemBase.CleanupDelete);

                Assert.IsFalse(Ctx.Server.Exists("/dav/a.txt"));
                Assert.AreEqual(1, Ctx.Server.CountRequests("DELETE"));
                Assert.IsTrue(Node.IsDeleted);
                Assert.IsFalse(Node.HasUnflushedData);
                var L = LastLog(Ctx, "Delete");
                Assert.AreEqual("STATUS_SUCCESS", L.Result);
                Assert.AreEqual("\\a.txt", L.File);

                Ctx.Fs.Close(Node, null);
                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, Ctx.TryOpen("\\a.txt", out FileNode Reopened), "Removed from the cache");
            }
        }

        [TestMethod]
        public void Cleanup_DeleteFlag_WithPendingChanges_DoesNotUploadThem()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Write(Node, 0, "X");

                Ctx.CleanupAndClose(Node, "\\a.txt", FileSystemBase.CleanupDelete | DavFsContext.ModifiedFlags);

                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));
                Assert.IsFalse(Ctx.Server.Exists("/dav/a.txt"), "The deleted file is not recreated");
            }
        }

        [TestMethod]
        public void Cleanup_DeleteFlag_DeletesTheFolderAndItsCachedChildren()
        {
            using (var Ctx = CreateContext())
            {
                var Folder = Ctx.Open("\\folder");
                var Child = Ctx.Open("\\folder\\b.txt");

                Ctx.Fs.Cleanup(Folder, null, "\\folder", FileSystemBase.CleanupDelete);

                Assert.IsFalse(Ctx.Server.Exists("/dav/folder"));
                Assert.IsFalse(Ctx.Server.Exists("/dav/folder/b.txt"));
                Assert.IsTrue(Folder.IsDeleted);
                Assert.AreEqual("STATUS_SUCCESS", LastLog(Ctx, "Delete").Result);
                Assert.IsFalse(GetCachedNodes(Ctx.Fs).ContainsKey("\\folder"));
                Assert.IsFalse(GetCachedNodes(Ctx.Fs).ContainsKey("\\folder\\b.txt"));
            }
        }

        [TestMethod]
        public void Cleanup_DeleteFlag_ServerError_KeepsTheFileAndLogsTheFailure()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Server.ForcedStatus = AnswerAll("DELETE", 500);

                Ctx.Fs.Cleanup(Node, null, "\\a.txt", FileSystemBase.CleanupDelete);

                Assert.IsTrue(Ctx.Server.Exists("/dav/a.txt"));
                Assert.IsFalse(Node.IsDeleted);
                Assert.AreEqual("STATUS_FAILED", LastLog(Ctx, "Delete").Result);
                Assert.AreEqual(0, Ctx.AuthenticationFailures);
                Assert.IsTrue(GetCachedNodes(Ctx.Fs).ContainsKey("\\a.txt"));
            }
        }

        [TestMethod]
        public void Cleanup_DeleteFlag_FolderServerError_KeepsTheFolderAndLogsTheFailure()
        {
            using (var Ctx = CreateContext())
            {
                var Folder = Ctx.Open("\\folder");
                Ctx.Server.ForcedStatus = AnswerAll("DELETE", 403);

                Ctx.Fs.Cleanup(Folder, null, "\\folder", FileSystemBase.CleanupDelete);

                Assert.IsTrue(Ctx.Server.Exists("/dav/folder/b.txt"));
                Assert.IsFalse(Folder.IsDeleted);
                Assert.AreEqual("STATUS_FAILED", LastLog(Ctx, "Delete").Result);
            }
        }

        [DataTestMethod]
        [DataRow("\\a.txt")]
        [DataRow("\\folder")]
        public void Cleanup_DeleteFlag_401_NotifiesTheAuthenticationFailure(String FileName)
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open(FileName);
                Ctx.Server.RejectCredentials = true;

                Ctx.Fs.Cleanup(Node, null, FileName, FileSystemBase.CleanupDelete);

                Assert.AreEqual(1, Ctx.AuthenticationFailures);
                Assert.AreEqual("STATUS_FAILED", LastLog(Ctx, "Delete").Result);
                Assert.IsFalse(Node.IsDeleted);
            }
        }

        [TestMethod]
        public void Cleanup_Root_IsIgnored()
        {
            using (var Ctx = CreateContext())
            {
                var Root = Ctx.Open("\\");
                int RequestsBefore = Ctx.Server.Requests.Count;

                Ctx.Fs.Cleanup(Root, null, "\\", FileSystemBase.CleanupDelete | FileSystemBase.CleanupSetAllocationSize);

                Assert.AreEqual(RequestsBefore, Ctx.Server.Requests.Count);
                Assert.IsTrue(Ctx.Server.Exists("/dav/a.txt"));
                Assert.IsFalse(Root.IsDeleted);
                Assert.IsNull(LastLog(Ctx, "Delete"));
            }
        }

        #endregion

        #region Cleanup - upload

        [DataTestMethod]
        [DataRow(0u)]
        [DataRow(2u)]   //CleanupSetAllocationSize
        [DataRow(16u)]  //CleanupSetArchiveBit
        [DataRow(32u)]  //CleanupSetLastAccessTime
        [DataRow(64u)]  //CleanupSetLastWriteTime
        [DataRow(128u)] //CleanupSetChangeTime
        [DataRow(80u)]  //ArchiveBit | LastWriteTime
        public void Cleanup_UploadsPendingChangesWhateverTheFlags(UInt32 Flags)
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Write(Node, 0, "XY");

                Ctx.Fs.Cleanup(Node, null, "\\a.txt", Flags);

                Assert.AreEqual(1, Ctx.Server.CountRequests("PUT"));
                Assert.AreEqual("XYcdef", Ctx.Server.GetText("/dav/a.txt"));
                Assert.IsFalse(Node.HasUnflushedData);
                var L = LastLog(Ctx, "Cleanup Flush");
                Assert.AreEqual("STATUS_SUCCESS", L.Result);
                Assert.AreEqual("\\a.txt", L.File);

                Ctx.Fs.Close(Node, null);
                Assert.AreEqual(1, Ctx.Server.CountRequests("PUT"), "Nothing left to upload at close");
            }
        }

        [DataTestMethod]
        [DataRow(0u)]
        [DataRow(80u)]
        public void Cleanup_WithoutPendingChanges_UploadsNothing(UInt32 Flags)
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.ReadAll(Node);

                Ctx.CleanupAndClose(Node, "\\a.txt", Flags);

                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));
                Assert.IsNull(LastLog(Ctx, "Cleanup Flush"));
            }
        }

        [TestMethod]
        public void Cleanup_SetAllocationSize_ShrinksTheAllocationToTheAllocationUnit()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.SetFileSize(Node, null, 20000, true, out FileInfo Info));
                Assert.AreEqual(20000UL, Node.FileInfo.AllocationSize);

                Ctx.Fs.Cleanup(Node, null, "\\a.txt", FileSystemBase.CleanupSetAllocationSize);

                Assert.AreEqual(4096UL, Node.FileInfo.AllocationSize);
                Assert.AreEqual(6UL, Node.FileInfo.FileSize);
                Assert.AreEqual("abcdef", Ctx.ReadAll(Node), "The content is kept");
                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"), "An allocation change alone is not uploaded");
            }
        }

        [TestMethod]
        public void Cleanup_EmptiedFile_IsUploadedEmpty()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Overwrite(Node, null, 0, false, 0, out FileInfo Info));

                Ctx.Fs.Cleanup(Node, null, "\\a.txt", 0);

                Assert.AreEqual(1, Ctx.Server.CountRequests("PUT"));
                Assert.AreEqual(0, Ctx.Server.GetContent("/dav/a.txt").Length);
                Assert.AreEqual("STATUS_SUCCESS", LastLog(Ctx, "Cleanup Flush").Result);
            }
        }

        [TestMethod]
        public void Cleanup_FileInASubFolder_IsUploadedAtItsPath()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\folder\\b.txt");
                Ctx.Write(Node, 2, "c");

                Ctx.CleanupAndClose(Node, "\\folder\\b.txt", DavFsContext.ModifiedFlags);

                Assert.AreEqual("bbc", Ctx.Server.GetText("/dav/folder/b.txt"));
                Assert.IsTrue(Ctx.Server.Requests.Contains("PUT /dav/folder/b.txt"));
            }
        }

        #endregion

        #region Upload failures (data safety)

        [TestMethod]
        public void Cleanup_UploadServerError_LogsAFailureWithARecoveryCopyOfTheExactContent()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                try
                {
                    Ctx.Write(Node, 6, "ghi");
                    Ctx.Server.ForcedStatus = AnswerAll("PUT", 500);

                    Ctx.Fs.Cleanup(Node, null, "\\a.txt", DavFsContext.ModifiedFlags);

                    var L = LastLog(Ctx, "Cleanup Flush");
                    Assert.AreEqual("STATUS_FAILED", L.Result);
                    Assert.AreEqual("\\a.txt", L.File);
                    Assert.IsTrue(L.AllowRetryOrRecover);
                    Assert.IsTrue(File.Exists(L.LocalTemporaryPath));
                    CollectionAssert.AreEqual(Bytes("abcdefghi"), File.ReadAllBytes(L.LocalTemporaryPath), "Exactly the file content, not the allocated buffer");
                    Assert.AreEqual(L.LocalTemporaryPath, Node.TemporaryLocalCopyPath);
                    Assert.IsTrue(Node.HasUnflushedData, "Kept for a later retry");
                    Assert.AreEqual("abcdef", Ctx.Server.GetText("/dav/a.txt"));
                }
                finally
                {
                    DeleteRecoveryCopies(Ctx, Node);
                }
            }
        }

        [TestMethod]
        public void Cleanup_UploadNetworkFailure_LogsAFailureWithARecoveryCopyOfTheExactContent()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Create("\\new.txt");
                try
                {
                    Ctx.Write(Node, 0, "precious data");
                    Ctx.Server.NetworkFailure = NetworkDown("PUT");

                    Ctx.Fs.Cleanup(Node, null, "\\new.txt", DavFsContext.ModifiedFlags);

                    var L = LastLog(Ctx, "Cleanup Flush");
                    Assert.AreEqual("STATUS_FAILED", L.Result);
                    CollectionAssert.AreEqual(Bytes("precious data"), File.ReadAllBytes(L.LocalTemporaryPath));
                    Assert.AreEqual(0, Ctx.AuthenticationFailures);
                }
                finally
                {
                    DeleteRecoveryCopies(Ctx, Node);
                }
            }
        }

        [TestMethod]
        public void Cleanup_Upload401_LogsAFailureAndNotifies()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                try
                {
                    Ctx.Write(Node, 0, "X");
                    Ctx.Server.ForcedStatus = AnswerAll("PUT", 401);

                    Ctx.Fs.Cleanup(Node, null, "\\a.txt", DavFsContext.ModifiedFlags);

                    var L = LastLog(Ctx, "Cleanup Flush");
                    Assert.AreEqual("STATUS_FAILED", L.Result);
                    CollectionAssert.AreEqual(Bytes("Xbcdef"), File.ReadAllBytes(L.LocalTemporaryPath));
                    Assert.AreEqual(1, Ctx.AuthenticationFailures);
                }
                finally
                {
                    DeleteRecoveryCopies(Ctx, Node);
                }
            }
        }

        [TestMethod]
        public void Cleanup_FailedUpload_SucceedingAtClose_UploadsAndDeletesTheRecoveryCopy()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                try
                {
                    Ctx.Write(Node, 0, "XY");
                    Ctx.Server.ForcedStatus = AnswerAll("PUT", 500);
                    Ctx.Fs.Cleanup(Node, null, "\\a.txt", DavFsContext.ModifiedFlags);
                    String CopyPath = LastLog(Ctx, "Cleanup Flush").LocalTemporaryPath;
                    Assert.IsTrue(File.Exists(CopyPath));

                    Ctx.Server.ForcedStatus = null;
                    Ctx.Fs.Close(Node, null);

                    Assert.AreEqual("XYcdef", Ctx.Server.GetText("/dav/a.txt"));
                    Assert.AreEqual("STATUS_SUCCESS", LastLog(Ctx, "Close Flush").Result);
                    Assert.IsFalse(File.Exists(CopyPath), "The recovery copy is obsolete once uploaded");
                    Assert.AreEqual("", Node.TemporaryLocalCopyPath);
                }
                finally
                {
                    DeleteRecoveryCopies(Ctx, Node);
                }
            }
        }

        [TestMethod]
        public void Cleanup_SecondFailure_UpdatesTheSameRecoveryCopy()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                try
                {
                    Ctx.Server.ForcedStatus = AnswerAll("PUT", 500);
                    Ctx.Write(Node, 0, "1");
                    Ctx.Fs.Cleanup(Node, null, "\\a.txt", DavFsContext.ModifiedFlags);
                    String FirstCopy = Node.TemporaryLocalCopyPath;

                    Ctx.Write(Node, 1, "2");
                    Ctx.Fs.Cleanup(Node, null, "\\a.txt", DavFsContext.ModifiedFlags);

                    Assert.AreEqual(FirstCopy, Node.TemporaryLocalCopyPath);
                    CollectionAssert.AreEqual(Bytes("12cdef"), File.ReadAllBytes(FirstCopy));
                }
                finally
                {
                    DeleteRecoveryCopies(Ctx, Node);
                }
            }
        }

        #endregion

        #region Close

        [TestMethod]
        public void Close_DecrementsTheHandleCount_AndKeepsTheContentForAShortTimeOnTheLastHandle()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Open("\\a.txt");
                Ctx.ReadAll(Node);

                Ctx.Fs.Close(Node, null);
                Assert.AreEqual(1, Node.OpenCount);
                Assert.IsNotNull(Node.FileData, "Still open");

                Ctx.Fs.Close(Node, null);
                Assert.AreEqual(0, Node.OpenCount);
                Assert.IsNotNull(Node.FileData, "Kept to be reused by a new open (see DavFSPerformanceTests)");
            }
        }

        [TestMethod]
        public void Close_UploadsPendingChangesWithoutCleanup()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Write(Node, 0, "Z");

                Ctx.Fs.Close(Node, null);

                Assert.AreEqual("Zbcdef", Ctx.Server.GetText("/dav/a.txt"));
                Assert.AreEqual("STATUS_SUCCESS", LastLog(Ctx, "Close Flush").Result);
                Assert.IsFalse(Node.HasUnflushedData);
            }
        }

        [TestMethod]
        public void Close_UploadsPendingChangesEvenIfOtherHandlesRemain()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Open("\\a.txt");
                Ctx.Write(Node, 0, "Z");

                Ctx.Fs.Close(Node, null);

                Assert.AreEqual("Zbcdef", Ctx.Server.GetText("/dav/a.txt"));
                Assert.AreEqual(1, Node.OpenCount);
                Assert.IsNotNull(Node.FileData);
            }
        }

        [TestMethod]
        public void Close_UploadFailureOnTheLastHandle_KeepsTheRecoveryCopy()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                try
                {
                    Ctx.Write(Node, 0, "Z");
                    Ctx.Server.ForcedStatus = AnswerAll("PUT", 500);

                    Ctx.Fs.Close(Node, null);

                    var L = LastLog(Ctx, "Close Flush");
                    Assert.AreEqual("STATUS_FAILED", L.Result);
                    CollectionAssert.AreEqual(Bytes("Zbcdef"), File.ReadAllBytes(L.LocalTemporaryPath));
                    Assert.AreEqual(0, Node.OpenCount);
                    Assert.IsNull(Node.FileData);
                    Assert.IsFalse(Node.HasUnflushedData);
                    Assert.IsTrue(File.Exists(L.LocalTemporaryPath), "Only the recovery copy holds the data now");
                }
                finally
                {
                    DeleteRecoveryCopies(Ctx, Node);
                }
            }
        }

        [TestMethod]
        public void Close_WithoutChanges_UploadsNothing()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.ReadAll(Node);

                Ctx.Fs.Close(Node, null);

                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));
            }
        }

        #endregion

        #region Flush

        [TestMethod]
        public void Flush_UploadsPendingChanges()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Write(Node, 6, "!");

                Int32 Status = Ctx.Fs.Flush(Node, null, out FileInfo Info);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                Assert.AreEqual(7UL, Info.FileSize);
                Assert.AreEqual("abcdef!", Ctx.Server.GetText("/dav/a.txt"));
                Assert.IsFalse(Node.HasUnflushedData);
                Assert.AreEqual("STATUS_SUCCESS", LastLog(Ctx, "Flush").Result);

                Ctx.CleanupAndClose(Node, "\\a.txt", DavFsContext.ModifiedFlags);
                Assert.AreEqual(1, Ctx.Server.CountRequests("PUT"));
            }
        }

        [TestMethod]
        public void Flush_UploadsPendingChanges_InFlushAtWriteModeToo()
        {
            using (var Ctx = CreateContext(FlushMode.FlushAtWrite))
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Fs.SetFileSize(Node, null, 2, false, out FileInfo SizeInfo); //Not uploaded by itself

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Flush(Node, null, out FileInfo Info));

                Assert.AreEqual("ab", Ctx.Server.GetText("/dav/a.txt"));
            }
        }

        [TestMethod]
        public void Flush_WithoutChanges_UploadsNothing()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Flush(Node, null, out FileInfo Info));

                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));
                Assert.AreEqual(6UL, Info.FileSize);
            }
        }

        [TestMethod]
        public void Flush_Volume_DoesNothing()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Write(Node, 0, "Z");
                int RequestsBefore = Ctx.Server.Requests.Count;

                Int32 Status = Ctx.Fs.Flush(null, null, out FileInfo Info);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                Assert.AreEqual(RequestsBefore, Ctx.Server.Requests.Count);
                Assert.IsTrue(Node.HasUnflushedData);
            }
        }

        [TestMethod]
        public void Flush_UploadFailure_ReturnsUnexpectedIoError_ThenSucceedsOnRetry()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                try
                {
                    Ctx.Write(Node, 0, "Z");
                    Ctx.Server.ForcedStatus = AnswerAll("PUT", 500);

                    Assert.AreEqual(FileSystemBase.STATUS_UNEXPECTED_IO_ERROR, Ctx.Fs.Flush(Node, null, out FileInfo Info));
                    var L = LastLog(Ctx, "Flush");
                    Assert.AreEqual("STATUS_FAILED", L.Result);
                    CollectionAssert.AreEqual(Bytes("Zbcdef"), File.ReadAllBytes(L.LocalTemporaryPath));
                    Assert.IsTrue(Node.HasUnflushedData);

                    Ctx.Server.ForcedStatus = null;
                    Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Flush(Node, null, out Info));
                    Assert.AreEqual("Zbcdef", Ctx.Server.GetText("/dav/a.txt"));
                    Assert.IsFalse(File.Exists(L.LocalTemporaryPath));
                }
                finally
                {
                    DeleteRecoveryCopies(Ctx, Node);
                }
            }
        }

        [TestMethod]
        public void Flush_OfADeletedNode_UploadsNothing()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Write(Node, 0, "Z");
                Ctx.Fs.Cleanup(Node, null, "\\a.txt", FileSystemBase.CleanupDelete);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Flush(Node, null, out FileInfo Info));

                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));
                Assert.IsFalse(Ctx.Server.Exists("/dav/a.txt"));
            }
        }

        #endregion

        #region Private methods

        private static DavFsContext CreateContext(FlushMode Flush = FlushMode.FlushAtCleanup)
        {
            return DavFsContext.Create(s =>
            {
                s.AddFile("/dav/a.txt", "abcdef");
                s.AddFolder("/dav/folder");
                s.AddFile("/dav/folder/b.txt", "bb");
                s.AddFolder("/dav/folder/sub");
            }, Flush);
        }

        #endregion
    }
}
