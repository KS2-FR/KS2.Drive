using KS2Drive.Tests.Infrastructure;
using KS2Drive.WebDav;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace KS2Drive.Tests.WebDav
{
    /// <summary>
    /// WebDavClient constructor, CreateHttpClient overloads and GetUrl.
    /// </summary>
    [TestClass]
    public class WebDavClientSetupTests
    {
        #region Constructor

        [TestMethod]
        public void Constructor_NullHttpClient_ThrowsArgumentNullException()
        {
            var ex = Assert.ThrowsException<ArgumentNullException>(() => new WebDavClient(null, FakeWebDavServer.ServerUrl));
            Assert.AreEqual("Http", ex.ParamName);
        }

        [DataTestMethod]
        [DataRow("http://fake.local", "http://fake.local")]
        [DataRow("http://fake.local/", "http://fake.local")]
        [DataRow("http://fake.local/dav", "http://fake.local")]
        [DataRow("https://docs.example.com:8443/dav/sub/?q=1#fragment", "https://docs.example.com:8443")]
        [DataRow("https://docs.example.com:443/dav/", "https://docs.example.com")]
        [DataRow("http://docs.example.com:80/dav/", "http://docs.example.com")]
        [DataRow("http://192.168.1.10:8080/remote.php/webdav/", "http://192.168.1.10:8080")]
        public void Constructor_Server_IsSchemeHostAndPortOnly(String ServerUrl, String Expected)
        {
            Assert.AreEqual(Expected, NewClient(ServerUrl).Server);
        }

        [TestMethod]
        public void Constructor_Server_DoesNotKeepUserInfo()
        {
            //Documented as "only its scheme, host and port are used": credentials typed in the URL must not end up in every URL
            var Client = NewClient("https://user:secret@docs.example.com/dav");

            Assert.AreEqual("https://docs.example.com", Client.Server);
            Assert.IsFalse(Client.GetUrl("/dav/a.txt", false).AbsoluteUri.Contains("secret"));
        }

        [TestMethod]
        public void Constructor_InvalidUrl_ThrowsUriFormatException()
        {
            Assert.ThrowsException<UriFormatException>(() => NewClient("not an url"));
        }

        [TestMethod]
        public void GetUrl_ServerUrlWithLibraryPath_DoesNotRepeatTheLibraryPath()
        {
            var Client = NewClient(FakeWebDavServer.LibraryUrl);

            Assert.AreEqual("http://fake.local/dav/a.txt", Client.GetUrl("/dav/a.txt", false).AbsoluteUri);
        }

        #endregion

        #region CreateHttpClient

        [TestMethod]
        public void CreateHttpClient_WithCredential_PreAuthenticatesAndDoesNotFollowRedirections()
        {
            var Credential = new NetworkCredential("user", "password");
            using (var Client = WebDavClient.CreateHttpClient(Credential, null))
            {
                var Handler = HandlerOf(Client) as HttpClientHandler;
                Assert.IsNotNull(Handler, "The handler is an HttpClientHandler");
                Assert.AreSame(Credential, Handler.Credentials);
                Assert.IsTrue(Handler.PreAuthenticate);
                Assert.IsFalse(Handler.AllowAutoRedirect);
                Assert.IsTrue(Handler.AutomaticDecompression.HasFlag(DecompressionMethods.GZip));
                Assert.IsTrue(Handler.AutomaticDecompression.HasFlag(DecompressionMethods.Deflate));
                Assert.AreEqual(false, Client.DefaultRequestHeaders.ExpectContinue);
            }
        }

        [TestMethod]
        public void CreateHttpClient_WithoutCredential_DoesNotPreAuthenticate()
        {
            using (var Client = WebDavClient.CreateHttpClient(null, null))
            {
                var Handler = HandlerOf(Client) as HttpClientHandler;
                Assert.IsNotNull(Handler, "The handler is an HttpClientHandler");
                Assert.IsNull(Handler.Credentials);
                Assert.IsFalse(Handler.PreAuthenticate);
                Assert.IsFalse(Handler.AllowAutoRedirect);
                Assert.AreEqual(false, Client.DefaultRequestHeaders.ExpectContinue);
            }
        }

        [TestMethod]
        public void CreateHttpClient_WithoutCertificate_HasNoClientCertificate()
        {
            using (var Client = WebDavClient.CreateHttpClient(new NetworkCredential("user", "password"), null))
            {
                var Handler = HandlerOf(Client) as WebRequestHandler;
                Assert.IsNotNull(Handler, "The handler is a WebRequestHandler");
                Assert.AreEqual(0, Handler.ClientCertificates.Count);
            }
        }

        [TestMethod]
        public void CreateHttpClient_WithCertificate_SendsItManually()
        {
            var Certificate = new X509Certificate();
            using (var Client = WebDavClient.CreateHttpClient(null, Certificate))
            {
                var Handler = HandlerOf(Client) as WebRequestHandler;
                Assert.IsNotNull(Handler, "The handler is a WebRequestHandler");
                Assert.AreEqual(ClientCertificateOption.Manual, Handler.ClientCertificateOptions);
                Assert.AreEqual(1, Handler.ClientCertificates.Count);
                Assert.AreSame(Certificate, Handler.ClientCertificates[0]);
                Assert.IsFalse(Handler.AllowAutoRedirect);
            }
        }

        [TestMethod]
        public async Task CreateHttpClient_WithHandler_UsesItWithoutExpectContinue()
        {
            var Recorder = new RecordingHandler { Respond = Request => DavResponses.Status(201) };
            using (var Client = WebDavClient.CreateHttpClient(Recorder))
            {
                Assert.AreSame(Recorder, HandlerOf(Client));
                Assert.AreEqual(false, Client.DefaultRequestHeaders.ExpectContinue);

                using (await Client.PutAsync("http://fake.local/dav/a.txt", new StringContent("abc"))) { }
                Assert.AreEqual(1, Recorder.Requests.Count, "The request went through the handler");
                Assert.IsFalse((Recorder.Requests[0].Header("Expect") ?? "").Contains("100-continue"));
            }
        }

        [TestMethod]
        public void CreateHttpClient_NullHandler_ThrowsArgumentNullException()
        {
            var ex = Assert.ThrowsException<ArgumentNullException>(() => WebDavClient.CreateHttpClient((HttpMessageHandler)null));
            Assert.AreEqual("Handler", ex.ParamName);
        }

        #endregion

        #region GetUrl

        [DataTestMethod]
        [DataRow("/dav/My folder/C++ notes.txt", "http://fake.local/dav/My%20folder/C%2B%2B%20notes.txt")]
        [DataRow("/dav/\u00e9t\u00e9/\u00dcn\u00efcode.txt", "http://fake.local/dav/%C3%A9t%C3%A9/%C3%9Cn%C3%AFcode.txt")]
        [DataRow("/dav/C# notes #1.txt", "http://fake.local/dav/C%23%20notes%20%231.txt")]
        [DataRow("/dav/100% sure.txt", "http://fake.local/dav/100%25%20sure.txt")]
        [DataRow("/dav/a%20b.txt", "http://fake.local/dav/a%2520b.txt")]
        [DataRow("/dav/what?.txt", "http://fake.local/dav/what%3F.txt")]
        [DataRow("/dav/a+b.txt", "http://fake.local/dav/a%2Bb.txt")]
        [DataRow("/dav/a&b=c;d.txt", "http://fake.local/dav/a%26b%3Dc%3Bd.txt")]
        [DataRow("dav/no leading slash.txt", "http://fake.local/dav/no%20leading%20slash.txt")]
        public void GetUrl_File_EncodesEachSegmentWithoutTrailingSlash(String Path, String Expected)
        {
            Uri Url = NewClient().GetUrl(Path, false);

            Assert.AreEqual(Expected, Url.AbsoluteUri);
            Assert.AreEqual("", Url.Query, "'?' is part of the path, not a query");
            Assert.AreEqual("", Url.Fragment, "'#' is part of the path, not a fragment");
        }

        [DataTestMethod]
        [DataRow("/dav/My folder", "http://fake.local/dav/My%20folder/")]
        [DataRow("/dav/My folder/", "http://fake.local/dav/My%20folder/")]
        [DataRow("/dav/C# #1", "http://fake.local/dav/C%23%20%231/")]
        [DataRow("/dav/v1.2", "http://fake.local/dav/v1.2/")]
        [DataRow("/dav", "http://fake.local/dav/")]
        public void GetUrl_Collection_EncodesEachSegmentWithTrailingSlash(String Path, String Expected)
        {
            Uri Url = NewClient().GetUrl(Path, true);

            Assert.AreEqual(Expected, Url.AbsoluteUri);
            Assert.AreEqual("", Url.Fragment);
        }

        [DataTestMethod]
        [DataRow("/dav/", "http://fake.local/dav")]
        [DataRow("/dav/My folder/", "http://fake.local/dav/My%20folder")]
        [DataRow("/dav", "http://fake.local/dav")]
        public void GetUrl_NotCollection_RemovesTrailingSlash(String Path, String Expected)
        {
            Assert.AreEqual(Expected, NewClient().GetUrl(Path, false).AbsoluteUri);
        }

        [DataTestMethod]
        [DataRow("", false)]
        [DataRow("", true)]
        [DataRow("/", false)]
        [DataRow("/", true)]
        public void GetUrl_Root_IsServerWithSingleSlash(String Path, bool AsCollection)
        {
            Assert.AreEqual("http://fake.local/", NewClient().GetUrl(Path, AsCollection).AbsoluteUri);
        }

        [TestMethod]
        public void GetUrl_NullPath_IsRoot()
        {
            Assert.AreEqual("http://fake.local/", NewClient().GetUrl(null, true).AbsoluteUri);
        }

        [DataTestMethod]
        [DataRow("/dav/My folder/C++ notes.txt")]
        [DataRow("/dav/C# notes #1.txt")]
        [DataRow("/dav/100% sure?.txt")]
        [DataRow("/dav/a%2520b.txt")]
        [DataRow("/dav/\u00e9t\u00e9 \u4e2d\u6587.txt")]
        public void GetUrl_DecodedPathOfUrl_IsTheOriginalPath(String Path)
        {
            Uri Url = NewClient().GetUrl(Path, false);

            Assert.AreEqual(Path, Uri.UnescapeDataString(Url.AbsolutePath));
        }

        [TestMethod]
        public void GetUrl_KeepsServerPort()
        {
            Assert.AreEqual("https://docs.example.com:8443/dav/a%20b/", NewClient("https://docs.example.com:8443/x").GetUrl("/dav/a b", true).AbsoluteUri);
        }

        #endregion

        #region Private methods

        private static WebDavClient NewClient(String ServerUrl = FakeWebDavServer.ServerUrl) => new WebDavClient(new HttpClient(), ServerUrl);

        /// <summary>
        /// Returns the handler of an HttpClient (private field of HttpMessageInvoker), or marks the test inconclusive.
        /// </summary>
        private static HttpMessageHandler HandlerOf(HttpClient Client)
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
            FieldInfo Field = typeof(HttpMessageInvoker).GetField("handler", Flags)
                ?? typeof(HttpMessageInvoker).GetField("_handler", Flags)
                ?? typeof(HttpMessageInvoker).GetFields(Flags).FirstOrDefault(x => typeof(HttpMessageHandler).IsAssignableFrom(x.FieldType));
            if (Field == null) Assert.Inconclusive("The handler field of HttpMessageInvoker was not found by reflection");
            var Handler = Field.GetValue(Client) as HttpMessageHandler;
            if (Handler == null) Assert.Inconclusive("The handler field of HttpMessageInvoker is empty");
            return Handler;
        }

        #endregion
    }
}
