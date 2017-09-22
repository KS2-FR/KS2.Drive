using Fsp.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KS2Drive.FS
{
    public class FileNode
    {
        public Int32 OpenCount;
        static int _handle;
        public string handle;
        public string Name;
        public String RepositoryPath;
        public String LocalPath;
        public FileInfo FileInfo;
        public Byte[] FileSecurity;
        public Byte[] FileData;
        public bool IsCreationPending { get; set; } = false;
        public bool HasUnflushedData { get; set; } = false; //TODO : Use lock ?

        private static ulong FileIndex = 0;
        private static object DictionnaryLock = new object();
        private static Dictionary<String, ulong> MappingNodeRef_FileId = new Dictionary<string, ulong>();

        public static Byte[] GetDefaultSecurity()
        {
            //TODO : Review security (handle read only)
            //https://blogs.technet.microsoft.com/askds/2008/04/18/the-security-descriptor-definition-language-of-love-part-1/
            //https://blogs.technet.microsoft.com/askds/2008/05/07/the-security-descriptor-definition-language-of-love-part-2/

            byte[] FileSecurity;
            /*
                O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)
                O:BA                Owner is Administrators
                G:BA                Group is Administrators
                D:P                 DACL is protected
                (A;;FA;;;SY)        Allow full file access to LocalSystem
                (A;;FA;;;BA)        Allow full file access to Administrators
                (A;;FA;;;WD)        Allow full file access to Everyone 
            */
            String RootSddl = "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)";
            RawSecurityDescriptor RootSecurityDescriptor = new RawSecurityDescriptor(RootSddl);
            FileSecurity = new Byte[RootSecurityDescriptor.BinaryLength];
            RootSecurityDescriptor.GetBinaryForm(FileSecurity, 0);

            return FileSecurity;
        }

        /// <summary>
        /// Gets an ulong id for a Repository file.
        /// Register the file if it now known yet
        /// </summary>
        private ulong GetIndex()
        {
            lock (DictionnaryLock)
            {
                var KnownDictionnayFile = MappingNodeRef_FileId.FirstOrDefault(x => x.Key.Equals(this.RepositoryPath));
                if (KnownDictionnayFile.Key == null)
                {
                    ulong FileId = ++FileIndex;
                    MappingNodeRef_FileId.Add(this.RepositoryPath, FileId);
                    return FileId;
                }
                else
                {
                    return KnownDictionnayFile.Value;
                }
            }
        }

        internal static FileNode CreateFromWebDavObject(RepositoryElement WebDavObject, WebDAVMode webDavMode)
        {
            FileNode CFN = new FileNode();

            CFN.Name = WebDavObject.DisplayName;

            if (Uri.TryCreate(WebDavObject.Href, UriKind.Absolute, out Uri ParsedUri))
            {
                CFN.RepositoryPath = new Uri(WebDavObject.Href).PathAndQuery;
            }
            else
            {
                CFN.RepositoryPath = WebDavObject.Href;
            }

            if (CFN.RepositoryPath[CFN.RepositoryPath.Length - 1] == '/' && CFN.RepositoryPath.Length > 1) CFN.RepositoryPath = CFN.RepositoryPath.Remove(CFN.RepositoryPath.Length - 1);

            if (webDavMode == WebDAVMode.AOS) //AOS
            {
                if (WebDavObject.IsCollection)
                {
                    CFN.FileInfo.FileAttributes = (UInt32)System.IO.FileAttributes.Directory;
                }
                else
                {
                    CFN.FileInfo.FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                }
            }
            else //Webdav
            {
                if (WebDavObject.Etag == null && WebDavObject.ContentLength == 0)
                {
                    CFN.FileInfo.FileAttributes = (UInt32)System.IO.FileAttributes.Directory;
                }
                else
                {
                    CFN.FileInfo.FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                }
            }

            CFN.LocalPath = WebDavObject.LocalFileName;
            if (WebDavObject.CreationDate.HasValue) CFN.FileInfo.CreationTime = (UInt64)WebDavObject.CreationDate.Value.ToFileTimeUtc();
            CFN.FileInfo.LastAccessTime = (UInt64)DateTime.Now.ToFileTimeUtc();
            if (WebDavObject.LastModified.HasValue) CFN.FileInfo.LastWriteTime = (UInt64)WebDavObject.LastModified.Value.ToFileTimeUtc();
            if (WebDavObject.LastModified.HasValue) CFN.FileInfo.ChangeTime = (UInt64)WebDavObject.LastModified.Value.ToFileTimeUtc();
            CFN.FileInfo.AllocationSize = WebDavObject.ContentLength.HasValue ? (UInt64)WebDavObject.ContentLength.Value : 0;
            CFN.FileInfo.FileSize = WebDavObject.ContentLength.HasValue ? (UInt64)WebDavObject.ContentLength.Value : 0;
            CFN.FileInfo.IndexNumber = CFN.GetIndex();
            CFN.FileSecurity = GetDefaultSecurity();

            return CFN;
        }
    }
}
