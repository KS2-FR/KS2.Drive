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
    public class Tools
    {
        public static string Protect(string str)
        {
            byte[] entropy = Encoding.ASCII.GetBytes(Assembly.GetExecutingAssembly().FullName);
            byte[] data = Encoding.ASCII.GetBytes(str);
            string protectedData = Convert.ToBase64String(ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser));
            return protectedData;
        }

        public static string Unprotect(string str)
        {
            byte[] protectedData = Convert.FromBase64String(str);
            byte[] entropy = Encoding.ASCII.GetBytes(Assembly.GetExecutingAssembly().FullName);
            string data = Encoding.ASCII.GetString(ProtectedData.Unprotect(protectedData, entropy, DataProtectionScope.CurrentUser));
            return data;
        }

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
