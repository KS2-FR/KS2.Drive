using System;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WebDAVClient.Helpers;
using DavItem = WebDAVClient.Model.Item;

namespace KS2Drive.Tests.WebDAVClientTests
{
    /// <summary>
    /// WebDAVClient.Helpers.WebDAVException.
    /// </summary>
    [TestClass]
    public class WebDavExceptionTests
    {
        private static T RoundTrip<T>(T Ex) where T : Exception
        {
            var Formatter = new BinaryFormatter();
            using (var Stream = new MemoryStream())
            {
                Formatter.Serialize(Stream, Ex);
                Stream.Position = 0;
                return (T)Formatter.Deserialize(Stream);
            }
        }

        [TestMethod]
        public void IsAnHttpException()
        {
            Assert.IsInstanceOfType(new WebDAVException(), typeof(HttpException));
        }

        [TestMethod]
        public void DefaultConstructor_HasCode500()
        {
            Assert.AreEqual(500, new WebDAVException().GetHttpCode());
        }

        [TestMethod]
        public void MessageConstructor()
        {
            var Ex = new WebDAVException("Boom");
            Assert.AreEqual("Boom", Ex.Message);
            Assert.AreEqual(500, Ex.GetHttpCode());
        }

        [TestMethod]
        public void MessageAndHResultConstructor()
        {
            var Ex = new WebDAVException("Boom", unchecked((int)0x80070020));
            Assert.AreEqual("Boom", Ex.Message);
            Assert.AreEqual(unchecked((int)0x80070020), Ex.ErrorCode);
        }

        [TestMethod]
        public void MessageAndInnerConstructor()
        {
            var Inner = new IOException("inner");
            var Ex = new WebDAVException("Boom", Inner);
            Assert.AreEqual("Boom", Ex.Message);
            Assert.AreSame(Inner, Ex.InnerException);
        }

        [DataTestMethod]
        [DataRow(401)]
        [DataRow(404)]
        [DataRow(409)]
        [DataRow(507)]
        public void CodeAndMessageConstructor(int Code)
        {
            var Ex = new WebDAVException(Code, "Failed");
            Assert.AreEqual(Code, Ex.GetHttpCode());
            Assert.AreEqual("Failed", Ex.Message);
        }

        [TestMethod]
        public void CodeMessageAndInnerConstructor()
        {
            var Inner = new IOException("inner");
            var Ex = new WebDAVException(423, "Locked", Inner);
            Assert.AreEqual(423, Ex.GetHttpCode());
            Assert.AreEqual("Locked", Ex.Message);
            Assert.AreSame(Inner, Ex.InnerException);
        }

        [TestMethod]
        public void CodeMessageAndHResultConstructor()
        {
            var Ex = new WebDAVException(403, "Forbidden", unchecked((int)0x80070005));
            Assert.AreEqual(403, Ex.GetHttpCode());
            Assert.AreEqual("Forbidden", Ex.Message);
            Assert.AreEqual(unchecked((int)0x80070005), Ex.ErrorCode);
        }

        [TestMethod]
        public void ToString_ContainsStatusCodeErrorCodeMessageAndStack()
        {
            var Ex = new WebDAVException(404, "Not there", 1234);
            String Text = Ex.ToString();
            StringAssert.StartsWith(Text, "HttpStatusCode: 404");
            StringAssert.Contains(Text, "ErrorCode: 1234");
            StringAssert.Contains(Text, "Message: Not there");
            StringAssert.Contains(Text, typeof(WebDAVException).FullName);
        }

        [TestMethod]
        public void ToString_IncludesInnerException()
        {
            var Ex = new WebDAVException(500, "Outer", new InvalidOperationException("the inner cause"));
            StringAssert.Contains(Ex.ToString(), "the inner cause");
        }

        [TestMethod]
        public void IsBinarySerializable()
        {
            // The class declares a serialization constructor, so it is expected to be serializable like HttpException
            var Copy = RoundTrip(new WebDAVException(404, "Not there"));
            Assert.AreEqual("Not there", Copy.Message);
            Assert.AreEqual(404, Copy.GetHttpCode());
        }
    }

