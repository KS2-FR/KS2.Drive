using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using DavClient = WebDAVClient.Client;

namespace KS2Drive.Tests.WebDAVClientTests
{
    /// <summary>
    /// Client sending its requests through a given HttpClient (protected constructor of the library).
    /// </summary>
    internal class DavTestClient : DavClient
    {
        public DavTestClient(HttpClient SharedClient) : base(SharedClient) { }
    }

    /// <summary>
    /// A request as seen on the wire: method, raw (encoded) URI, headers and body.
    /// </summary>
    internal class DavRecordedRequest
    {
        public String Method;
        public Uri Uri;
        public Dictionary<String, String> Headers = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
        public String ContentType;
        public String Body;

        /// <summary>Encoded absolute URI, e.g. http://fake.local/dav/a%20b.txt</summary>
        public String AbsoluteUri => Uri.AbsoluteUri;

        public String Header(String Name) => Headers.TryGetValue(Name, out String Value) ? Value : null;
    }

    /// <summary>
    /// Records every request (raw URI, headers, body for small text requests) before passing it to the inner handler.
    /// </summary>
    internal class DavRecordingHandler : DelegatingHandler
    {
        public List<DavRecordedRequest> Requests { get; } = new List<DavRecordedRequest>();

        public DavRecordedRequest Last => Requests.Last();

        public DavRecordingHandler(HttpMessageHandler Inner) : base(Inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
        {
            var Recorded = new DavRecordedRequest { Method = Request.Method.Method, Uri = Request.RequestUri };
            foreach (var H in Request.Headers) Recorded.Headers[H.Key] = String.Join(",", H.Value);
            //Only read the body of non-upload requests (byte array content, safe to read twice)
            if (Request.Content != null && Request.Method.Method != "PUT")
            {
                Recorded.ContentType = Request.Content.Headers.ContentType?.MediaType;
                Recorded.Body = Encoding.UTF8.GetString(await Request.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            }
            Requests.Add(Recorded);
            return await base.SendAsync(Request, CancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handler answering every request with a canned response.
    /// </summary>
    internal class DavStubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> Responder;

        public DavStubHandler(Func<HttpRequestMessage, HttpResponseMessage> Responder)
        {
            this.Responder = Responder;
        }

        public DavStubHandler(HttpStatusCode Status, String Body)
            : this(r => new HttpResponseMessage(Status) { Content = new StringContent(Body ?? "", Encoding.UTF8, "application/xml") })
        { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
        {
            return Task.FromResult(Responder(Request));
        }
    }

    /// <summary>
    /// A client wired to an in-memory fake server, with a recorder in between.
    /// </summary>
    internal class DavFixture : IDisposable
    {
        public FakeWebDavServer Server { get; }
        public DavRecordingHandler Recorder { get; }
        public HttpClient Http { get; }
        public DavTestClient Client { get; }

        public DavFixture() : this(new FakeWebDavServer()) { }

        public DavFixture(HttpMessageHandler Inner)
        {
            Server = Inner as FakeWebDavServer;
            Recorder = new DavRecordingHandler(Inner);
            Http = DavClient.CreateHttpClient(Recorder);
            Client = new DavTestClient(Http)
            {
                Server = FakeWebDavServer.ServerUrl,
                BasePath = FakeWebDavServer.LibraryPath
            };
        }

        public static DavFixture WithStub(HttpStatusCode Status, String Body) => new DavFixture(new DavStubHandler(Status, Body));

        public void Dispose()
        {
            Http.Dispose();
        }
    }

    internal static class DavAssert
    {
        /// <summary>
        /// Awaits the task and asserts it fails with an exception of exactly type T (not a derived type).
        /// </summary>
        public static T ThrowsExactly<T>(Func<Task> Action) where T : Exception
        {
            try
            {
                Action().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                if (ex.GetType() != typeof(T)) Assert.Fail($"Expected exception {typeof(T).Name} but got {ex.GetType().Name}: {ex}");
                return (T)ex;
            }
            Assert.Fail($"Expected exception {typeof(T).Name} but nothing was thrown");
            return null;
        }

        /// <summary>
        /// Awaits the task and asserts it fails with an exception of type T or a derived type.
        /// </summary>
        public static T Throws<T>(Func<Task> Action) where T : Exception
        {
            try
            {
                Action().GetAwaiter().GetResult();
            }
            catch (T ex)
            {
                return ex;
            }
            catch (Exception ex)
            {
                Assert.Fail($"Expected exception {typeof(T).Name} but got {ex.GetType().Name}: {ex}");
            }
            Assert.Fail($"Expected exception {typeof(T).Name} but nothing was thrown");
            return null;
        }

        public static T Run<T>(Task<T> Task) => Task.GetAwaiter().GetResult();

        public static void Run(Task Task) => Task.GetAwaiter().GetResult();
    }

    internal static class DavXml
    {
        public static String Multistatus(params String[] Responses)
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?><D:multistatus xmlns:D=\"DAV:\">" + String.Concat(Responses) + "</D:multistatus>";
        }

        public static String Response(String Href, String Props)
        {
            return "<D:response><D:href>" + Href + "</D:href><D:propstat><D:prop>" + Props + "</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>";
        }

        public static Stream ToStream(String Xml) => new MemoryStream(Encoding.UTF8.GetBytes(Xml));
    }
}
