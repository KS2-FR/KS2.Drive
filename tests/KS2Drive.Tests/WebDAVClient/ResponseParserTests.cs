using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Xml;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Parser = WebDAVClient.Helpers.ResponseParser;

namespace KS2Drive.Tests.WebDAVClientTests
{
    /// <summary>
    /// WebDAVClient.Helpers.ResponseParser (internal): ParseItems and ParseItem.
    /// </summary>
    [TestClass]
    public class ResponseParserTests
    {
        private static global::WebDAVClient.Model.Item ParseOne(String Href, String Props)
        {
            var Items = Parser.ParseItems(DavXml.ToStream(DavXml.Multistatus(DavXml.Response(Href, Props)))).ToList();
            Assert.AreEqual(1, Items.Count);
            return Items[0];
        }

        #region ParseItems: structure

        [TestMethod]
        public void ParseItems_MultipleResponses_AreReturnedInOrder()
        {
            String Xml = DavXml.Multistatus(
                DavXml.Response("/dav/", "<D:resourcetype><D:collection/></D:resourcetype>"),
                DavXml.Response("/dav/a.txt", "<D:getcontentlength>1</D:getcontentlength>"),
                DavXml.Response("/dav/b.txt", "<D:getcontentlength>2</D:getcontentlength>"));
            var Items = Parser.ParseItems(DavXml.ToStream(Xml)).ToList();
            CollectionAssert.AreEqual(new[] { "/dav/", "/dav/a.txt", "/dav/b.txt" }, Items.Select(i => i.Href).ToArray());
            CollectionAssert.AreEqual(new long?[] { null, 1, 2 }, Items.Select(i => i.ContentLength).ToArray());
        }

        [TestMethod]
        public void ParseItems_EmptyMultistatus_ReturnsEmptyList()
        {
            var Items = Parser.ParseItems(DavXml.ToStream(DavXml.Multistatus()));
            Assert.IsNotNull(Items);
            Assert.AreEqual(0, Items.Count());
        }

        [TestMethod]
        public void ParseItems_PropertiesDoNotLeakBetweenResponses()
        {
            String Xml = DavXml.Multistatus(
                DavXml.Response("/dav/a.txt", "<D:getetag>e1</D:getetag><D:getcontentlength>10</D:getcontentlength><D:ishidden>1</D:ishidden>"),
                DavXml.Response("/dav/b.txt", ""));
            var B = Parser.ParseItems(DavXml.ToStream(Xml)).ToList()[1];
            Assert.IsNull(B.Etag);
            Assert.IsNull(B.ContentLength);
            Assert.IsFalse(B.IsHidden);
        }

        [TestMethod]
        public void ParseItems_NamespacePrefixIsIgnored()
        {
            String Xml = "<?xml version=\"1.0\"?><multistatus xmlns=\"DAV:\"><response><href>/dav/a.txt</href><propstat><prop>"
                + "<getcontentlength>42</getcontentlength><displayname>A</displayname></prop></propstat></response></multistatus>";
            var Item = Parser.ParseItems(DavXml.ToStream(Xml)).Single();
            Assert.AreEqual("/dav/a.txt", Item.Href);
            Assert.AreEqual(42L, Item.ContentLength);
            Assert.AreEqual("A", Item.DisplayName);
        }

        [TestMethod]
        public void ParseItems_ElementNamesAreCaseInsensitive()
        {
            String Xml = "<lp1:MultiStatus xmlns:lp1=\"DAV:\"><lp1:Response><lp1:HREF>/dav/a.txt</lp1:HREF><lp1:GetContentLength>3</lp1:GetContentLength></lp1:Response></lp1:MultiStatus>";
            var Item = Parser.ParseItems(DavXml.ToStream(Xml)).Single();
            Assert.AreEqual("/dav/a.txt", Item.Href);
            Assert.AreEqual(3L, Item.ContentLength);
        }