    /// <summary>
    /// WebDAVClient.Helpers.WebDAVConflictException.
    /// </summary>
    [TestClass]
    public class WebDavConflictExceptionTests
    {
        [TestMethod]
        public void IsAWebDavException()
        {
            WebDAVException Ex = new WebDAVConflictException();
            Assert.IsInstanceOfType(Ex, typeof(WebDAVConflictException));
            Assert.AreEqual(500, Ex.GetHttpCode());
        }

        [TestMethod]
        public void CanBeCaughtAsWebDavException()
        {
            try
            {
                throw new WebDAVConflictException(409, "Conflict");
            }
            catch (WebDAVException ex)
            {
                Assert.AreEqual(409, ex.GetHttpCode());
                Assert.AreEqual("Conflict", ex.Message);
            }
        }

        [TestMethod]
        public void AllConstructors_KeepTheirArguments()
        {
            var Inner = new IOException("inner");

            Assert.AreEqual("m", new WebDAVConflictException("m").Message);

            var WithHr = new WebDAVConflictException("m", 77);
            Assert.AreEqual(77, WithHr.ErrorCode);

            Assert.AreSame(Inner, new WebDAVConflictException("m", Inner).InnerException);

            var WithCodeInner = new WebDAVConflictException(409, "m", Inner);
            Assert.AreEqual(409, WithCodeInner.GetHttpCode());
            Assert.AreSame(Inner, WithCodeInner.InnerException);

            var WithCode = new WebDAVConflictException(409, "m");
            Assert.AreEqual(409, WithCode.GetHttpCode());
            Assert.AreEqual("m", WithCode.Message);

            var WithCodeHr = new WebDAVConflictException(409, "m", 88);
            Assert.AreEqual(409, WithCodeHr.GetHttpCode());
            Assert.AreEqual(88, WithCodeHr.ErrorCode);
        }

        [TestMethod]
        public void ToString_UsesWebDavFormat()
        {
            StringAssert.StartsWith(new WebDAVConflictException(409, "Conflict").ToString(), "HttpStatusCode: 409");
        }

        [TestMethod]
        public void IsBinarySerializable()
        {
            var Formatter = new BinaryFormatter();
            using (var Stream = new MemoryStream())
            {
                Formatter.Serialize(Stream, new WebDAVConflictException(409, "Conflict"));
                Stream.Position = 0;
                var Copy = (WebDAVConflictException)Formatter.Deserialize(Stream);
                Assert.AreEqual("Conflict", Copy.Message);
                Assert.AreEqual(409, Copy.GetHttpCode());
            }
        }
    }

    /// <summary>
    /// WebDAVClient.Model.Item.
    /// </summary>
    [TestClass]
    public class ItemTests
    {
        [TestMethod]
        public void NewItem_HasEmptyDefaults()
        {
            var I = new DavItem();
            Assert.IsNull(I.Href);
            Assert.IsNull(I.CreationDate);
            Assert.IsNull(I.Etag);
            Assert.IsFalse(I.IsHidden);
            Assert.IsFalse(I.IsCollection);
            Assert.IsNull(I.ContentType);
            Assert.IsNull(I.LastModified);
            Assert.IsNull(I.DisplayName);
            Assert.IsNull(I.ContentLength);
        }

        [TestMethod]
        public void Properties_RoundTrip()
        {
            var Created = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var Modified = new DateTime(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            var I = new DavItem
            {
                Href = "/dav/a.txt",
                CreationDate = Created,
                Etag = "\"e\"",
                IsHidden = true,
                IsCollection = true,
                ContentType = "text/plain",
                LastModified = Modified,
                DisplayName = "a.txt",
                ContentLength = 12
            };
            Assert.AreEqual("/dav/a.txt", I.Href);
            Assert.AreEqual(Created, I.CreationDate);
            Assert.AreEqual("\"e\"", I.Etag);
            Assert.IsTrue(I.IsHidden);
            Assert.IsTrue(I.IsCollection);
            Assert.AreEqual("text/plain", I.ContentType);
            Assert.AreEqual(Modified, I.LastModified);
            Assert.AreEqual("a.txt", I.DisplayName);
            Assert.AreEqual(12L, I.ContentLength);
        }
    }
}
