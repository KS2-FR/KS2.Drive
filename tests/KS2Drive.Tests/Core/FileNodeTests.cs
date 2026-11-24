using KS2Drive.FS;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using KS2Drive.WebDav;
using Item = KS2Drive.WebDav.WebDavItem;

namespace KS2Drive.Tests.Core
{
    [TestClass]
    public class FileNodeTests
    {
        private const string Library = "/dav";

        [TestInitialize]
        public void InitFileNode()
        {
            FileNode.Init(Library, WebDAVMode.WebDAV);
        }

        private static Item FileItem(string Href, long? Length = 10, string ETag = "\"etag\"")
        {
            return new WebDavItem { Path = Href, DisplayName = "name", ContentLength = Length, ETag = ETag };
        }

        #region Init / constructor

        [TestMethod]
        public void Constructor_WithoutInit_Throws()
        {
            FieldInfo IsInited = typeof(FileNode).GetField("_IsInited", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(IsInited);
            IsInited.SetValue(null, false);
            try
            {
                Assert.ThrowsException<InvalidOperationException>(() => new FileNode(FileItem("/dav/a.txt")));
            }
            finally
            {
                FileNode.Init(Library, WebDAVMode.WebDAV);
            }
            Assert.IsTrue((bool)IsInited.GetValue(null));
        }

        [TestMethod]
        public void Init_SetsLibraryPathUsedByConversions()
        {
            FileNode.Init("/other/lib", WebDAVMode.WebDAV);
            Assert.AreEqual("/other/lib/x", FileNode.ConvertLocalPathToRepositoryPath("\\x"));
            Assert.AreEqual("\\x", FileNode.ConvertRepositoryPathToLocalPath("/other/lib/x"));
            Assert.IsTrue(FileNode.IsRepositoryRootPath("/other/lib"));
            Assert.IsFalse(FileNode.IsRepositoryRootPath("/dav"));
        }

        [TestMethod]
        public void Init_ModeNoLongerChangesTheFolderDetection()
        {
            //Both modes rely on the standard resourcetype property
            var I = new WebDavItem { Path = "/dav/f", ContentLength = 0, ETag = null, IsCollection = false };
            FileNode.Init(Library, WebDAVMode.WebDAV);
            Assert.AreEqual((uint)FileAttributes.Normal, new FileNode(I).FileInfo.FileAttributes);
            FileNode.Init(Library, WebDAVMode.AOS);
            Assert.AreEqual((uint)FileAttributes.Normal, new FileNode(I).FileInfo.FileAttributes);
        }

        [TestMethod]
        public void Constructor_RelativeHref_SetsPathsAndName()
        {
            var Node = new FileNode(new WebDavItem { Path = "/dav/folder/file.txt", DisplayName = "file.txt", ContentLength = 3, ETag = "e" });
            Assert.AreEqual("file.txt", Node.Name);
            Assert.AreEqual("/dav/folder/file.txt", Node.RepositoryPath);
            Assert.AreEqual("\\folder\\file.txt", Node.LocalPath);
        }

        [TestMethod]
        public void Constructor_TrailingSlash_IsRemovedFromRepositoryPath()
        {
            var Node = new FileNode(new WebDavItem { Path = "/dav/folder/", ContentLength = null });
            Assert.AreEqual("/dav/folder", Node.RepositoryPath);
            Assert.AreEqual("\\folder", Node.LocalPath);
        }

        [DataTestMethod]
        [DataRow("/dav/a b/C++ notes.txt", "\\a b\\C++ notes.txt")]
        [DataRow("/dav/a%20b.txt", "\\a%20b.txt")] //A literal "%20" in a name: paths are decoded, never decoded again
        [DataRow("/dav/caf\u00e9/r\u00e9sum\u00e9.docx", "\\caf\u00e9\\r\u00e9sum\u00e9.docx")]
        [DataRow("/dav/c#d.txt", "\\c#d.txt")]
        public void Constructor_DecodedPath_IsKeptAsIs(string Path, string ExpectedLocalPath)
        {
            //Decoding the hrefs of the server is the job of MultiStatusParser: FileNode receives decoded paths
            var Node = new FileNode(new WebDavItem { Path = Path, ContentLength = 1 });
            Assert.AreEqual(Path, Node.RepositoryPath);
            Assert.AreEqual(ExpectedLocalPath, Node.LocalPath);
        }

        [TestMethod]
        public void Constructor_LibraryRoot_MapsToDriveRoot()
        {
            var Node = new FileNode(new WebDavItem { Path = "/dav/", ContentLength = null });
            Assert.AreEqual("/dav", Node.RepositoryPath);
            Assert.AreEqual("\\", Node.LocalPath);
        }

        [TestMethod]
        public void Constructor_SlashHref_IsKept()
        {
            var Node = new FileNode(new WebDavItem { Path = "/", ContentLength = null });
            Assert.AreEqual("/", Node.RepositoryPath);
            Assert.AreEqual("\\", Node.LocalPath);
        }

        [DataTestMethod]
        [DataRow(true, 10L, "\"e\"", FileAttributes.Directory)]
        [DataRow(true, null, null, FileAttributes.Directory)]
        [DataRow(false, null, null, FileAttributes.Normal)]
        [DataRow(false, 0L, null, FileAttributes.Normal)]
        [DataRow(false, 10L, "\"e\"", FileAttributes.Normal)]
        public void Constructor_AosMode_UsesIsCollection(bool IsCollection, long? Length, string Etag, FileAttributes Expected)
        {
            FileNode.Init(Library, WebDAVMode.AOS);
            var Node = new FileNode(new WebDavItem { Path = "/dav/x", IsCollection = IsCollection, ContentLength = Length, ETag = Etag });
            Assert.AreEqual((uint)Expected, Node.FileInfo.FileAttributes);
        }

        [DataTestMethod]
        [DataRow(null, null, false, FileAttributes.Normal)]        //File without length nor ETag (server not sending getcontentlength)
        [DataRow(0L, null, false, FileAttributes.Normal)]          //Empty file without ETag
        [DataRow(5L, "\"e\"", false, FileAttributes.Normal)]
        [DataRow(null, null, true, FileAttributes.Directory)]      //resourcetype collection
        [DataRow(5L, "\"e\"", true, FileAttributes.Directory)]
        public void Constructor_WebDavMode_UsesResourceType(long? Length, string Etag, bool IsCollection, FileAttributes Expected)
        {
            //The former heuristic (no etag and no size, or no size => folder) made files folders on some servers
            var Node = new FileNode(new WebDavItem { Path = "/dav/x", IsCollection = IsCollection, ContentLength = Length, ETag = Etag });
            Assert.AreEqual((uint)Expected, Node.FileInfo.FileAttributes);
        }

        [TestMethod]
        public void Constructor_Dates_AreConverted()
        {
            var Created = new DateTime(2019, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            var Modified = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            long Before = DateTime.Now.ToFileTimeUtc();
            DateTime BeforeRefresh = DateTime.Now;
            var Node = new FileNode(new WebDavItem { Path = "/dav/x", ContentLength = 1, ETag = "e", CreationDate = Created, LastModified = Modified });
            long After = DateTime.Now.ToFileTimeUtc();

            Assert.AreEqual((ulong)Created.ToFileTimeUtc(), Node.FileInfo.CreationTime);
            Assert.AreEqual((ulong)Modified.ToFileTimeUtc(), Node.FileInfo.LastWriteTime);
            Assert.AreEqual((ulong)Modified.ToFileTimeUtc(), Node.FileInfo.ChangeTime);
            Assert.IsTrue(Node.FileInfo.LastAccessTime >= (ulong)Before && Node.FileInfo.LastAccessTime <= (ulong)After);
            Assert.IsTrue(Node.LastRefresh >= BeforeRefresh && Node.LastRefresh <= DateTime.Now);
        }

        [TestMethod]
        public void Constructor_MissingDates_AreZero()
        {
            var Node = new FileNode(new WebDavItem { Path = "/dav/x", ContentLength = 1, ETag = "e" });
            Assert.AreEqual(0UL, Node.FileInfo.CreationTime);
            Assert.AreEqual(0UL, Node.FileInfo.LastWriteTime);
            Assert.AreEqual(0UL, Node.FileInfo.ChangeTime);
            Assert.AreNotEqual(0UL, Node.FileInfo.LastAccessTime);
        }

        [TestMethod]
        public void Constructor_Sizes_FromContentLength()
        {
            var Node = new FileNode(new WebDavItem { Path = "/dav/x", ContentLength = 5000000000L, ETag = "e" });
            Assert.AreEqual(5000000000UL, Node.FileInfo.FileSize);
            Assert.AreEqual(5000000000UL, Node.FileInfo.AllocationSize);

            var Folder = new FileNode(new WebDavItem { Path = "/dav/y", ContentLength = null });
            Assert.AreEqual(0UL, Folder.FileInfo.FileSize);
            Assert.AreEqual(0UL, Folder.FileInfo.AllocationSize);
        }

        [TestMethod]
        public void Constructor_InitialState()
        {
            var Node = new FileNode(FileItem("/dav/x"));
            CollectionAssert.AreEqual(FileNode.GetDefaultSecurity(), Node.FileSecurity);
            Assert.IsNull(Node.FileData);
            Assert.IsFalse(Node.HasUnflushedData);
            Assert.IsFalse(Node.IsParsed);
            Assert.IsFalse(Node.IsDeleted);
            Assert.AreEqual(0, Node.OpenCount);
            Assert.AreEqual("", Node.TemporaryLocalCopyPath);
            Assert.IsNotNull(Node.OperationLock);
        }

        [TestMethod]
        public void Constructor_ObjectIds_AreSequentialAndUnique()
        {
            var N1 = new FileNode(FileItem("/dav/a"));
            var N2 = new FileNode(FileItem("/dav/a"));
            Assert.AreEqual(int.Parse(N1.ObjectId) + 1, int.Parse(N2.ObjectId));
            Assert.AreNotSame(N1.OperationLock, N2.OperationLock);
        }

        #endregion

        #region Path conversions

        [DataTestMethod]
        [DataRow("/dav/a/b.txt", "\\a\\b.txt")]
        [DataRow("/dav/a/b/", "\\a\\b")]
        [DataRow("/dav", "\\")]
        [DataRow("/dav/", "\\")]
        [DataRow("/DAV/a", "\\a")]                 //Case-insensitive library prefix
        [DataRow("/davx/a", "\\davx\\a")]          //Library removed only on a segment boundary
        [DataRow("/other/dav/a", "\\other\\dav\\a")] //Library removed only as a prefix
        [DataRow("/dav/a/dav/b", "\\a\\dav\\b")]
        [DataRow("/dav/a+b", "\\a+b")]
        public void ConvertRepositoryPathToLocalPath(string Repository, string Expected)
        {
            Assert.AreEqual(Expected, FileNode.ConvertRepositoryPathToLocalPath(Repository));
        }

        [DataTestMethod]
        [DataRow("\\a\\b.txt", "/dav/a/b.txt")]
        [DataRow("\\", "/dav")]
        [DataRow("\\a\\", "/dav/a")]
        [DataRow("\\a b+c", "/dav/a b+c")]   //Not URL-encoded
        [DataRow("\\caf\u00e9", "/dav/caf\u00e9")]
        public void ConvertLocalPathToRepositoryPath(string Local, string Expected)
        {
            Assert.AreEqual(Expected, FileNode.ConvertLocalPathToRepositoryPath(Local));
        }

        [TestMethod]
        public void ConvertLocalPathToRepositoryPath_RoundTrip()
        {
            string Local = "\\folder\\sub folder\\file+1.txt";
            Assert.AreEqual(Local, FileNode.ConvertRepositoryPathToLocalPath(FileNode.ConvertLocalPathToRepositoryPath(Local)));
        }

        [DataTestMethod]
        [DataRow("/dav/a", "/dav/a", true)]
        [DataRow("/dav/a/", "/dav/a", true)]
        [DataRow("/dav/a//", "/dav/a", true)]
        [DataRow("/DAV/A", "/dav/a", true)]
        [DataRow("/dav/a", "/dav/b", false)]
        [DataRow("/dav/a", "/dav/ab", false)]
        [DataRow("/dav/a+b", "/dav/a b", false)]
        [DataRow(null, null, true)]
        [DataRow(null, "/dav", false)]
        [DataRow("/dav", null, false)]
        public void IsSameRepositoryPath(string P1, string P2, bool Expected)
        {
            Assert.AreEqual(Expected, FileNode.IsSameRepositoryPath(P1, P2));
            Assert.AreEqual(Expected, FileNode.IsSameRepositoryPath(P2, P1));
        }

        [DataTestMethod]
        [DataRow("/dav", true)]
        [DataRow("/dav/", true)]
        [DataRow("/DAV", true)]
        [DataRow("/dav/a", false)]
        [DataRow("/", false)]
        [DataRow("/davx", false)]
        public void IsRepositoryRootPath(string Path, bool Expected)
        {
            Assert.AreEqual(Expected, FileNode.IsRepositoryRootPath(Path));
        }

        [DataTestMethod]
        [DataRow("/dav/a/b.txt", "b.txt")]
        [DataRow("/dav/a/", "a")]
        [DataRow("/dav/a", "a")]
        [DataRow("/dav/a%20b", "a%20b")]   //Not decoded
        [DataRow("/dav", "\\")]
        [DataRow("/dav/", "\\")]
        [DataRow("/DAV/", "\\")]
        public void GetRepositoryDocumentName(string Path, string Expected)
        {
            Assert.AreEqual(Expected, FileNode.GetRepositoryDocumentName(Path));
        }

        [DataTestMethod]
        [DataRow("/dav/a/b.txt", "/dav/a")]
        [DataRow("/dav/a/b/", "/dav/a")]
        [DataRow("/dav/a", "/dav")]
        [DataRow("/dav/a/", "/dav")]
        [DataRow("/dav", null)]
        [DataRow("/dav/", null)]
        [DataRow("/DAV", null)]
        [DataRow("/d", null)]
        [DataRow("/", null)]
        public void GetRepositoryParentPath(string Path, string Expected)
        {
            Assert.AreEqual(Expected, FileNode.GetRepositoryParentPath(Path));
        }

        #endregion

        #region Security

        [TestMethod]
        public void GetDefaultSecurity_IsTheExpectedDescriptor()
        {
            byte[] Bytes = FileNode.GetDefaultSecurity();
            Assert.IsNotNull(Bytes);
            var Descriptor = new RawSecurityDescriptor(Bytes, 0);
            Assert.AreEqual(Bytes.Length, Descriptor.BinaryLength);
            Assert.AreEqual("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)", Descriptor.GetSddlForm(AccessControlSections.All));

            var Admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            Assert.AreEqual(Admins, Descriptor.Owner);
            Assert.AreEqual(Admins, Descriptor.Group);
            Assert.IsTrue(Descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
            Assert.AreEqual(3, Descriptor.DiscretionaryAcl.Count);
        }

        [TestMethod]
        public void GetDefaultSecurity_ReturnsANewArrayEachTime()
        {
            byte[] B1 = FileNode.GetDefaultSecurity();
            byte[] B2 = FileNode.GetDefaultSecurity();
            Assert.AreNotSame(B1, B2);
            CollectionAssert.AreEqual(B1, B2);
        }

        #endregion

        #region Local copy

        private static FileNode NodeWithData(byte[] Data, ulong FileSize)
        {
            var Node = new FileNode(FileItem("/dav/copy.bin", (long)FileSize));
            Node.FileData = Data;
            Node.FileInfo.FileSize = FileSize;
            return Node;
        }

        private static void DeleteIfExists(string Path)
        {
            if (!String.IsNullOrEmpty(Path) && File.Exists(Path)) File.Delete(Path);
        }

        [TestMethod]
        public void GenerateLocalCopy_WritesOnlyFileSizeBytes()
        {
            var Node = NodeWithData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0, 0 }, 5);
            try
            {
                Node.GenerateLocalCopy();
                Assert.IsFalse(String.IsNullOrEmpty(Node.TemporaryLocalCopyPath));
                Assert.IsTrue(File.Exists(Node.TemporaryLocalCopyPath));
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(Node.TemporaryLocalCopyPath));
            }
            finally
            {
                DeleteIfExists(Node.TemporaryLocalCopyPath);
            }
        }

