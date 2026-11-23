using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KS2Drive.Tests.Infrastructure
{
    /// <summary>
    /// In-memory WebDAV server used as the HttpMessageHandler of the application's HttpClient, so that the WebDAV client
    /// and the file system can be tested without network. Supports PROPFIND (depth 0 and 1), GET, PUT, DELETE, MKCOL and MOVE,
    /// failure injection (401, any status code, transport error) and records every request.
    /// Paths are stored decoded and compared case-insensitively.
    /// </summary>
    internal class FakeWebDavServer : HttpMessageHandler
    {
        public const String ServerUrl = "http://fake.local";
        public const String LibraryPath = "/dav";
        public static String LibraryUrl => ServerUrl + LibraryPath;

        private class Entry
        {
            public bool IsFolder;
            public byte[] Content = new byte[0];
            public DateTime Created = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            public DateTime Modified = new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc);
            public String ETag = Guid.NewGuid().ToString("N");
        }

        private readonly object Lock = new object();
        private readonly Dictionary<String, Entry> Entries = new Dictionary<String, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every request received, as "METHOD /decoded/path".</summary>
        public List<String> Requests { get; } = new List<String>();

        /// <summary>
        /// Optional: returns true to hold the request (method, decoded path) until <see cref="ReleaseHeldRequests"/> is set,
        /// simulating a slow server. <see cref="RequestHeld"/> is set when a request starts being held.
        /// </summary>
        public Func<String, String, bool> HoldRequest { get; set; }
        public ManualResetEventSlim RequestHeld { get; } = new ManualResetEventSlim(false);
        public ManualResetEventSlim ReleaseHeldRequests { get; } = new ManualResetEventSlim(false);

        /// <summary>Every MOVE processed, as "/from -> /to (Overwrite: T|F)".</summary>
        public List<String> MoveRequests { get; } = new List<String>();

        /// <summary>When true, every request is answered with 401.</summary>
        public bool RejectCredentials { get; set; }

        /// <summary>Optional: returns a status code to answer instead of processing the request (null to process it).</summary>
        public Func<String, String, HttpStatusCode?> ForcedStatus { get; set; }

        /// <summary>Optional: returns true to simulate a transport failure (HttpRequestException) for the request.</summary>
        public Func<String, String, bool> NetworkFailure { get; set; }

        /// <summary>When false, PROPFIND responses do not contain getcontentlength (like some servers do).</summary>
        public bool SendContentLength { get; set; } = true;

        /// <summary>When true, a PROPFIND on a file path ending with '/' answers 404 (like most servers do).</summary>
        public bool FolderLookupOfFileIs404 { get; set; } = true;

        public FakeWebDavServer()
        {
            AddFolder(LibraryPath);
        }

        public void AddFolder(String Path)
        {
            lock (Lock) Entries[Normalize(Path)] = new Entry { IsFolder = true, ETag = null };
        }

        public void AddFile(String Path, String Content) => AddFile(Path, Encoding.UTF8.GetBytes(Content));

        public void AddFile(String Path, byte[] Content)
        {
            lock (Lock) Entries[Normalize(Path)] = new Entry { IsFolder = false, Content = Content };
        }

        /// <summary>Removes an element (e.g. deleted by another client), with its descendants.</summary>
        public void Remove(String Path)
        {
            lock (Lock)
            {
                String Key = Normalize(Path);
                foreach (var Existing in Entries.Keys.Where(x => x.Equals(Key, StringComparison.OrdinalIgnoreCase) || x.StartsWith(Key + "/", StringComparison.OrdinalIgnoreCase)).ToList()) Entries.Remove(Existing);
            }
        }

        public bool Exists(String Path)
        {
            lock (Lock) return Entries.ContainsKey(Normalize(Path));
        }

        public bool IsFolder(String Path)
        {
            lock (Lock) return Entries.TryGetValue(Normalize(Path), out Entry E) && E.IsFolder;
        }

        public byte[] GetContent(String Path)
        {
            lock (Lock) return Entries.TryGetValue(Normalize(Path), out Entry E) ? E.Content : null;
        }

        public String GetText(String Path)
        {
            var Content = GetContent(Path);
            return Content == null ? null : Encoding.UTF8.GetString(Content);
        }

        public int CountRequests(String Method) => Requests.Count(x => x.StartsWith(Method + " ", StringComparison.Ordinal));

        private static String Normalize(String Path)
        {
            String Decoded = Uri.UnescapeDataString(Path);
            if (Decoded.Length > 1) Decoded = Decoded.TrimEnd('/');
            if (!Decoded.StartsWith("/")) Decoded = "/" + Decoded;
            return Decoded;
        }

        private static String ParentOf(String Path)
        {
            int Index = Path.LastIndexOf('/');
            return Index <= 0 ? "/" : Path.Substring(0, Index);
        }

        private static String Escape(String Path)
        {
            return String.Join("/", Path.Split('/').Select(Uri.EscapeDataString));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
        {
            String Method = Request.Method.Method.ToUpperInvariant();
            String RawPath = Request.RequestUri.AbsolutePath;
            String Path = Normalize(RawPath);
            byte[] Body = Request.Content == null ? null : await Request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

            //Simulates a slow server for the matching requests, outside the server lock so that other requests are not delayed
            if (HoldRequest != null && HoldRequest(Method, Path))
            {
                RequestHeld.Set();
                if (!ReleaseHeldRequests.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Held request never released");
            }

            lock (Lock)
            {
                Requests.Add($"{Method} {Path}");

                if (NetworkFailure != null && NetworkFailure(Method, Path)) throw new HttpRequestException("Simulated network failure");
                if (RejectCredentials) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                var Forced = ForcedStatus?.Invoke(Method, Path);
                if (Forced.HasValue) return new HttpResponseMessage(Forced.Value);

                switch (Method)
                {
                    case "PROPFIND": return PropFind(Path, RawPath.EndsWith("/") && RawPath.Length > 1, Request);
                    case "GET": return Get(Path);
                    case "PUT": return Put(Path, Body ?? new byte[0]);
                    case "DELETE": return Delete(Path);
                    case "MKCOL": return MkCol(Path);
                    case "MOVE": return Move(Path, Request);
                    default: return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
                }
            }
        }

        private HttpResponseMessage PropFind(String Path, bool AsFolder, HttpRequestMessage Request)
        {
            if (!Entries.TryGetValue(Path, out Entry Target)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (AsFolder && !Target.IsFolder && FolderLookupOfFileIs404) return new HttpResponseMessage(HttpStatusCode.NotFound);

            String Depth = Request.Headers.TryGetValues("Depth", out IEnumerable<String> Values) ? Values.FirstOrDefault() : "1";
            var Listed = new List<KeyValuePair<String, Entry>> { new KeyValuePair<String, Entry>(Path, Target) };
            if (Target.IsFolder && Depth != "0")
            {
                String Prefix = Path == "/" ? "/" : Path + "/";
                Listed.AddRange(Entries.Where(x => x.Key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && x.Key.IndexOf('/', Prefix.Length) < 0 && x.Key.Length > Prefix.Length));
            }

            var Xml = new StringBuilder();
            Xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><D:multistatus xmlns:D=\"DAV:\">");
            foreach (var Item in Listed)
            {
                String Name = Item.Key == "/" ? "" : Item.Key.Substring(Item.Key.LastIndexOf('/') + 1);
                Xml.Append("<D:response><D:href>").Append(SecurityElement.Escape(Escape(Item.Key) + (Item.Value.IsFolder ? "/" : ""))).Append("</D:href>");
                Xml.Append("<D:propstat><D:prop>");
                Xml.Append("<D:displayname>").Append(SecurityElement.Escape(Name)).Append("</D:displayname>");
                Xml.Append("<D:creationdate>").Append(Item.Value.Created.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append("</D:creationdate>");
                Xml.Append("<D:getlastmodified>").Append(Item.Value.Modified.ToString("R", CultureInfo.InvariantCulture)).Append("</D:getlastmodified>");
                if (Item.Value.IsFolder)
                {
                    Xml.Append("<D:resourcetype><D:collection/></D:resourcetype>");
                }
                else
                {
                    Xml.Append("<D:resourcetype/>");
                    if (SendContentLength) Xml.Append("<D:getcontentlength>").Append(Item.Value.Content.Length).Append("</D:getcontentlength>");
                    Xml.Append("<D:getetag>\"").Append(Item.Value.ETag).Append("\"</D:getetag>");
                }
                Xml.Append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
            }
            Xml.Append("</D:multistatus>");

            return new HttpResponseMessage((HttpStatusCode)207) { Content = new StringContent(Xml.ToString(), Encoding.UTF8, "application/xml") };
        }

        private HttpResponseMessage Get(String Path)
        {
            if (!Entries.TryGetValue(Path, out Entry Target) || Target.IsFolder) return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Target.Content) };
        }

        private HttpResponseMessage Put(String Path, byte[] Body)
        {
            if (!Entries.TryGetValue(ParentOf(Path), out Entry Parent) || !Parent.IsFolder) return new HttpResponseMessage(HttpStatusCode.Conflict);
            bool Existed = Entries.TryGetValue(Path, out Entry Existing);
            if (Existed && Existing.IsFolder) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            Entries[Path] = new Entry { IsFolder = false, Content = Body, Created = Existed ? Existing.Created : DateTime.UtcNow, Modified = DateTime.UtcNow };
            return new HttpResponseMessage(Existed ? HttpStatusCode.NoContent : HttpStatusCode.Created);
        }

        private HttpResponseMessage Delete(String Path)
        {
            if (!Entries.ContainsKey(Path)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            foreach (var Key in Entries.Keys.Where(x => x.Equals(Path, StringComparison.OrdinalIgnoreCase) || x.StartsWith(Path + "/", StringComparison.OrdinalIgnoreCase)).ToList()) Entries.Remove(Key);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private HttpResponseMessage MkCol(String Path)
        {
            if (Entries.ContainsKey(Path)) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            if (!Entries.TryGetValue(ParentOf(Path), out Entry Parent) || !Parent.IsFolder) return new HttpResponseMessage(HttpStatusCode.Conflict);
            Entries[Path] = new Entry { IsFolder = true, ETag = null };
            return new HttpResponseMessage(HttpStatusCode.Created);
        }

        private HttpResponseMessage Move(String Path, HttpRequestMessage Request)
        {
            if (!Entries.ContainsKey(Path)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (!Request.Headers.TryGetValues("Destination", out IEnumerable<String> Destinations)) return new HttpResponseMessage(HttpStatusCode.BadRequest);
            String Destination = Normalize(new Uri(Destinations.First()).AbsolutePath);
            if (!Entries.TryGetValue(ParentOf(Destination), out Entry Parent) || !Parent.IsFolder) return new HttpResponseMessage(HttpStatusCode.Conflict);

            //RFC 4918: "Overwrite: F" and an existing destination (other than the source itself, e.g. a case-only rename) -> 412
            bool Overwrite = !(Request.Headers.TryGetValues("Overwrite", out IEnumerable<String> OverwriteValues) && OverwriteValues.FirstOrDefault() == "F");
            if (!Overwrite && Entries.ContainsKey(Destination) && !Destination.Equals(Path, StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            MoveRequests.Add($"{Path} -> {Destination} (Overwrite: {(Overwrite ? "T" : "F")})");

            foreach (var Key in Entries.Keys.Where(x => x.Equals(Path, StringComparison.OrdinalIgnoreCase) || x.StartsWith(Path + "/", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                Entry Moved = Entries[Key];
                Entries.Remove(Key);
                Entries[Destination + Key.Substring(Path.Length)] = Moved;
            }
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }
}
