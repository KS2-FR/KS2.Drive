using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KS2Drive.Config
{
    /// <summary>
    /// Application settings, persisted as JSON encrypted with DPAPI for the current user (see <see cref="Tools.Protect"/>).
    /// </summary>
    public class Configuration
    {
        /// <summary>
        /// Path of the configuration file this instance was loaded from and is saved to.
        /// </summary>
        public String Path;

        /// <summary>
        /// False when no valid configuration file could be loaded. Not persisted.
        /// </summary>
        [JsonIgnore]
        public bool IsConfigured { get; set; } = true;

        //Startup
        /// <summary>
        /// Mount the drive automatically when the application starts.
        /// </summary>
        public bool AutoMount { get; set; }
        /// <summary>
        /// Start the application with Windows.
        /// </summary>
        public bool AutoStart { get; set; }

        //Drive Parameter
        /// <summary>
        /// Drive letter to mount on, without the colon (e.g. "Z").
        /// </summary>
        public String DriveLetter { get; set; }
        /// <summary>
        /// Full URL of the WebDAV root to mount (server and document library path).
        /// </summary>
        public String ServerURL { get; set; }
        /// <summary>
        /// Server type, as an integer value of <see cref="KS2Drive.FS.WebDAVMode"/> (0 = WebDAV, 1 = AOS). Always 0 (WebDAV): AOS is no
        /// longer offered and <see cref="Load"/> switches configurations saved in AOS mode to WebDAV.
        /// </summary>
        public Int32 ServerType { get; set; } = 0;
        /// <summary>
        /// Login used to authenticate on the server.
        /// </summary>
        public String ServerLogin { get; set; }
        /// <summary>
        /// Password used to authenticate on the server.
        /// </summary>
        public String ServerPassword { get; set; }
        /// <summary>
        /// Windows kernel cache mode, as an integer value of <see cref="KS2Drive.FS.KernelCacheMode"/> (default -1 = data and metadata).
        /// </summary>
        public Int32 KernelCacheMode { get; set; } = -1;
        /// <summary>
        /// When written data is uploaded, as an integer value of <see cref="KS2Drive.FS.FlushMode"/> (default 1 = at cleanup).
        /// </summary>
        public Int32 FlushMode { get; set; } = 1;
        /// <summary>
        /// Passed to WinFSP's FileSystemHost.Mount as the Synchronized flag: when true, file system operations are serialized.
        /// </summary>
        public bool SyncOps { get; set; } = false;
        /// <summary>
        /// Pre-load sub-folder listings in the background when a folder is listed.
        /// </summary>
        public bool PreLoading { get; set; } = true;
        /// <summary>
        /// Mount the drive as a network drive (UNC prefix) instead of a local disk.
        /// </summary>
        public bool MountAsNetworkDrive { get; set; } = false;

        //Proxy
        /// <summary>
        /// HTTP proxy mode: 0 = no proxy, 1 = system default proxy, 2 = user defined proxy (<see cref="ProxyURL"/>).
        /// </summary>
        public Int16 HTTPProxyMode { get; set; } = 0; //0 = no proxy, 1 = default proxy, 2 = user defined proxy
        /// <summary>
        /// URL of the user defined proxy (used when <see cref="HTTPProxyMode"/> is 2).
        /// </summary>
        public String ProxyURL { get; set; } = "";
        /// <summary>
        /// Authenticate on the user defined proxy with <see cref="ProxyLogin"/> / <see cref="ProxyPassword"/>.
        /// </summary>
        public bool UseProxyAuthentication { get; set; } = false;
        /// <summary>
        /// Login for the user defined proxy.
        /// </summary>
        public String ProxyLogin { get; set; } = "";
        /// <summary>
        /// Password for the user defined proxy.
        /// </summary>
        public String ProxyPassword { get; set; } = "";

        //Client cert
        /// <summary>
        /// Authenticate on the server with a client certificate from the Windows certificate store.
        /// </summary>
        public bool UseClientCertForAuthentication { get; set; } = false;
        /// <summary>
        /// Name of the certificate store, as a <see cref="System.Security.Cryptography.X509Certificates.StoreName"/> value name (e.g. "My").
        /// </summary>
        public String CertStoreName { get; set; }
        /// <summary>
        /// Location of the certificate store, as a <see cref="System.Security.Cryptography.X509Certificates.StoreLocation"/> value name (e.g. "CurrentUser").
        /// </summary>
        public String CertStoreLocation { get; set; }
        /// <summary>
        /// Serial number of the client certificate.
        /// </summary>
        public String CertSerial { get; set; }

        /// <summary>
        /// Loads and decrypts the configuration file. A configuration saved in the legacy format of previous releases is
        /// migrated to the current format. If the file does not exist or cannot be read/decrypted/parsed, returns a default
        /// configuration with <see cref="IsConfigured"/> set to false; an unreadable file is first kept aside as
        /// <c>config.json.unreadable</c> so that the next save does not destroy it.
        /// </summary>
        /// <param name="ConfigurationFilePath">Path of the encrypted configuration file.</param>
        public static Configuration Load(String ConfigurationFilePath)
        {
            if (File.Exists(ConfigurationFilePath))
            {
                try
                {
                    var C = JsonConvert.DeserializeObject<Configuration>(Tools.Unprotect(File.ReadAllText(ConfigurationFilePath), out bool IsLegacyFormat));
                    C.Path = ConfigurationFilePath;

                    //AOS is no longer offered (the server type cannot be chosen in the configuration window any more):
                    //a configuration saved in AOS mode switches to standard WebDAV (KS2Drive.FS.WebDAVMode.WebDAV = 0)
                    C.ServerType = 0;
                    //FlushAtWrite (0) is no longer offered: it uploaded the whole file after every write (KS2Drive.FS.FlushMode.FlushAtCleanup = 1)
                    C.FlushMode = 1;

                    if (IsLegacyFormat)
                    {
                        //Encrypt it again in the current format, which does not depend on the application version
                        try
                        {
                            C.Save();
                        }
                        catch
                        {
                            //Not blocking: the configuration has been loaded, the migration will be done at the next save
                        }
                    }

                    return C;
                }
                catch
                {
                    try
                    {
                        File.Copy(ConfigurationFilePath, ConfigurationFilePath + ".unreadable", true);
                    }
                    catch
                    {
                        //Not blocking
                    }
                    return new Configuration() { IsConfigured = false, HTTPProxyMode = 0, Path = ConfigurationFilePath };
                }
            }
            else
            {
                return new Configuration() { IsConfigured = false, HTTPProxyMode = 0, Path = ConfigurationFilePath };
            }
        }

        /// <summary>
        /// Serializes the configuration to JSON, encrypts it and writes it to <see cref="Path"/>.
        /// </summary>
        public void Save()
        {
            File.WriteAllText(Path, Tools.Protect(JsonConvert.SerializeObject(this)));
        }
    }
}
