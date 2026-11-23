using KS2Drive.FS;
using KS2Drive.Log;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using Item = WebDAVClient.Model.Item;

namespace KS2Drive.Tests.Core
{
    [TestClass]
    public class EnumTests
    {
        [TestMethod]
        public void FlushMode_Values()
        {
            Assert.AreEqual(0, (int)FlushMode.FlushAtWrite);
            Assert.AreEqual(1, (int)FlushMode.FlushAtCleanup);
            Assert.AreEqual(2, Enum.GetValues(typeof(FlushMode)).Length);
        }

        [TestMethod]
        public void WebDAVMode_Values()
        {
            Assert.AreEqual(0, (int)WebDAVMode.WebDAV);
            Assert.AreEqual(1, (int)WebDAVMode.AOS);
            Assert.AreEqual(2, Enum.GetValues(typeof(WebDAVMode)).Length);
        }

        [TestMethod]
        public void KernelCacheMode_Values()
        {
            Assert.AreEqual(-1, (int)KernelCacheMode.DataAndMetaData);
            Assert.AreEqual(UInt32.MaxValue, unchecked((uint)(int)KernelCacheMode.DataAndMetaData), "-1 must map to an infinite WinFSP FileInfoTimeout");
            Assert.AreEqual(0, (int)KernelCacheMode.Disabled);
            Assert.AreEqual(1000, (int)KernelCacheMode.MetaDataOnly);
            Assert.AreEqual(3, Enum.GetValues(typeof(KernelCacheMode)).Length);
        }

        [TestMethod]
        public void CacheMode_Values()
        {
            Assert.AreEqual(0, (int)CacheMode.Enabled);
            Assert.AreEqual(1, (int)CacheMode.Disabled);
            Assert.AreEqual(2, Enum.GetValues(typeof(CacheMode)).Length);
        }

        [TestMethod]
        public void ConfigurationDefaults_MatchEnums()
        {
            var C = new KS2Drive.Config.Configuration();
            Assert.AreEqual(KernelCacheMode.DataAndMetaData, (KernelCacheMode)C.KernelCacheMode);
            Assert.AreEqual(FlushMode.FlushAtCleanup, (FlushMode)C.FlushMode);
            Assert.AreEqual(WebDAVMode.WebDAV, (WebDAVMode)C.ServerType);
        }
    }

    [TestClass]
    public class DirectoryEnumeratorContextTests
    {
        [TestMethod]
        public void Defaults()
        {
            var Ctx = new DirectoryEnumeratorContext();
            Assert.IsNull(Ctx.Enumerator);
            Assert.AreEqual(Guid.Empty, Ctx.OperationId);
        }

        [TestMethod]
        public void Properties_RoundTrip()
        {
            FileNode.Init("/dav", WebDAVMode.WebDAV);
            var Node = new FileNode(new Item { Href = "/dav/a.txt", ContentLength = 1, Etag = "e" });
            var Entries = new List<Tuple<string, FileNode>> { Tuple.Create(".", Node), Tuple.Create("a.txt", Node) };
            Guid Id = Guid.NewGuid();

            var Ctx = new DirectoryEnumeratorContext { Enumerator = Entries.GetEnumerator(), OperationId = Id };
            Assert.AreEqual(Id, Ctx.OperationId);

            Assert.IsTrue(Ctx.Enumerator.MoveNext());
            Assert.AreEqual(".", Ctx.Enumerator.Current.Item1);
            Assert.IsTrue(Ctx.Enumerator.MoveNext());
            Assert.AreEqual("a.txt", Ctx.Enumerator.Current.Item1);
            Assert.AreSame(Node, Ctx.Enumerator.Current.Item2);
            Assert.IsFalse(Ctx.Enumerator.MoveNext());
        }

        [TestMethod]
        public void Enumerator_KeepsPositionBetweenAccesses()
        {
            var Ctx = new DirectoryEnumeratorContext { Enumerator = ((IEnumerable<Tuple<string, FileNode>>)new[] { Tuple.Create<string, FileNode>("x", null), Tuple.Create<string, FileNode>("y", null) }).GetEnumerator() };
            Ctx.Enumerator.MoveNext();
            Assert.AreEqual("x", Ctx.Enumerator.Current.Item1);
            Ctx.Enumerator.MoveNext();
            Assert.AreEqual("y", Ctx.Enumerator.Current.Item1, "The same enumerator instance must be returned on each access");
        }
    }

    [TestClass]
    public class LogListItemTests
    {
        [TestMethod]
        public void Defaults()
        {
            var L = new LogListItem();
            Assert.IsNull(L.Date);
            Assert.IsNull(L.Object);
            Assert.IsNull(L.Method);
            Assert.IsNull(L.File);
            Assert.IsNull(L.Result);
            Assert.IsNull(L.LocalTemporaryPath);
            Assert.IsFalse(L.AllowRetryOrRecover);
        }

        [TestMethod]
        public void Properties_RoundTrip()
        {
            var L = new LogListItem
            {
                Date = "2026-11-23 10:00:00",
                Object = "File",
                Method = "Write",
                File = "\\folder\\caf\u00e9.txt",
                Result = "STATUS_SUCCESS",
                LocalTemporaryPath = "C:\\Temp\\tmp1.tmp"
            };
            Assert.AreEqual("2026-11-23 10:00:00", L.Date);
            Assert.AreEqual("File", L.Object);
            Assert.AreEqual("Write", L.Method);
            Assert.AreEqual("\\folder\\caf\u00e9.txt", L.File);
            Assert.AreEqual("STATUS_SUCCESS", L.Result);
            Assert.AreEqual("C:\\Temp\\tmp1.tmp", L.LocalTemporaryPath);
        }

        [DataTestMethod]
        [DataRow(null, false)]
        [DataRow("", false)]
        [DataRow(" ", true)]
        [DataRow("C:\\Temp\\copy.tmp", true)]
        public void AllowRetryOrRecover_DependsOnLocalTemporaryPath(string LocalTemporaryPath, bool Expected)
        {
            var L = new LogListItem { LocalTemporaryPath = LocalTemporaryPath, Result = "STATUS_UNEXPECTED_IO_ERROR" };
            Assert.AreEqual(Expected, L.AllowRetryOrRecover);
        }

        [TestMethod]
        public void AllowRetryOrRecover_FollowsChanges()
        {
            var L = new LogListItem { LocalTemporaryPath = "C:\\Temp\\copy.tmp" };
            Assert.IsTrue(L.AllowRetryOrRecover);
            L.LocalTemporaryPath = "";
            Assert.IsFalse(L.AllowRetryOrRecover);
            L.LocalTemporaryPath = null;
            Assert.IsFalse(L.AllowRetryOrRecover);
        }
    }
}
