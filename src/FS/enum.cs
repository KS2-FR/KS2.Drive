using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KS2Drive.FS
{
    public enum FlushMode
    {
        FlushAtWrite = 0,
        FlushAtCleanup =1
    }

    public enum WebDAVMode
    {
        WebDAV = 0,
        AOS = 1
    }

    //From WinFPS documentation : Windows Kernel Cache management
    //An infinite FileInfoTimeout, which enables caching of metadata and data.
    //A FileInfoTimeout of 1s(second), which enables caching of metadata but disables caching of data.
    //A FileInfoTimeout of 0, which completely disables caching.
    public enum KernelCacheMode
    {
        DataAndMetaData = -1,            
        Disabled = 0,
        MetaDataOnly = 1000
    }

    public enum CacheMode
    {
        Enabled,
        Disabled
    }
}