        [TestMethod]
        public void ParseItems_WhitespaceAndCommentsAreIgnored()
        {
            String Xml = "<?xml version=\"1.0\"?>\r\n<D:multistatus xmlns:D=\"DAV:\">\r\n  <!-- comment -->\r\n  <D:response>\r\n    <D:href>/dav/a.txt</D:href>\r\n"
                + "    <D:propstat>\r\n      <D:prop>\r\n        <D:getetag>tag</D:getetag>\r\n      </D:prop>\r\n    </D:propstat>\r\n  </D:response>\r\n</D:multistatus>";
            var Item = Parser.ParseItems(DavXml.ToStream(Xml)).Single();
            Assert.AreEqual("/dav/a.txt", Item.Href);
            Assert.AreEqual("tag", Item.Etag);
        }

        [TestMethod]
        public void ParseItems_MalformedXml_ThrowsXmlException()
        {
            Assert.ThrowsException<XmlException>(() => Parser.ParseItems(DavXml.ToStream("<D:multistatus xmlns:D=\"DAV:\"><D:response><D:href>/a</D:href>")));
        }

        [TestMethod]
        public void ParseItems_NotXml_ThrowsXmlException()
        {
            Assert.ThrowsException<XmlException>(() => Parser.ParseItems(DavXml.ToStream("<html><body>Error 500</body>")));
            Assert.ThrowsException<XmlException>(() => Parser.ParseItems(DavXml.ToStream("")));
        }

        [TestMethod]
        public void ParseItems_SkipsTheHrefInsideCheckedIn()
        {
            var Item = ParseOne("/dav/a.txt", "<D:checked-in><D:href>/versions/1</D:href></D:checked-in>");
            Assert.AreEqual("/dav/a.txt", Item.Href);
            Assert.AreEqual("a.txt", Item.DisplayName);
        }

        [TestMethod]
        public void ParseItems_SkipsTheHrefInsideVersionControlledConfiguration()
        {
            var Item = ParseOne("/dav/a.txt", "<D:version-controlled-configuration><D:href>/vcc/2</D:href></D:version-controlled-configuration>");
            Assert.AreEqual("/dav/a.txt", Item.Href);
        }

        [TestMethod]
        public void ParseItems_ConsecutiveSkippedProperties_DoNotLeakTheirHref()
        {
            // Two skipped properties in a row: the href of the second one must not replace the item's href
            var Item = ParseOne("/dav/a.txt",
                "<D:checked-in><D:href>/versions/1</D:href></D:checked-in>"
                + "<D:version-controlled-configuration><D:href>/vcc/2</D:href></D:version-controlled-configuration>");
            Assert.AreEqual("/dav/a.txt", Item.Href);
        }

        [TestMethod]
        public void ParseItems_PropertyRightAfterCheckedIn_IsStillRead()
        {
            // reader.Skip() already positions on the next sibling: the following property must not be skipped too
            var Item = ParseOne("/dav/a.txt",
                "<D:checked-in><D:href>/versions/1</D:href></D:checked-in><D:displayname>Real name</D:displayname>");
            Assert.AreEqual("Real name", Item.DisplayName);
        }

        #endregion

        #region Href and DisplayName

        [TestMethod]
        public void Href_HashIsReplacedByEncodedHash()
        {
            var Item = ParseOne("/dav/a#b#c.txt", "");
            Assert.AreEqual("/dav/a%23b%23c.txt", Item.Href);
            Assert.AreEqual("a#b#c.txt", Item.DisplayName);
        }

        [TestMethod]
        public void Href_EncodedCharactersAreKept()
        {
            var Item = ParseOne("/dav/my%20file%C3%A9.txt", "");
            Assert.AreEqual("/dav/my%20file%C3%A9.txt", Item.Href);
        }

        [TestMethod]
        public void Href_XmlEntitiesAreDecoded()
        {
            var Item = ParseOne("/dav/a&amp;b.txt", "");
            Assert.AreEqual("/dav/a&b.txt", Item.Href);
            Assert.AreEqual("a&b.txt", Item.DisplayName);
        }

        [TestMethod]
        public void Href_AbsoluteUrlIsKept()
        {
            var Item = ParseOne("http://server/dav/a.txt", "");
            Assert.AreEqual("http://server/dav/a.txt", Item.Href);
            Assert.AreEqual("a.txt", Item.DisplayName);
        }

        [TestMethod]
        public void Href_TrailingSlashIsRemovedForFiles()
        {
            var Item = ParseOne("/dav/a.txt/", "<D:resourcetype/>");
            Assert.AreEqual("/dav/a.txt", Item.Href);
            Assert.AreEqual("a.txt", Item.DisplayName);
        }

