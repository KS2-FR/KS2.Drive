using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using WebDAVClient.Helpers;

namespace KS2Drive.FS
{
    /// <summary>
    /// WebDAV client preconfigured with the connection parameters set by <see cref="Init"/>.
    /// Its methods accept full repository paths (including the base path) and strip the base path
    /// before calling the underlying <see cref="WebDAVClient.Client"/>, which prepends it again.
    /// </summary>
    public class WebDavClient2 : WebDAVClient.Client
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();
        private static String _RootPath;
        private static String _Server;
        private static String _Login;
        private static String _Password;
        private static X509Certificate2 _ClientCert;

        /// <summary>
        /// Stores the connection parameters used by every instance. Must be called before any instance is created.
        /// </summary>
        /// <param name="Server">Server URL (scheme, host and port).</param>
        /// <param name="BasePath">Server-side base path of the document library.</param>
        /// <param name="Login">User name for authentication.</param>
        /// <param name="Password">Password for authentication.</param>
        /// <param name="ClientCert">Optional client certificate for TLS client authentication (null if not used).</param>
        public static void Init(String Server, String BasePath, String Login, String Password, X509Certificate2 ClientCert)
        {
            WebDavClient2._RootPath = BasePath;
            WebDavClient2._Server = Server;
            WebDavClient2._Login = Login;
            WebDavClient2._Password = Password;
            WebDavClient2._ClientCert = ClientCert;

            //One HttpClient shared by all the instances: connections are reused instead of opening (and leaking) a new
            //handler and connection for each file system operation. It is recreated at each mount, with the new parameters.
            HttpClient PreviousClient = WebDavClient2._SharedHttpClient;
            WebDavClient2._SharedHttpClient = CreateHttpClient(new NetworkCredential { UserName = Login, Password = Password }, null, ClientCert);
            PreviousClient?.Dispose();
        }

        /// <summary>
        /// HttpClient shared by all the instances, created by <see cref="Init"/>.
        /// </summary>
        private static HttpClient _SharedHttpClient;

        /// <summary>
        /// Creates a client using the parameters given to <see cref="Init"/>. All the instances share the same HttpClient
        /// (and therefore the same connections); creating an instance is cheap. Requests use the default HttpClient timeout (100 s).
        /// </summary>
        /// <exception cref="InvalidOperationException"><see cref="Init"/> has not been called.</exception>
        public WebDavClient2() : base(WebDavClient2._SharedHttpClient ?? throw new InvalidOperationException("Please Call Init First"))
        {
            base.Server = WebDavClient2._Server;
            base.BasePath = WebDavClient2._RootPath;
        }

        /// <summary>
        /// Removes the base path prefix from a repository path, if present, since the base client prepends it itself.
        /// </summary>
        private String ParameterConvert(String input)
        {
            //Only strip a whole path segment: "/dav" must not be stripped from "/davfolder"
            if (input.StartsWith(WebDavClient2._RootPath) && (input.Length == WebDavClient2._RootPath.Length || input[WebDavClient2._RootPath.Length] == '/')) return input.Substring(WebDavClient2._RootPath.Length);
            else return input;
        }

        /// <summary>
        /// Creates the folder <paramref name="name"/> inside the repository folder <paramref name="remotePath"/>.
        /// </summary>
        public new Task<bool> CreateDir(string remotePath, string name)
        {
            remotePath = ParameterConvert(remotePath);
            return base.CreateDir(remotePath, name);
        }

        /// <summary>
        /// Deletes the file at the given repository path.
        /// </summary>
        public new Task DeleteFile(string remotePath)
        {
            remotePath = ParameterConvert(remotePath);
            return base.DeleteFile(remotePath);
        }

        /// <summary>
        /// Deletes the folder at the given repository path.
        /// </summary>
        public new Task DeleteFolder(string remotePath)
        {
            remotePath = ParameterConvert(remotePath);
            return base.DeleteFolder(remotePath);
        }

        /// <summary>
        /// Downloads the whole content of the file at the given repository path.
        /// </summary>
        public new Task<Byte[]> Download(string remotePath)
        {
            remotePath = ParameterConvert(remotePath);
            return base.Download(remotePath);
        }

