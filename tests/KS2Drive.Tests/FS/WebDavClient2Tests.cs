using KS2Drive.FS;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using WebDAVClient.Helpers;
using WebDAVClient.Model;

namespace KS2Drive.Tests.FS
{
    [TestClass]
    public class WebDavClient2Tests
    {
        private FakeWebDavServer Server;

        [TestInitialize]
        public void Setup()
        {
            Server = new FakeWebDavServer();
            WebDavClient2.TestHttpMessageHandler = Server;
            WebDavClient2.Init(FakeWebDavServer.ServerUrl, FakeWebDavServer.LibraryPath, "u", "p", null);
            FileNode.Init(FakeWebDavServer.LibraryPath, WebDAVMode.WebDAV);
        }

        [TestCleanup]
        public void Cleanup()
        {
            WebDavClient2.TestHttpMessageHandler = null;
        }

        #region Helpers

        private static FieldInfo SharedHttpClientField => typeof(WebDavClient2).GetField("_SharedHttpClient", BindingFlags.NonPublic | BindingFlags.Static);

        private static HttpClient SharedHttpClient => (HttpClient)SharedHttpClientField.GetValue(null);

        private static object InvokePrivate(object Target, String Name, params object[] Args)
        {
            try
            {
                return Target.GetType().GetMethod(Name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(Target, Args);
            }
            catch (TargetInvocationException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private List<String> RequestsSince(int Start) => Server.Requests.Skip(Start).ToList();

        #endregion

        #region Init / constructor

        [TestMethod]
        public void Init_RecreatesTheSharedHttpClientAndDisposesThePreviousOne()
        {
            HttpClient Previous = SharedHttpClient;
            Assert.IsNotNull(Previous);

            WebDavClient2.Init(FakeWebDavServer.ServerUrl, FakeWebDavServer.LibraryPath, "u2", "p2", null);

            HttpClient Current = SharedHttpClient;
            Assert.IsNotNull(Current);
            Assert.AreNotSame(Previous, Current);
            Assert.ThrowsException<ObjectDisposedException>(() => Previous.GetAsync(FakeWebDavServer.LibraryUrl).GetAwaiter().GetResult());
        }

        [TestMethod]
        public void Init_WithTestHandler_SendsRequestsToTheHandler()
        {
            Server.AddFile("/dav/a.txt", "abc");
            var Client = new WebDavClient2();

            var Content = Client.Download("/dav/a.txt").GetAwaiter().GetResult();

            Assert.AreEqual("abc", Encoding.UTF8.GetString(Content));
            CollectionAssert.Contains(Server.Requests, "GET /dav/a.txt");
        }

        [TestMethod]
        public void Init_WithoutTestHandler_CreatesANetworkClient()
        {
            HttpClient Previous = SharedHttpClient;
            WebDavClient2.TestHttpMessageHandler = null;
            int RequestsBefore = Server.Requests.Count;

            WebDavClient2.Init("http://other.local", "/lib", "u", "p", null);

            Assert.IsNotNull(SharedHttpClient);
            Assert.AreNotSame(Previous, SharedHttpClient);
            Assert.AreEqual(RequestsBefore, Server.Requests.Count, "Init does not send any request");
            var Client = new WebDavClient2();
            Assert.AreEqual("http://other.local", Client.Server);
            Assert.AreEqual("/lib/", Client.BasePath);
        }

        [TestMethod]
        public void Constructor_UsesInitParameters()
        {
            WebDavClient2.Init("http://server.local:8080/", "/alfresco/aos", "u", "p", null);
            var Client = new WebDavClient2();
            Assert.AreEqual("http://server.local:8080", Client.Server);
            Assert.AreEqual("/alfresco/aos/", Client.BasePath);
        }

        [TestMethod]
        public void Constructor_WithoutInit_Throws()
        {
            HttpClient Saved = SharedHttpClient;
            SharedHttpClientField.SetValue(null, null);
            try
            {
                var Ex = Assert.ThrowsException<InvalidOperationException>(() => new WebDavClient2());
                StringAssert.Contains(Ex.Message, "Init");
            }
            finally
            {
                SharedHttpClientField.SetValue(null, Saved);
            }
        }

        [TestMethod]
        public void Instances_ShareTheSameHttpClient()
        {
            Server.AddFile("/dav/a.txt", "abc");
            Server.AddFile("/dav/b.txt", "def");
            HttpClient Shared = SharedHttpClient;

            Assert.AreEqual("abc", Encoding.UTF8.GetString(new WebDavClient2().Download("/dav/a.txt").GetAwaiter().GetResult()));
            Assert.AreEqual("def", Encoding.UTF8.GetString(new WebDavClient2().Download("/dav/b.txt").GetAwaiter().GetResult()));
            Assert.AreSame(Shared, SharedHttpClient, "creating instances does not recreate the HttpClient");
        }

        #endregion

        #region ParameterConvert

        [DataTestMethod]
        [DataRow("/dav", "")]
        [DataRow("/dav/", "/")]
        [DataRow("/dav/a.txt", "/a.txt")]
        [DataRow("/dav/folder/sub", "/folder/sub")]
        [DataRow("/davfolder", "/davfolder")]
        [DataRow("/davfolder/x.txt", "/davfolder/x.txt")]
        [DataRow("/dav/davfolder", "/davfolder")]
        [DataRow("/other/x.txt", "/other/x.txt")]
        [DataRow("/", "/")]
        public void ParameterConvert_StripsTheBasePathOnlyAsAWholeSegment(String Input, String Expected)
        {
            Assert.AreEqual(Expected, (String)InvokePrivate(new WebDavClient2(), "ParameterConvert", Input));
        }

        [TestMethod]
        public void ParameterConvert_DoesNotStripAPrefixThatIsNotAWholeSegment_ThroughRequests()
        {
            Server.AddFolder("/dav/davfolder");
            Server.AddFile("/dav/davfolder/x.txt", "x");
            Server.AddFolder("/dav/folder");
            Server.AddFile("/dav/folder/x.txt", "wrong");
            var Client = new WebDavClient2();

            int Start = Server.Requests.Count;
            var Content = Client.Download("/dav/davfolder/x.txt").GetAwaiter().GetResult();

            Assert.AreEqual("x", Encoding.UTF8.GetString(Content));
            CollectionAssert.AreEqual(new[] { "GET /dav/davfolder/x.txt" }, RequestsSince(Start));

            Start = Server.Requests.Count;
            Client.List("/davfolder").GetAwaiter().GetResult();
            CollectionAssert.AreEqual(new[] { "PROPFIND /dav/davfolder" }, RequestsSince(Start), "a path not starting with the base path segment is used relative to it, unchanged");
        }

        [TestMethod]
        public void ParameterConvert_BasePathIsMatchedCaseInsensitively()
        {
            //Servers may return hrefs whose library path is cased differently from the configured URL
            //(FileNode.ConvertRepositoryPathToLocalPath compares it case-insensitively)
            Server.AddFile("/dav/a.txt", "abc");
            var Client = new WebDavClient2();

            var Content = Client.Download("/DAV/a.txt").GetAwaiter().GetResult();

            Assert.AreEqual("abc", Encoding.UTF8.GetString(Content));
        }

        #endregion

        #region Wrappers

        [TestMethod]
        public void CreateDir_CreatesTheFolderInsideTheRepositoryFolder()
        {
            Server.AddFolder("/dav/folder");
            var Client = new WebDavClient2();

            Assert.IsTrue(Client.CreateDir("/dav/folder", "new").GetAwaiter().GetResult());
            Assert.IsTrue(Client.CreateDir("/dav", "top").GetAwaiter().GetResult());

            Assert.IsTrue(Server.IsFolder("/dav/folder/new"));
            Assert.IsTrue(Server.IsFolder("/dav/top"));
            CollectionAssert.Contains(Server.Requests, "MKCOL /dav/folder/new");
            CollectionAssert.Contains(Server.Requests, "MKCOL /dav/top");
        }

        [TestMethod]
        public void CreateDir_ParentMissing_ThrowsConflict()
        {
            var Client = new WebDavClient2();
            var Ex = Assert.ThrowsException<WebDAVConflictException>(() => Client.CreateDir("/dav/missing", "new").GetAwaiter().GetResult());
            Assert.AreEqual(409, Ex.GetHttpCode());
        }

        [TestMethod]
        public void DeleteFile_DeletesTheFile()
        {
            Server.AddFile("/dav/folder/a.txt", "a");
            var Client = new WebDavClient2();

            Client.DeleteFile("/dav/folder/a.txt").GetAwaiter().GetResult();

            Assert.IsFalse(Server.Exists("/dav/folder/a.txt"));
            CollectionAssert.Contains(Server.Requests, "DELETE /dav/folder/a.txt");
        }

        [TestMethod]
        public void DeleteFile_Missing_ThrowsWith404()
        {
            var Client = new WebDavClient2();
            var Ex = Assert.ThrowsException<WebDAVException>(() => Client.DeleteFile("/dav/none.txt").GetAwaiter().GetResult());
            Assert.AreEqual(404, Ex.GetHttpCode());
        }

        [TestMethod]
        public void DeleteFolder_DeletesTheFolderAndItsContent()
        {
            Server.AddFolder("/dav/folder");
            Server.AddFile("/dav/folder/a.txt", "a");
            Server.AddFolder("/dav/folder2");
            var Client = new WebDavClient2();

            Client.DeleteFolder("/dav/folder").GetAwaiter().GetResult();

            Assert.IsFalse(Server.Exists("/dav/folder"));
            Assert.IsFalse(Server.Exists("/dav/folder/a.txt"));
            Assert.IsTrue(Server.Exists("/dav/folder2"));
            CollectionAssert.Contains(Server.Requests, "DELETE /dav/folder");
        }

        [TestMethod]
        public void Download_ReturnsTheFileContent()
        {
            var Data = new byte[] { 0, 1, 2, 255, 128 };
            Server.AddFile("/dav/folder/bin.dat", Data);
            var Client = new WebDavClient2();

            CollectionAssert.AreEqual(Data, Client.Download("/dav/folder/bin.dat").GetAwaiter().GetResult());
        }

        [TestMethod]
        public void Download_Missing_ThrowsWith404()
        {
            var Client = new WebDavClient2();
            var Ex = Assert.ThrowsException<WebDAVException>(() => Client.Download("/dav/none.txt").GetAwaiter().GetResult());
            Assert.AreEqual(404, Ex.GetHttpCode());
        }

        [TestMethod]
        public void GetFile_ReturnsTheFileProperties()
        {
            Server.AddFile("/dav/folder/a.txt", "hello");
            var Client = new WebDavClient2();

            Item File = Client.GetFile("/dav/folder/a.txt").GetAwaiter().GetResult();

            Assert.AreEqual("a.txt", File.DisplayName);
            Assert.AreEqual(5L, File.ContentLength);
            Assert.IsFalse(File.IsCollection);
            Assert.AreEqual("/dav/folder/a.txt", File.Href);
            CollectionAssert.Contains(Server.Requests, "PROPFIND /dav/folder/a.txt");
        }

        [TestMethod]
        public void GetFolder_ReturnsTheFolderProperties()
        {
            Server.AddFolder("/dav/folder");
            Server.AddFile("/dav/folder/a.txt", "a");
            var Client = new WebDavClient2();

            Item Folder = Client.GetFolder("/dav/folder").GetAwaiter().GetResult();

            Assert.AreEqual("folder", Folder.DisplayName);
            Assert.IsTrue(Folder.IsCollection);
        }

        [TestMethod]
        public void GetFolder_OnAFile_ThrowsWith404()
        {
            Server.AddFile("/dav/a.txt", "a");
            var Client = new WebDavClient2();
            var Ex = Assert.ThrowsException<WebDAVException>(() => Client.GetFolder("/dav/a.txt").GetAwaiter().GetResult());
            Assert.AreEqual(404, Ex.GetHttpCode());
        }

        [TestMethod]
        public void List_ReturnsTheFolderChildren()
        {
            Server.AddFolder("/dav/folder");
            Server.AddFile("/dav/folder/a.txt", "a");
            Server.AddFolder("/dav/folder/sub");
            Server.AddFile("/dav/folder/sub/deep.txt", "d");
            var Client = new WebDavClient2();

            var Items = Client.List("/dav/folder").GetAwaiter().GetResult().ToList();

            var Children = Items.Where(x => !FileNode.IsSameRepositoryPath(x.Href, "/dav/folder")).Select(x => x.DisplayName).OrderBy(x => x).ToList();
            CollectionAssert.AreEqual(new[] { "a.txt", "sub" }, Children);
            Assert.IsTrue(Items.Single(x => x.DisplayName == "sub").IsCollection);
            CollectionAssert.Contains(Server.Requests, "PROPFIND /dav/folder");
        }

        [TestMethod]
        public void List_Root_ReturnsRootChildren()
        {
            Server.AddFile("/dav/a.txt", "a");
            Server.AddFolder("/dav/f");
            var Client = new WebDavClient2();

            var Items = Client.List("/dav").GetAwaiter().GetResult().ToList();

            var Children = Items.Where(x => !FileNode.IsSameRepositoryPath(x.Href, "/dav")).Select(x => x.DisplayName).OrderBy(x => x).ToList();
            CollectionAssert.AreEqual(new[] { "a.txt", "f" }, Children);
            CollectionAssert.Contains(Server.Requests, "PROPFIND /dav");
        }

        [TestMethod]
        public void MoveFile_RenamesTheFile()
        {
            Server.AddFolder("/dav/folder");
            Server.AddFile("/dav/a.txt", "content");
            var Client = new WebDavClient2();

            Assert.IsTrue(Client.MoveFile("/dav/a.txt", "/dav/folder/b.txt").GetAwaiter().GetResult());

            Assert.IsFalse(Server.Exists("/dav/a.txt"));
            Assert.AreEqual("content", Server.GetText("/dav/folder/b.txt"));
            CollectionAssert.Contains(Server.Requests, "MOVE /dav/a.txt");
        }

        [TestMethod]
        public void MoveFolder_MovesTheFolderAndItsContent()
        {
            Server.AddFolder("/dav/old");
            Server.AddFile("/dav/old/a.txt", "a");
            var Client = new WebDavClient2();

            Assert.IsTrue(Client.MoveFolder("/dav/old", "/dav/new").GetAwaiter().GetResult());

            Assert.IsFalse(Server.Exists("/dav/old"));
            Assert.IsTrue(Server.IsFolder("/dav/new"));
            Assert.AreEqual("a", Server.GetText("/dav/new/a.txt"));
            CollectionAssert.Contains(Server.Requests, "MOVE /dav/old");
        }

        [TestMethod]
        public void Upload_WritesTheFileInsideTheRepositoryFolder()
        {
            Server.AddFolder("/dav/folder");
            var Client = new WebDavClient2();

            using (var Stream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes("uploaded")))
            {
                Assert.IsTrue(Client.Upload("/dav/folder", Stream, "new.txt").GetAwaiter().GetResult());
            }

            Assert.AreEqual("uploaded", Server.GetText("/dav/folder/new.txt"));
            CollectionAssert.Contains(Server.Requests, "PUT /dav/folder/new.txt");
        }

        [TestMethod]
        public void Upload_ReplacesAnExistingFile()
        {
            Server.AddFile("/dav/a.txt", "old");
            var Client = new WebDavClient2();

            using (var Stream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes("new content")))
            {
                Assert.IsTrue(Client.Upload("/dav", Stream, "a.txt").GetAwaiter().GetResult());
            }

            Assert.AreEqual("new content", Server.GetText("/dav/a.txt"));
        }