        [TestMethod]
        public void Href_TrailingSlashIsKeptForCollections()
        {
            var Item = ParseOne("/dav/sub/", "<D:resourcetype><D:collection/></D:resourcetype>");
            Assert.AreEqual("/dav/sub/", Item.Href);
            Assert.AreEqual("sub", Item.DisplayName);
        }

        [TestMethod]
        public void DisplayName_FromServerIsUsed()
        {
            var Item = ParseOne("/dav/a.txt", "<D:displayname>Pretty name</D:displayname>");
            Assert.AreEqual("Pretty name", Item.DisplayName);
        }

        [TestMethod]
        public void DisplayName_EmptyElement_IsDerivedFromHref()
        {
            var Item = ParseOne("/dav/my%20file.txt", "<D:displayname/>");
            Assert.AreEqual("my file.txt", Item.DisplayName);
        }

        [TestMethod]
        public void DisplayName_Missing_IsDecodedLastSegment()
        {
            var Item = ParseOne("/dav/sub/%C3%A9t%C3%A9%20%2B.txt", "");
            Assert.AreEqual("été +.txt", Item.DisplayName);
        }

        [TestMethod]
        public void DisplayName_LiteralPlusInHref_IsKept()
        {
            // In a URL path '+' is a literal plus sign, not an encoded space
            var Item = ParseOne("/dav/c++.txt", "");
            Assert.AreEqual("c++.txt", Item.DisplayName);
        }

        #endregion

        #region Properties

        [TestMethod]
        public void MissingProperties_AreNullOrFalse()
        {
            var Item = ParseOne("/dav/a.txt", "");
            Assert.IsNull(Item.CreationDate);
            Assert.IsNull(Item.LastModified);
            Assert.IsNull(Item.ContentLength);
            Assert.IsNull(Item.ContentType);
            Assert.IsNull(Item.Etag);
            Assert.IsFalse(Item.IsCollection);
            Assert.IsFalse(Item.IsHidden);
        }

        [TestMethod]
        public void EmptyPropertyElements_AreNullOrFalse()
        {
            var Item = ParseOne("/dav/a.txt", "<D:creationdate/><D:getlastmodified/><D:getcontentlength/><D:getcontenttype/><D:getetag/><D:iscollection/><D:resourcetype/>");
            Assert.IsNull(Item.CreationDate);
            Assert.IsNull(Item.LastModified);
            Assert.IsNull(Item.ContentLength);
            Assert.IsNull(Item.ContentType);
            Assert.IsNull(Item.Etag);
            Assert.IsFalse(Item.IsCollection);
        }

        [TestMethod]
        public void ContentLengthContentTypeAndEtag_AreRead()
        {
            var Item = ParseOne("/dav/a.txt", "<D:getcontentlength>5000000000</D:getcontentlength><D:getcontenttype>text/plain</D:getcontenttype><D:getetag>\"abc-123\"</D:getetag>");
            Assert.AreEqual(5000000000L, Item.ContentLength);
            Assert.AreEqual("text/plain", Item.ContentType);
            Assert.AreEqual("\"abc-123\"", Item.Etag);
        }

        [TestMethod]
        public void InvalidContentLength_IsNull()
        {
            Assert.IsNull(ParseOne("/dav/a.txt", "<D:getcontentlength>abc</D:getcontentlength>").ContentLength);
        }

        [TestMethod]
        public void CreationDate_Iso8601_IsParsed()
        {
            var Item = ParseOne("/dav/a.txt", "<D:creationdate>2020-01-02T03:04:05Z</D:creationdate>");
            Assert.AreEqual(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), Item.CreationDate.Value.ToUniversalTime());
        }

