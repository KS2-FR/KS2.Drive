using Fsp.Interop;
using Newtonsoft.Json;
using System;
using System.Security.AccessControl;
using KS2Drive.WebDav;

namespace KS2Drive.FS
{
    /// <summary>
    /// A file or folder of the remote repository as seen by the file system. Holds its local and repository paths,
    /// its WinFSP file information and security descriptor, and (for opened files) its content in memory.
    /// Also provides static helpers to convert between local (Windows) paths and repository (WebDAV URL) paths.
    /// </summary>
    public class FileNode
    {
        /// <summary>
        /// Lock used to serialize file system operations (read, write, cleanup, close...) on this node.
        /// </summary>
        public object OperationLock = new object();

        [JsonIgnore]
        private static Int32 ObjetIdSequence = 0;
        [JsonIgnore]
        private static object _handlelock = new object();

        /// <summary>
        /// Number of currently open handles on this node (updated with Interlocked).
        /// </summary>
        public Int32 OpenCount;
        /// <summary>
        /// Process-unique sequential identifier of this node instance, used in logs.
        /// </summary>
        public String ObjectId;
        /// <summary>
        /// Display name of the file or folder.
        /// </summary>
        public string Name;
        /// <summary>
        /// Path of the item on the server (URL path, without trailing slash), including the document library base path.
        /// </summary>
        public String RepositoryPath;
        /// <summary>
        /// URL-decoded Windows path of the item relative to the drive root (e.g. "\folder\file.txt"). Used as the cache key.
        /// </summary>
        public String LocalPath;
        /// <summary>
        /// WinFSP file information (attributes, timestamps, sizes).
        /// </summary>
        public FileInfo FileInfo;

        /// <summary>
        /// Binary security descriptor returned to Windows for this node.
        /// </summary>
        [JsonIgnore]
        public Byte[] FileSecurity;
        /// <summary>
        /// In-memory content of the file, loaded from the server on open. May be larger than <c>FileInfo.FileSize</c>.
        /// </summary>
        [JsonIgnore]
        public Byte[] FileData;

        /// <summary>
        /// True when <see cref="FileData"/> contains local modifications not yet uploaded to the server.
        /// </summary>
        public bool HasUnflushedData { get; set; } = false;
        /// <summary>
        /// Date of the last refresh of this node (or of its folder content) from the server.
        /// </summary>
        public DateTime LastRefresh { get; set; }
        /// <summary>
        /// Path to the temporary local file containing the binary content of this FileNode (used in case of upload error
        /// in order not to lose data). Empty when no copy has been made, null when the copy could not be written.
        /// </summary>
        public string TemporaryLocalCopyPath { get; internal set; } = ""; //Path to the temporary local file containing the binary information of this FileNode (used in case of error in order not to lose data)

        /// <summary>
        /// For a folder: true once its content has been listed from the server and stored in the cache.
        /// </summary>
        public bool IsParsed = false;
        /// <summary>
        /// True once the item has been deleted on the server; pending data is then no longer flushed.
        /// </summary>
        public bool IsDeleted = false;

        private static String _DocumentLibraryPath;
        private static WebDAVMode _WebDAVMode;

        private static bool _IsInited = false;

        /// <summary>
        /// Size and last write time of the file on the server when <see cref="FileData"/> was downloaded or last uploaded.
        /// If a later refresh of the folder reports other values, the file changed on the server and the content kept in
        /// memory is obsolete.
        /// </summary>
        internal UInt64 ContentServerFileSize;
        internal UInt64 ContentServerLastWriteTime;

        /// <summary>
        /// When the last handle was closed while the content was kept in memory (see <see cref="CanReuseContent"/>).
        /// </summary>
        internal DateTime ContentRetainedAt;

        /// <summary>
        /// When the content of this node was last uploaded successfully (creation included). Used to skip a Flush that comes
        /// right after an upload: the pending changes are uploaded by the following Flush, Cleanup or Close.
        /// </summary>
        internal DateTime LastUploadAt;

        /// <summary>
        /// True for a file created on the drive and not uploaded yet: it does not exist on the server until its first upload
        /// (Flush, Cleanup or Close). Creating it on the server right away with an empty content created an empty version in the
        /// repository, could trigger server rules on an empty file, and showed an empty file to other users.
        /// </summary>
        internal bool IsNewOnServer;

