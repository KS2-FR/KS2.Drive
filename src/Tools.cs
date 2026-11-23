using KS2Drive.Config;
using Microsoft.Win32;
using System;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Linq;

namespace KS2Drive
{
    /// <summary>
    /// Miscellaneous helpers: configuration encryption, proxy setup, certificate lookup and installed product detection.
    /// </summary>
    public class Tools
    {
        /// <summary>
        /// Encrypts a string with DPAPI for the current user, using the assembly full name as entropy.
        /// The string is converted with ASCII encoding (non-ASCII characters are lost).
        /// </summary>
        /// <returns>The encrypted data as a Base64 string.</returns>
        public static string Protect(string str)
        {
            byte[] entropy = Encoding.ASCII.GetBytes(Assembly.GetExecutingAssembly().FullName);
            byte[] data = Encoding.ASCII.GetBytes(str);
            string protectedData = Convert.ToBase64String(ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser));
            return protectedData;
        }

        /// <summary>
        /// Decrypts a Base64 string produced by <see cref="Protect"/> (same user and same assembly full name required).
        /// </summary>
        public static string Unprotect(string str)
        {
            byte[] protectedData = Convert.FromBase64String(str);
            byte[] entropy = Encoding.ASCII.GetBytes(Assembly.GetExecutingAssembly().FullName);
            string data = Encoding.ASCII.GetString(ProtectedData.Unprotect(protectedData, entropy, DataProtectionScope.CurrentUser));
            return data;
        }

        /// <summary>
        /// Sets the process-wide <see cref="WebRequest.DefaultWebProxy"/> from the configuration: no proxy (mode 0 or null configuration),
        /// system proxy with default credentials (mode 1), or the user defined proxy with optional credentials (other modes).
        /// </summary>
        public static void LoadProxy(Configuration C)
        {
            if (C == null || C.HTTPProxyMode == 0)
            {
                WebRequest.DefaultWebProxy = null;
            }
            else if (C.HTTPProxyMode == 1)
            {
                WebRequest.DefaultWebProxy = WebRequest.GetSystemWebProxy();
                WebRequest.DefaultWebProxy.Credentials = System.Net.CredentialCache.DefaultCredentials;
            }
            else
            {
                if (C.UseProxyAuthentication)
                {
                    WebRequest.DefaultWebProxy = new WebProxy(C.ProxyURL, false, null, new NetworkCredential(C.ProxyLogin, C.ProxyPassword));
                }
                else
                {
                    WebRequest.DefaultWebProxy = new WebProxy(C.ProxyURL, false);
                }
            }
        }

        /// <summary>
        /// Looks up a certificate by serial number in a Windows certificate store.
        /// </summary>
        /// <param name="CertStoreName">Name of a <see cref="StoreName"/> value.</param>
        /// <param name="CertStoreLocation">Name of a <see cref="StoreLocation"/> value.</param>
        /// <param name="Serial">Certificate serial number.</param>
        /// <returns>The first matching certificate (validity is not checked), or null if none is found or the store names are invalid.</returns>
        public static X509Certificate2 FindCertificate(String CertStoreName, String CertStoreLocation, String Serial)
        {
            if (Enum.TryParse(CertStoreName, out StoreName StoreNameParsed) && Enum.TryParse(CertStoreLocation, out StoreLocation StoreLocationParsed))
            {
                X509Store store = new X509Store(StoreNameParsed, StoreLocationParsed);
                store.Open(OpenFlags.ReadOnly);
                var CertificateCollection = store.Certificates.Find(X509FindType.FindBySerialNumber, Serial, false);
                store.Close();
                if (CertificateCollection.Count > 0)
                {
                    return CertificateCollection[0];
                }
            }

            return null;
        }

        /// <summary>
        /// Checks whether an MSI product is installed by looking for its product code under the Uninstall registry keys
        /// (current user, local machine, and local machine WOW6432Node).
        /// </summary>
        /// <param name="ProductCode">MSI product code, including braces (e.g. "{...}").</param>
        public static bool IsMsiIntalled(String ProductCode)
        {
            // search in: CurrentUser
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                if (key.GetSubKeyNames().FirstOrDefault(x => x.Equals(ProductCode)) != null) return true;
            }

            // search in: LocalMachine_32
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                if (key.GetSubKeyNames().FirstOrDefault(x => x.Equals(ProductCode)) != null) return true;
            }

            // search in: LocalMachine_64
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                if (key.GetSubKeyNames().FirstOrDefault(x => x.Equals(ProductCode)) != null) return true;
            }

            return false;
        }
    }
}