        [TestMethod]
        public void Wrappers_Unauthorized_ThrowWith401()
        {
            Server.AddFile("/dav/a.txt", "a");
            Server.RejectCredentials = true;
            var Client = new WebDavClient2();

            Assert.AreEqual(401, Assert.ThrowsException<WebDAVException>(() => Client.Download("/dav/a.txt").GetAwaiter().GetResult()).GetHttpCode());
            Assert.AreEqual(401, Assert.ThrowsException<WebDAVException>(() => Client.List("/dav").GetAwaiter().GetResult()).GetHttpCode());
            Assert.AreEqual(401, Assert.ThrowsException<WebDAVException>(() => Client.GetFile("/dav/a.txt").GetAwaiter().GetResult()).GetHttpCode());
            Assert.AreEqual(401, Assert.ThrowsException<WebDAVException>(() => Client.DeleteFile("/dav/a.txt").GetAwaiter().GetResult()).GetHttpCode());
        }

        #endregion

        #region GetRepositoryElement / TryGet

        [TestMethod]
        public void GetRepositoryElement_FileWithExtension_FoundWithOneRequest()
        {
            Server.AddFolder("/dav/folder");
            Server.AddFile("/dav/folder/a.txt", "hello");
            var Client = new WebDavClient2();
            int Start = Server.Requests.Count;

            Item Element = Client.GetRepositoryElement("\\folder\\a.txt");

            Assert.IsNotNull(Element);
            Assert.IsFalse(Element.IsCollection);
            Assert.AreEqual("a.txt", Element.DisplayName);
            Assert.AreEqual(5L, Element.ContentLength);
            Assert.AreEqual(1, Server.Requests.Count - Start);
        }

