using KS2Drive.WebDav;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;

namespace KS2Drive.Tests.WebDav
{
    [TestClass]
    public class WebDavExceptionTests
    {
        [TestMethod]
        public void Constructor_SetsStatusCodeAndMessage()
        {
            var ex = new WebDavException(409, "MKCOL failed");

            Assert.AreEqual(409, ex.StatusCode);
            Assert.AreEqual("MKCOL failed", ex.Message);
            Assert.IsNull(ex.InnerException);
        }

        [TestMethod]
        public void WebDavException_IsNotAnHttpRequestException()
        {
            //Transport errors (HttpRequestException) and HTTP statuses (WebDavException) are handled separately by callers
            Assert.IsFalse(typeof(HttpRequestException).IsAssignableFrom(typeof(WebDavException)));
            Assert.IsTrue(typeof(Exception).IsAssignableFrom(typeof(WebDavException)));
        }

        [TestMethod]
        public void ToString_StartsWithStatusAndContainsMessageAndType()
        {
            String Text = new WebDavException(412, "PUT failed: precondition").ToString();

            Assert.IsTrue(Text.StartsWith("HTTP 412: "), Text);
            StringAssert.Contains(Text, "PUT failed: precondition");
            StringAssert.Contains(Text, typeof(WebDavException).FullName);
        }

        [TestMethod]
        public void ToString_ThrownException_ContainsStackTrace()
        {
            WebDavException Caught = null;
            try
            {
                throw new WebDavException(500, "boom");
            }
            catch (WebDavException ex)
            {
                Caught = ex;
            }

            String Text = Caught.ToString();
            Assert.IsTrue(Text.StartsWith("HTTP 500: "), Text);
            StringAssert.Contains(Text, nameof(ToString_ThrownException_ContainsStackTrace));
        }

        [TestMethod]
        public void BinarySerialization_RoundTrip_KeepsStatusCodeAndMessage()
        {
            var Original = new WebDavException(423, "DELETE /dav/a.txt failed: 423 Locked");
            WebDavException Copy;
            var Formatter = new BinaryFormatter();
            using (var Stream = new MemoryStream())
            {
                Formatter.Serialize(Stream, Original);
                Stream.Position = 0;
                Copy = (WebDavException)Formatter.Deserialize(Stream);
            }

            Assert.AreNotSame(Original, Copy);
            Assert.AreEqual(423, Copy.StatusCode);
            Assert.AreEqual(Original.Message, Copy.Message);
            Assert.IsTrue(Copy.ToString().StartsWith("HTTP 423: "));
        }

        [TestMethod]
        public async Task ThrownByClient_MessageNamesOperationUrlAndStatus()
        {
            var Recorder = new RecordingHandler { Respond = Request => DavResponses.Status(404) };
            var Client = TestClients.For(Recorder);

            var ex = await AsyncAssert.ThrowsWebDavAsync(404, () => Client.Get("/dav/missing-file.txt"));

            StringAssert.Contains(ex.Message, "GET");
            StringAssert.Contains(ex.Message, "missing-file.txt");
            StringAssert.Contains(ex.Message, "404");
        }
    }

    [TestClass]
    public class WebDavItemTests
    {
        [TestMethod]
        public void NewItem_HasEmptyDefaults()
        {
            var Item = new WebDavItem();

            Assert.IsNull(Item.Path);
            Assert.IsNull(Item.DisplayName);
            Assert.IsFalse(Item.IsCollection);
            Assert.IsFalse(Item.IsHidden);
            Assert.IsNull(Item.ContentLength);
            Assert.IsNull(Item.ContentType);
            Assert.IsNull(Item.ETag);
            Assert.IsNull(Item.CreationDate);
            Assert.IsNull(Item.LastModified);
        }

        [TestMethod]
        public void Properties_KeepTheirValues()
        {
            var Created = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var Modified = new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc);
            var Item = new WebDavItem
            {
                Path = "/dav/My folder/C++ notes.txt",
                DisplayName = "C++ notes.txt",
                IsCollection = true,
                IsHidden = true,
                ContentLength = 5000000000L,
                ContentType = "text/plain",
                ETag = "\"abc\"",
                CreationDate = Created,
                LastModified = Modified
            };

            Assert.AreEqual("/dav/My folder/C++ notes.txt", Item.Path);
            Assert.AreEqual("C++ notes.txt", Item.DisplayName);
            Assert.IsTrue(Item.IsCollection);
            Assert.IsTrue(Item.IsHidden);
            Assert.AreEqual(5000000000L, Item.ContentLength);
            Assert.AreEqual("text/plain", Item.ContentType);
            Assert.AreEqual("\"abc\"", Item.ETag);
            Assert.AreEqual(Created, Item.CreationDate);
            Assert.AreEqual(Modified, Item.LastModified);
        }
    }
}