        /// <summary>
        /// Builds a node from a WebDAV item: computes its repository and local paths, detects whether it is a folder,
        /// and fills the file information from the item properties. A default security descriptor is assigned.
        /// </summary>
        /// <exception cref="InvalidOperationException"><see cref="Init"/> has not been called.</exception>
        public FileNode(WebDavItem WebDavObject)
        {
            if (!FileNode._IsInited) throw new InvalidOperationException("Please Call Init First");

            lock (_handlelock)
            {
                this.ObjectId = (++ObjetIdSequence).ToString();
            }

            this.LastRefresh = DateTime.Now;
            this.Name = WebDavObject.DisplayName;

            //The path is already decoded (see WebDavItem.Path): like every path in the application
            this.RepositoryPath = WebDavObject.Path.Length > 1 ? WebDavObject.Path.TrimEnd('/') : WebDavObject.Path;
            this.LocalPath = ConvertRepositoryPathToLocalPath(this.RepositoryPath);

            //Folder or file: the standard WebDAV "resourcetype" property (<collection/> for a folder). The former heuristic of the
            //WebDAV mode (no etag and no size, or no size) made every file a folder on servers that do not send getcontentlength
            this.FileInfo.FileAttributes = WebDavObject.IsCollection ? (UInt32)System.IO.FileAttributes.Directory : (UInt32)System.IO.FileAttributes.Normal;

            if (WebDavObject.CreationDate.HasValue) this.FileInfo.CreationTime = (UInt64)WebDavObject.CreationDate.Value.ToFileTimeUtc();
            this.FileInfo.LastAccessTime = (UInt64)DateTime.Now.ToFileTimeUtc();
            if (WebDavObject.LastModified.HasValue) this.FileInfo.LastWriteTime = (UInt64)WebDavObject.LastModified.Value.ToFileTimeUtc();
            if (WebDavObject.LastModified.HasValue) this.FileInfo.ChangeTime = (UInt64)WebDavObject.LastModified.Value.ToFileTimeUtc();
            this.FileInfo.AllocationSize = WebDavObject.ContentLength.HasValue ? (UInt64)WebDavObject.ContentLength.Value : 0;
            this.FileInfo.FileSize = WebDavObject.ContentLength.HasValue ? (UInt64)WebDavObject.ContentLength.Value : 0;
            //CFN.FileInfo.IndexNumber = CFN.GetIndex(); //TEMP
            this.FileSecurity = GetDefaultSecurity();
        }

        /// <summary>
        /// Sets the static parameters shared by all nodes. Must be called before any FileNode is created.
        /// </summary>
        /// <param name="DocumentLibraryPath">Decoded server-side path of the mounted library (e.g. "/alfresco/webdav/My Site"); it maps to the drive root.</param>
        /// <param name="webDavMode">Server type (no longer used to detect folders: the resourcetype property is).</param>
        public static void Init(String DocumentLibraryPath, WebDAVMode webDavMode)
        {
            FileNode._DocumentLibraryPath = DocumentLibraryPath;
            FileNode._WebDAVMode = webDavMode;
            FileNode._IsInited = true;
        }

        /// <summary>
        /// Converts a repository path to a local path: strips the trailing slash and the document library path prefix, replaces
        /// '/' by '\' and makes sure the result starts with '\'. Repository paths are decoded (see <see cref="WebDavItem.Path"/>):
        /// nothing is decoded here, so a name like "a%41.txt" stays as is. The library path is only removed as a prefix (not
        /// elsewhere in the path), case-insensitively since servers may case it differently from the configured URL.
        /// </summary>
        public static String ConvertRepositoryPathToLocalPath(String RepositoryPath)
        {
            String Path = RepositoryPath;
            if (Path.EndsWith("/")) Path = Path.Substring(0, Path.Length - 1);

            if (Path.StartsWith(_DocumentLibraryPath, StringComparison.OrdinalIgnoreCase) && (Path.Length == _DocumentLibraryPath.Length || Path[_DocumentLibraryPath.Length] == '/'))
            {
                Path = Path.Substring(_DocumentLibraryPath.Length);
            }

            String ReworkdPath = Path.Replace('/', System.IO.Path.DirectorySeparatorChar);
            if (!ReworkdPath.StartsWith(System.IO.Path.DirectorySeparatorChar.ToString())) ReworkdPath = System.IO.Path.DirectorySeparatorChar + ReworkdPath;
            return ReworkdPath;
        }

