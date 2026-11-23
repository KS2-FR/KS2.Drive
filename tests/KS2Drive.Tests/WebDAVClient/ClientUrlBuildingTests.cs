using System;
using System.IO;
using System.Linq;
using System.Text;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using DavClient = WebDAVClient.Client;

namespace KS2Drive.Tests.WebDAVClientTests
{
    /// <summary>
    /// Server / BasePath / Port / UserAgent properties and the URL built by GetServerUrl, observed through the requests sent.
    /// </summary>
    [TestClass]
    public class ClientUrlBuildingTests
    {
        #region Properties

        [TestMethod]
        public void Server_TrailingSlashesAreTrimmed()
        {
            using (var F = new DavFixture())
            {
                F.Client.Server = "http://fake.local//";
                Assert.AreEqual("http://fake.local", F.Client.Server);
            }
        }

        [TestMethod]
        public void Server_WithoutTrailingSlashIsKept()
        {
            using (var F = new DavFixture())
            {
                F.Client.Server = "https://host:8443";
                Assert.AreEqual("https://host:8443", F.Client.Server);
            }
        }

        [TestMethod]
        public void BasePath_DefaultIsRoot()
        {
            var C = new DavClient();
            Assert.AreEqual("/", C.BasePath);
            Assert.IsNull(C.Server);
            Assert.IsNull(C.Port);
            Assert.IsNull(C.UserAgent);
            Assert.IsNull(C.UserAgentVersion);
        }

        [DataTestMethod]
        [DataRow("dav", "/dav/")]
        [DataRow("/dav", "/dav/")]
        [DataRow("/dav/", "/dav/")]
        [DataRow("//dav//", "/dav/")]
        [DataRow("/a/b", "/a/b/")]
        [DataRow("", "/")]
        [DataRow("/", "/")]
        [DataRow("///", "/")]
        public void BasePath_IsNormalizedWithLeadingAndTrailingSlash(String Input, String Expected)
        {
            var C = new DavClient();
            C.BasePath = Input;
            Assert.AreEqual(Expected, C.BasePath);
        }

        [TestMethod]
        public void PortAndUserAgentProperties_RoundTrip()
        {
            var C = new DavClient { Port = 8080, UserAgent = "Agent", UserAgentVersion = "2.0" };
            Assert.AreEqual(8080, C.Port);
            Assert.AreEqual("Agent", C.UserAgent);
            Assert.AreEqual("2.0", C.UserAgentVersion);
        }

        #endregion

        #region Headers common to every request

        [TestMethod]
        public void Requests_UseDefaultUserAgentWithLibraryVersion()
        {
            using (var F = new DavFixture())
            {
                DavAssert.Run(F.Client.GetFolder("/"));
                String Version = typeof(global::WebDAVClient.IClient).Assembly.GetName().Version.ToString();
                Assert.AreEqual("WebDAVClient/" + Version, F.Recorder.Last.Header("User-Agent"));
                Assert.AreEqual("Keep-Alive", F.Recorder.Last.Header("Connection"));
            }
        }

        [TestMethod]
        public void Requests_UseCustomUserAgent()
        {
            using (var F = new DavFixture())
            {
                F.Client.UserAgent = "KS2Drive";
                F.Client.UserAgentVersion = "3.1";
                DavAssert.Run(F.Client.GetFolder("/"));
                F.Server.AddFile("/dav/a.txt", "x");
                DavAssert.Run(F.Client.Upload("/", new MemoryStream(new byte[] { 1 }), "b.txt"));
                Assert.AreEqual(2, F.Recorder.Requests.Count);
                Assert.IsTrue(F.Recorder.Requests.All(r => r.Header("User-Agent") == "KS2Drive/3.1"));
            }
        }

        #endregion

        #region URL building

