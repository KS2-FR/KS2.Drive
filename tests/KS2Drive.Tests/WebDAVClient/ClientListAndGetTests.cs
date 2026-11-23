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
    /// Client.List, Client.GetFolder and Client.GetFile.
    /// </summary>
    [TestClass]
    public class ClientListAndGetTests
    {
        #region List

        [TestMethod]
        public void List_Root_ReturnsChildrenButNotTheFolderItself()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "abc");
                F.Server.AddFolder("/dav/sub");
                F.Server.AddFile("/dav/sub/inner.txt", "x");

                var Items = DavAssert.Run(F.Client.List()).ToList();

                Assert.AreEqual(2, Items.Count);
                var File = Items.Single(i => !i.IsCollection);
                Assert.AreEqual("/dav/a.txt", File.Href);
                Assert.AreEqual("a.txt", File.DisplayName);
                Assert.AreEqual(3L, File.ContentLength);
                var Folder = Items.Single(i => i.IsCollection);
                Assert.AreEqual("/dav/sub/", Folder.Href);
                Assert.AreEqual("sub", Folder.DisplayName);
            }
        }

        [TestMethod]
        public void List_SendsPropfindWithDepth1AndAllpropBody()
        {
            using (var F = new DavFixture())
            {
                DavAssert.Run(F.Client.List("/"));
                var R = F.Recorder.Last;
                Assert.AreEqual("PROPFIND", R.Method);
                Assert.AreEqual("http://fake.local/dav/", R.AbsoluteUri);
                Assert.AreEqual("1", R.Header("Depth"));
                Assert.AreEqual("text/xml", R.ContentType);
                StringAssert.Contains(R.Body, "<propfind xmlns=\"DAV:\">");
                StringAssert.Contains(R.Body, "<allprop/>");
            }
        }

        [TestMethod]
        public void List_WithDepth0_SendsDepth0AndReturnsNothingForAFolder()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "abc");
                var Items = DavAssert.Run(F.Client.List("/", 0)).ToList();
                Assert.AreEqual("0", F.Recorder.Last.Header("Depth"));
                Assert.AreEqual(0, Items.Count);
            }
        }

        [TestMethod]
        public void List_WithNullDepth_SendsNoDepthHeader()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "abc");
                var Items = DavAssert.Run(F.Client.List("/", null)).ToList();
                Assert.IsNull(F.Recorder.Last.Header("Depth"));
                // The fake server treats a missing Depth as 1
                Assert.AreEqual(1, Items.Count);
            }
        }

        [TestMethod]
        public void List_SubFolder_UsesTrailingSlashAndExcludesItself()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/sub");
                F.Server.AddFile("/dav/sub/x.txt", "x");
                F.Server.AddFolder("/dav/sub/child");

                var Items = DavAssert.Run(F.Client.List("/sub")).ToList();

                Assert.AreEqual("http://fake.local/dav/sub/", F.Recorder.Last.AbsoluteUri);
                CollectionAssert.AreEquivalent(new[] { "/dav/sub/x.txt", "/dav/sub/child/" }, Items.Select(i => i.Href).ToArray());
            }
        }

        [TestMethod]
        public void List_WithFullRepositoryPath_StripsTheBasePathOnce()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/sub");
                F.Server.AddFile("/dav/sub/x.txt", "x");
                var Items = DavAssert.Run(F.Client.List("/dav/sub/")).ToList();
                Assert.AreEqual("http://fake.local/dav/sub/", F.Recorder.Last.AbsoluteUri);
                Assert.AreEqual(1, Items.Count);
            }
        }

        [TestMethod]
        public void List_FolderWithSpecialCharacters_ExcludesItselfAndKeepsEncodedHrefs()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/my docs");
                F.Server.AddFile("/dav/my docs/a#1 é.txt", "x");

                var Items = DavAssert.Run(F.Client.List("/my docs")).ToList();

                Assert.AreEqual("http://fake.local/dav/my%20docs/", F.Recorder.Last.AbsoluteUri);
                Assert.AreEqual(1, Items.Count);
                Assert.AreEqual("a#1 é.txt", Items[0].DisplayName);
                StringAssert.Contains(Items[0].Href, "%23");
                Assert.IsFalse(Items[0].Href.Contains("#"));
            }
        }

        [TestMethod]
        public void List_HrefsReturnedCanBeUsedAgainAsPaths()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/my docs");
                F.Server.AddFile("/dav/my docs/a#1 + b.txt", "content");

                var Child = DavAssert.Run(F.Client.List("/my docs")).Single();
                var Again = DavAssert.Run(F.Client.GetFile(Child.Href));
                Assert.AreEqual("PROPFIND /dav/my docs/a#1 + b.txt", F.Server.Requests.Last());
                Assert.AreEqual(7L, Again.ContentLength);
                Assert.AreEqual("content", System.Text.Encoding.UTF8.GetString(DavAssert.Run(F.Client.Download(Child.Href))));
            }
        }

        [TestMethod]
        public void List_EmptyFolder_ReturnsEmpty()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/empty");
                Assert.AreEqual(0, DavAssert.Run(F.Client.List("/empty")).Count());
            }
        }

        [TestMethod]
        public void List_Accepts200OkWithMultistatusBody()
        {
            String Xml = DavXml.Multistatus(
                DavXml.Response("/dav/", "<D:resourcetype><D:collection/></D:resourcetype>"),
                DavXml.Response("/dav/a.txt", "<D:getcontentlength>5</D:getcontentlength>"));
            using (var F = DavFixture.WithStub(HttpStatusCode.OK, Xml))
            {
                var Items = DavAssert.Run(F.Client.List("/")).ToList();
                Assert.AreEqual(1, Items.Count);
                Assert.AreEqual("/dav/a.txt", Items[0].Href);
            }
        }

        [TestMethod]
        public void List_ExcludesTheFolderItselfWhenTheServerReturnsAnAbsoluteHref()
        {
            String Xml = DavXml.Multistatus(
                DavXml.Response("http://fake.local/dav/sub/", "<D:resourcetype><D:collection/></D:resourcetype>"),
                DavXml.Response("http://fake.local/dav/sub/a.txt", ""));
            using (var F = DavFixture.WithStub((HttpStatusCode)207, Xml))
            {
                var Items = DavAssert.Run(F.Client.List("/sub")).ToList();
                Assert.AreEqual(1, Items.Count);
                Assert.AreEqual("http://fake.local/dav/sub/a.txt", Items[0].Href);
            }
        }

        [TestMethod]
        public void List_EmptyMultistatus_ReturnsEmpty()
        {
            using (var F = DavFixture.WithStub((HttpStatusCode)207, DavXml.Multistatus()))
            {
                Assert.AreEqual(0, DavAssert.Run(F.Client.List("/")).Count());
            }
        }

        [TestMethod]
        public void List_MissingFolder_ThrowsWebDavException404()
        {
            using (var F = new DavFixture())
            {
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.List("/missing"));
                Assert.AreEqual(404, Ex.GetHttpCode());
            }
        }

        [TestMethod]
        public void List_Unauthorized_ThrowsWebDavException401()
        {
            using (var F = new DavFixture())
            {
                F.Server.RejectCredentials = true;
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.List("/"));
                Assert.AreEqual(401, Ex.GetHttpCode());
            }
        }

        [DataTestMethod]
        [DataRow(403)]
        [DataRow(500)]
        [DataRow(503)]
        [DataRow(204)]
        public void List_UnexpectedStatus_ThrowsWebDavExceptionWithThatCode(int Status)
        {
            using (var F = new DavFixture())
            {
                F.Server.ForcedStatus = (m, p) => (HttpStatusCode?)(HttpStatusCode)Status;
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.List("/"));
                Assert.AreEqual(Status, Ex.GetHttpCode());
            }
        }

        [TestMethod]
        public void List_NetworkFailure_PropagatesHttpRequestException()
        {
            using (var F = new DavFixture())
            {
                F.Server.NetworkFailure = (m, p) => true;
                DavAssert.ThrowsExactly<HttpRequestException>(() => F.Client.List("/"));
            }
        }

        [TestMethod]
        public void List_MalformedResponse_ThrowsXmlException()
        {
            using (var F = DavFixture.WithStub((HttpStatusCode)207, "<D:multistatus xmlns:D=\"DAV:\"><D:response>"))
            {
                DavAssert.ThrowsExactly<System.Xml.XmlException>(() => F.Client.List("/"));
            }
        }

        #endregion

        #region GetFolder

        [TestMethod]
        public void GetFolder_Root_ReturnsTheLibraryFolder()
        {
            using (var F = new DavFixture())
            {
                var Item = DavAssert.Run(F.Client.GetFolder());
                Assert.IsTrue(Item.IsCollection);
                Assert.AreEqual("/dav/", Item.Href);
                Assert.AreEqual("dav", Item.DisplayName);
                var R = F.Recorder.Last;
                Assert.AreEqual("PROPFIND", R.Method);
                Assert.AreEqual("http://fake.local/dav/", R.AbsoluteUri);
                Assert.AreEqual("0", R.Header("Depth"));
                StringAssert.Contains(R.Body, "<allprop/>");
            }
        }

        [TestMethod]
        public void GetFolder_SubFolder_UsesTrailingSlash()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/sub");
                F.Server.AddFile("/dav/sub/a.txt", "a");
                var Item = DavAssert.Run(F.Client.GetFolder("/sub"));
                Assert.AreEqual("http://fake.local/dav/sub/", F.Recorder.Last.AbsoluteUri);
                Assert.AreEqual("/dav/sub/", Item.Href);
                Assert.IsTrue(Item.IsCollection);
                Assert.AreEqual(1, F.Server.CountRequests("PROPFIND"));
            }
        }

        [TestMethod]
        public void GetFolder_OnAFile_Throws404()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "a");
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.GetFolder("/a.txt"));
                Assert.AreEqual(404, Ex.GetHttpCode());
                Assert.AreEqual("http://fake.local/dav/a.txt/", F.Recorder.Last.AbsoluteUri);
            }
        }

        [TestMethod]
        public void GetFolder_Missing_Throws404()
        {
            using (var F = new DavFixture())
            {
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.GetFolder("/nope"));
                Assert.AreEqual(404, Ex.GetHttpCode());
            }
        }

        #endregion

        #region GetFile

        [TestMethod]
        public void GetFile_ReturnsAllProperties()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "hello");
                var Item = DavAssert.Run(F.Client.GetFile("/a.txt"));

                Assert.AreEqual("/dav/a.txt", Item.Href);
                Assert.AreEqual("a.txt", Item.DisplayName);
                Assert.IsFalse(Item.IsCollection);
                Assert.IsFalse(Item.IsHidden);
                Assert.AreEqual(5L, Item.ContentLength);
                Assert.IsNotNull(Item.Etag);
                Assert.IsTrue(Item.Etag.StartsWith("\"") && Item.Etag.EndsWith("\""));
                Assert.AreEqual(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), Item.CreationDate.Value.ToUniversalTime());

                var R = F.Recorder.Last;
                Assert.AreEqual("PROPFIND", R.Method);
                Assert.AreEqual("http://fake.local/dav/a.txt", R.AbsoluteUri);
                Assert.AreEqual("0", R.Header("Depth"));
            }
        }

        [TestMethod]
        public void GetFile_LastModifiedRfc1123_IsParsed()
        {
            var Previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
                using (var F = new DavFixture())
                {
                    F.Server.AddFile("/dav/a.txt", "hello");
                    var Item = DavAssert.Run(F.Client.GetFile("/a.txt"));
                    Assert.IsTrue(Item.LastModified.HasValue);
                    Assert.AreEqual(new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc), Item.LastModified.Value.ToUniversalTime());
                }
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = Previous;
            }
        }

        [TestMethod]
        public void GetFile_WithoutContentLength_HasNullLength()
        {
            using (var F = new DavFixture())
            {
                F.Server.SendContentLength = false;
                F.Server.AddFile("/dav/a.txt", "hello");
                Assert.IsNull(DavAssert.Run(F.Client.GetFile("/a.txt")).ContentLength);
            }
        }

        [TestMethod]
        public void GetFile_OnAFolderPath_ReturnsTheFolder()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/sub");
                var Item = DavAssert.Run(F.Client.GetFile("/sub"));
                Assert.AreEqual("http://fake.local/dav/sub", F.Recorder.Last.AbsoluteUri);
                Assert.IsTrue(Item.IsCollection);
                Assert.AreEqual("/dav/sub/", Item.Href);
            }
        }

        [TestMethod]
        public void GetFile_Missing_Throws404()
        {
            using (var F = new DavFixture())
            {
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.GetFile("/nope.txt"));
                Assert.AreEqual(404, Ex.GetHttpCode());
            }
        }

        [DataTestMethod]
        [DataRow(401)]
        [DataRow(403)]
        [DataRow(500)]
        public void GetFile_ErrorStatus_ThrowsWithCode(int Status)
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a.txt", "a");
                F.Server.ForcedStatus = (m, p) => m == "PROPFIND" ? (HttpStatusCode?)(HttpStatusCode)Status : null;
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.GetFile("/a.txt"));
                Assert.AreEqual(Status, Ex.GetHttpCode());
            }
        }

        [TestMethod]
        public void GetFile_EmptyMultistatus_ThrowsDeserializationError()
        {
            using (var F = DavFixture.WithStub((HttpStatusCode)207, DavXml.Multistatus()))
            {
                var Ex = DavAssert.ThrowsExactly<WebDAVException>(() => F.Client.GetFile("/a.txt"));
                StringAssert.Contains(Ex.Message, "Failed deserializing");
            }
        }

        [TestMethod]
        public void GetFile_200Ok_IsAccepted()
        {
            String Xml = DavXml.Multistatus(DavXml.Response("/dav/a.txt", "<D:getetag>abc</D:getetag>"));
            using (var F = DavFixture.WithStub(HttpStatusCode.OK, Xml))
            {
                Assert.AreEqual("abc", DavAssert.Run(F.Client.GetFile("/a.txt")).Etag);
            }
        }

        [TestMethod]
        public void GetFile_NetworkFailure_PropagatesHttpRequestException()
        {
            using (var F = new DavFixture())
            {
                F.Server.NetworkFailure = (m, p) => true;
                DavAssert.ThrowsExactly<HttpRequestException>(() => F.Client.GetFile("/a.txt"));
            }
        }

        #endregion
    }
}
