using KS2Drive.Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KS2Drive.FS
{
    class MountingHelper
    {
        public Thread t;
        public FSPService service { get; set; }
        public Configuration config { get; set; }

        public void Start()
        {
            t = new Thread(() => this.service.Run());
            t.Start();
        }

        public void Mount()
        {
            service.Mount(config);
        }

        public void Unmount()
        {
            service.Unmount();
        }
    }
}
