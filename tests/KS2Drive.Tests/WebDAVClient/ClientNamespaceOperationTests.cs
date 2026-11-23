using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WebDAVClient.Helpers;

namespace KS2Drive.Tests.WebDAVClientTests
{
    /// <summary>
    /// Client.CreateDir, Client.DeleteFile, Client.DeleteFolder, Client.MoveFile and Client.MoveFolder.
    /// </summary>
    [TestClass]
    public class ClientNamespaceOperationTests
    {
        #region CreateDir

        [TestMethod]
        public void CreateDir_SendsMkcolAndReturnsTrue()
        {
            using (var F = new DavFixture())
            {
                Assert.IsTrue(DavAssert.Run(F.Client.CreateDir("/", "newdir")));
                var R = F.Recorder.Last;
                Assert.AreEqual("MKCOL", R.Method);
                Assert.AreEqual("http://fake.local/dav/newdir", R.AbsoluteUri);
                Assert.IsTrue(F.Server.IsFolder("/dav/newdir"));
            }
        }

        [DataTestMethod]
        [DataRow("/sub", "child")]
        [DataRow("/sub/", "/child")]
        [DataRow("/dav/sub", "child")]
        [DataRow("sub", "child")]
        public void CreateDir_InSubFolder(String Parent, String Name)
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/sub");
                Assert.IsTrue(DavAssert.Run(F.Client.CreateDir(Parent, Name)));
                Assert.AreEqual("http://fake.local/dav/sub/child", F.Recorder.Last.AbsoluteUri);
                Assert.IsTrue(F.Server.IsFolder("/dav/sub/child"));
            }
        }

        [TestMethod]
        public void CreateDir_NameWithSpacesAndAccents_IsEncoded()
        {
            using (var F = new DavFixture())
            {
                DavAssert.Run(F.Client.CreateDir("/", "Dossier é"));
                Assert.AreEqual("http://fake.local/dav/Dossier%20%C3%A9", F.Recorder.Last.AbsoluteUri);
                Assert.IsTrue(F.Server.IsFolder("/dav/Dossier é"));
            }
        }

        [TestMethod]
        public void CreateDir_AlreadyExists_ThrowsWebDavException405()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/sub");
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.CreateDir("/", "sub"));
                Assert.AreEqual(405, Ex.GetHttpCode());
            }
        }

        [TestMethod]
        public void CreateDir_MissingParent_ThrowsConflictException()
        {
            using (var F = new DavFixture())
            {
                var Ex = DavAssert.ThrowsExactly<WebDAVConflictException>(() => F.Client.CreateDir("/missing", "child"));
                Assert.AreEqual(409, Ex.GetHttpCode());
                Assert.IsFalse(F.Server.Exists("/dav/missing/child"));
            }
        }

        [DataTestMethod]
        [DataRow(401)]
        [DataRow(403)]
        [DataRow(500)]
        public void CreateDir_ErrorStatus_ThrowsWithCode(int Status)
        {
            using (var F = new DavFixture())
            {
                F.Server.ForcedStatus = (m, p) => (HttpStatusCode?)(HttpStatusCode)Status;
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.CreateDir("/", "d"));
                Assert.AreEqual(Status, Ex.GetHttpCode());
            }
        }

        [DataTestMethod]
        [DataRow(200)]
        [DataRow(204)]
        public void CreateDir_OtherSuccessStatus_ReturnsTrue(int Status)
        {
            using (var F = new DavFixture())
            {
                F.Server.ForcedStatus = (m, p) => (HttpStatusCode?)(HttpStatusCode)Status;
                Assert.IsTrue(DavAssert.Run(F.Client.CreateDir("/", "d")));
            }
        }

        [TestMethod]
        public void CreateDir_NetworkFailure_PropagatesHttpRequestException()
        {
            using (var F = new DavFixture())
            {
                F.Server.NetworkFailure = (m, p) => true;
                DavAssert.ThrowsExactly<HttpRequestException>(() => F.Client.CreateDir("/", "d"));
            }
        }

        #endregion

        #region DeleteFile / DeleteFolder

        [TestMethod]
        public void DeleteFile_SendsDeleteWithoutTrailingSlash()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a b.txt", "x");
                DavAssert.Run(F.Client.DeleteFile("/a b.txt"));
                var R = F.Recorder.Last;
                Assert.AreEqual("DELETE", R.Method);
                Assert.AreEqual("http://fake.local/dav/a%20b.txt", R.AbsoluteUri);
                Assert.IsFalse(F.Server.Exists("/dav/a b.txt"));
            }
        }

        [TestMethod]
        public void DeleteFile_WithEncodedHref()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a#1.txt", "x");
                DavAssert.Run(F.Client.DeleteFile("/dav/a%231.txt"));
                Assert.IsFalse(F.Server.Exists("/dav/a#1.txt"));
            }
        }

        [TestMethod]
        public void DeleteFile_Missing_Throws404()
        {
            using (var F = new DavFixture())
            {
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.DeleteFile("/nope.txt"));
                Assert.AreEqual(404, Ex.GetHttpCode());
            }
        }

        [DataTestMethod]
        [DataRow(207)]
        [DataRow(401)]
        [DataRow(423)]
        [DataRow(500)]
        public void DeleteFile_ErrorStatus_ThrowsWithCode(int Status)
        {
            // 207 on DELETE means that some members could not be deleted: it is a failure
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "x");
                F.Server.ForcedStatus = (m, p) => (HttpStatusCode?)(HttpStatusCode)Status;
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.DeleteFile("/a.txt"));
                Assert.AreEqual(Status, Ex.GetHttpCode());
            }
        }

        [TestMethod]
        public void DeleteFile_200Ok_IsSuccess()
        {
            using (var F = new DavFixture())
            {
                F.Server.ForcedStatus = (m, p) => HttpStatusCode.OK;
                DavAssert.Run(F.Client.DeleteFile("/a.txt"));
            }
        }

        [TestMethod]
        public void DeleteFolder_SendsDeleteWithTrailingSlashAndRemovesChildren()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/sub");
                F.Server.AddFile("/dav/sub/a.txt", "x");
                DavAssert.Run(F.Client.DeleteFolder("/sub"));
                var R = F.Recorder.Last;
                Assert.AreEqual("DELETE", R.Method);
                Assert.AreEqual("http://fake.local/dav/sub/", R.AbsoluteUri);
                Assert.IsFalse(F.Server.Exists("/dav/sub"));
                Assert.IsFalse(F.Server.Exists("/dav/sub/a.txt"));
            }
        }

        [TestMethod]
        public void DeleteFolder_WithFullRepositoryPathAndTrailingSlash()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/sub");
                DavAssert.Run(F.Client.DeleteFolder("/dav/sub/"));
                Assert.AreEqual("http://fake.local/dav/sub/", F.Recorder.Last.AbsoluteUri);
                Assert.IsFalse(F.Server.Exists("/dav/sub"));
            }
        }

        [TestMethod]
        public void DeleteFolder_Missing_Throws404()
        {
            using (var F = new DavFixture())
            {
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.DeleteFolder("/nope"));
                Assert.AreEqual(404, Ex.GetHttpCode());
            }
        }

        [TestMethod]
        public void Delete_NetworkFailure_PropagatesHttpRequestException()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "x");
                F.Server.AddFolder("/dav/sub");
                F.Server.NetworkFailure = (m, p) => m == "DELETE";
                DavAssert.ThrowsExactly<HttpRequestException>(() => F.Client.DeleteFile("/a.txt"));
                DavAssert.ThrowsExactly<HttpRequestException>(() => F.Client.DeleteFolder("/sub"));
            }
        }

        #endregion

        #region MoveFile / MoveFolder

        [TestMethod]
        public void MoveFile_SendsMoveWithEncodedDestinationAndMovesTheFile()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "content");
                Assert.IsTrue(DavAssert.Run(F.Client.MoveFile("/a.txt", "/b c.txt")));
                var R = F.Recorder.Last;
                Assert.AreEqual("MOVE", R.Method);
                Assert.AreEqual("http://fake.local/dav/a.txt", R.AbsoluteUri);
                Assert.AreEqual("http://fake.local/dav/b%20c.txt", R.Header("Destination"));
                Assert.IsFalse(F.Server.Exists("/dav/a.txt"));
                Assert.AreEqual("content", F.Server.GetText("/dav/b c.txt"));
            }
        }

        [TestMethod]
        public void MoveFile_ToAnotherFolder_WithRepositoryPaths()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/src");
                F.Server.AddFolder("/dav/dst");
                F.Server.AddFile("/dav/src/a.txt", "x");
                Assert.IsTrue(DavAssert.Run(F.Client.MoveFile("/dav/src/a.txt", "/dav/dst/a.txt")));
                Assert.AreEqual("http://fake.local/dav/dst/a.txt", F.Recorder.Last.Header("Destination"));
                Assert.AreEqual("x", F.Server.GetText("/dav/dst/a.txt"));
            }
        }

        [TestMethod]
        public void MoveFile_AccentedAndPlusDestination_ArrivesDecodedOnTheServer()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "x");
                DavAssert.Run(F.Client.MoveFile("/a.txt", "/résumé + notes.txt"));
                Assert.IsTrue(F.Server.Exists("/dav/résumé + notes.txt"));
                Assert.IsFalse(F.Server.Exists("/dav/a.txt"));
                String Destination = F.Recorder.Last.Header("Destination");
                Assert.IsTrue(Destination.StartsWith("http://fake.local/dav/"));
                Assert.IsFalse(Destination.Contains(" "));
                Assert.IsFalse(Destination.Contains("é"));
            }
        }

        [TestMethod]
        public void MoveFile_EncodedDestinationHref_IsNotDoubleEncoded()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "x");
                DavAssert.Run(F.Client.MoveFile("/dav/a.txt", "/dav/x%20y%231.txt"));
                Assert.IsTrue(F.Server.Exists("/dav/x y#1.txt"));
            }
        }

        [TestMethod]
        public void MoveFile_DecodedHashInDestination_IsNotTruncated()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "x");
                DavAssert.Run(F.Client.MoveFile("/a.txt", "/b#2.txt"));
                Assert.IsTrue(F.Server.Exists("/dav/b#2.txt"));
                Assert.IsFalse(F.Server.Exists("/dav/b"));
            }
        }

        [TestMethod]
        public void MoveFile_MissingSource_Throws404()
        {
            using (var F = new DavFixture())
            {
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.MoveFile("/nope.txt", "/b.txt"));
                Assert.AreEqual(404, Ex.GetHttpCode());
            }
        }

        [TestMethod]
        public void MoveFile_MissingDestinationParent_Throws409()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "x");
                var Ex = DavAssert.Throws<WebDAVException>(() => F.Client.MoveFile("/a.txt", "/missing/b.txt"));
                Assert.AreEqual(409, Ex.GetHttpCode());
                Assert.IsTrue(F.Server.Exists("/dav/a.txt"));
            }
        }

        [DataTestMethod]
        [DataRow(401)]
        [DataRow(412)]
        [DataRow(423)]
        [DataRow(502)]
        public void MoveFile_ErrorStatus_ThrowsWithCode(int Status)
        {
            using (var F = new DavFixture())
            {
                F.Server.ForcedStatus = (m, p) => (HttpStatusCode?)(HttpStatusCode)Status;
                var Ex = DavAssert.Throws<WebDAVException>(() => F.Client.MoveFile("/a.txt", "/b.txt"));
                Assert.AreEqual(Status, Ex.GetHttpCode());
            }
        }

        [DataTestMethod]
        [DataRow(200)]
        [DataRow(201)]
        [DataRow(204)]
        public void MoveFile_SuccessStatus_ReturnsTrue(int Status)
        {
            using (var F = new DavFixture())
            {
                F.Server.ForcedStatus = (m, p) => (HttpStatusCode?)(HttpStatusCode)Status;
                Assert.IsTrue(DavAssert.Run(F.Client.MoveFile("/a.txt", "/b.txt")));
            }
        }

        [TestMethod]
        public void MoveFolder_SourceHasTrailingSlashDestinationDoesNot()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/f1");
                F.Server.AddFile("/dav/f1/inner.txt", "x");
                Assert.IsTrue(DavAssert.Run(F.Client.MoveFolder("/f1", "/f2")));
                var R = F.Recorder.Last;
                Assert.AreEqual("MOVE", R.Method);
                Assert.AreEqual("http://fake.local/dav/f1/", R.AbsoluteUri);
                Assert.AreEqual("http://fake.local/dav/f2", R.Header("Destination"));
                Assert.IsTrue(F.Server.IsFolder("/dav/f2"));
                Assert.AreEqual("x", F.Server.GetText("/dav/f2/inner.txt"));
                Assert.IsFalse(F.Server.Exists("/dav/f1"));
            }
        }

        [TestMethod]
        public void MoveFolder_WithSpacesInBothNames()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/old name");
                Assert.IsTrue(DavAssert.Run(F.Client.MoveFolder("/dav/old name/", "/dav/new name")));
                Assert.AreEqual("http://fake.local/dav/old%20name/", F.Recorder.Last.AbsoluteUri);
                Assert.AreEqual("http://fake.local/dav/new%20name", F.Recorder.Last.Header("Destination"));
                Assert.IsTrue(F.Server.IsFolder("/dav/new name"));
            }
        }

        [TestMethod]
        public void MoveFolder_Missing_Throws404()
        {
            using (var F = new DavFixture())
            {
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.MoveFolder("/nope", "/other"));
                Assert.AreEqual(404, Ex.GetHttpCode());
            }
        }

        [TestMethod]
        public void Move_NetworkFailure_PropagatesHttpRequestException()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "x");
                F.Server.AddFolder("/dav/f");
                F.Server.NetworkFailure = (m, p) => m == "MOVE";
                DavAssert.ThrowsExactly<HttpRequestException>(() => F.Client.MoveFile("/a.txt", "/b.txt"));
                DavAssert.ThrowsExactly<HttpRequestException>(() => F.Client.MoveFolder("/f", "/g"));
            }
        }

        #endregion
    }
}
