using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using DavClient = WebDAVClient.Client;

namespace KS2Drive.Tests.WebDAVClientTests
{
    /// <summary>
    /// Client.CreateHttpClient (both overloads) and the protected Client(HttpClient) constructor.
    /// </summary>
    [TestClass]
    public class CreateHttpClientTests
    {
        private static HttpMessageHandler HandlerOf(HttpClient Client)
        {
            var Field = typeof(HttpMessageInvoker).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(f => typeof(HttpMessageHandler).IsAssignableFrom(f.FieldType));
            if (Field == null) Assert.Inconclusive("Cannot find the handler of HttpClient by reflection");
            return (HttpMessageHandler)Field.GetValue(Client);
        }

        #region CreateHttpClient(NetworkCredential, IWebProxy, X509Certificate)

        [TestMethod]
        public void CreateHttpClient_Default_UsesWebRequestHandlerWithDecompressionAndNoExpectContinue()
        {
            using (var Client = DavClient.CreateHttpClient())
            {
                Assert.AreEqual(false, Client.DefaultRequestHeaders.ExpectContinue);
                var Handler = HandlerOf(Client) as WebRequestHandler;
                Assert.IsNotNull(Handler);
                Assert.AreEqual(DecompressionMethods.Deflate | DecompressionMethods.GZip, Handler.AutomaticDecompression);
                Assert.IsNull(Handler.Credentials);
                Assert.IsFalse(Handler.PreAuthenticate);
                Assert.AreEqual(0, Handler.ClientCertificates.Count);
            }
        }

        [TestMethod]
        public void CreateHttpClient_WithCredential_PreAuthenticates()
        {
            var Credential = new NetworkCredential("user", "test-password");
            using (var Client = DavClient.CreateHttpClient(Credential))
            {
                var Handler = (WebRequestHandler)HandlerOf(Client);
                Assert.AreSame(Credential, Handler.Credentials);
                Assert.IsTrue(Handler.PreAuthenticate);
                Assert.AreEqual(false, Client.DefaultRequestHeaders.ExpectContinue);
            }
        }

        [TestMethod]
        public void CreateHttpClient_WithProxy_UsesIt()
        {
            var Proxy = new WebProxy("http://proxy.local:3128");
            using (var Client = DavClient.CreateHttpClient(null, Proxy))
            {
                var Handler = (WebRequestHandler)HandlerOf(Client);
                Assert.AreSame(Proxy, Handler.Proxy);
            }
        }

        [TestMethod]
        public void CreateHttpClient_EachCallCreatesANewClientAndHandler()
        {
            using (var A = DavClient.CreateHttpClient())
            using (var B = DavClient.CreateHttpClient())
            {
                Assert.AreNotSame(A, B);
                Assert.AreNotSame(HandlerOf(A), HandlerOf(B));
            }
        }

        #endregion

        #region CreateHttpClient(HttpMessageHandler)

        [TestMethod]
        public void CreateHttpClientWithHandler_NullHandler_Throws()
        {
            var Ex = Assert.ThrowsException<ArgumentNullException>(() => DavClient.CreateHttpClient((HttpMessageHandler)null));
            Assert.AreEqual("handler", Ex.ParamName);
        }

        [TestMethod]
        public void CreateHttpClientWithHandler_UsesTheHandlerAndDisablesExpectContinue()
        {
            var Server = new FakeWebDavServer();
            using (var Client = DavClient.CreateHttpClient(Server))
            {
                Assert.AreSame(Server, HandlerOf(Client));
                Assert.AreEqual(false, Client.DefaultRequestHeaders.ExpectContinue);
            }
        }

        [TestMethod]
        public void CreateHttpClientWithHandler_NoExpectHeaderIsSentOnUpload()
        {
            using (var F = new DavFixture())
            {
                DavAssert.Run(F.Client.Upload("/", new MemoryStream(new byte[100]), "a.bin"));
                Assert.IsNull(F.Recorder.Last.Header("Expect"));
            }
        }

        #endregion

        #region Client(HttpClient)

        [TestMethod]
        public void ProtectedConstructor_NullHttpClient_Throws()
        {
            var Ex = Assert.ThrowsException<ArgumentNullException>(() => new DavTestClient(null));
            Assert.AreEqual("sharedClient", Ex.ParamName);
        }

        [TestMethod]
        public void ProtectedConstructor_SharedHttpClient_ServesSeveralClientsWithTheirOwnSettings()
        {
            var Server = new FakeWebDavServer();
            Server.AddFolder("/other");
            Server.AddFile("/dav/a.txt", "in dav");
            Server.AddFile("/other/a.txt", "in other");
            using (var Http = DavClient.CreateHttpClient(Server))
            {
                var C1 = new DavTestClient(Http) { Server = FakeWebDavServer.ServerUrl, BasePath = "/dav" };
                var C2 = new DavTestClient(Http) { Server = FakeWebDavServer.ServerUrl, BasePath = "/other" };
                Assert.AreEqual("in dav", System.Text.Encoding.UTF8.GetString(DavAssert.Run(C1.Download("/a.txt"))));
                Assert.AreEqual("in other", System.Text.Encoding.UTF8.GetString(DavAssert.Run(C2.Download("/a.txt"))));
                Assert.AreEqual("in dav", System.Text.Encoding.UTF8.GetString(DavAssert.Run(C1.Download("/a.txt"))));
            }
        }

        [TestMethod]
        public void ProtectedConstructor_UploadUsesTheSharedClient()
        {
            var Server = new FakeWebDavServer();
            using (var Http = DavClient.CreateHttpClient(Server))
            {
                var C = new DavTestClient(Http) { Server = FakeWebDavServer.ServerUrl, BasePath = "/dav" };
                Assert.IsTrue(DavAssert.Run(C.Upload("/", new MemoryStream(new byte[] { 65 }), "up.txt")));
                Assert.AreEqual("A", Server.GetText("/dav/up.txt"));
                Assert.AreEqual(1, Server.CountRequests("PUT"));
            }
        }

        #endregion

        #region Public constructor

        [TestMethod]
        public void PublicConstructor_WithAllParameters_DoesNotThrow()
        {
            var C = new DavClient(new NetworkCredential("u", "test-password"), TimeSpan.FromSeconds(5), new WebProxy("http://proxy.local:3128"));
            C.Server = "http://server";
            C.BasePath = "lib";
            Assert.AreEqual("http://server", C.Server);
            Assert.AreEqual("/lib/", C.BasePath);
        }

        #endregion
    }
}