        [TestMethod]
        public void GetRepositoryElement_Folder_Found()
        {
            Server.AddFolder("/dav/folder");
            var Client = new WebDavClient2();
            int Start = Server.Requests.Count;

            Item Element = Client.GetRepositoryElement("\\folder");

            Assert.IsNotNull(Element);
            Assert.IsTrue(Element.IsCollection);
            Assert.AreEqual("folder", Element.DisplayName);
            Assert.AreEqual(1, Server.Requests.Count - Start);
        }

        [TestMethod]
        public void GetRepositoryElement_FolderNameWithDot_Found()
        {
            Server.AddFolder("/dav/v1.2");
            Server.AddFile("/dav/v1.2/readme.txt", "r");
            var Client = new WebDavClient2();

            Item Element = Client.GetRepositoryElement("\\v1.2");

            Assert.IsNotNull(Element);
            Assert.IsTrue(Element.IsCollection);
            Assert.AreEqual("v1.2", Element.DisplayName);
        }

        [TestMethod]
        public void GetRepositoryElement_FolderNameWithDot_FoundWhenFileLookupAnswers404()
        {
            //Some servers answer 404 when a folder is requested without its trailing slash
            Server.AddFolder("/dav/v1.2");
            var Client = new WebDavClient2();
            int Start = Server.Requests.Count;
            bool FirstLookup = true;
            Server.ForcedStatus = (m, p) =>
            {
                if (m == "PROPFIND" && p == "/dav/v1.2" && FirstLookup) { FirstLookup = false; return HttpStatusCode.NotFound; }
                return null;
            };

            Item Element = Client.GetRepositoryElement("\\v1.2");

            Assert.IsNotNull(Element);
            Assert.IsTrue(Element.IsCollection);
            Assert.AreEqual(2, Server.Requests.Count - Start, "the file lookup answered 404, then the folder lookup succeeded");
        }

