using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WebDAVClient.Helpers;

namespace KS2Drive.Tests.WebDAVClientTests
{
    /// <summary>
    /// Client.Download and Client.Upload.
    /// </summary>
    [TestClass]
    public class ClientTransferTests
    {
        private static MemoryStream Text(String Content) => new MemoryStream(Encoding.UTF8.GetBytes(Content));

        #region Download

        [TestMethod]
        public void Download_ReturnsTheExactBytes()
        {
            var Bytes = Enumerable.Range(0, 70000).Select(i => (byte)(i % 251)).ToArray();
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/data.bin", Bytes);
                CollectionAssert.AreEqual(Bytes, DavAssert.Run(F.Client.Download("/data.bin")));
            }
        }

        [TestMethod]
        public void Download_SendsGetWithTranslateHeaderAndNoTrailingSlash()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/sub");
                F.Server.AddFile("/dav/sub/a b.txt", "x");
                DavAssert.Run(F.Client.Download("/sub/a b.txt"));
                var R = F.Recorder.Last;
                Assert.AreEqual("GET", R.Method);
                Assert.AreEqual("http://fake.local/dav/sub/a%20b.txt", R.AbsoluteUri);
                Assert.AreEqual("f", R.Header("translate"));
            }
        }

        [TestMethod]
        public void Download_WithFullRepositoryPath()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "abc");
                Assert.AreEqual("abc", Encoding.UTF8.GetString(DavAssert.Run(F.Client.Download("/dav/a.txt"))));
                Assert.AreEqual("GET /dav/a.txt", F.Server.Requests.Last());
            }
        }

        [TestMethod]
        public void Download_EmptyFile_ReturnsEmptyArray()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/empty.txt", new byte[0]);
                var Result = DavAssert.Run(F.Client.Download("/empty.txt"));
                Assert.IsNotNull(Result);
                Assert.AreEqual(0, Result.Length);
            }
        }

        [TestMethod]
        public void Download_Missing_ThrowsWebDavException404()
        {
            using (var F = new DavFixture())
            {
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.Download("/nope.txt"));
                Assert.AreEqual(404, Ex.GetHttpCode());
            }
        }

        [DataTestMethod]
        [DataRow(401)]
        [DataRow(403)]
        [DataRow(500)]
        [DataRow(204)]
        public void Download_NonOkStatus_ThrowsWithCode(int Status)
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "a");
                F.Server.ForcedStatus = (m, p) => (HttpStatusCode?)(HttpStatusCode)Status;
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.Download("/a.txt"));
                Assert.AreEqual(Status, Ex.GetHttpCode());
            }
        }

        [TestMethod]
        public void Download_NetworkFailure_PropagatesHttpRequestException()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "a");
                F.Server.NetworkFailure = (m, p) => m == "GET";
                DavAssert.ThrowsExactly<HttpRequestException>(() => F.Client.Download("/a.txt"));
            }
        }

        #endregion

        #region Upload

        [TestMethod]
        public void Upload_NewFile_ReturnsTrueAndStoresContent()
        {
            using (var F = new DavFixture())
            {
                Assert.IsTrue(DavAssert.Run(F.Client.Upload("/", Text("hello"), "new.txt")));
                Assert.AreEqual("hello", F.Server.GetText("/dav/new.txt"));
                var R = F.Recorder.Last;
                Assert.AreEqual("PUT", R.Method);
                Assert.AreEqual("http://fake.local/dav/new.txt", R.AbsoluteUri);
            }
        }

        [TestMethod]
        public void Upload_ExistingFile_IsOverwrittenAndReturnsTrue()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "old content");
                Assert.IsTrue(DavAssert.Run(F.Client.Upload("/", Text("new"), "a.txt")));
                Assert.AreEqual("new", F.Server.GetText("/dav/a.txt"));
            }
        }

        [DataTestMethod]
        [DataRow("/sub", "n.txt")]
        [DataRow("/sub/", "n.txt")]
        [DataRow("/sub/", "/n.txt")]
        [DataRow("sub", "n.txt")]
        [DataRow("/dav/sub", "n.txt")]
        [DataRow("/dav/sub/", "/n.txt")]
        public void Upload_JoinsFolderAndNameWithASingleSlash(String Folder, String Name)
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/sub");
                DavAssert.Run(F.Client.Upload(Folder, Text("x"), Name));
                Assert.AreEqual("http://fake.local/dav/sub/n.txt", F.Recorder.Last.AbsoluteUri);
                Assert.AreEqual("x", F.Server.GetText("/dav/sub/n.txt"));
            }
        }

        [TestMethod]
        public void Upload_NameWithSpacesAccentsAndPlus_IsEncoded()
        {
            using (var F = new DavFixture())
            {
                DavAssert.Run(F.Client.Upload("/", Text("x"), "a b é+.txt"));
                Assert.AreEqual("http://fake.local/dav/a%20b%20%C3%A9+.txt", F.Recorder.Last.AbsoluteUri);
                Assert.AreEqual("x", F.Server.GetText("/dav/a b é+.txt"));
            }
        }

        [TestMethod]
        public void Upload_IntoEncodedFolderHref()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/my docs");
                DavAssert.Run(F.Client.Upload("/dav/my%20docs/", Text("x"), "f.txt"));
                Assert.AreEqual("http://fake.local/dav/my%20docs/f.txt", F.Recorder.Last.AbsoluteUri);
                Assert.AreEqual("x", F.Server.GetText("/dav/my docs/f.txt"));
            }
        }

        [TestMethod]
        public void Upload_NullContent_CreatesAnEmptyFile()
        {
            using (var F = new DavFixture())
            {
                Assert.IsTrue(DavAssert.Run(F.Client.Upload("/", null, "empty.txt")));
                Assert.IsTrue(F.Server.Exists("/dav/empty.txt"));
                Assert.AreEqual(0, F.Server.GetContent("/dav/empty.txt").Length);
            }
        }

        [TestMethod]
        public void Upload_LargeBinaryContent_IsSentEntirely()
        {
            var Bytes = Enumerable.Range(0, 300000).Select(i => (byte)(i * 7)).ToArray();
            using (var F = new DavFixture())
            {
                DavAssert.Run(F.Client.Upload("/", new MemoryStream(Bytes), "big.bin"));
                CollectionAssert.AreEqual(Bytes, F.Server.GetContent("/dav/big.bin"));
            }
        }

        [TestMethod]
        public void Upload_MissingParent_ThrowsConflictException()
        {
            using (var F = new DavFixture())
            {
                var Ex = DavAssert.ThrowsExactly<WebDAVConflictException>(() => F.Client.Upload("/missing", Text("x"), "a.txt"));
                Assert.AreEqual(409, Ex.GetHttpCode());
                Assert.IsFalse(F.Server.Exists("/dav/missing/a.txt"));
            }
        }

        [TestMethod]
        public void Upload_OverAFolder_ThrowsWebDavException405()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/sub");
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.Upload("/", Text("x"), "sub"));
                Assert.AreEqual(405, Ex.GetHttpCode());
            }
        }

        [DataTestMethod]
        [DataRow(401)]
        [DataRow(403)]
        [DataRow(423)]
        [DataRow(500)]
        [DataRow(507)]
        public void Upload_ErrorStatus_ThrowsWebDavExceptionWithCode(int Status)
        {
            using (var F = new DavFixture())
            {
                F.Server.ForcedStatus = (m, p) => (HttpStatusCode?)(HttpStatusCode)Status;
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.Upload("/", Text("x"), "a.txt"));
                Assert.AreEqual(Status, Ex.GetHttpCode());
            }
        }

        [DataTestMethod]
        [DataRow(200)]
        [DataRow(201)]
        [DataRow(204)]
        public void Upload_SuccessStatus_ReturnsTrue(int Status)
        {
            using (var F = new DavFixture())
            {
                F.Server.ForcedStatus = (m, p) => (HttpStatusCode?)(HttpStatusCode)Status;
                Assert.IsTrue(DavAssert.Run(F.Client.Upload("/", Text("x"), "a.txt")));
            }
        }

        [TestMethod]
        public void Upload_NetworkFailure_PropagatesHttpRequestException()
        {
            using (var F = new DavFixture())
            {
                F.Server.NetworkFailure = (m, p) => m == "PUT";
                DavAssert.ThrowsExactly<HttpRequestException>(() => F.Client.Upload("/", Text("x"), "a.txt"));
            }
        }

        #endregion
    }
}
