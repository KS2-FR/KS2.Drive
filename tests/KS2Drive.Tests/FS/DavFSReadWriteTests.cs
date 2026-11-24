using Fsp;
using KS2Drive.FS;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text;
using static KS2Drive.Tests.FS.DavFSTestHelpers;

namespace KS2Drive.Tests.FS
{
    /// <summary>
    /// Read, Write (both flush modes) and EnsureContentLoaded / SetFileSizeInternal through them.
    /// </summary>
    [TestClass]
    public class DavFSReadWriteTests
    {
        private static DavFsContext CreateContext(FlushMode Flush = FlushMode.FlushAtCleanup)
        {
            return DavFsContext.Create(s =>
            {
                s.AddFile("/dav/a.txt", "abcdef");
                s.AddFile("/dav/empty.txt", "");
                s.AddFolder("/dav/folder");
            }, Flush);
        }

        #region Read

        [TestMethod]
        public void Read_WholeFile_DownloadsItOnce()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual("abcdef", Ctx.ReadAll(Node));
                Assert.AreEqual("abcdef", Ctx.ReadAll(Node));

                Assert.AreEqual(1, Ctx.Server.CountRequests("GET"));
                Assert.IsNotNull(Node.FileData);
            }
        }

        [TestMethod]
        public void Read_Range_ReturnsTheRequestedBytes()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Read(Node, 2, 3, out byte[] Data));
                Assert.AreEqual("cde", Encoding.UTF8.GetString(Data));
            }
        }

        [TestMethod]
        public void Read_PastTheEnd_IsTruncatedToTheFileSize()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Read(Node, 4, 100, out byte[] Data));
                Assert.AreEqual("ef", Encoding.UTF8.GetString(Data));
            }
        }

        [DataTestMethod]
        [DataRow(6UL)]
        [DataRow(100UL)]
        public void Read_AtOrAfterTheEnd_ReturnsEndOfFile(UInt64 Offset)
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual(FileSystemBase.STATUS_END_OF_FILE, Ctx.Read(Node, Offset, 10, out byte[] Data));
                Assert.AreEqual(0, Data.Length);
            }
        }

        [TestMethod]
        public void Read_EmptyFile_ReturnsEndOfFile()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\empty.txt");

                Assert.AreEqual(FileSystemBase.STATUS_END_OF_FILE, Ctx.Read(Node, 0, 10, out byte[] Data));
            }
        }

        [TestMethod]
        public void Read_Folder_ReturnsEndOfFileWithoutDownload()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\folder");

                Assert.AreEqual(FileSystemBase.STATUS_END_OF_FILE, Ctx.Read(Node, 0, 10, out byte[] Data));
                Assert.AreEqual(0, Ctx.Server.CountRequests("GET"));
            }
        }

        [TestMethod]
        public void Read_ServerWithoutContentLength_DownloadsTheContentAnyway()
        {
            using (var Ctx = DavFsContext.Create(s => { s.SendContentLength = false; s.AddFile("/dav/a.txt", "abc"); }, FlushMode.FlushAtCleanup, WebDAVMode.AOS))
            {
                var Node = Ctx.Open("\\a.txt");
                Assert.AreEqual(0UL, Node.FileInfo.FileSize, "Size unknown before the download");

                Assert.AreEqual("abc", Ctx.ReadAll(Node));
                Assert.AreEqual(3UL, Node.FileInfo.FileSize);
                Assert.AreEqual(3UL, Node.FileInfo.AllocationSize);
            }
        }

        [TestMethod]
        public void Read_DownloadFailure_ReturnsNetworkUnreachable()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Server.ForcedStatus = AnswerAll("GET", 500);

                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Ctx.Read(Node, 0, 10, out byte[] Data));
                Assert.IsNull(Node.FileData);
                Assert.AreEqual(0, Ctx.AuthenticationFailures);

                //Next read retries the download
                Ctx.Server.ForcedStatus = null;
                Assert.AreEqual("abcdef", Ctx.ReadAll(Node));
            }
        }

        [TestMethod]
        public void Read_NetworkFailure_ReturnsNetworkUnreachable()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Server.NetworkFailure = NetworkDown("GET");

                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Ctx.Read(Node, 0, 10, out byte[] Data));
            }
        }

        [TestMethod]
        public void Read_401_ReturnsNetworkUnreachableAndNotifies()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Server.RejectCredentials = true;

                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Ctx.Read(Node, 0, 10, out byte[] Data));
                Assert.AreEqual(1, Ctx.AuthenticationFailures);
            }
        }

        [TestMethod]
        public void Read_AfterAnExtendingWrite_StopsAtTheFileSizeNotAtTheBufferSize()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Create("\\new.txt");
                Ctx.Write(Node, 0, "abc");
                Assert.IsTrue(Node.FileData.Length > 3, "The buffer is rounded up to the allocation unit");

                Assert.AreEqual("abc", Ctx.ReadAll(Node));
            }
        }

        #endregion

        #region Write (FlushAtCleanup)

        [TestMethod]
        public void Write_InRangeOnANeverReadFile_KeepsTheOtherBytes()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Write(Node, 2, "XY"));
                Assert.AreEqual(1, Ctx.Server.CountRequests("GET"), "The current content is loaded before writing into it");
                Assert.AreEqual("abcdef", Ctx.Server.GetText("/dav/a.txt"), "Nothing uploaded before cleanup");

                Ctx.CleanupAndClose(Node, "\\a.txt", DavFsContext.ModifiedFlags);

                Assert.AreEqual("abXYef", Ctx.Server.GetText("/dav/a.txt"));
            }
        }

        [TestMethod]
        public void Write_ReturnsBytesTransferredAndUpdatedFileInfo()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                IntPtr Buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(Bytes("WXYZ"), 0, Buffer, 4);
                    Int32 Status = Ctx.Fs.Write(Node, null, Buffer, 4, 4, false, false, out UInt32 BytesTransferred, out Fsp.Interop.FileInfo Info);

                    Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                    Assert.AreEqual(4u, BytesTransferred);
                    Assert.AreEqual(8UL, Info.FileSize);
                    Assert.IsTrue(Info.AllocationSize >= 8UL);
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(Buffer);
                }
            }
        }

        [TestMethod]
        public void Write_Append_ExtendsTheFile()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Write(Node, 6, "gh"));
                Assert.AreEqual(8UL, Node.FileInfo.FileSize);
                Ctx.CleanupAndClose(Node, "\\a.txt", DavFsContext.ModifiedFlags);

                Assert.AreEqual("abcdefgh", Ctx.Server.GetText("/dav/a.txt"), "Exactly FileSize bytes are uploaded, not the allocation");
            }
        }

        [TestMethod]
        public void Write_AfterTheEnd_FillsTheGapWithZeros()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Ctx.Write(Node, 8, "Z");
                Ctx.CleanupAndClose(Node, "\\a.txt", DavFsContext.ModifiedFlags);

                CollectionAssert.AreEqual(new byte[] { (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e', (byte)'f', 0, 0, (byte)'Z' }, Ctx.Server.GetContent("/dav/a.txt"));
            }
        }

        [TestMethod]
        public void Write_WriteToEndOfFile_AppendsWhateverTheOffset()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Write(Node, 0, "!!", WriteToEndOfFile: true));
                Ctx.CleanupAndClose(Node, "\\a.txt", DavFsContext.ModifiedFlags);

                Assert.AreEqual("abcdef!!", Ctx.Server.GetText("/dav/a.txt"));
            }
        }

        [TestMethod]
        public void Write_ConstrainedIo_DoesNotExtendTheFile()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Int32 Status = Ctx.Write(Node, 4, Bytes("WXYZ"), false, true, out UInt32 BytesTransferred);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                Assert.AreEqual(2u, BytesTransferred);
                Assert.AreEqual(6UL, Node.FileInfo.FileSize);
                Ctx.CleanupAndClose(Node, "\\a.txt", DavFsContext.ModifiedFlags);
                Assert.AreEqual("abcdWX", Ctx.Server.GetText("/dav/a.txt"));
            }
        }

        [TestMethod]
        public void Write_ConstrainedIoAfterTheEnd_WritesNothing()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Int32 Status = Ctx.Write(Node, 6, Bytes("XY"), false, true, out UInt32 BytesTransferred);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                Assert.AreEqual(0u, BytesTransferred);
                Assert.AreEqual(6UL, Node.FileInfo.FileSize);
                Ctx.CleanupAndClose(Node, "\\a.txt", DavFsContext.ModifiedFlags);
                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));
                Assert.AreEqual("abcdef", Ctx.Server.GetText("/dav/a.txt"));
            }
        }

        [TestMethod]
        public void Write_FlushAtCleanup_LogsTheWriteAndUploadsNothingYet()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Ctx.Write(Node, 0, "X");

                Assert.IsTrue(Node.HasUnflushedData);
                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));
                Assert.AreEqual("STATUS_SUCCESS", LastLog(Ctx, "Write").Result);
                Assert.AreEqual("\\a.txt", LastLog(Ctx, "Write").File);
            }
        }

        [TestMethod]
        public void Write_SeveralWrites_AreUploadedOnceAtCleanup()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Create("\\new.txt");
                int PutBefore = Ctx.Server.CountRequests("PUT");

                Ctx.Write(Node, 0, "Hello");
                Ctx.Write(Node, 5, " ");
                Ctx.Write(Node, 6, "World");
                Ctx.CleanupAndClose(Node, "\\new.txt", DavFsContext.ModifiedFlags);

                Assert.AreEqual(PutBefore + 1, Ctx.Server.CountRequests("PUT"));
                Assert.AreEqual("Hello World", Ctx.Server.GetText("/dav/new.txt"));
            }
        }

        [TestMethod]
        public void Write_OnADeletedNode_ReturnsUnexpectedIoError()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Fs.Cleanup(Node, null, "\\a.txt", FileSystemBase.CleanupDelete);

                Assert.AreEqual(FileSystemBase.STATUS_UNEXPECTED_IO_ERROR, Ctx.Write(Node, 0, "X"));
                Ctx.Fs.Close(Node, null);
                Assert.IsFalse(Ctx.Server.Exists("/dav/a.txt"), "The deleted file is not recreated");
            }
        }

        [TestMethod]
        public void Write_DownloadFailure_ReturnsNetworkUnreachable_AndNeverUploadsAPartialContent()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Server.ForcedStatus = AnswerAll("GET", 500);

                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Ctx.Write(Node, 2, "XY"));
                Assert.IsFalse(Node.HasUnflushedData);

                Ctx.Server.ForcedStatus = null;
                Ctx.CleanupAndClose(Node, "\\a.txt", DavFsContext.ModifiedFlags);

                Assert.AreEqual(0, Ctx.Server.CountRequests("PUT"));
                Assert.AreEqual("abcdef", Ctx.Server.GetText("/dav/a.txt"));
            }
        }

        [TestMethod]
        public void Write_401WhileLoadingTheContent_ReturnsNetworkUnreachableAndNotifies()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Server.RejectCredentials = true;

                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Ctx.Write(Node, 0, "X"));
                Assert.AreEqual(1, Ctx.AuthenticationFailures);
            }
        }

        [TestMethod]
        public void Write_BeyondTheMaximumFileSize_ReturnsDiskFull()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Int32 Status = Ctx.Write(Node, ThreeGigabytes, "X");

                Assert.AreEqual(FileSystemBase.STATUS_DISK_FULL, Status);
                Assert.AreEqual(6UL, Node.FileInfo.FileSize, "The file is left unchanged");
            }
        }

        [TestMethod]
        public void Write_BeyondTheMaximumFileSize_LeavesTheFileUnchanged()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Int32 Status = Ctx.Write(Node, ThreeGigabytes, "X");

                Assert.IsTrue(Status < 0, "The write must fail");
                Assert.AreEqual(6UL, Node.FileInfo.FileSize);
                Ctx.CleanupAndClose(Node, "\\a.txt", DavFsContext.ModifiedFlags);
                Assert.AreEqual("abcdef", Ctx.Server.GetText("/dav/a.txt"));
            }
        }

        #endregion

        #region Write (FlushAtWrite)

        #endregion
    }
}
