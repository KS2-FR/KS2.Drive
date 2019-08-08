using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KS2Drive.Config
{
    public class Configuration
    {
        public String Path;
        public String Name;
        public bool IsMounted = false;

        public static Configuration Load(String ConfigurationFilePath)
        {
            if (File.Exists(ConfigurationFilePath))
            {
                try
                {
                    var C = JsonConvert.DeserializeObject<Configuration>(Tools.Unprotect(File.ReadAllText(ConfigurationFilePath)));
                    C.MountAsNetworkDrive = true;
                    if (C.ServerPassword == null)
                    {
                        C.IsConfigured = false;
                    }
                    C.Path = ConfigurationFilePath;
                    return C;
                }
                catch
                {
                    return new Configuration() { IsConfigured = false, HTTPProxyMode = 0, Path = ConfigurationFilePath };
                }
            }
            else
            {
                return new Configuration() { IsConfigured = false, HTTPProxyMode = 0, Path = ConfigurationFilePath };
            }
        }

        public void Save()
        {
            File.WriteAllText(Path, Tools.Protect(JsonConvert.SerializeObject(this)));
        }

        public bool ShouldSerializeServerPassword() => RememberPassword;

        [JsonIgnore]
        public bool IsConfigured { get; set; } = true;

        //Startup
        public bool AutoMount { get; set; } = true;
        public bool AutoStart { get; set; } = true;

        //Drive Parameter
        public String DriveLetter { get; set; }
        public String ServerURL { get; set; }
        public Int32 ServerType { get; set; } = 0;
        public String ServerLogin { get; set; }
        public String ServerPassword { get; set; }
        public bool RememberPassword { get; set; } = false;
        public Int32 KernelCacheMode { get; set; } = -1;
        public Int32 FlushMode { get; set; } = 0;
        public bool SyncOps { get; set; } = false;
        public bool PreLoading { get; set; } = true;
        public bool MountAsNetworkDrive { get; set; } = true;

        //Proxy
        public Int16 HTTPProxyMode { get; set; } = 0; //0 = no proxy, 1 = default proxy, 2 = user defined proxy
        public String ProxyURL { get; set; } = "";
        public bool UseProxyAuthentication { get; set; } = false;
        public String ProxyLogin { get; set; } = "";
        public String ProxyPassword { get; set; } = "";

        //Client cert
        public bool UseClientCertForAuthentication { get; set; } = false;
        public String CertStoreName { get; set; }
        public String CertStoreLocation { get; set; }
        public String CertSerial { get; set; }
    }
}
