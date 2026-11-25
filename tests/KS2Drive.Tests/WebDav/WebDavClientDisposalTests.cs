using KS2Drive.Tests.Infrastructure;
using KS2Drive.WebDav;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml;

namespace KS2Drive.Tests.WebDav
{
    /// <summary>
    /// Every response received by WebDavClient is disposed, on success and on failure (an undisposed response keeps its
    /// connection busy).
    /// </summary>
    [TestClass]
    public class WebDavClientDisposalTests
    {
        private static readonly String FolderBody = DavResponses.MultiStatus(DavResponses.Folder("/dav/v1.2/"));
        private static readonly String FileBody = DavResponses.MultiStatus(DavResponses.File("/dav/a.txt"));

        [TestMethod]
        public async Task PropFind_Success_DisposesResponse() => await AssertResponsesDisposed(r => DavResponses.Xml(FileBody), c => c.PropFind("/dav/a.txt", 0, false), 1);

        [TestMethod]
        public async Task PropFind_ErrorStatus_DisposesResponse() => await AssertResponsesDisposed(r => DavResponses.Xml(FileBody, 500), c => c.PropFind("/dav/a.txt", 0, false), 1);

        [TestMethod]
        public async Task PropFind_MalformedXml_DisposesResponse() => await AssertResponsesDisposed(r => DavResponses.Xml("<D:multistatus xmlns:D=\"DAV:\">"), c => c.PropFind("/dav/a.txt", 0, false), 1);

        [TestMethod]
        public async Task GetItem_Fallback_DisposesBothResponses() => await AssertResponsesDisposed(r => r.RequestUri.AbsolutePath.EndsWith("/") ? DavResponses.Xml(FolderBody) : DavResponses.Status(404), c => c.GetItem("/dav/v1.2"), 2);

        [TestMethod]
        public async Task GetItem_Missing_DisposesBothResponses() => await AssertResponsesDisposed(r => DavResponses.Status(404), c => c.GetItem("/dav/missing"), 2);

        [TestMethod]
        public async Task Exists_Missing_DisposesResponse() => await AssertResponsesDisposed(r => DavResponses.Status(404), c => c.Exists("/dav/a.txt"), 1);

        [TestMethod]
        public async Task Exists_Redirection_DisposesResponse() => await AssertResponsesDisposed(r => DavResponses.Status(301), c => c.Exists("/dav/folder"), 1);

        [TestMethod]
        public async Task Exists_Found_DisposesResponse() => await AssertResponsesDisposed(r => DavResponses.Xml(FileBody), c => c.Exists("/dav/a.txt"), 1);

        [TestMethod]
        public async Task Get_Success_DisposesResponse() => await AssertResponsesDisposed(r => DavResponses.Bytes(new byte[] { 1, 2, 3 }), c => c.Get("/dav/a.txt"), 1);

        [TestMethod]
        public async Task Get_ErrorStatus_DisposesResponse() => await AssertResponsesDisposed(r => DavResponses.Bytes(new byte[] { 1 }, 404), c => c.Get("/dav/a.txt"), 1);

        [TestMethod]
        public async Task Put_Success_DisposesResponse() => await AssertResponsesDisposed(r => DavResponses.Status(201), c => c.Put("/dav/a.txt", TestClients.Text("x"), false), 1);

        [TestMethod]
        public async Task Put_ErrorStatus_DisposesResponse() => await AssertResponsesDisposed(r => DavResponses.Status(412), c => c.Put("/dav/a.txt", TestClients.Text("x"), true), 1);

        [TestMethod]
        public async Task Delete_Success_DisposesResponse() => await AssertResponsesDisposed(r => DavResponses.Status(204), c => c.Delete("/dav/a.txt", false), 1);

        [TestMethod]
        public async Task Delete_ErrorStatus_DisposesResponse() => await AssertResponsesDisposed(r => DavResponses.Status(404), c => c.Delete("/dav/a.txt", false), 1);

        [TestMethod]
        public async Task MkCol_Success_DisposesResponse() => await AssertResponsesDisposed(r => DavResponses.Status(201), c => c.MkCol("/dav/folder"), 1);

        [TestMethod]
        public async Task MkCol_ErrorStatus_DisposesResponse() => await AssertResponsesDisposed(r => DavResponses.Status(405), c => c.MkCol("/dav/folder"), 1);

        [TestMethod]
        public async Task Move_Success_DisposesResponse() => await AssertResponsesDisposed(r => DavResponses.Status(201), c => c.Move("/dav/a.txt", "/dav/b.txt", false, true), 1);

        [TestMethod]
        public async Task Move_ErrorStatus_DisposesResponse() => await AssertResponsesDisposed(r => DavResponses.Status(412), c => c.Move("/dav/a.txt", "/dav/b.txt", false, false), 1);

        [TestMethod]
        public async Task SequenceAgainstServer_DisposesEveryResponse()
        {
            var Server = new FakeWebDavServer();
            var Handler = new RecordingHandler(Server);
            var Client = TestClients.For(Handler);

            await Client.MkCol("/dav/folder");
            await Client.Put("/dav/folder/a.txt", TestClients.Text("abc"), true);
            await Client.PropFind("/dav/folder", 1, true);
            await Client.GetItem("/dav/folder/a.txt");
            await Client.GetItem("/dav/missing");
            await Client.Exists("/dav/folder/a.txt");
            await Client.Get("/dav/folder/a.txt");
            await Client.Move("/dav/folder/a.txt", "/dav/folder/b.txt", false, false);
            try { await Client.Get("/dav/folder/a.txt"); } catch (WebDavException) { }
            await Client.Delete("/dav/folder", true);

            Assert.AreEqual(Server.Requests.Count, Handler.ResponsesCreated, "Every request answered by the server went through the recorder");
            Assert.AreEqual(Handler.ResponsesCreated, Handler.ResponsesDisposed);
        }

        #region Private methods

        /// <summary>
        /// Runs the operation (a WebDavException or XmlException is expected for error responses) and checks that the
        /// expected number of responses was received and that all of them were disposed.
        /// </summary>
        private static async Task AssertResponsesDisposed(Func<HttpRequestMessage, HttpResponseMessage> Respond, Func<WebDavClient, Task> Operation, int ExpectedResponses)
        {
            var Handler = new RecordingHandler { Respond = Respond };
            var Client = TestClients.For(Handler);
            try
            {
                await Operation(Client);
            }
            catch (WebDavException)
            {
            }
            catch (XmlException)
            {
            }

            Assert.AreEqual(ExpectedResponses, Handler.ResponsesCreated, "Responses received");
            Assert.AreEqual(Handler.ResponsesCreated, Handler.ResponsesDisposed, "Responses disposed");
        }

        #endregion
    }
}