        [TestMethod]
        public void GenerateLocalCopy_FileSizeLargerThanBuffer_WritesWholeBuffer()
        {
            var Node = NodeWithData(new byte[] { 9, 8, 7 }, 100);
            try
            {
                Node.GenerateLocalCopy();
                CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, File.ReadAllBytes(Node.TemporaryLocalCopyPath));
            }
            finally
            {
                DeleteIfExists(Node.TemporaryLocalCopyPath);
            }
        }

        [TestMethod]
        public void GenerateLocalCopy_EmptyFile_WritesEmptyCopy()
        {
            var Node = NodeWithData(new byte[16], 0);
            try
            {
                Node.GenerateLocalCopy();
                Assert.IsTrue(File.Exists(Node.TemporaryLocalCopyPath));
                Assert.AreEqual(0, File.ReadAllBytes(Node.TemporaryLocalCopyPath).Length);
            }
            finally
            {
                DeleteIfExists(Node.TemporaryLocalCopyPath);
            }
        }

        [TestMethod]
        public void GenerateLocalCopy_SecondCall_ReusesPathAndReplacesContent()
        {
            var Node = NodeWithData(new byte[] { 1, 2, 3, 4, 5, 6 }, 6);
            try
            {
                Node.GenerateLocalCopy();
                string FirstPath = Node.TemporaryLocalCopyPath;

                Node.FileData = new byte[] { 7, 7, 0, 0 };
                Node.FileInfo.FileSize = 2;
                Node.GenerateLocalCopy();

                Assert.AreEqual(FirstPath, Node.TemporaryLocalCopyPath);
                CollectionAssert.AreEqual(new byte[] { 7, 7 }, File.ReadAllBytes(FirstPath), "The previous longer content must be truncated");
            }
            finally
            {
                DeleteIfExists(Node.TemporaryLocalCopyPath);
            }
        }

