using KS2Drive.Tests.Infrastructure;
using KS2Drive.WebDav;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace KS2Drive.Tests.WebDav
{
    /// <summary>
    /// WebDavClient requests: PROPFIND, GetItem, Exists, GET, PUT, DELETE, MKCOL and MOVE, against the in-memory server or a
    /// scripted handler.
    /// </summary>
    [TestClass]
    public class WebDavClientOperationTests
    {
        private FakeWebDavServer Server;
        private RecordingHandler Recorder;
        private WebDavClient Client;

        [TestInitialize]
        public void Initialize()
        {
            Server = new FakeWebDavServer();
            Recorder = new RecordingHandler(Server);
            Client = TestClients.For(Recorder);
        }

        /// <summary>A client whose responses are built by the given function (requests still recorded in <see cref="Recorder"/>).</summary>
        private WebDavClient Scripted(Func<HttpRequestMessage, HttpResponseMessage> Respond)
        {
            Recorder = new RecordingHandler { Respond = Respond };
            return TestClients.For(Recorder);
        }

        private static bool IsCollectionUrl(HttpRequestMessage Request) => Request.RequestUri.AbsolutePath.EndsWith("/");

        #region PropFind

        [TestMethod]
        public async Task PropFind_Depth0_SendsDepthHeaderAndAllPropBody()
        {
            Server.AddFile("/dav/a.txt", "abc");

            var Items = await Client.PropFind("/dav/a.txt", 0, false);

            var Request = Recorder.Requests.Single();
            Assert.AreEqual("PROPFIND", Request.Method);
            Assert.AreEqual("http://fake.local/dav/a.txt", Request.Url.AbsoluteUri);
            Assert.AreEqual("0", Request.Header("Depth"));
            Assert.AreEqual("application/xml", Request.ContentType);
            var Body = XDocument.Parse(Request.BodyText);
            Assert.AreEqual(XName.Get("propfind", "DAV:"), Body.Root.Name);
            Assert.AreEqual(XName.Get("allprop", "DAV:"), Body.Root.Elements().Single().Name);

            Assert.AreEqual(1, Items.Count);
            Assert.AreEqual("/dav/a.txt", Items[0].Path);
            Assert.AreEqual(3L, Items[0].ContentLength);
            Assert.IsFalse(Items[0].IsCollection);
        }

        [TestMethod]
        public async Task PropFind_Depth1Collection_ListsFolderAndChildrenWithDecodedPaths()
        {
            Server.AddFolder("/dav/My folder");
            Server.AddFile("/dav/My folder/C# #1.txt", "x");
            Server.AddFile("/dav/My folder/C++ notes.txt", "xy");
            Server.AddFolder("/dav/My folder/sub");
            Server.AddFile("/dav/My folder/sub/deep.txt", "not listed");

            var Items = await Client.PropFind("/dav/My folder", 1, true);

            var Request = Recorder.Requests.Single();
            Assert.AreEqual("http://fake.local/dav/My%20folder/", Request.Url.AbsoluteUri);
            Assert.AreEqual("1", Request.Header("Depth"));
            CollectionAssert.AreEquivalent(new[] { "/dav/My folder", "/dav/My folder/C# #1.txt", "/dav/My folder/C++ notes.txt", "/dav/My folder/sub" }, Items.Select(x => x.Path).ToArray());
            Assert.IsTrue(Items.Single(x => x.Path == "/dav/My folder").IsCollection);
            Assert.IsTrue(Items.Single(x => x.Path == "/dav/My folder/sub").IsCollection);
            Assert.AreEqual("C# #1.txt", Items.Single(x => x.Path == "/dav/My folder/C# #1.txt").DisplayName);
        }

        [TestMethod]
        public async Task PropFind_NotCollection_UrlHasNoTrailingSlash()
        {
            Server.AddFolder("/dav/folder");

            await Client.PropFind("/dav/folder", 0, false);

            Assert.AreEqual("http://fake.local/dav/folder", Recorder.Requests.Single().Url.AbsoluteUri);
        }

        [DataTestMethod]
        [DataRow(207)]
        [DataRow(200)]
        public async Task PropFind_MultiStatusOrOk_IsAccepted(int Status)
        {
            var Client = Scripted(Request => DavResponses.Xml(DavResponses.MultiStatus(DavResponses.File("/dav/a.txt", 5)), Status));

            var Items = await Client.PropFind("/dav/a.txt", 0, false);

            Assert.AreEqual(1, Items.Count);
            Assert.AreEqual(5L, Items[0].ContentLength);
        }

        [DataTestMethod]
        [DataRow(201)]
        [DataRow(204)]
        [DataRow(301)]
        [DataRow(400)]
        [DataRow(401)]
        [DataRow(403)]
        [DataRow(404)]
        [DataRow(500)]
        [DataRow(503)]
        public async Task PropFind_UnexpectedStatus_ThrowsWebDavExceptionWithStatus(int Status)
        {
            var Client = Scripted(Request => DavResponses.Xml(DavResponses.MultiStatus(), Status));

            var ex = await AsyncAssert.ThrowsWebDavAsync(Status, () => Client.PropFind("/dav/a.txt", 0, false));
            StringAssert.Contains(ex.Message, "PROPFIND");
            StringAssert.Contains(ex.Message, Status.ToString());
        }

        [TestMethod]
        public async Task PropFind_MalformedXml_ThrowsXmlException()
        {
            var Client = Scripted(Request => DavResponses.Xml("<D:multistatus xmlns:D=\"DAV:\"><D:response><D:href>/dav/a.txt</D:href>"));

            await AsyncAssert.ThrowsAsync<XmlException>(() => Client.PropFind("/dav/a.txt", 0, false));
        }

        [TestMethod]
        public async Task PropFind_Missing_Throws404()
        {
            await AsyncAssert.ThrowsWebDavAsync(404, () => Client.PropFind("/dav/missing", 1, true));
        }

        #endregion

        #region GetItem

        [TestMethod]
        public async Task GetItem_NameWithDot_IsLookedUpAsFileFirst()
        {
            Server.AddFile("/dav/notes.txt", "abc");

            var Item = await Client.GetItem("/dav/notes.txt");

            Assert.IsNotNull(Item);
            Assert.AreEqual("/dav/notes.txt", Item.Path);
            Assert.IsFalse(Item.IsCollection);
            var Request = Recorder.Requests.Single();
            Assert.AreEqual("http://fake.local/dav/notes.txt", Request.Url.AbsoluteUri);
            Assert.AreEqual("0", Request.Header("Depth"));
        }

        [TestMethod]
        public async Task GetItem_FolderWithDot_FallsBackToCollectionAfter404()
        {
            var Client = Scripted(Request => IsCollectionUrl(Request)
                ? DavResponses.Xml(DavResponses.MultiStatus(DavResponses.Folder("/dav/v1.2/")))
                : DavResponses.Status(404));

            var Item = await Client.GetItem("/dav/v1.2");

            Assert.IsNotNull(Item);
            Assert.AreEqual("/dav/v1.2", Item.Path);
            Assert.IsTrue(Item.IsCollection);
            CollectionAssert.AreEqual(new[] { "http://fake.local/dav/v1.2", "http://fake.local/dav/v1.2/" }, Recorder.Requests.Select(x => x.Url.AbsoluteUri).ToArray());
            Assert.IsTrue(Recorder.Requests.All(x => x.Header("Depth") == "0"));
        }

        [TestMethod]
        public async Task GetItem_FolderWithDotDescribedWithoutSlash_IsFoundWithOneRequest()
        {
            Server.AddFolder("/dav/v1.2");

            var Item = await Client.GetItem("/dav/v1.2");

            Assert.IsNotNull(Item);
            Assert.IsTrue(Item.IsCollection);
            Assert.AreEqual(1, Recorder.Requests.Count);
        }

        [TestMethod]
        public async Task GetItem_FolderWithDotRedirectedOnFileLookup_FallsBackToCollection()
        {
            //A server may redirect a folder URL without trailing slash (a redirection is an answer in itself, see Exists):
            //the folder must still be found
            var Client = Scripted(Request =>
            {
                if (IsCollectionUrl(Request)) return DavResponses.Xml(DavResponses.MultiStatus(DavResponses.Folder("/dav/v1.2/")));
                var Redirect = DavResponses.Status(301);
                Redirect.Headers.Location = new Uri("http://fake.local/dav/v1.2/");
                return Redirect;
            });

            var Item = await Client.GetItem("/dav/v1.2");

            Assert.IsNotNull(Item);
            Assert.IsTrue(Item.IsCollection);
            Assert.AreEqual("/dav/v1.2", Item.Path);
        }

        [TestMethod]
        public async Task GetItem_NameWithoutDot_IsLookedUpAsFolderFirst()
        {
            Server.AddFolder("/dav/Documents");

            var Item = await Client.GetItem("/dav/Documents");

            Assert.IsNotNull(Item);
            Assert.IsTrue(Item.IsCollection);
            Assert.AreEqual("http://fake.local/dav/Documents/", Recorder.Requests.Single().Url.AbsoluteUri);
        }

        [TestMethod]
        public async Task GetItem_DotInParentFolderOnly_IsLookedUpAsFolderFirst()
        {
            Server.AddFolder("/dav/v1.2");
            Server.AddFolder("/dav/v1.2/Documents");

            var Item = await Client.GetItem("/dav/v1.2/Documents");

            Assert.IsNotNull(Item);
            Assert.IsTrue(Item.IsCollection);
            Assert.AreEqual("http://fake.local/dav/v1.2/Documents/", Recorder.Requests.Single().Url.AbsoluteUri);
        }

        [TestMethod]
        public async Task GetItem_FileWithoutExtension_FallsBackToFileAfter404()
        {
            Server.AddFile("/dav/README", "x");

            var Item = await Client.GetItem("/dav/README");

            Assert.IsNotNull(Item);
            Assert.AreEqual("/dav/README", Item.Path);
            Assert.IsFalse(Item.IsCollection);
            Assert.AreEqual(1L, Item.ContentLength);
            CollectionAssert.AreEqual(new[] { "http://fake.local/dav/README/", "http://fake.local/dav/README" }, Recorder.Requests.Select(x => x.Url.AbsoluteUri).ToArray());
        }

        [DataTestMethod]
        [DataRow("/dav/missing.txt")]
        [DataRow("/dav/missing")]
        public async Task GetItem_404ForBothKinds_ReturnsNull(String Path)
        {
            var Item = await Client.GetItem(Path);

            Assert.IsNull(Item);
            Assert.AreEqual(2, Recorder.Requests.Count);
            Assert.AreEqual(1, Recorder.Requests.Count(x => x.Url.AbsoluteUri.EndsWith("/")), "One lookup as a folder, one as a file");
        }

        [TestMethod]
        public async Task GetItem_OtherErrorOnFirstLookup_IsPropagatedWithoutFallback()
        {
            var Client = Scripted(Request => DavResponses.Status(500));

            await AsyncAssert.ThrowsWebDavAsync(500, () => Client.GetItem("/dav/a.txt"));
            Assert.AreEqual(1, Recorder.Requests.Count);
        }

        [TestMethod]
        public async Task GetItem_OtherErrorOnFallback_IsPropagated()
        {
            var Client = Scripted(Request => IsCollectionUrl(Request) ? DavResponses.Status(503) : DavResponses.Status(404));

            await AsyncAssert.ThrowsWebDavAsync(503, () => Client.GetItem("/dav/v1.2"));
            Assert.AreEqual(2, Recorder.Requests.Count);
        }

        [TestMethod]
        public async Task GetItem_CredentialsRejected_Throws401()
        {
            Server.AddFile("/dav/a.txt", "abc");
            Server.RejectCredentials = true;

            await AsyncAssert.ThrowsWebDavAsync(401, () => Client.GetItem("/dav/a.txt"));
        }

        #endregion

        #region Exists

        [TestMethod]
        public async Task Exists_File_IsTrueWithSingleDepth0Request()
        {
            Server.AddFile("/dav/My file.txt", "abc");

            Assert.IsTrue(await Client.Exists("/dav/My file.txt"));

            var Request = Recorder.Requests.Single();
            Assert.AreEqual("PROPFIND", Request.Method);
            Assert.AreEqual("0", Request.Header("Depth"));
            Assert.AreEqual("http://fake.local/dav/My%20file.txt", Request.Url.AbsoluteUri);
        }

        [TestMethod]
        public async Task Exists_Folder_IsTrue()
        {
            Server.AddFolder("/dav/folder");

            Assert.IsTrue(await Client.Exists("/dav/folder"));
            Assert.AreEqual("http://fake.local/dav/folder", Recorder.Requests.Single().Url.AbsoluteUri);
        }

        [TestMethod]
        public async Task Exists_Missing_IsFalse()
        {
            Assert.IsFalse(await Client.Exists("/dav/missing.txt"));
        }

        [DataTestMethod]
        [DataRow(301)]
        [DataRow(302)]
        [DataRow(307)]
        [DataRow(308)]
        public async Task Exists_Redirection_IsTrue(int Status)
        {
            var Client = Scripted(Request => DavResponses.Status(Status));

            Assert.IsTrue(await Client.Exists("/dav/folder"));
            Assert.AreEqual(1, Recorder.Requests.Count);
        }

        [DataTestMethod]
        [DataRow(401)]
        [DataRow(403)]
        [DataRow(500)]
        public async Task Exists_OtherError_Throws(int Status)
        {
            var Client = Scripted(Request => DavResponses.Status(Status));

            await AsyncAssert.ThrowsWebDavAsync(Status, () => Client.Exists("/dav/a.txt"));
        }

        #endregion

        #region Get

        [TestMethod]
        public async Task Get_ReturnsContentBytes()
        {
            var Content = new byte[] { 0, 1, 2, 127, 128, 255 };
            Server.AddFile("/dav/My folder/C++ #1.bin", Content);

            var Result = await Client.Get("/dav/My folder/C++ #1.bin");

            CollectionAssert.AreEqual(Content, Result);
            var Request = Recorder.Requests.Single();
            Assert.AreEqual("GET", Request.Method);
            Assert.AreEqual("http://fake.local/dav/My%20folder/C%2B%2B%20%231.bin", Request.Url.AbsoluteUri);
        }

        [TestMethod]
        public async Task Get_EmptyFile_ReturnsEmptyArray()
        {
            Server.AddFile("/dav/empty.txt", new byte[0]);

            var Result = await Client.Get("/dav/empty.txt");

            Assert.IsNotNull(Result);
            Assert.AreEqual(0, Result.Length);
        }

        [TestMethod]
        public async Task Get_Missing_Throws404()
        {
            await AsyncAssert.ThrowsWebDavAsync(404, () => Client.Get("/dav/missing.txt"));
        }

        [DataTestMethod]
        [DataRow(206)]
        [DataRow(302)]
        [DataRow(500)]
        public async Task Get_UnexpectedStatus_Throws(int Status)
        {
            var Client = Scripted(Request => DavResponses.Bytes(new byte[] { 1 }, Status));

            var ex = await AsyncAssert.ThrowsWebDavAsync(Status, () => Client.Get("/dav/a.txt"));
            StringAssert.Contains(ex.Message, "GET");
        }

        #endregion

        #region Put

        [TestMethod]
        public async Task Put_New_SendsContentWithoutIfNoneMatch()
        {
            await Client.Put("/dav/new #1.txt", TestClients.Text("hello"), false);

            Assert.AreEqual("hello", Server.GetText("/dav/new #1.txt"));
            var Request = Recorder.Requests.Single();
            Assert.AreEqual("PUT", Request.Method);
            Assert.AreEqual("http://fake.local/dav/new%20%231.txt", Request.Url.AbsoluteUri);
            Assert.AreEqual("hello", Request.BodyText);
            Assert.IsNull(Request.Header("If-None-Match"));
        }

        [TestMethod]
        public async Task Put_Existing_ReplacesContent()
        {
            Server.AddFile("/dav/a.txt", "old");

            await Client.Put("/dav/a.txt", TestClients.Text("new content"), false);

            Assert.AreEqual("new content", Server.GetText("/dav/a.txt"));
        }

        [TestMethod]
        public async Task Put_BinaryContent_IsSentUnchanged()
        {
            var Content = Enumerable.Range(0, 70000).Select(x => (byte)(x % 251)).ToArray();

            await Client.Put("/dav/big.bin", new System.IO.MemoryStream(Content), false);

            CollectionAssert.AreEqual(Content, Server.GetContent("/dav/big.bin"));
        }

        [TestMethod]
        public async Task Put_OnlyIfNew_SendsIfNoneMatchStar()
        {
            await Client.Put("/dav/new.txt", TestClients.Text("hello"), true);

            Assert.AreEqual("*", Recorder.Requests.Single().Header("If-None-Match"));
            Assert.AreEqual("hello", Server.GetText("/dav/new.txt"));
        }

        [TestMethod]
        public async Task Put_OnlyIfNewOnExistingFile_Throws412AndKeepsContent()
        {
            Server.AddFile("/dav/a.txt", "other client");

            await AsyncAssert.ThrowsWebDavAsync(412, () => Client.Put("/dav/a.txt", TestClients.Text("mine"), true));
            Assert.AreEqual("other client", Server.GetText("/dav/a.txt"));
        }

        [TestMethod]
        public async Task Put_MissingParent_Throws409()
        {
            await AsyncAssert.ThrowsWebDavAsync(409, () => Client.Put("/dav/missing/a.txt", TestClients.Text("x"), false));
        }

        [DataTestMethod]
        [DataRow(200)]
        [DataRow(201)]
        [DataRow(204)]
        public async Task Put_SuccessStatus_IsAccepted(int Status)
        {
            var Client = Scripted(Request => DavResponses.Status(Status));

            await Client.Put("/dav/a.txt", TestClients.Text("x"), false);

            Assert.AreEqual(1, Recorder.Requests.Count);
        }

        [DataTestMethod]
        [DataRow(207)]
        [DataRow(409)]
        [DataRow(412)]
        [DataRow(423)]
        [DataRow(500)]
        [DataRow(507)]
        public async Task Put_UnexpectedStatus_ThrowsWithStatus(int Status)
        {
            var Client = Scripted(Request => DavResponses.Status(Status));

            var ex = await AsyncAssert.ThrowsWebDavAsync(Status, () => Client.Put("/dav/a.txt", TestClients.Text("x"), false));
            StringAssert.Contains(ex.Message, "PUT");
        }

        #endregion

        #region Delete

        [TestMethod]
        public async Task Delete_File_UsesUrlWithoutTrailingSlash()
        {
            Server.AddFile("/dav/My file #1.txt", "x");

            await Client.Delete("/dav/My file #1.txt", false);

            Assert.IsFalse(Server.Exists("/dav/My file #1.txt"));
            var Request = Recorder.Requests.Single();
            Assert.AreEqual("DELETE", Request.Method);
            Assert.AreEqual("http://fake.local/dav/My%20file%20%231.txt", Request.Url.AbsoluteUri);
        }

        [TestMethod]
        public async Task Delete_Collection_UsesUrlWithTrailingSlashAndRemovesContent()
        {
            Server.AddFolder("/dav/My folder");
            Server.AddFile("/dav/My folder/a.txt", "x");

            await Client.Delete("/dav/My folder", true);

            Assert.IsFalse(Server.Exists("/dav/My folder"));
            Assert.IsFalse(Server.Exists("/dav/My folder/a.txt"));
            Assert.AreEqual("http://fake.local/dav/My%20folder/", Recorder.Requests.Single().Url.AbsoluteUri);
        }

        [TestMethod]
        public async Task Delete_Missing_Throws404()
        {
            await AsyncAssert.ThrowsWebDavAsync(404, () => Client.Delete("/dav/missing.txt", false));
        }

        [DataTestMethod]
        [DataRow(200)]
        [DataRow(202)]
        [DataRow(204)]
        public async Task Delete_SuccessStatus_IsAccepted(int Status)
        {
            var Client = Scripted(Request => DavResponses.Status(Status));

            await Client.Delete("/dav/a.txt", false);

            Assert.AreEqual(1, Recorder.Requests.Count);
        }

        [DataTestMethod]
        [DataRow(207)]
        [DataRow(403)]
        [DataRow(423)]
        [DataRow(500)]
        public async Task Delete_UnexpectedStatus_ThrowsWithStatus(int Status)
        {
            //207 on a DELETE means that some members of the collection could not be deleted
            var Client = Scripted(Request => DavResponses.Status(Status));

            var ex = await AsyncAssert.ThrowsWebDavAsync(Status, () => Client.Delete("/dav/folder", true));
            StringAssert.Contains(ex.Message, "DELETE");
        }

        #endregion

        #region MkCol

        [TestMethod]
        public async Task MkCol_CreatesFolderWithTrailingSlashUrl()
        {
            await Client.MkCol("/dav/New folder #1");

            Assert.IsTrue(Server.IsFolder("/dav/New folder #1"));
            var Request = Recorder.Requests.Single();
            Assert.AreEqual("MKCOL", Request.Method);
            Assert.AreEqual("http://fake.local/dav/New%20folder%20%231/", Request.Url.AbsoluteUri);
        }

        [TestMethod]
        public async Task MkCol_Existing_Throws405()
        {
            Server.AddFolder("/dav/folder");

            await AsyncAssert.ThrowsWebDavAsync(405, () => Client.MkCol("/dav/folder"));
        }

        [TestMethod]
        public async Task MkCol_MissingParent_Throws409()
        {
            await AsyncAssert.ThrowsWebDavAsync(409, () => Client.MkCol("/dav/missing/folder"));
            Assert.IsFalse(Server.Exists("/dav/missing/folder"));
        }

        [DataTestMethod]
        [DataRow(200)]
        [DataRow(201)]
        [DataRow(204)]
        public async Task MkCol_SuccessStatus_IsAccepted(int Status)
        {
            var Client = Scripted(Request => DavResponses.Status(Status));

            await Client.MkCol("/dav/folder");

            Assert.AreEqual(1, Recorder.Requests.Count);
        }

        [DataTestMethod]
        [DataRow(403)]
        [DataRow(415)]
        [DataRow(507)]
        public async Task MkCol_UnexpectedStatus_ThrowsWithStatus(int Status)
        {
            var Client = Scripted(Request => DavResponses.Status(Status));

            var ex = await AsyncAssert.ThrowsWebDavAsync(Status, () => Client.MkCol("/dav/folder"));
            StringAssert.Contains(ex.Message, "MKCOL");
        }

        #endregion

        #region Move

        [TestMethod]
        public async Task Move_File_DestinationIsAbsoluteEncodedUrl()
        {
            Server.AddFile("/dav/a.txt", "x");
            Server.AddFolder("/dav/My folder");

            await Client.Move("/dav/a.txt", "/dav/My folder/C# notes #1.txt", false, null);

            var Request = Recorder.Requests.Single();
            Assert.AreEqual("MOVE", Request.Method);
            Assert.AreEqual("http://fake.local/dav/a.txt", Request.Url.AbsoluteUri);
            Assert.AreEqual("http://fake.local/dav/My%20folder/C%23%20notes%20%231.txt", Request.Header("Destination"));
            Assert.IsNull(Request.Header("Overwrite"), "No Overwrite header when Overwrite is null");
            Assert.IsFalse(Server.Exists("/dav/a.txt"));
            Assert.AreEqual("x", Server.GetText("/dav/My folder/C# notes #1.txt"));
            CollectionAssert.AreEqual(new[] { "/dav/a.txt -> /dav/My folder/C# notes #1.txt (Overwrite: T)" }, Server.MoveRequests);
        }

        [TestMethod]
        public async Task Move_SpecialCharacters_AreEncodedInDestination()
        {
            Server.AddFile("/dav/a.txt", "x");

            await Client.Move("/dav/a.txt", "/dav/\u00e9t\u00e9 + 100%?.txt", false, null);

            Assert.AreEqual("http://fake.local/dav/%C3%A9t%C3%A9%20%2B%20100%25%3F.txt", Recorder.Requests.Single().Header("Destination"));
            Assert.AreEqual("x", Server.GetText("/dav/\u00e9t\u00e9 + 100%?.txt"));
        }

        [DataTestMethod]
        [DataRow(true, "T")]
        [DataRow(false, "F")]
        public async Task Move_Overwrite_SendsHeader(bool Overwrite, String Expected)
        {
            Server.AddFile("/dav/a.txt", "x");

            await Client.Move("/dav/a.txt", "/dav/b.txt", false, Overwrite);

            Assert.AreEqual(Expected, Recorder.Requests.Single().Header("Overwrite"));
            Assert.IsTrue(Server.Exists("/dav/b.txt"));
        }

        [TestMethod]
        public async Task Move_Collection_UsesTrailingSlashForSourceAndDestination()
        {
            Server.AddFolder("/dav/Old folder");
            Server.AddFile("/dav/Old folder/a.txt", "x");

            await Client.Move("/dav/Old folder", "/dav/New folder", true, false);

            var Request = Recorder.Requests.Single();
            Assert.AreEqual("http://fake.local/dav/Old%20folder/", Request.Url.AbsoluteUri);
            Assert.AreEqual("http://fake.local/dav/New%20folder/", Request.Header("Destination"));
            Assert.IsTrue(Server.IsFolder("/dav/New folder"));
            Assert.AreEqual("x", Server.GetText("/dav/New folder/a.txt"));
        }

        [TestMethod]
        public async Task Move_NoOverwriteOnExistingDestination_Throws412()
        {
            Server.AddFile("/dav/a.txt", "a");
            Server.AddFile("/dav/b.txt", "b");

            await AsyncAssert.ThrowsWebDavAsync(412, () => Client.Move("/dav/a.txt", "/dav/b.txt", false, false));
            Assert.AreEqual("a", Server.GetText("/dav/a.txt"));
            Assert.AreEqual("b", Server.GetText("/dav/b.txt"));
        }

        [TestMethod]
        public async Task Move_OverwriteOnExistingDestination_ReplacesIt()
        {
            Server.AddFile("/dav/a.txt", "a");
            Server.AddFile("/dav/b.txt", "b");

            await Client.Move("/dav/a.txt", "/dav/b.txt", false, true);

            Assert.IsFalse(Server.Exists("/dav/a.txt"));
            Assert.AreEqual("a", Server.GetText("/dav/b.txt"));
        }

        [TestMethod]
        public async Task Move_MissingSource_Throws404()
        {
            await AsyncAssert.ThrowsWebDavAsync(404, () => Client.Move("/dav/missing.txt", "/dav/b.txt", false, null));
        }

        [TestMethod]
        public async Task Move_MissingDestinationParent_Throws409()
        {
            Server.AddFile("/dav/a.txt", "a");

            await AsyncAssert.ThrowsWebDavAsync(409, () => Client.Move("/dav/a.txt", "/dav/missing/b.txt", false, null));
        }

        [DataTestMethod]
        [DataRow(200)]
        [DataRow(201)]
        [DataRow(204)]
        public async Task Move_SuccessStatus_IsAccepted(int Status)
        {
            var Client = Scripted(Request => DavResponses.Status(Status));

            await Client.Move("/dav/a.txt", "/dav/b.txt", false, null);

            Assert.AreEqual(1, Recorder.Requests.Count);
        }

        [DataTestMethod]
        [DataRow(207)]
        [DataRow(403)]
        [DataRow(412)]
        [DataRow(423)]
        [DataRow(502)]
        public async Task Move_UnexpectedStatus_ThrowsWithStatus(int Status)
        {
            var Client = Scripted(Request => DavResponses.Status(Status));

            var ex = await AsyncAssert.ThrowsWebDavAsync(Status, () => Client.Move("/dav/a.txt", "/dav/b.txt", false, false));
            StringAssert.Contains(ex.Message, "MOVE");
        }

        #endregion

        #region Errors common to every operation

        private IEnumerable<KeyValuePair<String, Func<Task>>> EveryOperation()
        {
            yield return new KeyValuePair<String, Func<Task>>("PropFind", () => Client.PropFind("/dav/a.txt", 0, false));
            yield return new KeyValuePair<String, Func<Task>>("GetItem", () => Client.GetItem("/dav/a.txt"));
            yield return new KeyValuePair<String, Func<Task>>("Exists", () => Client.Exists("/dav/a.txt"));
            yield return new KeyValuePair<String, Func<Task>>("Get", () => Client.Get("/dav/a.txt"));
            yield return new KeyValuePair<String, Func<Task>>("Put", () => Client.Put("/dav/b.txt", TestClients.Text("x"), false));
            yield return new KeyValuePair<String, Func<Task>>("Delete", () => Client.Delete("/dav/a.txt", false));
            yield return new KeyValuePair<String, Func<Task>>("MkCol", () => Client.MkCol("/dav/folder"));
            yield return new KeyValuePair<String, Func<Task>>("Move", () => Client.Move("/dav/a.txt", "/dav/c.txt", false, null));
        }

        [TestMethod]
        public async Task EveryOperation_CredentialsRejected_Throws401()
        {
            Server.AddFile("/dav/a.txt", "abc");
            Server.RejectCredentials = true;

            foreach (var Operation in EveryOperation())
            {
                var ex = await AsyncAssert.ThrowsAsync<WebDavException>(Operation.Value);
                Assert.AreEqual(401, ex.StatusCode, Operation.Key);
            }
        }

        [TestMethod]
        public async Task EveryOperation_TransportError_ThrowsHttpRequestException()
        {
            Server.AddFile("/dav/a.txt", "abc");
            Server.NetworkFailure = (Method, Path) => true;

            foreach (var Operation in EveryOperation())
            {
                try
                {
                    await Operation.Value();
                    Assert.Fail($"{Operation.Key}: no exception");
                }
                catch (HttpRequestException)
                {
                }
                catch (AssertFailedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Assert.Fail($"{Operation.Key}: expected HttpRequestException, got {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        #endregion
    }
}
