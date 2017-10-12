using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KS2Drive
{
    public class Configuration
    {
        public bool AutoMount { get; set; }
        

        //Proxy
        public bool UseHTTPProxy { get; set; }
        public String ProxyURL { get; set; }
        public bool UseProxyAuthentication { get; set; }
        public String Login { get; set; }
        public String Password { get; set; }
    }
}