        /// <summary>
        /// Retrieves the properties of the file at the given repository path.
        /// </summary>
        public new Task<WebDAVClient.Model.Item> GetFile(string remotePath = "/")
        {
            remotePath = ParameterConvert(remotePath);
            return base.GetFile(remotePath);
        }

        /// <summary>
        /// Retrieves the properties of the folder at the given repository path.
        /// </summary>
        public new Task<WebDAVClient.Model.Item> GetFolder(string remotePath = "/")
        {
            remotePath = ParameterConvert(remotePath);
            return base.GetFolder(remotePath);
        }

        /// <summary>
        /// Lists the content of the folder at the given repository path (PROPFIND with the given depth).
        /// The result may include the folder itself.
        /// </summary>
        public new Task<IEnumerable<WebDAVClient.Model.Item>> List(string remotePath = "/", int? depth = 1)
        {
            remotePath = ParameterConvert(remotePath);
            return base.List(remotePath, depth);
        }

        /// <summary>
        /// Moves or renames a file; both paths are repository paths.
        /// </summary>
        public new Task<bool> MoveFile(string srcFilePath, string dstFilePath)
        {
            srcFilePath = ParameterConvert(srcFilePath);
            dstFilePath = ParameterConvert(dstFilePath);
            return base.MoveFile(srcFilePath, dstFilePath);
        }

        /// <summary>
        /// Moves or renames a folder; both paths are repository paths.
        /// </summary>
        public new Task<bool> MoveFolder(string srcFolderPath, string dstFolderPath)
        {
            srcFolderPath = ParameterConvert(srcFolderPath);
            dstFolderPath = ParameterConvert(dstFolderPath);
            return base.MoveFolder(srcFolderPath, dstFolderPath);
        }

        /// <summary>
        /// Uploads <paramref name="content"/> as the file <paramref name="name"/> inside the repository folder
        /// <paramref name="remoteFilePath"/>, replacing any existing file.
        /// </summary>
        public new Task<bool> Upload(string remoteFilePath, Stream content, string name)
        {
            remoteFilePath = ParameterConvert(remoteFilePath);
            return base.Upload(remoteFilePath, content, name);
        }

        /// <summary>
        /// Retrieves a file or folder from the remote repository from its local path.
        /// The element is first looked up as a file if its name (last segment of the path only, not its parent folders)
        /// contains a '.', as a folder otherwise; if that lookup answers 404, the other kind is tried. A folder named
        /// "v1.2" or a file without extension is therefore found as well, at the cost of one more request.
        /// For the library root, the display name is set to an empty string.
        /// </summary>
        /// <param name="LocalFileName">Local (Windows) path of the item.</param>
        /// <returns>The WebDAV item, or null when the server answers 404 for both kinds.</returns>
        /// <exception cref="WebDAVException">Any other WebDAV error is propagated with its original stack trace.</exception>
        public WebDAVClient.Model.Item GetRepositoryElement(String LocalFileName)
        {
            String RepositoryDocumentName = FileNode.ConvertLocalPathToRepositoryPath(LocalFileName);
            bool LooksLikeFile = Path.GetFileName(LocalFileName.TrimEnd('\\')).Contains(".");

            WebDAVClient.Model.Item RepositoryElement = LooksLikeFile ? TryGet(RepositoryDocumentName, false) : TryGet(RepositoryDocumentName, true);
            if (RepositoryElement == null) RepositoryElement = LooksLikeFile ? TryGet(RepositoryDocumentName, true) : TryGet(RepositoryDocumentName, false);

            if (RepositoryElement != null && FileNode.IsRepositoryRootPath(RepositoryDocumentName)) RepositoryElement.DisplayName = "";
            return RepositoryElement;
        }

        /// <summary>
        /// Looks up an element as a folder (URL with a trailing slash) or as a file.
        /// </summary>
        /// <returns>The WebDAV item, or null when the server answers 404. Any other error is propagated.</returns>
        private WebDAVClient.Model.Item TryGet(String RepositoryDocumentName, bool AsFolder)
        {
            try
            {
                return AsFolder ? this.GetFolder(RepositoryDocumentName).GetAwaiter().GetResult() : this.GetFile(RepositoryDocumentName).GetAwaiter().GetResult();
            }
            catch (WebDAVException ex) when (ex.GetHttpCode() == 404)
            {
                return null;
            }
        }
    }
}
