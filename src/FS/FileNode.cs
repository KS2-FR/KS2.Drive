using Fsp.Interop;
using Newtonsoft.Json;
using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace KS2Drive.FS
{
    public class FileNode
    {
        public object OperationLock = new object();

        [JsonIgnore]
        private static Int32 ObjetIdSequence = 0;
        [JsonIgnore]
        private static object _handlelock = new object();

        public Int32 OpenCount;
        public String ObjectId;
        public string Name;
        public String RepositoryPath;
        public String LocalPath;
        public FileInfo FileInfo;

        [JsonIgnore]
        public Byte[] FileSecurity;
        [JsonIgnore]
        public Byte[] FileData;

        public bool HasUnflushedData { get; set; } = false;
        public DateTime LastRefresh { get; set; }
        public string TemporaryLocalCopyPath { get; internal set; } = ""; //Path to the temporary local file containing the binary information of this FileNode (used in case of error in order not to lose data)

        public bool IsParsed = false;
        public bool IsDeleted = false;

        private static String _DocumentLibraryPath;
        private static WebDAVMode _WebDAVMode;

        private AnonymousPipeServerStream UploadStream = null;
        private UInt64 UploadOffset;
        private Task<bool> UploadTask = null;

        private static bool _IsInited = false;

        public static void Init(String DocumentLibraryPath, WebDAVMode webDavMode)
        {
            FileNode._DocumentLibraryPath = DocumentLibraryPath;
            FileNode._WebDAVMode = webDavMode;
            FileNode._IsInited = true;
        }

        public FileNode(WebDAVClient.Model.Item WebDavObject)
        {
            if (!FileNode._IsInited) throw new InvalidOperationException("Please Call Init First");

            lock (_handlelock)
            {
                this.ObjectId = (++ObjetIdSequence).ToString();
            }

            this.LastRefresh = DateTime.Now;
            this.Name = WebDavObject.DisplayName;

            if (Uri.TryCreate(WebDavObject.Href, UriKind.Absolute, out Uri ParsedUri))
            {
                this.RepositoryPath = new Uri(WebDavObject.Href).PathAndQuery;
            }
            else
            {
                this.RepositoryPath = WebDavObject.Href;
            }
            this.RepositoryPath = this.RepositoryPath.Replace("//", "/");

            this.LocalPath = HttpUtility.UrlDecode(ConvertRepositoryPathToLocalPath(this.RepositoryPath));

            if (this.RepositoryPath[this.RepositoryPath.Length - 1] == '/' && this.RepositoryPath.Length > 1) this.RepositoryPath = this.RepositoryPath.Remove(this.RepositoryPath.Length - 1);

            if (FileNode._WebDAVMode == WebDAVMode.AOS) //AOS
            {
                if (WebDavObject.IsCollection)
                {
                    this.FileInfo.FileAttributes = (UInt32)System.IO.FileAttributes.Directory;
                }
                else
                {
                    this.FileInfo.FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                }
            }
            else //Webdav
            {
                //Note : Detecting a webDAV directory from properties can change from one implementation to another. The folowing test is subject to evolve when testing new webDAV servers
                if ((WebDavObject.Etag == null && WebDavObject.ContentLength == 0) || (WebDavObject.ContentLength == null))
                {
                    this.FileInfo.FileAttributes = (UInt32)System.IO.FileAttributes.Directory;
                }
                else
                {
                    this.FileInfo.FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                }
            }

            if (WebDavObject.CreationDate.HasValue) this.FileInfo.CreationTime = (UInt64)WebDavObject.CreationDate.Value.ToFileTimeUtc();
            this.FileInfo.LastAccessTime = (UInt64)DateTime.Now.ToFileTimeUtc();
            if (WebDavObject.LastModified.HasValue) this.FileInfo.LastWriteTime = (UInt64)WebDavObject.LastModified.Value.ToFileTimeUtc();
            if (WebDavObject.LastModified.HasValue) this.FileInfo.ChangeTime = (UInt64)WebDavObject.LastModified.Value.ToFileTimeUtc();
            this.FileInfo.AllocationSize = WebDavObject.ContentLength.HasValue ? (UInt64)WebDavObject.ContentLength.Value : 0;
            this.FileInfo.FileSize = WebDavObject.ContentLength.HasValue ? (UInt64)WebDavObject.ContentLength.Value : 0;
            //CFN.FileInfo.IndexNumber = CFN.GetIndex(); //TEMP
            this.FileSecurity = GetDefaultSecurity();
        }

        public bool PendingUpload(UInt64 Offset)
        {
            return (UploadStream != null && UploadOffset < Offset);
        }

        public bool FlushUpload()
        {
            if (UploadStream != null)
            {
                UploadStream.Close();
                bool result = UploadTask.GetAwaiter().GetResult();
                UploadStream = null;
                UploadTask = null;
                return result;
            }
            return false;
        }

        public void Upload(byte[] Data, UInt64 Offset, UInt32 Length)
        {
            if (UploadStream != null && UploadOffset != Offset)
            {
                FlushUpload();
            }
            if (UploadStream == null)
            {
                UploadStream = new AnonymousPipeServerStream();
                UploadOffset = Offset;
                var Proxy = new WebDavClient2(Timeout.InfiniteTimeSpan);
                var PipeStream = new AnonymousPipeClientStream(PipeDirection.In, UploadStream.ClientSafePipeHandle);
                if (UploadOffset == 0)
                {
                    UploadTask = Proxy.Upload(GetRepositoryParentPath(RepositoryPath), PipeStream, Name);
                }
                else
                {
                    throw new Exception("Partial upload not yet implemented");
                }
            }
            if (Data != null)
            {
                UploadStream.Write(Data, 0, (int)Length);
                UploadOffset += Length;
            }
        }

        public static String ConvertRepositoryPathToLocalPath(String CMISPath)
        {
            if (CMISPath.EndsWith("/")) CMISPath = CMISPath.Substring(0, CMISPath.Length - 1);

            if (!_DocumentLibraryPath.Equals("")) CMISPath = CMISPath.Replace(_DocumentLibraryPath, "");
            String ReworkdPath = CMISPath.Replace('/', System.IO.Path.DirectorySeparatorChar);
            if (!ReworkdPath.StartsWith(System.IO.Path.DirectorySeparatorChar.ToString())) ReworkdPath = System.IO.Path.DirectorySeparatorChar + ReworkdPath;
            return ReworkdPath;
        }

        public static String ConvertLocalPathToRepositoryPath(String LocalPath)
        {
            String ReworkdPath = _DocumentLibraryPath + LocalPath.Replace(System.IO.Path.DirectorySeparatorChar, '/');
            if (ReworkdPath.EndsWith("/")) ReworkdPath = ReworkdPath.Substring(0, ReworkdPath.Length - 1);
            return ReworkdPath;
        }

        public static Byte[] GetDefaultSecurity()
        {
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

        ///// <summary>
        ///// Gets an ulong id for a Repository file.
        ///// Register the file if it now known yet
        ///// </summary>
        //private ulong GetIndex()
        //{
        //    lock (DictionnaryLock)
        //    {
        //        var KnownDictionnayFile = MappingNodeRef_FileId.FirstOrDefault(x => x.Key.Equals(this.RepositoryPath));
        //        if (KnownDictionnayFile.Key == null)
        //        {
        //            ulong FileId = ++FileIndex;
        //            MappingNodeRef_FileId.Add(this.RepositoryPath, FileId);
        //            return FileId;
        //        }
        //        else
        //        {
        //            return KnownDictionnayFile.Value;
        //        }
        //    }
        //}

        internal static bool IsRepositoryRootPath(String RepositoryPath)
        {
            return RepositoryPath.Equals(_DocumentLibraryPath);
        }

        internal static String GetRepositoryDocumentName(String DocumentPath)
        {
            if (DocumentPath.EndsWith("/")) DocumentPath = DocumentPath.Substring(0, DocumentPath.Length - 1);

            if (DocumentPath.Equals(_DocumentLibraryPath)) return "\\";
            return DocumentPath.Substring(DocumentPath.LastIndexOf('/') + 1);
        }

        internal static String GetRepositoryParentPath(String DocumentPath)
        {
            if (DocumentPath.EndsWith("/")) DocumentPath = DocumentPath.Substring(0, DocumentPath.Length - 1);

            if (DocumentPath.Equals(_DocumentLibraryPath)) return "";
            if (DocumentPath.Length < _DocumentLibraryPath.Length) return null;
            return DocumentPath.Substring(0, DocumentPath.LastIndexOf('/'));
        }

        //Generate a local copy of the FileNode
        internal void GenerateLocalCopy()
        {
            if (String.IsNullOrEmpty(TemporaryLocalCopyPath)) TemporaryLocalCopyPath = System.IO.Path.GetTempFileName();
            try
            {
                System.IO.File.WriteAllBytes(TemporaryLocalCopyPath, FileData);
            }
            catch
            {
                TemporaryLocalCopyPath = null; //Cannot generate a local copy of the file. Disable the option allowing the user to save the file locally
            }
        }
    }
}