        [TestMethod]
        public void GetRepositoryElement_FileWithoutExtension_FoundAfterFolderLookup404()
        {
            Server.AddFile("/dav/README", "read me");
            var Client = new WebDavClient2();
            int Start = Server.Requests.Count;

            Item Element = Client.GetRepositoryElement("\\README");

            Assert.IsNotNull(Element);
            Assert.IsFalse(Element.IsCollection);
            Assert.AreEqual("README", Element.DisplayName);
            CollectionAssert.AreEqual(new[] { "PROPFIND /dav/README", "PROPFIND /dav/README" }, RequestsSince(Start));
        }

        [TestMethod]
        public void GetRepositoryElement_OnlyTheLastSegmentDecidesTheFirstLookup()
        {
            Server.AddFolder("/dav/v1.2");
            Server.AddFile("/dav/v1.2/readme", "r");
            var Client = new WebDavClient2();

            Item Element = Client.GetRepositoryElement("\\v1.2\\readme");

            Assert.IsNotNull(Element);
            Assert.IsFalse(Element.IsCollection);
            Assert.AreEqual("readme", Element.DisplayName);
        }

        [TestMethod]
        public void GetRepositoryElement_Root_HasEmptyDisplayName()
        {
            Server.AddFile("/dav/a.txt", "a");
            var Client = new WebDavClient2();

            Item Element = Client.GetRepositoryElement("\\");

            Assert.IsNotNull(Element);
            Assert.IsTrue(Element.IsCollection);
            Assert.AreEqual("", Element.DisplayName);
            CollectionAssert.Contains(Server.Requests, "PROPFIND /dav");
        }

