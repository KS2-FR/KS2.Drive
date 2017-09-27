using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace KS2Drive.FS
{
    public class WebDavClient2 : WebDAVClient.Client
    {
        private WebDAVMode Mode;
        private String RootPath;

        public WebDavClient2(WebDAVMode mode, String Server, String BasePath, String Login, String Password, TimeSpan? uploadTimeout = null, IWebProxy proxy = null) :
            base(new NetworkCredential { UserName = Login, Password = Password }, uploadTimeout, proxy)
        {
            this.Mode = mode;
            base.Server = Server;
            base.BasePath = BasePath;
            this.RootPath = BasePath;
        }

        private String ParameterConvert(String input)
        {
            if (input.StartsWith(this.RootPath)) return input.Substring(this.RootPath.Length);
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
            remotePath = ParameterConvert(remotePath);
            return base.GetFile(remotePath);
        }

        public new Task<WebDAVClient.Model.Item> GetFolder(string remotePath = "/")
        {
            remotePath = ParameterConvert(remotePath);
            return base.GetFolder(remotePath);
        }

        public new Task<IEnumerable<WebDAVClient.Model.Item>> List(string remotePath = "/", int? depth = 1)
        {
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
    }
}
