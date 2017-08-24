using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KS2Drive.FS
{
    public enum FlushMode
    {
        FlushAtWrite,
        FlushAtCleanup
    }

    public enum WebDAVMode
    {
        WebDAV = 0,
        AOS = 1
    }
}
