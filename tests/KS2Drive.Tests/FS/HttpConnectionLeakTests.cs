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
using WebDAVClient.Helpers;

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

        [TestInitialize]
        public void Initialize()
        {
            Server = new LocalHttpServer();
            //.NET Framework does not apply the default limit of 2 connections to loopback addresses: apply it here, like for a
            //remote server such as Alfresco
            ServicePointManager.FindServicePoint(new Uri(Server.Url)).ConnectionLimit = ServicePointManager.DefaultConnectionLimit;
            WebDavClient2.TestHttpMessageHandler = null; //Real HTTP stack
            WebDavClient2.Init(Server.Url, "/dav", "user", "password", null);
            FileNode.Init("/dav", WebDAVMode.WebDAV);
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
                Assert.Fail("A WebDAVException was expected");
            }
            catch (WebDAVException)
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
        public void FailedUploads_DoNotBlockTheFollowingRequests()
        {
            Server.Status = Method => Method == "PUT" ? 500 : 204;
            for (int i = 0; i < 3; i++) ExpectWebDavError(() => new WebDavClient2().Upload("/dav", new MemoryStream(new byte[] { 1 }), $"~WRD000{i}.tmp"));

            AssertCompletesQuickly(() => new WebDavClient2().DeleteFile("/dav/~$doc.docx"), "The DELETE following failed uploads");
        }

        [TestMethod]
        public void FailedDeletesMovesAndFolderCreations_DoNotBlockTheFollowingRequests()
        {
            Server.Status = Method => Method == "PUT" ? 201 : 500;
            ExpectWebDavError(() => new WebDavClient2().DeleteFile("/dav/a.txt"));
            ExpectWebDavError(() => new WebDavClient2().MoveFile("/dav/a.txt", "/dav/b.txt", false));
            ExpectWebDavError(() => new WebDavClient2().CreateDir("/dav", "folder"));

            AssertCompletesQuickly(() => new WebDavClient2().Upload("/dav", new MemoryStream(new byte[] { 1 }), "a.txt"), "The PUT following failed requests");
        }

        [TestMethod]
        public void ManySuccessfulRequests_DoNotExhaustTheConnections()
        {
            Server.Status = Method => Method == "PUT" ? 201 : 204;
            AssertCompletesQuickly(async () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    await new WebDavClient2().Upload("/dav", new MemoryStream(new byte[] { 1 }), $"f{i}.txt").ConfigureAwait(false);
                    await new WebDavClient2().MoveFile($"/dav/f{i}.txt", $"/dav/g{i}.txt", false).ConfigureAwait(false);
                    await new WebDavClient2().DeleteFile($"/dav/g{i}.txt").ConfigureAwait(false);
                }
            }, "20 upload/move/delete cycles");
            Assert.AreEqual(60, Server.RequestCount);
        }
    }
}
