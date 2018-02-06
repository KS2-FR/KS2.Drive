using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using WebDAVClient.Helpers;

namespace KS2Drive.FS
{
    public class WebDavClient2 : WebDAVClient.Client
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();
        private static WebDAVMode _Mode;
        private static String _RootPath;
        private static String _Server;
        private static String _Login;
        private static String _Password;
        private static bool _IsInited = false;

        public static void Init(WebDAVMode mode, String Server, String BasePath, String Login, String Password)
        {
            WebDavClient2._Mode = mode;
            WebDavClient2._RootPath = BasePath;
            WebDavClient2._Server = Server;
            WebDavClient2._Login = Login;
            WebDavClient2._Password = Password;
            WebDavClient2._IsInited = true;
        }

        public WebDavClient2(TimeSpan? uploadTimeout = null) :
            base(new NetworkCredential { UserName = WebDavClient2._Login, Password = WebDavClient2._Password }, uploadTimeout, null)
        {
            if (!WebDavClient2._IsInited) throw new InvalidOperationException("Please Call Init First");
            base.Server = WebDavClient2._Server;
            base.BasePath = WebDavClient2._RootPath;
        }

        private String ParameterConvert(String input)
        {
            if (input.StartsWith(WebDavClient2._RootPath)) return input.Substring(WebDavClient2._RootPath.Length);
            else return input;
        }

        public new Task<bool> CreateDir(string remotePath, string name)
        {
            remotePath = ParameterConvert(remotePath);
            return base.CreateDir(remotePath, name);
        }

        public new Task DeleteFile(string remotePath)
        {
            remotePath = ParameterConvert(remotePath);
            return base.DeleteFile(remotePath);
        }

        public new Task DeleteFolder(string remotePath)
        {
            remotePath = ParameterConvert(remotePath);
            return base.DeleteFolder(remotePath);
        }

        public new Task<Byte[]> Download(string remotePath)
        {
            remotePath = ParameterConvert(remotePath);
            return base.Download(remotePath);
        }

        public new Task<WebDAVClient.Model.Item> GetFile(string remotePath = "/")
        {
            logger.Trace($"DAV2 GetFile {remotePath}");
            remotePath = ParameterConvert(remotePath);
            return base.GetFile(remotePath);
        }

        public new Task<WebDAVClient.Model.Item> GetFolder(string remotePath = "/")
        {
            logger.Trace($"DAV2 GetFolder {remotePath}");
            remotePath = ParameterConvert(remotePath);
            return base.GetFolder(remotePath);
        }

        public new Task<IEnumerable<WebDAVClient.Model.Item>> List(string remotePath = "/", int? depth = 1)
        {
            logger.Trace($"DAV2 List {remotePath}");
            remotePath = ParameterConvert(remotePath);
            return base.List(remotePath, depth);
        }

        public new Task<bool> MoveFile(string srcFilePath, string dstFilePath)
        {
            srcFilePath = ParameterConvert(srcFilePath);
            dstFilePath = ParameterConvert(dstFilePath);
            return base.MoveFile(srcFilePath, dstFilePath);
        }

        public new Task<bool> MoveFolder(string srcFolderPath, string dstFolderPath)
        {
            srcFolderPath = ParameterConvert(srcFolderPath);
            dstFolderPath = ParameterConvert(dstFolderPath);
            return base.MoveFolder(srcFolderPath, dstFolderPath);
        }

        public new Task<bool> Upload(string remoteFilePath, Stream content, string name)
        {
            remoteFilePath = ParameterConvert(remoteFilePath);
            return base.Upload(remoteFilePath, content, name);
        }

        /// <summary>
        /// Retrieve a file or folder from the remote repo
        /// Return either a RepositoryElement or a FileSystem Error Message
        /// </summary>
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