        [TestMethod]
        public void GetRepositoryElement_NotFoundEitherWay_ReturnsNull()
        {
            var Client = new WebDavClient2();
            int Start = Server.Requests.Count;

            Assert.IsNull(Client.GetRepositoryElement("\\none.txt"));
            Assert.IsNull(Client.GetRepositoryElement("\\nofolder"));
            Assert.AreEqual(4, Server.Requests.Count - Start, "both kinds are tried for each element");
        }

        [TestMethod]
        public void GetRepositoryElement_OtherError_PropagatesWithStatusCode()
        {
            Server.AddFile("/dav/a.txt", "a");
            Server.ForcedStatus = (m, p) => HttpStatusCode.InternalServerError;
            var Client = new WebDavClient2();
            int Start = Server.Requests.Count;

            var Ex = Assert.ThrowsException<WebDAVException>(() => Client.GetRepositoryElement("\\a.txt"));

            Assert.AreEqual(500, Ex.GetHttpCode());
            Assert.AreEqual(1, Server.Requests.Count - Start, "the other kind is not tried after an error other than 404");
        }

        [TestMethod]
        public void GetRepositoryElement_ErrorOnSecondLookup_Propagates()
        {
            Server.ForcedStatus = (m, p) => p == "/dav/x.txt" && Server.Requests.Count(r => r == "PROPFIND /dav/x.txt") >= 2 ? HttpStatusCode.ServiceUnavailable : (HttpStatusCode?)null;
            var Client = new WebDavClient2();

            var Ex = Assert.ThrowsException<WebDAVException>(() => Client.GetRepositoryElement("\\x.txt"));

            Assert.AreEqual(503, Ex.GetHttpCode());
        }

