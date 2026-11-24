using KS2Drive.FS;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KS2Drive.WebDav;

namespace KS2Drive.Tests.FS
{
    /// <summary>
    /// Regression tests for "Explorer freezes while Office saves a document on an AOS drive" (real server).
    /// Cause: the WebDAV client never disposed the HTTP responses of Upload, CreateDir, Delete and Move. Requests are sent with
    /// ResponseHeadersRead, so an undisposed response keeps its connection busy; .NET Framework allows only 2 connections per
    /// server (ServicePointManager.DefaultConnectionLimit), so after 2 failed uploads (error pages with a body) every following
    /// request waited forever, and the WinFSP callback waiting for it (e.g. Cleanup deleting Word's lock file) froze Explorer.
    /// These tests use a real local HTTP server (sockets and connection pooling), unlike the in-memory fake server.
    /// </summary>
    [TestClass]
    public class HttpConnectionLeakTests
    {
        private static readonly TimeSpan NotBlocked = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Minimal local HTTP server: answers each request with the status returned by <see cref="Status"/> and an HTML body.
        /// </summary>
        private sealed class LocalHttpServer : IDisposable
        {
            private readonly HttpListener Listener = new HttpListener();
            public String Url { get; }
            public Func<String, int> Status { get; set; } = Method => 200;
            public int RequestCount;

            public LocalHttpServer()
            {
                var Probe = new TcpListener(IPAddress.Loopback, 0);
                Probe.Start();
                int Port = ((IPEndPoint)Probe.LocalEndpoint).Port;
                Probe.Stop();
                Url = $"http://localhost:{Port}";
                Listener.Prefixes.Add(Url + "/");
                Listener.Start();
                Task.Run(Loop);
            }

            private async Task Loop()
            {
                while (Listener.IsListening)
                {
                    HttpListenerContext Context;
                    try { Context = await Listener.GetContextAsync().ConfigureAwait(false); }
                    catch { return; }
                    Interlocked.Increment(ref RequestCount);
                    try
                    {
                        using (var Input = Context.Request.InputStream) Input.CopyTo(Stream.Null);
                        int Code = Status(Context.Request.HttpMethod);
                        Context.Response.StatusCode = Code;
                        //Like Alfresco, errors come with an HTML page: the client must read or dispose it to free the connection
                        byte[] Body = Code >= 400 ? Encoding.UTF8.GetBytes("<html><body>" + new String('x', 64 * 1024) + "</body></html>") : new byte[0];
                        Context.Response.ContentLength64 = Body.Length;
                        Context.Response.OutputStream.Write(Body, 0, Body.Length);
                        Context.Response.Close();
                    }
                    catch
                    {
                        //Client gone
                    }
                }
            }

            public void Dispose()
            {
                try { Listener.Stop(); Listener.Close(); } catch { }
            }
        }

        private LocalHttpServer Server;
        private WebDavClient Client;

        [TestInitialize]
        public void Initialize()
        {
            Server = new LocalHttpServer();
            Client = new WebDavClient(WebDavClient.CreateHttpClient(new NetworkCredential("user", "password"), null), Server.Url); //Real HTTP stack
            //Small limit (DavFS raises it, and .NET Framework does not limit loopback addresses at all), so that a few leaked
            //connections are enough to block the next request, like with the framework default on a remote server
            ServicePointManager.FindServicePoint(new Uri(Server.Url)).ConnectionLimit = 2;
        }

        [TestCleanup]
        public void Cleanup()
        {
            Server.Dispose();
        }

        private static void AssertCompletesQuickly(Func<Task> Request, String What)
        {
            var Running = Task.Run(Request);
            try
            {
                Assert.IsTrue(Running.Wait(NotBlocked), $"{What} is blocked: HTTP connections are still held by previous responses");
            }
            catch (AggregateException)
            {
                //The request completed with an error: not blocked
            }
        }

        private static void ExpectWebDavError(Func<Task> Request)
        {
            try
            {
                Request().GetAwaiter().GetResult();
                Assert.Fail("A WebDavException was expected");
            }
            catch (WebDavException)
            {
            }
        }

        [TestMethod]
        public void ConnectionLimitIsTheFrameworkDefault()
        {
            //The scenario relies on the default limit of 2 connections per server of a desktop .NET Framework application
            Assert.AreEqual(2, ServicePointManager.DefaultConnectionLimit);
        }

        [TestMethod]
        public void Mount_AllowsSeveralSimultaneousConnectionsToTheServer()
        {
            var Config = KS2Drive.Tests.Infrastructure.DavFsContext.TestConfiguration();
            Config.ServerURL = Server.Url + "/dav";
            try { new DavFS(Config); } catch (Exception) { } //The connection test may fail on this minimal server: the limit is set before
            Assert.AreEqual(DavFS.MaxConnectionsPerServer, ServicePointManager.FindServicePoint(new Uri(Server.Url)).ConnectionLimit);
            Assert.IsTrue(DavFS.MaxConnectionsPerServer > 2);
        }

        [TestMethod]
        public void FailedUploads_DoNotBlockTheFollowingRequests()
        {
            Server.Status = Method => Method == "PUT" ? 500 : 204;
            for (int i = 0; i < 3; i++) ExpectWebDavError(() => Client.Put($"/dav/~WRD000{i}.tmp", new MemoryStream(new byte[] { 1 }), false));

            AssertCompletesQuickly(() => Client.Delete("/dav/~$doc.docx", false), "The DELETE following failed uploads");
        }

        [TestMethod]
        public void FailedDeletesMovesAndFolderCreations_DoNotBlockTheFollowingRequests()
        {
            Server.Status = Method => Method == "PUT" ? 201 : 500;
            ExpectWebDavError(() => Client.Delete("/dav/a.txt", false));
            ExpectWebDavError(() => Client.Move("/dav/a.txt", "/dav/b.txt", false, false));
            ExpectWebDavError(() => Client.MkCol("/dav/folder"));

            AssertCompletesQuickly(() => Client.Put("/dav/a.txt", new MemoryStream(new byte[] { 1 }), false), "The PUT following failed requests");
        }

        [TestMethod]
        public void ManySuccessfulRequests_DoNotExhaustTheConnections()
        {
            Server.Status = Method => Method == "PUT" ? 201 : 204;
            AssertCompletesQuickly(async () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    await Client.Put($"/dav/f{i}.txt", new MemoryStream(new byte[] { 1 }), false).ConfigureAwait(false);
                    await Client.Move($"/dav/f{i}.txt", $"/dav/g{i}.txt", false, false).ConfigureAwait(false);
                    await Client.Delete($"/dav/g{i}.txt", false).ConfigureAwait(false);
                }
            }, "20 upload/move/delete cycles");
            Assert.AreEqual(60, Server.RequestCount);
        }
    }
}