        /// <summary>
        /// Returns true if two (decoded) repository paths designate the same element, whatever their trailing slash or case.
        /// </summary>
        internal static bool IsSameRepositoryPath(String Path1, String Path2)
        {
            if (Path1 == null || Path2 == null) return Path1 == Path2;
            return String.Equals(Path1.TrimEnd('/'), Path2.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Converts a local path to a repository path: prefixes it with the document library path, replaces '\' by '/'
        /// and strips the trailing slash. The result is not URL-encoded.
        /// </summary>
        public static String ConvertLocalPathToRepositoryPath(String LocalPath)
        {
            String ReworkdPath = _DocumentLibraryPath + LocalPath.Replace(System.IO.Path.DirectorySeparatorChar, '/');
            if (ReworkdPath.EndsWith("/")) ReworkdPath = ReworkdPath.Substring(0, ReworkdPath.Length - 1);
            return ReworkdPath;
        }

        /// <summary>
        /// Builds the binary security descriptor applied to every node: owner and group Administrators,
        /// protected DACL granting full access to LocalSystem, Administrators and Everyone.
        /// </summary>
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

        /// <summary>
        /// Returns true when the repository path is the document library path, i.e. the drive root
        /// (whatever the URL encoding, trailing slash or case).
        /// </summary>
        internal static bool IsRepositoryRootPath(String RepositoryPath)
        {
            return IsSameRepositoryPath(RepositoryPath, _DocumentLibraryPath);
        }

        /// <summary>
        /// Returns the last segment (file or folder name) of a repository path, or "\" for the library root.
        /// </summary>
        internal static String GetRepositoryDocumentName(String DocumentPath)
        {
            if (DocumentPath.EndsWith("/")) DocumentPath = DocumentPath.Substring(0, DocumentPath.Length - 1);

            if (IsSameRepositoryPath(DocumentPath, _DocumentLibraryPath)) return "\\";
            return DocumentPath.Substring(DocumentPath.LastIndexOf('/') + 1);
        }

        /// <summary>
        /// Returns the repository path of the parent folder, or null for the library root (or a path above it).
        /// </summary>
        internal static String GetRepositoryParentPath(String DocumentPath)
        {
            if (DocumentPath.EndsWith("/")) DocumentPath = DocumentPath.Substring(0, DocumentPath.Length - 1);

            if (IsSameRepositoryPath(DocumentPath, _DocumentLibraryPath)) return null;
            if (DocumentPath.Length < _DocumentLibraryPath.Length) return null;
            return DocumentPath.Substring(0, DocumentPath.LastIndexOf('/'));
        }

        /// <summary>
        /// Records that <see cref="FileData"/> is identical to the file on the server (just downloaded or uploaded).
        /// </summary>
        internal void MarkContentInSyncWithServer()
        {
            ContentServerFileSize = FileInfo.FileSize;
            ContentServerLastWriteTime = FileInfo.LastWriteTime;
        }

        /// <summary>
        /// Tells whether the content kept in memory after the last close can be reused by a new open: it must have been closed
        /// less than <paramref name="MaxAgeInSeconds"/> ago, and the file must not have changed on the server since (same size
        /// and last write time as when the content was obtained).
        /// </summary>
        internal bool CanReuseContent(Int32 MaxAgeInSeconds)
        {
            if (FileData == null) return false;
            if ((DateTime.Now - ContentRetainedAt).TotalSeconds > MaxAgeInSeconds) return false;
            return FileInfo.FileSize == ContentServerFileSize && FileInfo.LastWriteTime == ContentServerLastWriteTime;
        }

        /// <summary>
        /// Deletes the temporary recovery file created by <see cref="GenerateLocalCopy"/>, if any. Called once the content has
        /// been uploaded successfully: the copy is then obsolete and would otherwise stay in the temporary folder forever.
        /// </summary>
        internal void DeleteLocalCopy()
        {
            if (String.IsNullOrEmpty(TemporaryLocalCopyPath)) return;
            try
            {
                System.IO.File.Delete(TemporaryLocalCopyPath);
            }
            catch
            {
                //Not blocking: the file stays in the temporary folder
            }
            TemporaryLocalCopyPath = "";
        }

        /// <summary>
        /// Writes the first <c>FileInfo.FileSize</c> bytes of <see cref="FileData"/> (the buffer may be larger, rounded up to the
        /// allocation size) to a temporary file (created on first call) so that the content is not lost when it cannot be uploaded.
        /// On failure, <see cref="TemporaryLocalCopyPath"/> is set to null.
        /// </summary>
        internal void GenerateLocalCopy()
        {
            try
            {
                if (String.IsNullOrEmpty(TemporaryLocalCopyPath)) TemporaryLocalCopyPath = System.IO.Path.GetTempFileName();
                int Length = (int)Math.Min((UInt64)FileData.LongLength, FileInfo.FileSize);
                using (var Stream = new System.IO.FileStream(TemporaryLocalCopyPath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                {
                    Stream.Write(FileData, 0, Length);
                }
            }
            catch
            {
                TemporaryLocalCopyPath = null; //Cannot generate a local copy of the file. Disable the option allowing the user to save the file locally
            }
        }
    }
}