        [TestMethod]
        public void GenerateLocalCopy_Failure_SetsPathToNull()
        {
            var Node = NodeWithData(new byte[] { 1, 2, 3 }, 3);
            Node.TemporaryLocalCopyPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KS2DriveTests_missing_" + Guid.NewGuid().ToString("N"), "copy.bin");
            Node.GenerateLocalCopy();
            Assert.IsNull(Node.TemporaryLocalCopyPath);
        }

        [TestMethod]
        public void GenerateLocalCopy_AfterFailure_CreatesNewCopy()
        {
            var Node = NodeWithData(new byte[] { 1, 2, 3 }, 3);
            Node.TemporaryLocalCopyPath = null;
            try
            {
                Node.GenerateLocalCopy();
                Assert.IsFalse(String.IsNullOrEmpty(Node.TemporaryLocalCopyPath));
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Node.TemporaryLocalCopyPath));
            }
            finally
            {
                DeleteIfExists(Node.TemporaryLocalCopyPath);
            }
        }

        [TestMethod]
        public void DeleteLocalCopy_DeletesFileAndResetsPath()
        {
            var Node = NodeWithData(new byte[] { 1, 2 }, 2);
            Node.GenerateLocalCopy();
            string Path = Node.TemporaryLocalCopyPath;
            try
            {
                Assert.IsTrue(File.Exists(Path));
                Node.DeleteLocalCopy();
                Assert.IsFalse(File.Exists(Path));
                Assert.AreEqual("", Node.TemporaryLocalCopyPath);
            }
            finally
            {
                DeleteIfExists(Path);
            }
        }

        [TestMethod]
        public void DeleteLocalCopy_NoCopy_DoesNothing()
        {
            var Node = NodeWithData(new byte[] { 1 }, 1);
            Node.DeleteLocalCopy();
            Assert.AreEqual("", Node.TemporaryLocalCopyPath);

            Node.TemporaryLocalCopyPath = null;
            Node.DeleteLocalCopy();
            Assert.IsNull(Node.TemporaryLocalCopyPath);
        }

        [TestMethod]
        public void DeleteLocalCopy_FileAlreadyGone_ResetsPath()
        {
            var Node = NodeWithData(new byte[] { 1 }, 1);
            Node.TemporaryLocalCopyPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KS2DriveTests_gone_" + Guid.NewGuid().ToString("N") + ".tmp");
            Node.DeleteLocalCopy();
            Assert.AreEqual("", Node.TemporaryLocalCopyPath);
        }

        [TestMethod]
        public void DeleteLocalCopy_LockedFile_DoesNotThrowAndResetsPath()
        {
            var Node = NodeWithData(new byte[] { 1, 2, 3 }, 3);
            Node.GenerateLocalCopy();
            string Path = Node.TemporaryLocalCopyPath;
            try
            {
                using (new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Node.DeleteLocalCopy();
                    Assert.AreEqual("", Node.TemporaryLocalCopyPath);
                }
                Assert.IsTrue(File.Exists(Path), "A file that could not be deleted stays in the temporary folder");
            }
            finally
            {
                DeleteIfExists(Path);
            }
        }

        #endregion
    }
}
