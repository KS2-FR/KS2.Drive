using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

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

        private String ConvertForAOS(String input)
        {
            if (this.Mode == WebDAVMode.AOS && input.StartsWith(this.RootPath)) return input.Substring(this.RootPath.Length);
            return input;
        }

        public new Task<bool> CreateDir(string remotePath, string name)
        {
            remotePath = ConvertForAOS(remotePath);
            return base.CreateDir(remotePath, name);
        }

        public new Task DeleteFile(string remotePath)
        {
            remotePath = ConvertForAOS(remotePath);
            return base.DeleteFile(remotePath);
        }

        public new Task DeleteFolder(string remotePath)
        {
            remotePath = ConvertForAOS(remotePath);
            return base.DeleteFolder(remotePath);
        }

        public new Task<Stream> Download(string remotePath)
        {
            remotePath = ConvertForAOS(remotePath);
            return base.Download(remotePath);
        }

        public new Task<WebDAVClient.Model.Item> GetFile(string remotePath = "/")
        {
            remotePath = ConvertForAOS(remotePath);
            return base.GetFile(remotePath);
        }

        public new Task<WebDAVClient.Model.Item> GetFolder(string remotePath = "/")
        {
            remotePath = ConvertForAOS(remotePath);
            return base.GetFolder(remotePath);
        }

        public new Task<IEnumerable<WebDAVClient.Model.Item>> List(string remotePath = "/", int? depth = 1)
        {
            remotePath = ConvertForAOS(remotePath);
            return base.List(remotePath, depth);
        }

        public new Task<bool> MoveFile(string srcFilePath, string dstFilePath)
        {
            srcFilePath = ConvertForAOS(srcFilePath);
            dstFilePath = ConvertForAOS(dstFilePath);
            return base.MoveFile(srcFilePath, dstFilePath);
        }

        public new Task<bool> MoveFolder(string srcFolderPath, string dstFolderPath)
        {
            srcFolderPath = ConvertForAOS(srcFolderPath);
            dstFolderPath = ConvertForAOS(dstFolderPath);
            return base.MoveFolder(srcFolderPath, dstFolderPath);
        }

        public new Task<bool> Upload(string remoteFilePath, Stream content, string name)
        {
            remoteFilePath = ConvertForAOS(remoteFilePath);
            return base.Upload(remoteFilePath, content, name);
        }
    }
}
