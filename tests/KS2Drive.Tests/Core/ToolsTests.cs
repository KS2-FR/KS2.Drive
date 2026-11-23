using KS2Drive.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace KS2Drive.Tests.Core
{
    [TestClass]
    public class ToolsTests
    {
        private IWebProxy _OriginalProxy;

        [TestInitialize]
        public void SaveProxy()
        {
            _OriginalProxy = WebRequest.DefaultWebProxy;
        }

        [TestCleanup]
        public void RestoreProxy()
        {
            WebRequest.DefaultWebProxy = _OriginalProxy;
        }

        /// <summary>
        /// Encrypts data the way releases up to 0.8.6.1 did: ASCII data, entropy = assembly full name (ASCII).
        /// </summary>
        internal static string LegacyProtect(string Data, string Version)
        {
            byte[] Entropy = Encoding.ASCII.GetBytes($"KS2Drive, Version={Version}, Culture=neutral, PublicKeyToken=null");
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.ASCII.GetBytes(Data), Entropy, DataProtectionScope.CurrentUser));
        }

        #region Protect / Unprotect

        [TestMethod]
        public void Protect_ReturnsBase64DifferentFromInput()
        {
            string Protected = Tools.Protect("secret value");
            Assert.IsFalse(String.IsNullOrEmpty(Protected));
            Assert.AreNotEqual("secret value", Protected);
            byte[] Raw = Convert.FromBase64String(Protected); //Throws if not Base64
            Assert.IsTrue(Raw.Length > 0);
            Assert.IsFalse(Encoding.UTF8.GetString(Raw).Contains("secret value"));
        }

        [TestMethod]
        public void Protect_IsNotDeterministic()
        {
            //DPAPI uses a random salt: two encryptions of the same data differ but both decrypt
            string P1 = Tools.Protect("same");
            string P2 = Tools.Protect("same");
            Assert.AreNotEqual(P1, P2);
            Assert.AreEqual("same", Tools.Unprotect(P1, out _));
            Assert.AreEqual("same", Tools.Unprotect(P2, out _));
        }

        [DataTestMethod]
        [DataRow("hello")]
        [DataRow("")]
        [DataRow("{\"ServerURL\":\"https://server/alfresco/aos\",\"ServerPassword\":\"p@ss\"}")]
        [DataRow("café € 日本語 \U0001F600")]
        public void ProtectUnprotect_RoundTrip_CurrentFormat(string Value)
        {
            string Result = Tools.Unprotect(Tools.Protect(Value), out bool IsLegacy);
            Assert.AreEqual(Value, Result);
            Assert.IsFalse(IsLegacy);
        }

        [TestMethod]
        public void Protect_UsesUtf8AndFixedEntropy()
        {
            string Protected = Tools.Protect("été");
            byte[] Clear = ProtectedData.Unprotect(Convert.FromBase64String(Protected), Encoding.UTF8.GetBytes("KS2Drive.Configuration"), DataProtectionScope.CurrentUser);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("été"), Clear);
        }

        [DataTestMethod]
        [DataRow("0.8.4.0")]
        [DataRow("0.8.3.0")]
        [DataRow("0.8.2.0")]
        [DataRow("0.8.1.0")]
        [DataRow("0.8.0.0")]
        [DataRow("1.0.0.0")]
        public void Unprotect_LegacyFormat_IsDecryptedAndFlagged(string Version)
        {
            string Legacy = LegacyProtect("{\"DriveLetter\":\"Z\"}", Version);
            string Result = Tools.Unprotect(Legacy, out bool IsLegacy);
            Assert.AreEqual("{\"DriveLetter\":\"Z\"}", Result);
            Assert.IsTrue(IsLegacy);
        }

        [TestMethod]
        public void Unprotect_LegacyFormatOfUnknownVersion_Throws()
        {
            string Legacy = LegacyProtect("data", "0.9.0.0");
            Assert.ThrowsException<CryptographicException>(() => Tools.Unprotect(Legacy, out _));
        }

        [TestMethod]
        public void Unprotect_WrongEntropy_ThrowsCryptographicException()
        {
            string Other = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes("data"), Encoding.UTF8.GetBytes("Another.Entropy"), DataProtectionScope.CurrentUser));
            Assert.ThrowsException<CryptographicException>(() => Tools.Unprotect(Other, out _));
        }

        [TestMethod]
        public void Unprotect_NoEntropy_ThrowsCryptographicException()
        {
            string Other = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes("data"), null, DataProtectionScope.CurrentUser));
            Assert.ThrowsException<CryptographicException>(() => Tools.Unprotect(Other, out _));
        }

        [TestMethod]
        public void Unprotect_CorruptedData_ThrowsCryptographicException()
        {
            byte[] Raw = Convert.FromBase64String(Tools.Protect("data"));
            Raw[Raw.Length - 1] ^= 0xFF;
            Raw[Raw.Length / 2] ^= 0xFF;
            Assert.ThrowsException<CryptographicException>(() => Tools.Unprotect(Convert.ToBase64String(Raw), out _));
        }

        [TestMethod]
        public void Unprotect_NotBase64_ThrowsFormatException()
        {
            Assert.ThrowsException<FormatException>(() => Tools.Unprotect("this is not base64 !", out _));
        }

        #endregion

        #region LoadProxy

        [TestMethod]
        public void LoadProxy_NullConfiguration_RemovesProxy()
        {
            WebRequest.DefaultWebProxy = new WebProxy("http://before:1234");
            Tools.LoadProxy(null);
            Assert.IsNull(WebRequest.DefaultWebProxy);
        }

        [TestMethod]
        public void LoadProxy_Mode0_RemovesProxy()
        {
            WebRequest.DefaultWebProxy = new WebProxy("http://before:1234");
            Tools.LoadProxy(new Configuration { HTTPProxyMode = 0, ProxyURL = "http://ignored:8080", UseProxyAuthentication = true, ProxyLogin = "l", ProxyPassword = "p" });
            Assert.IsNull(WebRequest.DefaultWebProxy);
        }

        [TestMethod]
        public void LoadProxy_Mode1_UsesSystemProxyWithDefaultCredentials()
        {
            WebRequest.DefaultWebProxy = null;
            Tools.LoadProxy(new Configuration { HTTPProxyMode = 1, ProxyURL = "http://ignored:8080" });
            IWebProxy Proxy = WebRequest.DefaultWebProxy;
            Assert.IsNotNull(Proxy);
            Assert.AreNotEqual(new Uri("http://ignored:8080"), Proxy.GetProxy(new Uri("http://www.example.com/")), "The user defined proxy URL must be ignored in mode 1");
            Assert.AreSame(CredentialCache.DefaultCredentials, Proxy.Credentials);
        }

        [TestMethod]
        public void LoadProxy_Mode2WithoutAuthentication_UsesUserDefinedProxy()
        {
            Tools.LoadProxy(new Configuration { HTTPProxyMode = 2, ProxyURL = "http://proxy.example:8080", UseProxyAuthentication = false, ProxyLogin = "l", ProxyPassword = "p" });
            var Proxy = WebRequest.DefaultWebProxy as WebProxy;
            Assert.IsNotNull(Proxy);
            Assert.AreEqual(new Uri("http://proxy.example:8080"), Proxy.Address);
            Assert.IsFalse(Proxy.BypassProxyOnLocal);
            Assert.IsNull(Proxy.Credentials);
        }

        [TestMethod]
        public void LoadProxy_Mode2WithAuthentication_UsesUserDefinedProxyAndCredentials()
        {
            Tools.LoadProxy(new Configuration { HTTPProxyMode = 2, ProxyURL = "http://proxy.example:3128", UseProxyAuthentication = true, ProxyLogin = "proxyUser", ProxyPassword = "proxyPâss" });
            var Proxy = WebRequest.DefaultWebProxy as WebProxy;
            Assert.IsNotNull(Proxy);
            Assert.AreEqual(new Uri("http://proxy.example:3128"), Proxy.Address);
            Assert.IsFalse(Proxy.BypassProxyOnLocal);
            var Credential = Proxy.Credentials as NetworkCredential;
            Assert.IsNotNull(Credential);
            Assert.AreEqual("proxyUser", Credential.UserName);
            Assert.AreEqual("proxyPâss", Credential.Password);
        }

        [TestMethod]
        public void LoadProxy_OtherMode_BehavesAsUserDefinedProxy()
        {
            Tools.LoadProxy(new Configuration { HTTPProxyMode = 3, ProxyURL = "http://proxy.example:8080" });
            var Proxy = WebRequest.DefaultWebProxy as WebProxy;
            Assert.IsNotNull(Proxy);
            Assert.AreEqual(new Uri("http://proxy.example:8080"), Proxy.Address);
        }

        [TestMethod]
        public void LoadProxy_Mode2ThenMode0_RemovesProxy()
        {
            Tools.LoadProxy(new Configuration { HTTPProxyMode = 2, ProxyURL = "http://proxy.example:8080" });
            Assert.IsNotNull(WebRequest.DefaultWebProxy);
            Tools.LoadProxy(new Configuration { HTTPProxyMode = 0 });
            Assert.IsNull(WebRequest.DefaultWebProxy);
        }

        #endregion

        #region FindCertificate

        [TestMethod]
        public void FindCertificate_InvalidStoreName_ReturnsNull()
        {
            Assert.IsNull(Tools.FindCertificate("NotAStore", "CurrentUser", "01"));
        }

        [TestMethod]
        public void FindCertificate_InvalidStoreLocation_ReturnsNull()
        {
            Assert.IsNull(Tools.FindCertificate("My", "NotALocation", "01"));
        }

        [TestMethod]
        public void FindCertificate_NullStoreNames_ReturnsNull()
        {
            Assert.IsNull(Tools.FindCertificate(null, null, "01"));
            Assert.IsNull(Tools.FindCertificate("My", null, "01"));
            Assert.IsNull(Tools.FindCertificate(null, "CurrentUser", "01"));
        }

        [TestMethod]
        public void FindCertificate_OutOfRangeNumericStoreName_ReturnsNull()
        {
            //Enum.TryParse accepts any number: "999" is not a valid store name and must be treated as invalid
            Assert.IsNull(Tools.FindCertificate("999", "CurrentUser", "01"));
        }

        [TestMethod]
        public void FindCertificate_UnknownSerial_ReturnsNull()
        {
            Assert.IsNull(Tools.FindCertificate("My", "CurrentUser", "00DEADBEEF00DEADBEEF00DEADBEEF0001"));
        }

        [TestMethod]
        public void FindCertificate_ExistingCertificate_IsFound()
        {
            X509Certificate2 Existing;
            X509Store Store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            Store.Open(OpenFlags.ReadOnly);
            try
            {
                Existing = Store.Certificates.Cast<X509Certificate2>().FirstOrDefault();
            }
            finally
            {
                Store.Close();
            }
            if (Existing == null) Assert.Inconclusive("No certificate in CurrentUser\\My");

            X509Certificate2 Found = Tools.FindCertificate("My", "CurrentUser", Existing.SerialNumber);
            Assert.IsNotNull(Found);
            Assert.AreEqual(Existing.Thumbprint, Found.Thumbprint);
        }

        #endregion

        #region IsMsiIntalled

        [TestMethod]
        public void IsMsiIntalled_UnknownProductCode_ReturnsFalse()
        {
            Assert.IsFalse(Tools.IsMsiIntalled("{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}"));
        }

        [TestMethod]
        public void IsMsiIntalled_EmptyOrNullProductCode_ReturnsFalse()
        {
            Assert.IsFalse(Tools.IsMsiIntalled(""));
            Assert.IsFalse(Tools.IsMsiIntalled(null));
        }

        [TestMethod]
        public void IsMsiIntalled_InstalledProductCode_ReturnsTrueWhateverTheCase()
        {
            string ProductCode = null;
            foreach (RegistryView View in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (RegistryKey BaseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, View))
                using (RegistryKey Uninstall = BaseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    ProductCode = Uninstall?.GetSubKeyNames().FirstOrDefault(x => x.StartsWith("{"));
                }
                if (ProductCode != null) break;
            }
            if (ProductCode == null) Assert.Inconclusive("No MSI product code found under HKLM Uninstall");

            Assert.IsTrue(Tools.IsMsiIntalled(ProductCode));
            Assert.IsTrue(Tools.IsMsiIntalled(ProductCode.ToLowerInvariant()));
            Assert.IsTrue(Tools.IsMsiIntalled(ProductCode.ToUpperInvariant()));
        }

        #endregion
    }
}
