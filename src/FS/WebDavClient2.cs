using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
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
        private static bool _IsInited = false;
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
            WebDavClient2._IsInited = true;
            WebDavClient2._ClientCert = ClientCert;
        }

        /// <summary>
        /// Creates a client using the parameters given to <see cref="Init"/>.
        /// </summary>
        /// <param name="uploadTimeout">Optional timeout for uploads; the base client default is used when null.</param>
        /// <exception cref="InvalidOperationException"><see cref="Init"/> has not been called.</exception>
        public WebDavClient2(TimeSpan? uploadTimeout = null) :
            base(new NetworkCredential { UserName = WebDavClient2._Login, Password = WebDavClient2._Password }, uploadTimeout, null, WebDavClient2._ClientCert)
        {
            if (!WebDavClient2._IsInited) throw new InvalidOperationException("Please Call Init First");
            base.Server = WebDavClient2._Server;
            base.BasePath = WebDavClient2._RootPath;
        }

        /// <summary>
        /// Removes the base path prefix from a repository path, if present, since the base client prepends it itself.
        /// </summary>
        private String ParameterConvert(String input)
        {
            if (input.StartsWith(WebDavClient2._RootPath)) return input.Substring(WebDavClient2._RootPath.Length);
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
        /// A path containing a '.' is first looked up as a file; otherwise it is looked up as a folder, then as a file.
        /// For the library root, the display name is set to an empty string.
        /// </summary>
        /// <param name="LocalFileName">Local (Windows) path of the item.</param>
        /// <returns>The WebDAV item, or null when the server answers 404.</returns>
        /// <exception cref="WebDAVException">Any other WebDAV error is rethrown.</exception>
        public WebDAVClient.Model.Item GetRepositoryElement(String LocalFileName)
        {
            String RepositoryDocumentName = FileNode.ConvertLocalPathToRepositoryPath(LocalFileName);
            WebDAVClient.Model.Item RepositoryElement = null;

            if (RepositoryDocumentName.Contains("."))
            {
                //We assume the FileName refers to a file
                try
                {
                    RepositoryElement = this.GetFile(RepositoryDocumentName).GetAwaiter().GetResult();
                    return RepositoryElement;
                }
                catch (WebDAVException ex) when (ex.GetHttpCode() == 404)
                {
                    return null;
                }
                catch (WebDAVException ex)
                {
                    throw ex;
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
            else
            {
                //We assume it's a folder
                try
                {
                    RepositoryElement = this.GetFolder(RepositoryDocumentName).GetAwaiter().GetResult();
                    if (FileNode.IsRepositoryRootPath(RepositoryDocumentName)) RepositoryElement.DisplayName = "";
                    return RepositoryElement;
                }
                catch (WebDAVException ex) when (ex.GetHttpCode() == 404)
                {
                    //Try as a file
                    try
                    {
                        RepositoryElement = this.GetFile(RepositoryDocumentName).GetAwaiter().GetResult();
                        return RepositoryElement;
                    }
                    catch (WebDAVException ex1) when (ex1.GetHttpCode() == 404)
                    {
                        return null;
                    }
                    catch (WebDAVException ex1)
                    {
                        throw ex1;
                    }
                    catch (Exception ex1)
                    {
                        throw ex1;
                    }
                }
                catch (WebDAVException ex)
                {
                    throw ex;
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
        }
    }
}
