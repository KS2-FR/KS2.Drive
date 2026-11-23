using Fsp;
using KS2Drive.FS;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using static KS2Drive.Tests.FS.DavFSTestHelpers;
using FileInfo = Fsp.Interop.FileInfo;

namespace KS2Drive.Tests.FS
{
    /// <summary>
    /// SetFileSize (and SetFileSizeInternal / EnsureContentLoaded through it) and SetBasicInfo.
    /// </summary>
    [TestClass]
    public class DavFSFileSizeAndInfoTests
    {
        private static DavFsContext CreateContext()
        {
            return DavFsContext.Create(s => s.AddFile("/dav/a.txt", "abcdef"));
        }

        #region SetFileSize

        [TestMethod]
        public void SetFileSize_Truncation_IsUploadedAtCleanup()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Int32 Status = Ctx.Fs.SetFileSize(Node, null, 3, false, out FileInfo Info);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                Assert.AreEqual(3UL, Info.FileSize);
                Assert.IsTrue(Node.HasUnflushedData);
                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"), "Nothing is sent before cleanup");

                Ctx.CleanupAndClose(Node, "\\a.txt", DavFsContext.ModifiedFlags);

                Assert.AreEqual("abc", Ctx.Server.GetText("/dav/a.txt"));
            }
        }

        [TestMethod]
        public void SetFileSize_TruncationToZero_IsUploadedEmpty()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.SetFileSize(Node, null, 0, false, out FileInfo Info));
                Assert.AreEqual(FileSystemBase.STATUS_END_OF_FILE, Ctx.Read(Node, 0, 10, out byte[] Data));
                Ctx.CleanupAndClose(Node, "\\a.txt");

                Assert.AreEqual(0, Ctx.Server.GetContent("/dav/a.txt").Length);
            }
        }

        [TestMethod]
        public void SetFileSize_Extension_IsZeroFilledAndUploadedAtCleanup()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.SetFileSize(Node, null, 8, false, out FileInfo Info));
                Assert.AreEqual(8UL, Info.FileSize);
                Assert.IsTrue(Info.AllocationSize >= 8UL);

                Ctx.CleanupAndClose(Node, "\\a.txt");

                CollectionAssert.AreEqual(new byte[] { (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e', (byte)'f', 0, 0 }, Ctx.Server.GetContent("/dav/a.txt"));
            }
        }

        [TestMethod]
        public void SetFileSize_TruncationThenExtension_ZeroFillsTheReusedRange()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Ctx.Fs.SetFileSize(Node, null, 2, false, out FileInfo Info);
                Ctx.Fs.SetFileSize(Node, null, 4, false, out Info);
                Ctx.CleanupAndClose(Node, "\\a.txt");

                CollectionAssert.AreEqual(new byte[] { (byte)'a', (byte)'b', 0, 0 }, Ctx.Server.GetContent("/dav/a.txt"));
            }
        }

        [TestMethod]
        public void SetFileSize_SameSize_DoesNotMarkTheFileAsModified()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.SetFileSize(Node, null, 6, false, out FileInfo Info));

                Assert.IsFalse(Node.HasUnflushedData);
                Ctx.CleanupAndClose(Node, "\\a.txt");
                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));
            }
        }

        [TestMethod]
        public void SetFileSize_AllocationSizeOnly_KeepsTheContentAndIsNotUploaded()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.SetFileSize(Node, null, 8192, true, out FileInfo Info));

                Assert.AreEqual(8192UL, Info.AllocationSize);
                Assert.AreEqual(6UL, Info.FileSize);
                Assert.IsFalse(Node.HasUnflushedData);
                Assert.AreEqual("abcdef", Ctx.ReadAll(Node));
                Ctx.CleanupAndClose(Node, "\\a.txt");
                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));
            }
        }

        [TestMethod]
        public void SetFileSize_AllocationSizeBelowTheFileSize_TruncatesTheFile()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.SetFileSize(Node, null, 4, true, out FileInfo Info));

                Assert.AreEqual(4UL, Info.FileSize);
                Assert.IsTrue(Node.HasUnflushedData, "The file size changed");
                Ctx.CleanupAndClose(Node, "\\a.txt");
                Assert.AreEqual("abcd", Ctx.Server.GetText("/dav/a.txt"));
            }
        }

        [TestMethod]
        public void SetFileSize_DownloadFailure_IsPropagatedAndNothingIsUploaded()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Server.ForcedStatus = AnswerAll("GET", 500);

                Int32 Status = Ctx.Fs.SetFileSize(Node, null, 3, false, out FileInfo Info);

                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Status);
                Assert.AreEqual(0UL, Info.FileSize);
                Assert.AreEqual(6UL, Node.FileInfo.FileSize, "Unchanged");
                Assert.IsFalse(Node.HasUnflushedData);

                Ctx.Server.ForcedStatus = null;
                Ctx.CleanupAndClose(Node, "\\a.txt", DavFsContext.ModifiedFlags);
                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));
                Assert.AreEqual("abcdef", Ctx.Server.GetText("/dav/a.txt"));
            }
        }

        [TestMethod]
        public void SetFileSize_401WhileLoadingTheContent_ReturnsNetworkUnreachableAndNotifies()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Server.RejectCredentials = true;

                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Ctx.Fs.SetFileSize(Node, null, 3, false, out FileInfo Info));
                Assert.AreEqual(1, Ctx.AuthenticationFailures);
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void SetFileSize_Above2GB_ReturnsDiskFullAndLeavesTheFileUnchanged(bool SetAllocationSize)
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Int32 Status = Ctx.Fs.SetFileSize(Node, null, ThreeGigabytes, SetAllocationSize, out FileInfo Info);

                Assert.AreEqual(FileSystemBase.STATUS_DISK_FULL, Status);
                Assert.AreEqual(6UL, Node.FileInfo.FileSize);
                Assert.IsFalse(Node.HasUnflushedData);
                Assert.AreEqual("abcdef", Ctx.ReadAll(Node));
            }
        }

        [TestMethod]
        public void SetFileSize_JustAboveTheMaximum_ReturnsDiskFull()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Create("\\new.txt");

                Assert.AreEqual(FileSystemBase.STATUS_DISK_FULL, Ctx.Fs.SetFileSize(Node, null, MaxFileSize + 1, true, out FileInfo Info));
            }
        }

        #endregion

        #region SetBasicInfo

        [TestMethod]
        public void SetBasicInfo_UpdatesTheCachedNodeOnly()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                int RequestsBefore = Ctx.Server.Requests.Count;
                UInt32 Attributes = (UInt32)(System.IO.FileAttributes.Hidden | System.IO.FileAttributes.Archive);
                UInt64 Creation = (UInt64)new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
                UInt64 Access = (UInt64)new DateTime(2011, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
                UInt64 Write = (UInt64)new DateTime(2012, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
                UInt64 Change = (UInt64)new DateTime(2013, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

                Int32 Status = Ctx.Fs.SetBasicInfo(Node, null, Attributes, Creation, Access, Write, Change, out FileInfo Info);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                Assert.AreEqual(Attributes, Info.FileAttributes);
                Assert.AreEqual(Creation, Info.CreationTime);
                Assert.AreEqual(Access, Info.LastAccessTime);
                Assert.AreEqual(Write, Info.LastWriteTime);
                Assert.AreEqual(Change, Info.ChangeTime);
                Assert.AreEqual(Attributes, Node.FileInfo.FileAttributes);
                Assert.AreEqual(Write, Node.FileInfo.LastWriteTime);
                Assert.AreEqual(RequestsBefore, Ctx.Server.Requests.Count, "Not sent to the server");
                Assert.IsFalse(Node.HasUnflushedData);
            }
        }

        [TestMethod]
        public void SetBasicInfo_MinusOneAndZero_LeaveTheValuesUnchanged()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                FileInfo Before = Node.FileInfo;

                Int32 Status = Ctx.Fs.SetBasicInfo(Node, null, unchecked((UInt32)(-1)), 0, 0, 0, 0, out FileInfo Info);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                Assert.AreEqual(Before.FileAttributes, Info.FileAttributes);
                Assert.AreEqual(Before.CreationTime, Info.CreationTime);
                Assert.AreEqual(Before.LastAccessTime, Info.LastAccessTime);
                Assert.AreEqual(Before.LastWriteTime, Info.LastWriteTime);
                Assert.AreEqual(Before.ChangeTime, Info.ChangeTime);
            }
        }

        [TestMethod]
        public void SetBasicInfo_OnlyTheGivenValuesAreChanged()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                FileInfo Before = Node.FileInfo;
                UInt64 Write = (UInt64)new DateTime(2012, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

                Ctx.Fs.SetBasicInfo(Node, null, unchecked((UInt32)(-1)), 0, 0, Write, 0, out FileInfo Info);

                Assert.AreEqual(Write, Info.LastWriteTime);
                Assert.AreEqual(Before.FileAttributes, Info.FileAttributes);
                Assert.AreEqual(Before.CreationTime, Info.CreationTime);
                Assert.AreEqual(Before.ChangeTime, Info.ChangeTime);
            }
        }

        #endregion
    }
}
