using KS2Drive.FS;
using MahApps.Metro.Controls;
using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Deployment.Application;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;

namespace KS2Drive.Config
{
    /// <summary>
    /// Configuration dialog: server URL (standard WebDAV only) and credentials, drive letter, WinFSP cache and flush
    /// options, client certificate, proxy and auto-start settings.
    /// </summary>
    public partial class ConfigurationUI : MetroWindow
    {
        /// <summary>
        /// The application configuration edited by this window (also the binding source for proxy settings).
        /// </summary>
        public Configuration AppConfiguration { get; set; }

        /// <summary>
        /// Initializes the dialog: fills the combo boxes (free drive letters, server mode, kernel cache,
        /// flush mode and yes/no options), then preselects values from the current configuration,
        /// including the client certificate subject if the stored certificate can be found.
        /// </summary>
        public ConfigurationUI()
        {
            this.DataContext = this;
            this.AppConfiguration = ((App)Application.Current).AppConfiguration;
            InitializeComponent();

            //Get Free drives
            ArrayList FreeDrives = new ArrayList(26); // Allocate space for alphabet
            for (int i = 65; i < 91; i++) // increment from ASCII values for A-Z
            {
                FreeDrives.Add(Convert.ToChar(i)); // Add uppercase letters to possible drive letters
            }

            foreach (string drive in Directory.GetLogicalDrives())
            {
                FreeDrives.Remove(drive[0]); // removed used drive letters from possible drive letters
            }

            //Keep the configured letter selectable even if it is currently in use (e.g. by another device): otherwise the
            //first free letter would be selected silently and the configured letter replaced at the next save
            if (!String.IsNullOrEmpty(this.AppConfiguration.DriveLetter) && !FreeDrives.Contains(Char.ToUpperInvariant(this.AppConfiguration.DriveLetter[0])))
            {
                FreeDrives.Add(Char.ToUpperInvariant(this.AppConfiguration.DriveLetter[0]));
                FreeDrives.Sort();
            }

            foreach (char drive in FreeDrives)
            {
                CBFreeDrives.Items.Add(drive); // add unused drive letters to the combo box
            }

            CBFreeDrives.SelectedIndex = 0;

            CBKernelCache.SelectedValuePath = "Key";
            CBKernelCache.DisplayMemberPath = "Value";
            CBKernelCache.Items.Add(new KeyValuePair<int, string>((Int32)KernelCacheMode.Disabled, KernelCacheMode.Disabled.ToString()));
            CBKernelCache.Items.Add(new KeyValuePair<int, string>((Int32)KernelCacheMode.MetaDataOnly, KernelCacheMode.MetaDataOnly.ToString()));
            CBKernelCache.Items.Add(new KeyValuePair<int, string>((Int32)KernelCacheMode.DataAndMetaData, KernelCacheMode.DataAndMetaData.ToString()));
            CBKernelCache.SelectedIndex = 2;

            CBSyncOps.SelectedValuePath = "Key";
            CBSyncOps.DisplayMemberPath = "Value";
            CBSyncOps.Items.Add(new KeyValuePair<int, string>(0, "No"));
            CBSyncOps.Items.Add(new KeyValuePair<int, string>(1, "Yes"));
            CBSyncOps.SelectedIndex = 0;

            CBPreloading.SelectedValuePath = "Key";
            CBPreloading.DisplayMemberPath = "Value";
            CBPreloading.Items.Add(new KeyValuePair<int, string>(0, "No"));
            CBPreloading.Items.Add(new KeyValuePair<int, string>(1, "Yes"));
            CBPreloading.SelectedIndex = 1;

            CBMountAsNetworkDrive.SelectedValuePath = "Key";
            CBMountAsNetworkDrive.DisplayMemberPath = "Value";
            CBMountAsNetworkDrive.Items.Add(new KeyValuePair<int, string>(0, "No"));
            CBMountAsNetworkDrive.Items.Add(new KeyValuePair<int, string>(1, "Yes"));
            CBMountAsNetworkDrive.SelectedIndex = 0;

            //Reload values from config
            this.AppConfiguration = ((App)Application.Current).AppConfiguration;
            if (!String.IsNullOrEmpty(this.AppConfiguration.DriveLetter)) CBFreeDrives.SelectedIndex = CBFreeDrives.Items.IndexOf(Char.ToUpperInvariant(this.AppConfiguration.DriveLetter[0])) == -1 ? 0 : CBFreeDrives.Items.IndexOf(Char.ToUpperInvariant(this.AppConfiguration.DriveLetter[0]));
            if (!String.IsNullOrEmpty(this.AppConfiguration.ServerURL)) txtURL.Text = this.AppConfiguration.ServerURL;

            if (!String.IsNullOrEmpty(this.AppConfiguration.ServerLogin)) txtLogin.Text = this.AppConfiguration.ServerLogin;
            if (!String.IsNullOrEmpty(this.AppConfiguration.ServerPassword)) txtPassword.Password = this.AppConfiguration.ServerPassword;

            var KernelCacheMatchingItem = CBKernelCache.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(this.AppConfiguration.KernelCacheMode));
            if (!KernelCacheMatchingItem.Equals(default(KeyValuePair<int, string>))) CBKernelCache.SelectedItem = KernelCacheMatchingItem;