        [TestMethod]
        public void GetRepositoryElement_Unauthorized_ThrowsWith401()
        {
            Server.AddFile("/dav/a.txt", "a");
            Server.RejectCredentials = true;
            var Client = new WebDavClient2();

            var Ex = Assert.ThrowsException<WebDAVException>(() => Client.GetRepositoryElement("\\a.txt"));

            Assert.AreEqual(401, Ex.GetHttpCode());
        }

        [TestMethod]
        public void GetRepositoryElement_NetworkFailure_Propagates()
        {
            Server.NetworkFailure = (m, p) => true;
            var Client = new WebDavClient2();

            Assert.ThrowsException<HttpRequestException>(() => Client.GetRepositoryElement("\\a.txt"));
        }

        [TestMethod]
        public void TryGet_ReturnsItemOrNullOn404()
        {
            Server.AddFile("/dav/a.txt", "abc");
            Server.AddFolder("/dav/folder");
            var Client = new WebDavClient2();

            var AsFile = (Item)InvokePrivate(Client, "TryGet", "/dav/a.txt", false);
            var FileAsFolder = (Item)InvokePrivate(Client, "TryGet", "/dav/a.txt", true);
            var AsFolder = (Item)InvokePrivate(Client, "TryGet", "/dav/folder", true);
            var Missing = (Item)InvokePrivate(Client, "TryGet", "/dav/missing", false);

            Assert.IsNotNull(AsFile);
            Assert.AreEqual(3L, AsFile.ContentLength);
            Assert.IsNull(FileAsFolder, "a folder lookup of a file answers 404");
            Assert.IsNotNull(AsFolder);
            Assert.IsTrue(AsFolder.IsCollection);
            Assert.IsNull(Missing);
        }

        [TestMethod]
        public void TryGet_OtherError_Propagates()
        {
            Server.ForcedStatus = (m, p) => HttpStatusCode.Forbidden;
            var Client = new WebDavClient2();

            var Ex = Assert.ThrowsException<WebDAVException>(() => InvokePrivate(Client, "TryGet", "/dav/a.txt", false));

            Assert.AreEqual(403, Ex.GetHttpCode());
        }

        #endregion
    }
}