        [TestMethod]
        public void LastModified_Rfc1123_IsParsed_InvariantCulture()
        {
            var Previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                var Item = ParseOne("/dav/a.txt", "<D:getlastmodified>Mon, 07 Jun 2021 08:09:10 GMT</D:getlastmodified>");
                Assert.AreEqual(new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc), Item.LastModified.Value.ToUniversalTime());
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = Previous;
            }
        }

        [TestMethod]
        public void LastModified_Rfc1123_IsParsed_FrenchCulture()
        {
            // HTTP dates are always English (RFC 7231): they must be parsed whatever the user's culture
            var Previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
                var Item = ParseOne("/dav/a.txt", "<D:getlastmodified>Mon, 07 Jun 2021 08:09:10 GMT</D:getlastmodified>");
                Assert.IsTrue(Item.LastModified.HasValue);
                Assert.AreEqual(new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc), Item.LastModified.Value.ToUniversalTime());
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = Previous;
            }
        }

        [TestMethod]
        public void InvalidDates_AreNull()
        {
            var Item = ParseOne("/dav/a.txt", "<D:creationdate>not a date</D:creationdate><D:getlastmodified>never</D:getlastmodified>");
            Assert.IsNull(Item.CreationDate);
            Assert.IsNull(Item.LastModified);
        }

        [DataTestMethod]
        [DataRow("true", true)]
        [DataRow("True", true)]
        [DataRow("1", true)]
        [DataRow("false", false)]
        [DataRow("0", false)]
        [DataRow("2", false)]
        [DataRow("yes", false)]
        public void IsCollectionElement_IsParsed(String Value, bool Expected)
        {
            var Item = ParseOne("/dav/x/", "<D:iscollection>" + Value + "</D:iscollection>");
            Assert.AreEqual(Expected, Item.IsCollection);
        }

        [TestMethod]
        public void ResourceTypeCollection_MarksACollection()
        {
            Assert.IsTrue(ParseOne("/dav/x/", "<D:resourcetype><D:collection/></D:resourcetype>").IsCollection);
            Assert.IsTrue(ParseOne("/dav/x/", "<D:resourcetype><D:collection></D:collection></D:resourcetype>").IsCollection);
        }

        [TestMethod]
        public void ResourceTypeEmptyOrOther_IsNotACollection()
        {
            Assert.IsFalse(ParseOne("/dav/x", "<D:resourcetype/>").IsCollection);
            Assert.IsFalse(ParseOne("/dav/x", "<D:resourcetype></D:resourcetype>").IsCollection);
            Assert.IsFalse(ParseOne("/dav/x", "<D:resourcetype><D:principal/></D:resourcetype>").IsCollection);
        }

        [TestMethod]
        public void HiddenElements_MarkTheItemHidden()
        {
            Assert.IsTrue(ParseOne("/dav/a.txt", "<D:hidden/>").IsHidden);
            Assert.IsTrue(ParseOne("/dav/a.txt", "<D:ishidden>1</D:ishidden>").IsHidden);
        }

        [TestMethod]
        public void IsHiddenZero_IsNotHidden()
        {
            // IIS sends <D:ishidden>0</D:ishidden> for every visible item
            Assert.IsFalse(ParseOne("/dav/a.txt", "<D:ishidden>0</D:ishidden>").IsHidden);
        }

        [TestMethod]
        public void UnknownProperties_AreIgnored()
        {
            var Item = ParseOne("/dav/a.txt", "<D:lockdiscovery><D:activelock><D:locktype><D:write/></D:locktype></D:activelock></D:lockdiscovery><Z:custom xmlns:Z=\"urn:x\">v</Z:custom><D:getetag>e</D:getetag>");
            Assert.AreEqual("e", Item.Etag);
            Assert.AreEqual("/dav/a.txt", Item.Href);
        }

        #endregion

        #region ParseItem

        [TestMethod]
        public void ParseItem_ReturnsTheFirstResponse()
        {
            String Xml = DavXml.Multistatus(DavXml.Response("/dav/first/", "<D:resourcetype><D:collection/></D:resourcetype>"), DavXml.Response("/dav/first/second.txt", ""));
            var Item = Parser.ParseItem(DavXml.ToStream(Xml));
            Assert.AreEqual("/dav/first/", Item.Href);
            Assert.IsTrue(Item.IsCollection);
        }

        [TestMethod]
        public void ParseItem_EmptyMultistatus_ReturnsNull()
        {
            Assert.IsNull(Parser.ParseItem(DavXml.ToStream(DavXml.Multistatus())));
        }

        [TestMethod]
        public void ParseItem_MalformedXml_ThrowsXmlException()
        {
            Assert.ThrowsException<XmlException>(() => Parser.ParseItem(DavXml.ToStream("<not closed")));
        }

        #endregion
    }
}
