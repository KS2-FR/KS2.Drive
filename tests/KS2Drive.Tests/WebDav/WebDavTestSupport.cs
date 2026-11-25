using KS2Drive.Tests.Infrastructure;
using KS2Drive.WebDav;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KS2Drive.Tests.WebDav
{
    /// <summary>
    /// A request seen by <see cref="RecordingHandler"/>, captured while it was being sent (HttpClient disposes the request
    /// content afterwards).
    /// </summary>
    internal sealed class RecordedRequest
    {
        public String Method;
        public Uri Url;
        public readonly Dictionary<String, String> Headers = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body;
        public String ContentType;

        public String BodyText => Body == null ? null : Encoding.UTF8.GetString(Body);

        public String Header(String Name) => Headers.TryGetValue(Name, out String Value) ? Value : null;
    }

    /// <summary>
    /// Response whose disposal is counted.
    /// </summary>
    internal sealed class TrackingResponse : HttpResponseMessage
    {
        private readonly Action OnDispose;
        private int Disposed;

        public TrackingResponse(HttpStatusCode Status, Action OnCreate, Action OnDispose) : base(Status)
        {
            this.OnDispose = OnDispose;
            OnCreate();
        }

        #region Overrides

        protected override void Dispose(bool Disposing)
        {
            if (Disposing && Interlocked.Exchange(ref Disposed, 1) == 0) OnDispose();
            base.Dispose(Disposing);
        }

        #endregion
    }

    /// <summary>
    /// Records every request (method, URL, headers, body) and answers either with <see cref="Respond"/> or with the inner
    /// handler (e.g. a <see cref="FakeWebDavServer"/>). Every response returned is a <see cref="TrackingResponse"/>, so that
    /// the number of responses created and disposed can be compared.
    /// </summary>
    internal sealed class RecordingHandler : DelegatingHandler
    {
        private int Created;
        private int Disposed;

        public List<RecordedRequest> Requests { get; } = new List<RecordedRequest>();

        /// <summary>Optional: builds the response instead of the inner handler.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; }

        public int ResponsesCreated => Volatile.Read(ref Created);
        public int ResponsesDisposed => Volatile.Read(ref Disposed);

        public RecordingHandler()
        {
        }

        public RecordingHandler(HttpMessageHandler Inner) : base(Inner)
        {
        }

        #region Overrides

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
        {
            var Recorded = new RecordedRequest { Method = Request.Method.Method, Url = Request.RequestUri };
            foreach (var Header in Request.Headers) Recorded.Headers[Header.Key] = String.Join(", ", Header.Value);
            if (Request.Content != null)
            {
                Recorded.Body = await Request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                Recorded.ContentType = Request.Content.Headers.ContentType?.MediaType;
            }
            lock (Requests) Requests.Add(Recorded);

            HttpResponseMessage Inner;
            if (Respond != null) Inner = Respond(Request);
            else if (InnerHandler != null) Inner = await base.SendAsync(Request, CancellationToken).ConfigureAwait(false);
            else throw new InvalidOperationException("No response configured");

            var Tracked = new TrackingResponse(Inner.StatusCode, () => Interlocked.Increment(ref Created), () => Interlocked.Increment(ref Disposed))
            {
                Content = Inner.Content,
                ReasonPhrase = Inner.ReasonPhrase,
                RequestMessage = Request
            };
            foreach (var Header in Inner.Headers) Tracked.Headers.TryAddWithoutValidation(Header.Key, Header.Value);
            return Tracked;
        }

        #endregion
    }

    /// <summary>
    /// Builders of responses and multistatus bodies.
    /// </summary>
    internal static class DavResponses
    {
        public static HttpResponseMessage Status(int Code) => new HttpResponseMessage((HttpStatusCode)Code);

        public static HttpResponseMessage Xml(String Body, int Code = 207) => new HttpResponseMessage((HttpStatusCode)Code) { Content = new StringContent(Body, Encoding.UTF8, "application/xml") };

        public static HttpResponseMessage Bytes(byte[] Body, int Code = 200) => new HttpResponseMessage((HttpStatusCode)Code) { Content = new ByteArrayContent(Body) };

        public static String MultiStatus(params String[] Responses) => "<?xml version=\"1.0\" encoding=\"utf-8\"?><D:multistatus xmlns:D=\"DAV:\">" + String.Concat(Responses) + "</D:multistatus>";

        public static String File(String Href, long Length = 3) => "<D:response><D:href>" + Href + "</D:href><D:propstat><D:prop><D:resourcetype/><D:getcontentlength>" + Length + "</D:getcontentlength></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>";

        public static String Folder(String Href) => "<D:response><D:href>" + Href + "</D:href><D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>";
    }

    internal static class TestClients
    {
        /// <summary>A client of <see cref="FakeWebDavServer.ServerUrl"/> sending its requests through the given handler.</summary>
        public static WebDavClient For(HttpMessageHandler Handler) => new WebDavClient(WebDavClient.CreateHttpClient(Handler), FakeWebDavServer.ServerUrl);

        public static Stream Text(String Content) => new MemoryStream(Encoding.UTF8.GetBytes(Content));
    }

    internal static class AsyncAssert
    {
        /// <summary>Asserts that the operation throws an exception of type T (or derived) and returns it.</summary>
        public static async Task<T> ThrowsAsync<T>(Func<Task> Operation) where T : Exception
        {
            try
            {
                await Operation().ConfigureAwait(false);
            }
            catch (T ex)
            {
                return ex;
            }
            catch (Exception ex)
            {
                Assert.Fail($"Expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}");
            }
            Assert.Fail($"Expected {typeof(T).Name}, but no exception was thrown");
            return null;
        }

        /// <summary>Asserts that the operation throws a <see cref="WebDavException"/> with the given status code.</summary>
        public static async Task<WebDavException> ThrowsWebDavAsync(int StatusCode, Func<Task> Operation)
        {
            var ex = await ThrowsAsync<WebDavException>(Operation).ConfigureAwait(false);
            Assert.AreEqual(StatusCode, ex.StatusCode, "Status code of the WebDavException");
            return ex;
        }
    }
}
