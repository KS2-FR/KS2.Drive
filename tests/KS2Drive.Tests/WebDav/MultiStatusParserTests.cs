using KS2Drive.WebDav;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;

namespace KS2Drive.Tests.WebDav
{
    [TestClass]
    public class MultiStatusParserTests
    {
        #region Parse: responses

        [TestMethod]
        public void Parse_MultipleResponses_ReturnsOneItemPerResponseInOrder()
        {
            var Items = Parse(@"<?xml version=""1.0"" encoding=""utf-8""?>
<D:multistatus xmlns:D=""DAV:"">
  <D:response>
    <D:href>/dav/My%20folder/</D:href>
    <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>
  </D:response>
  <D:response>
    <D:href>/dav/My%20folder/a.txt</D:href>
    <D:propstat><D:prop><D:resourcetype/><D:getcontentlength>12</D:getcontentlength></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>
  </D:response>
  <D:response>
    <D:href>/dav/My%20folder/sub/</D:href>
    <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>
  </D:response>
</D:multistatus>");

            CollectionAssert.AreEqual(new[] { "/dav/My folder", "/dav/My folder/a.txt", "/dav/My folder/sub" }, Items.Select(x => x.Path).ToArray());
            CollectionAssert.AreEqual(new[] { true, false, true }, Items.Select(x => x.IsCollection).ToArray());
            Assert.AreEqual(12L, Items[1].ContentLength);
            Assert.IsNull(Items[0].ContentLength);
        }

        [TestMethod]
        public void Parse_EmptyMultiStatus_ReturnsEmptyList()
        {
            var Items = Parse("<?xml version=\"1.0\"?><D:multistatus xmlns:D=\"DAV:\"/>");

            Assert.IsNotNull(Items);
            Assert.AreEqual(0, Items.Count);
        }

        [TestMethod]
        public void Parse_OtherPrefix_IsSupported()
        {
            var Items = Parse("<a:multistatus xmlns:a=\"DAV:\"><a:response><a:href>/dav/a.txt</a:href><a:propstat><a:prop><a:displayname>Name</a:displayname><a:getcontentlength>7</a:getcontentlength></a:prop><a:status>HTTP/1.1 200 OK</a:status></a:propstat></a:response></a:multistatus>");

            Assert.AreEqual(1, Items.Count);
            Assert.AreEqual("/dav/a.txt", Items[0].Path);
            Assert.AreEqual("Name", Items[0].DisplayName);
            Assert.AreEqual(7L, Items[0].ContentLength);
        }

        [TestMethod]
        public void Parse_DefaultNamespace_IsSupported()
        {
            var Items = Parse("<multistatus xmlns=\"DAV:\"><response><href>/dav/folder/</href><propstat><prop><resourcetype><collection/></resourcetype></prop><status>HTTP/1.1 200 OK</status></propstat></response></multistatus>");

            Assert.AreEqual(1, Items.Count);
            Assert.AreEqual("/dav/folder", Items[0].Path);
            Assert.IsTrue(Items[0].IsCollection);
        }

        [TestMethod]
        public void Parse_PropertiesInOtherNamespaces_AreRead()
        {
            var Item = ParseSingle("/dav/folder/", "<Z:iscollection>1</Z:iscollection><Z:ishidden>1</Z:ishidden><x:getcontenttype xmlns:x=\"http://example.com/ns\">text/plain</x:getcontenttype>");

            Assert.IsTrue(Item.IsCollection);
            Assert.IsTrue(Item.IsHidden);
            Assert.AreEqual("text/plain", Item.ContentType);
        }

        [TestMethod]
        public void Parse_PropStatWith404_IsIgnored()
        {
            var Items = Parse(@"<D:multistatus xmlns:D=""DAV:""><D:response><D:href>/dav/a.txt</D:href>
<D:propstat><D:prop><D:displayname>Good</D:displayname></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>
<D:propstat><D:prop><D:displayname>Bad</D:displayname><D:getcontentlength>99</D:getcontentlength><D:resourcetype><D:collection/></D:resourcetype><D:getetag>""bad""</D:getetag></D:prop><D:status>HTTP/1.1 404 Not Found</D:status></D:propstat>
</D:response></D:multistatus>");

            Assert.AreEqual(1, Items.Count);
            Assert.AreEqual("Good", Items[0].DisplayName);
            Assert.IsNull(Items[0].ContentLength);
            Assert.IsFalse(Items[0].IsCollection);
            Assert.IsNull(Items[0].ETag);
        }

        [TestMethod]
        public void Parse_PropStatWithoutStatus_IsUsed()
        {
            var Items = Parse("<D:multistatus xmlns:D=\"DAV:\"><D:response><D:href>/dav/a.txt</D:href><D:propstat><D:prop><D:getcontentlength>5</D:getcontentlength></D:prop></D:propstat></D:response></D:multistatus>");

            Assert.AreEqual(5L, Items.Single().ContentLength);
        }

        [TestMethod]
        public void Parse_ResponseWithoutHref_IsSkipped()
        {
            var Items = Parse(@"<D:multistatus xmlns:D=""DAV:"">
<D:response><D:propstat><D:prop><D:displayname>No href</D:displayname></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
<D:response><D:href>  </D:href><D:propstat><D:prop><D:displayname>Blank href</D:displayname></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
<D:response><D:href>/dav/a.txt</D:href><D:propstat><D:prop><D:displayname>Kept</D:displayname></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
</D:multistatus>");

            Assert.AreEqual(1, Items.Count);
            Assert.AreEqual("Kept", Items[0].DisplayName);
        }

        [TestMethod]
        public void Parse_Dtd_IsProhibited()
        {
            String Xml = "<?xml version=\"1.0\"?><!DOCTYPE multistatus [<!ENTITY name \"injected\">]><D:multistatus xmlns:D=\"DAV:\"><D:response><D:href>/dav/a.txt</D:href><D:propstat><D:prop><D:displayname>&name;</D:displayname></D:prop></D:propstat></D:response></D:multistatus>";

            Assert.ThrowsException<XmlException>(() => Parse(Xml));
        }

        [DataTestMethod]
        [DataRow("<D:multistatus xmlns:D=\"DAV:\"><D:response>")]
        [DataRow("")]
        [DataRow("not xml")]
        [DataRow("<D:multistatus><D:response/></D:multistatus>")]
        public void Parse_MalformedXml_ThrowsXmlException(String Xml)
        {
            Assert.ThrowsException<XmlException>(() => Parse(Xml));
        }

        #endregion

        #region Parse: href and display name

        [TestMethod]
        public void Parse_AbsoluteAndRelativeHrefs_GiveDecodedPaths()
        {
            var Items = Parse(@"<D:multistatus xmlns:D=""DAV:"">
<D:response><D:href>https://docs.example.com:8443/dav/My%20folder/</D:href></D:response>
<D:response><D:href>/dav/My%20folder/a%20b.txt</D:href></D:response>
<D:response><D:href> http://docs.example.com/dav/My%20folder/c.txt </D:href></D:response>
</D:multistatus>");

            CollectionAssert.AreEqual(new[] { "/dav/My folder", "/dav/My folder/a b.txt", "/dav/My folder/c.txt" }, Items.Select(x => x.Path).ToArray());
        }

        [TestMethod]
        public void Parse_EncodedPercent_IsDecodedOnce()
        {
            var Item = ParseSingle("/dav/a%2520b.txt", "");

            Assert.AreEqual("/dav/a%20b.txt", Item.Path);
            Assert.AreEqual("a%20b.txt", Item.DisplayName);
        }

        [TestMethod]
        public void Parse_Plus_IsKept()
        {
            var Item = ParseSingle("/dav/C++%20notes.txt", "");

            Assert.AreEqual("/dav/C++ notes.txt", Item.Path);
            Assert.AreEqual("C++ notes.txt", Item.DisplayName);
        }

        [DataTestMethod]
        [DataRow("/dav/C#%20notes%20#1.txt")]
        [DataRow("http://fake.local/dav/C#%20notes%20#1.txt")]
        [DataRow("/dav/C%23%20notes%20%231.txt")]
        public void Parse_Hash_IsPartOfThePath(String Href)
        {
            var Item = ParseSingle(Href, "");

            Assert.AreEqual("/dav/C# notes #1.txt", Item.Path);
            Assert.AreEqual("C# notes #1.txt", Item.DisplayName);
        }

        [TestMethod]
        public void Parse_Root_KeepsSingleSlash()
        {
            Assert.AreEqual("/", ParseSingle("/", "").Path);
        }

        [TestMethod]
        public void Parse_TrailingSlash_IsRemoved()
        {
            Assert.AreEqual("/dav/folder", ParseSingle("http://fake.local/dav/folder/", "<D:resourcetype><D:collection/></D:resourcetype>").Path);
        }

        [TestMethod]
        public void Parse_DisplayName_IsUsedWhenPresent()
        {
            var Item = ParseSingle("/dav/a.txt", "<D:displayname>Pretty name</D:displayname>");

            Assert.AreEqual("Pretty name", Item.DisplayName);
            Assert.AreEqual("/dav/a.txt", Item.Path);
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow("<D:displayname/>")]
        [DataRow("<D:displayname>   </D:displayname>")]
        public void Parse_NoDisplayName_FallsBackToDecodedLastSegment(String Properties)
        {
            Assert.AreEqual("My folder", ParseSingle("/dav/My%20folder/", Properties).DisplayName);
            Assert.AreEqual("\u00e9t\u00e9.txt", ParseSingle("/dav/%C3%A9t%C3%A9.txt", Properties).DisplayName);
        }

        #endregion

        #region Parse: properties

        [DataTestMethod]
        [DataRow("0", 0L)]
        [DataRow("12345", 12345L)]
        [DataRow(" 5000000000 ", 5000000000L)]
        public void Parse_GetContentLength_IsRead(String Value, long Expected)
        {
            Assert.AreEqual(Expected, ParseSingle("/dav/a.txt", $"<D:getcontentlength>{Value}</D:getcontentlength>").ContentLength);
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow("<D:getcontentlength/>")]
        [DataRow("<D:getcontentlength>abc</D:getcontentlength>")]
        public void Parse_GetContentLengthMissingOrInvalid_IsNull(String Properties)
        {
            Assert.IsNull(ParseSingle("/dav/a.txt", Properties).ContentLength);
        }

        [TestMethod]
        public void Parse_GetETagAndContentType_AreRead()
        {
            var Item = ParseSingle("/dav/a.txt", "<D:getetag>\"abc-123\"</D:getetag><D:getcontenttype>text/plain; charset=utf-8</D:getcontenttype>");

            Assert.AreEqual("\"abc-123\"", Item.ETag);
            Assert.AreEqual("text/plain; charset=utf-8", Item.ContentType);
        }

        [TestMethod]
        public void Parse_NoProperties_LeavesDefaults()
        {
            var Item = ParseSingle("/dav/a.txt", "");

            Assert.IsNull(Item.ETag);
            Assert.IsNull(Item.ContentType);
            Assert.IsNull(Item.ContentLength);
            Assert.IsNull(Item.CreationDate);
            Assert.IsNull(Item.LastModified);
            Assert.IsFalse(Item.IsCollection);
            Assert.IsFalse(Item.IsHidden);
        }

        [DataTestMethod]
        [DataRow("en-US")]
        [DataRow("fr-FR")]
        [DataRow("th-TH")]
        [DataRow("ar-SA")]
        public void Parse_Dates_AreParsedToUtcWhateverTheCulture(String Culture)
        {
            WithCulture(Culture, () =>
            {
                var Item = ParseSingle("/dav/a.txt", "<D:creationdate>2020-01-02T03:04:05Z</D:creationdate><D:getlastmodified>Mon, 07 Jun 2021 08:09:10 GMT</D:getlastmodified>");

                Assert.AreEqual(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), Item.CreationDate);
                Assert.AreEqual(DateTimeKind.Utc, Item.CreationDate.Value.Kind);
                Assert.AreEqual(new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc), Item.LastModified);
                Assert.AreEqual(DateTimeKind.Utc, Item.LastModified.Value.Kind);
            });
        }

        [DataTestMethod]
        [DataRow("2020-01-02T05:04:05+02:00")]
        [DataRow("2020-01-01T22:04:05-05:00")]
        [DataRow("2020-01-02T03:04:05.000Z")]
        [DataRow("2020-01-02T03:04:05")]
        public void Parse_CreationDateWithOffset_IsConvertedToUtc(String Value)
        {
            WithCulture("fr-FR", () =>
            {
                var Item = ParseSingle("/dav/a.txt", $"<D:creationdate>{Value}</D:creationdate>");

                Assert.AreEqual(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), Item.CreationDate);
                Assert.AreEqual(DateTimeKind.Utc, Item.CreationDate.Value.Kind);
            });
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow("not a date")]
        [DataRow("32/13/2020")]
        public void Parse_InvalidDates_AreNull(String Value)
        {
            var Item = ParseSingle("/dav/a.txt", $"<D:creationdate>{Value}</D:creationdate><D:getlastmodified>{Value}</D:getlastmodified>");

            Assert.IsNull(Item.CreationDate);
            Assert.IsNull(Item.LastModified);
        }

        [TestMethod]
        public void Parse_ResourceTypeCollection_IsCollection()
        {
            Assert.IsTrue(ParseSingle("/dav/folder/", "<D:resourcetype><D:collection/></D:resourcetype>").IsCollection);
            Assert.IsFalse(ParseSingle("/dav/a.txt", "<D:resourcetype/>").IsCollection);
            Assert.IsFalse(ParseSingle("/dav/a.txt", "<D:resourcetype><D:other/></D:resourcetype>").IsCollection);
        }

        [DataTestMethod]
        [DataRow("1", true)]
        [DataRow("true", true)]
        [DataRow("TRUE", true)]
        [DataRow("0", false)]
        [DataRow("false", false)]
        [DataRow("", false)]
        public void Parse_IsCollection_IsRead(String Value, bool Expected)
        {
            Assert.AreEqual(Expected, ParseSingle("/dav/x", $"<Z:iscollection>{Value}</Z:iscollection>").IsCollection);
        }

        [DataTestMethod]
        [DataRow("hidden", "1", true)]
        [DataRow("hidden", "true", true)]
        [DataRow("hidden", "0", false)]
        [DataRow("hidden", "false", false)]
        [DataRow("hidden", "FALSE", false)]
        [DataRow("ishidden", "1", true)]
        [DataRow("ishidden", "true", true)]
        [DataRow("ishidden", "0", false)]
        [DataRow("ishidden", "false", false)]
        public void Parse_Hidden_IsRead(String Property, String Value, bool Expected)
        {
            Assert.AreEqual(Expected, ParseSingle("/dav/x", $"<Z:{Property}>{Value}</Z:{Property}>").IsHidden);
            Assert.AreEqual(Expected, ParseSingle("/dav/x", $"<D:{Property}>{Value}</D:{Property}>").IsHidden);
        }

        [DataTestMethod]
        [DataRow("hidden")]
        [DataRow("ishidden")]
        public void Parse_EmptyHiddenElement_IsHidden(String Property)
        {
            Assert.IsTrue(ParseSingle("/dav/x", $"<Z:{Property}/>").IsHidden);
            Assert.IsTrue(ParseSingle("/dav/x", $"<Z:{Property}></Z:{Property}>").IsHidden);
        }

        [TestMethod]
        public void Parse_NoHiddenElement_IsVisible()
        {
            Assert.IsFalse(ParseSingle("/dav/x", "<D:displayname>x</D:displayname>").IsHidden);
        }

        #endregion

        #region DecodeHref

        [DataTestMethod]
        [DataRow("/dav/My%20folder/", "/dav/My folder/")]
        [DataRow("/dav/My%20folder/a.txt", "/dav/My folder/a.txt")]
        [DataRow("http://fake.local/dav/My%20folder/a.txt", "/dav/My folder/a.txt")]
        [DataRow("https://docs.example.com:8443/dav/a.txt", "/dav/a.txt")]
        [DataRow("/dav/a%2520b", "/dav/a%20b")]
        [DataRow("http://fake.local/dav/a%2520b", "/dav/a%20b")]
        [DataRow("/dav/C++ notes.txt", "/dav/C++ notes.txt")]
        [DataRow("/dav/C%2B%2B.txt", "/dav/C++.txt")]
        [DataRow("http://fake.local/dav/C++.txt", "/dav/C++.txt")]
        [DataRow("/dav/C#1.txt", "/dav/C#1.txt")]
        [DataRow("http://fake.local/dav/C#1.txt", "/dav/C#1.txt")]
        [DataRow("/dav/C%231.txt", "/dav/C#1.txt")]
        [DataRow("/dav/%C3%A9t%C3%A9.txt", "/dav/\u00e9t\u00e9.txt")]
        [DataRow("http://fake.local/dav/%C3%A9t%C3%A9.txt", "/dav/\u00e9t\u00e9.txt")]
        [DataRow("/dav/what%3F.txt", "/dav/what?.txt")]
        [DataRow("http://fake.local/dav/what%3F.txt", "/dav/what?.txt")]
        [DataRow("/dav/100%25.txt", "/dav/100%.txt")]
        [DataRow("http://fake.local/", "/")]
        public void DecodeHref_ReturnsDecodedPath(String Href, String Expected)
        {
            Assert.AreEqual(Expected, MultiStatusParser.DecodeHref(Href));
        }

        #endregion

        #region Private methods

        private static List<WebDavItem> Parse(String Xml) => MultiStatusParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(Xml)));

        /// <summary>A multistatus with a single response, whose 200 propstat contains the given properties.</summary>
        private static WebDavItem ParseSingle(String Href, String Properties)
        {
            var Items = Parse("<?xml version=\"1.0\" encoding=\"utf-8\"?><D:multistatus xmlns:D=\"DAV:\" xmlns:Z=\"urn:schemas-microsoft-com:\"><D:response><D:href>"
                + Href + "</D:href><D:propstat><D:prop>" + Properties + "</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>");
            Assert.AreEqual(1, Items.Count);
            return Items[0];
        }

        private static void WithCulture(String Culture, Action Action)
        {
            var Thread = System.Threading.Thread.CurrentThread;
            CultureInfo Previous = Thread.CurrentCulture, PreviousUI = Thread.CurrentUICulture;
            try
            {
                Thread.CurrentCulture = new CultureInfo(Culture);
                Thread.CurrentUICulture = new CultureInfo(Culture);
                Action();
            }
            finally
            {
                Thread.CurrentCulture = Previous;
                Thread.CurrentUICulture = PreviousUI;
            }
        }

        #endregion
    }
}
