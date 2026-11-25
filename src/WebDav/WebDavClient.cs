using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace KS2Drive.WebDav
{
    /// <summary>
    /// Minimal WebDAV client (RFC 4918) with the operations used by the drive: PROPFIND, GET, PUT, DELETE, MKCOL and MOVE.
    /// <list type="bullet">
    /// <item>Paths are decoded server paths, including the library path (e.g. "/dav/My folder/a+b.txt"). This class is the only
    /// place where they are encoded into URLs (<see cref="GetUrl"/>), and <see cref="MultiStatusParser"/> the only place where
    /// URLs from the server are decoded.</item>
    /// <item>Every response is disposed: an undisposed response keeps its HTTP connection busy, and the number of connections
    /// to a server is limited.</item>
    /// <item>An unexpected status raises a <see cref="WebDavException"/> with the status code; a transport error raises an
    /// HttpRequestException.</item>
    /// </list>
    /// Instances are cheap and thread-safe; the HttpClient is shared and owned by the caller.
    /// </summary>
    public sealed class WebDavClient
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private static readonly HttpMethod PropFindMethod = new HttpMethod("PROPFIND");
        private static readonly HttpMethod MkColMethod = new HttpMethod("MKCOL");
        private static readonly HttpMethod MoveMethod = new HttpMethod("MOVE");

        /// <summary>
        /// PROPFIND body asking for all the properties (RFC 4918, section 9.1).
        /// </summary>
        private const String AllPropRequest = "<?xml version=\"1.0\" encoding=\"utf-8\"?><D:propfind xmlns:D=\"DAV:\"><D:allprop/></D:propfind>";

        private const int MultiStatus = 207;

        private readonly HttpClient Http;

        /// <summary>
        /// Scheme, host and port of the server, without trailing slash (e.g. "https://docs.example.com").
        /// </summary>
        public String Server { get; }

        /// <param name="Http">HttpClient used for every request (shared, not disposed by this class).</param>
        /// <param name="ServerUrl">URL of the server; only its scheme, host and port are used.</param>
        public WebDavClient(HttpClient Http, String ServerUrl)
        {
            this.Http = Http ?? throw new ArgumentNullException(nameof(Http));
            //Scheme, host and port only: credentials written in the URL (user:password@) must never end up in request URLs or logs
            this.Server = new Uri(ServerUrl).GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        }

        /// <summary>
        /// Creates the HttpClient for a server: credentials sent with the first request, optional TLS client certificate,
        /// compressed responses accepted, no "Expect: 100-continue", and no automatic redirection (a redirection is an answer
        /// in itself, e.g. for a folder URL without trailing slash). The system proxy settings (WebRequest.DefaultWebProxy) apply.
        /// </summary>
        public static HttpClient CreateHttpClient(NetworkCredential Credential, X509Certificate ClientCertificate)
        {
            var Handler = new WebRequestHandler
            {
                Credentials = Credential,
                PreAuthenticate = Credential != null,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            if (ClientCertificate != null)
            {
                Handler.ClientCertificateOptions = ClientCertificateOption.Manual;
                Handler.ClientCertificates.Add(ClientCertificate);
            }
            return CreateHttpClient(Handler);
        }

        /// <summary>
        /// Creates an HttpClient sending its requests through the given handler (e.g. an in-memory server in unit tests).
        /// </summary>
        public static HttpClient CreateHttpClient(HttpMessageHandler Handler)
        {
            var Client = new HttpClient(Handler ?? throw new ArgumentNullException(nameof(Handler)));
            Client.DefaultRequestHeaders.ExpectContinue = false;
            return Client;
        }

        /// <summary>
        /// Builds the URL of a decoded server path: each segment is percent-encoded (spaces, '#', '%', '?', accents...),
        /// and a collection URL ends with a slash.
        /// </summary>
        public Uri GetUrl(String Path, bool AsCollection)
        {
            String Trimmed = (Path ?? "").Trim('/');
            String Encoded = String.Join("/", Trimmed.Split('/').Select(Uri.EscapeDataString));
            String Url = Server + "/" + Encoded;
            if (AsCollection && !Url.EndsWith("/")) Url += "/";
            return new Uri(Url);
        }

        /// <summary>
        /// PROPFIND with all the properties. Depth 0 describes the element itself; depth 1 also lists the direct children of a
        /// folder (the folder itself is part of the result).
        /// </summary>
        /// <param name="AsCollection">Send the URL with a trailing slash (folders).</param>
        public async Task<List<WebDavItem>> PropFind(String Path, int Depth, bool AsCollection)
        {
            using (var Response = await Send(PropFindMethod, GetUrl(Path, AsCollection), Request =>
            {
                Request.Headers.Add("Depth", Depth.ToString());
                Request.Content = new StringContent(AllPropRequest, Encoding.UTF8, "application/xml");
            }).ConfigureAwait(false))
            {
                EnsureStatus(Response, "PROPFIND", MultiStatus, 200);
                using (var Body = await Response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    return MultiStatusParser.Parse(Body);
                }
            }
        }

        /// <summary>
        /// Describes an element whose kind is unknown. It is first looked up as a file if its name (last segment only) contains
        /// a '.', as a folder otherwise; if that lookup answers 404, the other kind is tried (folder "v1.2", file "README").
        /// </summary>
        /// <returns>The element, or null if it does not exist (404 for both kinds).</returns>
        public async Task<WebDavItem> GetItem(String Path)
        {
            String Name = (Path ?? "").TrimEnd('/');
            bool LooksLikeFile = Name.Substring(Name.LastIndexOf('/') + 1).Contains(".");
            return await TryGetItem(Path, !LooksLikeFile).ConfigureAwait(false) ?? await TryGetItem(Path, LooksLikeFile).ConfigureAwait(false);
        }

        /// <summary>
        /// Tells whether an element (file or folder) exists, with a single request: a PROPFIND without trailing slash finds a
        /// file, and for a folder servers either describe it or redirect to the URL with a slash (which also means it exists).
        /// </summary>
        public async Task<bool> Exists(String Path)
        {
            try
            {
                await PropFind(Path, 0, false).ConfigureAwait(false);
                return true;
            }
            catch (WebDavException ex) when (ex.StatusCode == 404)
            {
                return false;
            }
            catch (WebDavException ex) when (ex.StatusCode >= 300 && ex.StatusCode < 400)
            {
                return true;
            }
        }

        /// <summary>
        /// Downloads the content of a file.
        /// </summary>
        public async Task<byte[]> Get(String Path)
        {
            using (var Response = await Send(HttpMethod.Get, GetUrl(Path, false), null).ConfigureAwait(false))
            {
                EnsureStatus(Response, "GET", 200);
                return await Response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Uploads the content of a file, creating or replacing it.
        /// </summary>
        /// <param name="OnlyIfNew">Send "If-None-Match: *": a server that supports it answers 412 instead of replacing an existing
        /// file (e.g. created meanwhile by another client).</param>
        public async Task Put(String Path, Stream Content, bool OnlyIfNew)
        {
            using (var Response = await Send(HttpMethod.Put, GetUrl(Path, false), Request =>
            {
                Request.Content = new StreamContent(Content);
                if (OnlyIfNew) Request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
            }).ConfigureAwait(false))
            {
                EnsureStatus(Response, "PUT", 200, 201, 204);
            }
        }

        /// <summary>
        /// Deletes a file, or a folder with all its content (a WebDAV DELETE on a collection is recursive).
        /// </summary>
        public async Task Delete(String Path, bool IsCollection)
        {
            using (var Response = await Send(HttpMethod.Delete, GetUrl(Path, IsCollection), null).ConfigureAwait(false))
            {
                EnsureStatus(Response, "DELETE", 200, 202, 204);
            }
        }

        /// <summary>
        /// Creates a folder. 405 means that the name is already taken, 409 that the parent folder does not exist.
        /// </summary>
        public async Task MkCol(String Path)
        {
            using (var Response = await Send(MkColMethod, GetUrl(Path, true), null).ConfigureAwait(false))
            {
                EnsureStatus(Response, "MKCOL", 200, 201, 204);
            }
        }

        /// <summary>
        /// Moves or renames a file or folder.
        /// </summary>
        /// <param name="Overwrite">"Overwrite" header: true replaces an existing destination; false makes the server answer 412
        /// if the destination exists; null omits the header (the server default is to replace).</param>
        public async Task Move(String Source, String Destination, bool IsCollection, bool? Overwrite)
        {
            using (var Response = await Send(MoveMethod, GetUrl(Source, IsCollection), Request =>
            {
                Request.Headers.Add("Destination", GetUrl(Destination, IsCollection).AbsoluteUri);
                if (Overwrite.HasValue) Request.Headers.Add("Overwrite", Overwrite.Value ? "T" : "F");
            }).ConfigureAwait(false))
            {
                EnsureStatus(Response, "MOVE", 200, 201, 204);
            }
        }

        #region Private methods

        private async Task<WebDavItem> TryGetItem(String Path, bool AsCollection)
        {
            try
            {
                return (await PropFind(Path, 0, AsCollection).ConfigureAwait(false)).FirstOrDefault();
            }
            catch (WebDavException ex) when (ex.StatusCode == 404)
            {
                return null;
            }
            catch (WebDavException ex) when (!AsCollection && ex.StatusCode >= 300 && ex.StatusCode < 400)
            {
                //A folder looked up without trailing slash: servers may redirect to the URL with a slash. The lookup as a folder follows
                return null;
            }
        }

        /// <summary>
        /// Sends a request. The caller must dispose the response.
        /// </summary>
        private async Task<HttpResponseMessage> Send(HttpMethod Method, Uri Url, Action<HttpRequestMessage> Configure)
        {
            if (logger.IsTraceEnabled) logger.Trace($"WEBDAV {Method} {Url}");
            using (var Request = new HttpRequestMessage(Method, Url))
            {
                Configure?.Invoke(Request);
                return await Http.SendAsync(Request).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Throws a <see cref="WebDavException"/> (and disposes nothing: the caller owns the response) unless the status is expected.
        /// </summary>
        private static void EnsureStatus(HttpResponseMessage Response, String Operation, params int[] Expected)
        {
            int Status = (int)Response.StatusCode;
            if (!Expected.Contains(Status)) throw new WebDavException(Status, $"{Operation} {Response.RequestMessage?.RequestUri} failed: {Status} {Response.ReasonPhrase}");
        }

        #endregion
    }
}