            var SyncOpsMatchingItem = CBSyncOps.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(Convert.ToInt32(this.AppConfiguration.SyncOps)));
            if (!SyncOpsMatchingItem.Equals(default(KeyValuePair<int, string>))) CBSyncOps.SelectedItem = SyncOpsMatchingItem;

            var PreloadingMatchingItem = CBPreloading.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(Convert.ToInt32(this.AppConfiguration.PreLoading)));
            if (!PreloadingMatchingItem.Equals(default(KeyValuePair<int, string>))) CBPreloading.SelectedItem = PreloadingMatchingItem;

            var MountAsNetworkDriveMatchingItem = CBMountAsNetworkDrive.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(Convert.ToInt32(this.AppConfiguration.MountAsNetworkDrive)));
            if (!MountAsNetworkDriveMatchingItem.Equals(default(KeyValuePair<int, string>))) CBMountAsNetworkDrive.SelectedItem = MountAsNetworkDriveMatchingItem;

            //Look for certificate: the option is shown as checked only if it is enabled and the certificate can still be found
            Chk_UserClientCert.IsChecked = false;
            if (!String.IsNullOrEmpty(this.AppConfiguration.CertSerial) && !String.IsNullOrEmpty(this.AppConfiguration.CertStoreLocation) && !String.IsNullOrEmpty(this.AppConfiguration.CertStoreName))
            {
                var FoundCertificate = Tools.FindCertificate(this.AppConfiguration.CertStoreName, this.AppConfiguration.CertStoreLocation, this.AppConfiguration.CertSerial);
                if (FoundCertificate != null)
                {
                    txt_ClientCertSubject.Text = FoundCertificate.Subject;
                    Chk_UserClientCert.IsChecked = this.AppConfiguration.UseClientCertForAuthentication;
                }
            }
        }

        /// <summary>
        /// Validates the mandatory fields (URL, login, password), updates the Windows "Run" registry key
        /// for auto-start, copies the form values into the configuration, saves it, closes the window,
        /// marks the application as configured and reloads the proxy settings.
        /// </summary>
        private void bt_Save_Click(object sender, RoutedEventArgs e)
        {
            if (String.IsNullOrEmpty(txtURL.Text))
            {
                MessageBox.Show("URL is mandatory");
                return;
            }

            try
            {
                var u = new Uri(txtURL.Text);
            }
            catch
            {
                MessageBox.Show("The selected URL is not valid");
                return;
            }

            if (String.IsNullOrEmpty(txtLogin.Text))
            {
                MessageBox.Show("Server login is mandatory");
                return;
            }

            if (String.IsNullOrEmpty(txtPassword.Password))
            {
                MessageBox.Show("Server password is mandatory");
                return;
            }

            //From : https://stackoverflow.com/questions/5089601/how-to-run-a-c-sharp-application-at-windows-startup
            try
            {
                using (RegistryKey rkApp = Registry.CurrentUser.CreateSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run"))
                {
                    if (chk_AutoStart.IsChecked == true)
                    {
                        if (ApplicationDeployment.IsNetworkDeployed) //If running from Click-Once link, autostart the Click-Once bootstrap
                        {
                            //Click-Once shortcut: Start menu\Programs\<PublisherName>\<ProductName>.appref-ms (see the publish settings of the project)
                            rkApp.SetValue("KS2Drive", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "KS2", "KS2 Drive.appref-ms"));
                        }
                        else //If portable, autostart the program itself
                        {
                            rkApp.SetValue("KS2Drive", System.Reflection.Assembly.GetEntryAssembly().Location);
                        }
                    }
                    else
                    {
                        rkApp.DeleteValue("KS2Drive", false);
                    }
                }
            }
            catch (Exception ex)
            {
                //Not blocking: the rest of the configuration can still be saved
                MessageBox.Show($"Cannot update the \"Start with Windows\" setting : {ex.Message}");
            }

            this.AppConfiguration.DriveLetter = CBFreeDrives.SelectedValue.ToString();
            this.AppConfiguration.ServerURL = txtURL.Text;
            this.AppConfiguration.ServerType = (Int32)WebDAVMode.WebDAV; //AOS is no longer offered: the drive always uses standard WebDAV
            this.AppConfiguration.ServerLogin = txtLogin.Text;
            this.AppConfiguration.ServerPassword = txtPassword.Password;
            this.AppConfiguration.KernelCacheMode = Convert.ToInt32(CBKernelCache.SelectedValue);
            this.AppConfiguration.SyncOps = Convert.ToBoolean(Convert.ToInt16(CBSyncOps.SelectedValue));
            this.AppConfiguration.PreLoading = Convert.ToBoolean(Convert.ToInt16(CBPreloading.SelectedValue));
            this.AppConfiguration.FlushMode = (Int32)FlushMode.FlushAtCleanup; //The only mode left (FlushAtWrite uploaded the whole file after every write)
            this.AppConfiguration.MountAsNetworkDrive = Convert.ToBoolean(CBMountAsNetworkDrive.SelectedValue);
            this.AppConfiguration.UseClientCertForAuthentication = Chk_UserClientCert.IsChecked.Value;

            try
            {
                this.AppConfiguration.Save();
            }
            catch (Exception ex)
            {
                //Keep the window open and the previous state: the configuration has not been saved
                MessageBox.Show($"Cannot save configuration : {ex.Message}");
                return;
            }

            this.AppConfiguration.IsConfigured = true;
            Tools.LoadProxy(this.AppConfiguration);
            this.Close();
        }

        /// <summary>
        /// Copies the proxy password into the configuration (PasswordBox.Password cannot be data-bound).
        /// </summary>
        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            this.AppConfiguration.ProxyPassword = ProxyPassword.Password;
        }

        /// <summary>
        /// Opens the Windows certificate picker on the store named by the clicked button's content
        /// ("StoreLocation.StoreName", e.g. "CurrentUser.My") and stores the selected certificate's
        /// store name, location and serial number in the configuration.
        /// </summary>
        private void bt_UserClientCertSelect_Click(object sender, RoutedEventArgs e)
        {
            var SeachScope = ((Button)sender).Content.ToString();

            String StoreLocationAsString = SeachScope.Substring(0, SeachScope.IndexOf("."));
            String StoreNameAsString = SeachScope.Substring(SeachScope.IndexOf(".") + 1);

            StoreName StoreNameParsed;
            StoreLocation StoreLocationParsed;

            if (Enum.TryParse(StoreNameAsString, out StoreNameParsed) && Enum.TryParse(StoreLocationAsString, out StoreLocationParsed))
            {
                X509Store store = new X509Store(StoreNameParsed, StoreLocationParsed);
                store.Open(OpenFlags.ReadOnly);
                X509Certificate2Collection sel = X509Certificate2UI.SelectFromCollection(store.Certificates, null, null, X509SelectionFlag.SingleSelection);
                store.Close();

                if (sel.Count > 0)
                {
                    this.AppConfiguration.CertStoreName = StoreNameAsString;
                    this.AppConfiguration.CertStoreLocation = StoreLocationAsString;
                    this.AppConfiguration.CertSerial = sel[0].SerialNumber;
                    txt_ClientCertSubject.Text = sel[0].Subject;
                }
            }
        }
    }
}