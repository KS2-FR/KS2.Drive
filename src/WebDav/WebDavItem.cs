using System;

namespace KS2Drive.WebDav
{
    /// <summary>
    /// A file or folder described by the server in a PROPFIND response.
    /// </summary>
    public class WebDavItem
    {
        /// <summary>
        /// Decoded path of the element on the server (e.g. "/dav/My folder/C++ notes.txt"), without scheme, host nor trailing
        /// slash. Like every path in the application, it is decoded: only <see cref="WebDavClient"/> encodes paths into URLs.
        /// </summary>
        public String Path { get; set; }

        /// <summary>
        /// Name to display: the "displayname" property, or else the last segment of <see cref="Path"/>.
        /// </summary>
        public String DisplayName { get; set; }

        /// <summary>
        /// True for a folder (WebDAV collection: "resourcetype" containing "collection").
        /// </summary>
        public bool IsCollection { get; set; }

        /// <summary>
        /// "hidden"/"ishidden" property set to a true value.
        /// </summary>
        public bool IsHidden { get; set; }

        /// <summary>
        /// "getcontentlength" property; null when the server does not send it (folders, some servers).
        /// </summary>
        public long? ContentLength { get; set; }

        public String ContentType { get; set; }

        public String ETag { get; set; }

        /// <summary>
        /// "creationdate" property, in UTC.
        /// </summary>
        public DateTime? CreationDate { get; set; }

        /// <summary>
        /// "getlastmodified" property, in UTC.
        /// </summary>
        public DateTime? LastModified { get; set; }
    }
}
