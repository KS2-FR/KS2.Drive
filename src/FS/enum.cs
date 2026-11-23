using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KS2Drive.FS
{
    /// <summary>
    /// Defines when locally written file data is uploaded (flushed) to the remote server.
    /// Stored as an integer in <see cref="KS2Drive.Config.Configuration.FlushMode"/>.
    /// </summary>
    public enum FlushMode
    {
        /// <summary>Upload the whole file to the server on every write operation.</summary>
        FlushAtWrite = 0,
        /// <summary>Keep written data in memory and upload it when the file handle is cleaned up/closed.</summary>
        FlushAtCleanup = 1
    }

    /// <summary>
    /// Type of remote server. Stored as an integer in <see cref="KS2Drive.Config.Configuration.ServerType"/>.
    /// </summary>
    public enum WebDAVMode
    {
        /// <summary>Generic WebDAV server. Folders are detected heuristically from the item properties (ETag / content length).</summary>
        WebDAV = 0,
        /// <summary>Alfresco Office Services (AOS) server. Folders are detected through the WebDAV collection flag.</summary>
        AOS = 1
    }

    //From WinFSP documentation : Windows Kernel Cache management
    //An infinite FileInfoTimeout, which enables caching of metadata and data.
    //A FileInfoTimeout of 1s(second), which enables caching of metadata but disables caching of data.
    //A FileInfoTimeout of 0, which completely disables caching.
    /// <summary>
    /// Windows kernel cache behavior. Each value is the WinFSP FileInfoTimeout (in milliseconds) to apply.
    /// Stored as an integer in <see cref="KS2Drive.Config.Configuration.KernelCacheMode"/>.
    /// </summary>
    public enum KernelCacheMode
    {
        /// <summary>Infinite FileInfoTimeout (-1, i.e. UInt32.MaxValue): caches both file metadata and data.</summary>
        DataAndMetaData = -1,            
        /// <summary>FileInfoTimeout of 0: kernel caching is completely disabled.</summary>
        Disabled = 0,
        /// <summary>FileInfoTimeout of 1 second: caches metadata only, data caching is disabled.</summary>
        MetaDataOnly = 1000
    }

    /// <summary>
    /// Enables or disables the in-process <see cref="CacheManager"/> file node cache.
    /// </summary>
    public enum CacheMode
    {
        /// <summary>File nodes and folder listings are cached in memory.</summary>
        Enabled,
        /// <summary>No caching: every lookup/listing goes to the server.</summary>
        Disabled
    }
}
