using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace KS2Drive.WebDav
{
    /// <summary>
    /// Reads the "multistatus" body of a PROPFIND response (RFC 4918, section 14.16) into <see cref="WebDavItem"/>s.
    /// Elements are matched on their local name, whatever their namespace prefix (servers differ); only the properties of a
    /// "propstat" with a 200 status (or without status) are used.
    /// </summary>
    public static class MultiStatusParser
    {
        private static readonly XmlReaderSettings ReaderSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, //Never resolve external entities from a server response
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true
        };

        /// <summary>
        /// Parses a multistatus body.
        /// </summary>
        /// <exception cref="XmlException">The body is not well-formed XML.</exception>
        public static List<WebDavItem> Parse(Stream Body)
        {
            XDocument Document;
            using (var Reader = XmlReader.Create(Body, ReaderSettings))
            {
                Document = XDocument.Load(Reader);
            }

            var Items = new List<WebDavItem>();
            foreach (XElement Response in Document.Descendants().Where(x => Is(x, "response")))
            {
                String Href = Response.Elements().FirstOrDefault(x => Is(x, "href"))?.Value?.Trim();
                if (String.IsNullOrEmpty(Href)) continue;

                var Item = new WebDavItem();
                foreach (XElement PropStat in Response.Elements().Where(x => Is(x, "propstat")))
                {
                    String Status = PropStat.Elements().FirstOrDefault(x => Is(x, "status"))?.Value;
                    if (Status != null && !Status.Contains(" 200")) continue; //Properties the server could not return
                    foreach (XElement Prop in PropStat.Elements().Where(x => Is(x, "prop")).SelectMany(x => x.Elements())) ReadProperty(Item, Prop);
                }

                Item.Path = DecodeHref(Href);
                if (Item.Path.Length > 1) Item.Path = Item.Path.TrimEnd('/');
                if (String.IsNullOrEmpty(Item.DisplayName)) Item.DisplayName = Item.Path.Substring(Item.Path.LastIndexOf('/') + 1);
                Items.Add(Item);
            }
            return Items;
        }

        /// <summary>
        /// Returns the decoded path of a href, which servers send either as an absolute URL or as a path.
        /// A raw '#' (invalid in a href, but sent by some servers for file names containing it) is part of the path, not a fragment.
        /// '+' is kept: it is not a space in a path.
        /// </summary>
        public static String DecodeHref(String Href)
        {
            String Escaped = Href.Replace("#", "%23");
            if (Uri.TryCreate(Escaped, UriKind.Absolute, out Uri Absolute) && (Absolute.Scheme == Uri.UriSchemeHttp || Absolute.Scheme == Uri.UriSchemeHttps))
            {
                Escaped = Absolute.AbsolutePath;
            }
            return Uri.UnescapeDataString(Escaped);
        }

        #region Private methods

        private static bool Is(XElement Element, String LocalName) => String.Equals(Element.Name.LocalName, LocalName, StringComparison.OrdinalIgnoreCase);

        private static void ReadProperty(WebDavItem Item, XElement Prop)
        {
            String Value = Prop.Value?.Trim();
            switch (Prop.Name.LocalName.ToLowerInvariant())
            {
                case "displayname":
                    if (!String.IsNullOrEmpty(Value)) Item.DisplayName = Value;
                    break;
                case "getcontentlength":
                    if (long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long Length)) Item.ContentLength = Length;
                    break;
                case "getcontenttype":
                    Item.ContentType = Value;
                    break;
                case "getetag":
                    Item.ETag = Value;
                    break;
                case "creationdate":
                    Item.CreationDate = ParseDate(Value);
                    break;
                case "getlastmodified":
                    Item.LastModified = ParseDate(Value);
                    break;
                case "resourcetype":
                    if (Prop.Elements().Any(x => Is(x, "collection"))) Item.IsCollection = true;
                    break;
                case "iscollection": //Microsoft extension
                    if (Value == "1" || String.Equals(Value, "true", StringComparison.OrdinalIgnoreCase)) Item.IsCollection = true;
                    break;
                case "hidden":
                case "ishidden": //Microsoft extension: IIS sends <ishidden>0</ishidden> for every visible element
                    Item.IsHidden = String.IsNullOrEmpty(Value) || !(Value == "0" || String.Equals(Value, "false", StringComparison.OrdinalIgnoreCase));
                    break;
            }
        }

        /// <summary>
        /// Parses an ISO 8601 date (creationdate) or an RFC 1123 date (getlastmodified), independently of the user's culture.
        /// </summary>
        private static DateTime? ParseDate(String Value)
        {
            if (String.IsNullOrEmpty(Value)) return null;
            if (DateTimeOffset.TryParse(Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset Parsed)) return Parsed.UtcDateTime;
            return null;
        }

        #endregion
    }
}