        [DataTestMethod]
        [DataRow("/", "http://fake.local/dav/")]
        [DataRow("/sub", "http://fake.local/dav/sub/")]
        [DataRow("/sub/", "http://fake.local/dav/sub/")]
        [DataRow("sub", "http://fake.local/dav/sub/")]
        [DataRow("/dav/sub", "http://fake.local/dav/sub/")]
        [DataRow("/dav/sub/", "http://fake.local/dav/sub/")]
        [DataRow("/dav/", "http://fake.local/dav/")]
        [DataRow("http://fake.local/dav/sub", "http://fake.local/dav/sub/")]
        [DataRow("/sub/deep", "http://fake.local/dav/sub/deep/")]
        public void FolderUrl_IsBuiltFromServerBasePathAndPath(String Path, String ExpectedUrl)
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/sub");
                F.Server.AddFolder("/dav/sub/deep");
                DavAssert.Run(F.Client.GetFolder(Path));
                Assert.AreEqual(ExpectedUrl, F.Recorder.Last.AbsoluteUri);
            }
        }

        [DataTestMethod]
        [DataRow("/a.txt", "http://fake.local/dav/a.txt")]
        [DataRow("a.txt", "http://fake.local/dav/a.txt")]
        [DataRow("/dav/a.txt", "http://fake.local/dav/a.txt")]
        [DataRow("http://fake.local/dav/a.txt", "http://fake.local/dav/a.txt")]
        [DataRow("/sub/a.txt", "http://fake.local/dav/sub/a.txt")]
        [DataRow("/dav/sub/a.txt", "http://fake.local/dav/sub/a.txt")]
        public void FileUrl_IsBuiltWithoutTrailingSlash(String Path, String ExpectedUrl)
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/sub");
                F.Server.AddFile("/dav/a.txt", "a");
                F.Server.AddFile("/dav/sub/a.txt", "a");
                DavAssert.Run(F.Client.GetFile(Path));
                Assert.AreEqual(ExpectedUrl, F.Recorder.Last.AbsoluteUri);
            }
        }

        [TestMethod]
        public void ServerWithTrailingSlash_DoesNotProduceDoubleSlash()
        {
            using (var F = new DavFixture())
            {
                F.Client.Server = "http://fake.local/";
                F.Server.AddFile("/dav/a.txt", "a");
                DavAssert.Run(F.Client.GetFile("/a.txt"));
                Assert.AreEqual("http://fake.local/dav/a.txt", F.Recorder.Last.AbsoluteUri);
            }
        }

        [TestMethod]
        public void PathContainingTheBasePathTwice_KeepsTheInnerSegment()
        {
            // "/dav/a/dav/b.txt" is the file b.txt in the folder /dav/a/dav: only the leading base path must be stripped
            using (var F = new DavFixture())
            {
                F.Server.AddFolder("/dav/a");
                F.Server.AddFolder("/dav/a/dav");
                F.Server.AddFile("/dav/a/dav/b.txt", "b");
                var Item = DavAssert.Run(F.Client.GetFile("/dav/a/dav/b.txt"));
                Assert.AreEqual("http://fake.local/dav/a/dav/b.txt", F.Recorder.Last.AbsoluteUri);
                Assert.AreEqual("b.txt", Item.DisplayName);
            }
        }

        [TestMethod]
        public void RootBasePath_NestedFilePathKeepsItsSlashes()
        {
            using (var F = new DavFixture())
            {
                F.Client.BasePath = "/";
                F.Server.AddFolder("/sub");
                F.Server.AddFile("/sub/a.txt", "a");
                var Item = DavAssert.Run(F.Client.GetFile("/sub/a.txt"));
                Assert.AreEqual("http://fake.local/sub/a.txt", F.Recorder.Last.AbsoluteUri);
                Assert.AreEqual("a.txt", Item.DisplayName);
            }
        }

        [TestMethod]
        public void RootBasePath_FileAtRoot()
        {
            using (var F = new DavFixture())
            {
                F.Client.BasePath = "/";
                F.Server.AddFile("/a.txt", "a");
                DavAssert.Run(F.Client.GetFile("/a.txt"));
                Assert.AreEqual("http://fake.local/a.txt", F.Recorder.Last.AbsoluteUri);
            }
        }

        [TestMethod]
        public void MultiSegmentBasePath_IsPrepended()
        {
            using (var F = new DavFixture())
            {
                F.Client.BasePath = "alfresco/aos";
                F.Server.AddFolder("/alfresco");
                F.Server.AddFolder("/alfresco/aos");
                F.Server.AddFile("/alfresco/aos/a.txt", "a");
                DavAssert.Run(F.Client.GetFile("/a.txt"));
                Assert.AreEqual("http://fake.local/alfresco/aos/a.txt", F.Recorder.Last.AbsoluteUri);
                DavAssert.Run(F.Client.GetFile("/alfresco/aos/a.txt"));
                Assert.AreEqual("http://fake.local/alfresco/aos/a.txt", F.Recorder.Last.AbsoluteUri);
            }
        }

        [TestMethod]
        public void DecodedNamesWithSpacesAndAccents_AreEncodedOnTheWire()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/été 2020.txt", "x");
                var Item = DavAssert.Run(F.Client.GetFile("/été 2020.txt"));
                Assert.AreEqual("http://fake.local/dav/%C3%A9t%C3%A9%202020.txt", F.Recorder.Last.AbsoluteUri);
                Assert.AreEqual("été 2020.txt", Item.DisplayName);
            }
        }

        [TestMethod]
        public void PlusSign_IsSentLiterally()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/c++.txt", "x");
                DavAssert.Run(F.Client.GetFile("/c++.txt"));
                Assert.AreEqual("http://fake.local/dav/c++.txt", F.Recorder.Last.AbsoluteUri);
                Assert.AreEqual("PROPFIND /dav/c++.txt", F.Server.Requests.Last());
            }
        }

        [TestMethod]
        public void AlreadyEncodedHref_IsNotEncodedTwice()
        {
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a b#1.txt", "x");
                var Item = DavAssert.Run(F.Client.GetFile("/dav/a%20b%231.txt"));
                Assert.AreEqual("http://fake.local/dav/a%20b%231.txt", F.Recorder.Last.AbsoluteUri);
                Assert.AreEqual("PROPFIND /dav/a b#1.txt", F.Server.Requests.Last());
                Assert.AreEqual("a b#1.txt", Item.DisplayName);
            }
        }

        [TestMethod]
        public void DecodedNameWithHash_IsNotTruncatedAsAFragment()
        {
            // A '#' in a decoded name must be sent as %23, not interpreted as the start of a URL fragment
            using (var F = new DavFixture())
            {
                F.Server.AddFile("/dav/a#b.txt", "x");
                DavAssert.Run(F.Client.Upload("/", new MemoryStream(Encoding.UTF8.GetBytes("new")), "c#d.txt"));
                Assert.AreEqual("PUT /dav/c#d.txt", F.Server.Requests.Last());
                Assert.AreEqual("new", F.Server.GetText("/dav/c#d.txt"));
            }
        }

        #endregion
    }
}
