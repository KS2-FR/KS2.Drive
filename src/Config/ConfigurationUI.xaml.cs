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
    public partial class ConfigurationUI : MetroWindow
    {
        public Configuration AppConfiguration { get; set; }

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

            foreach (char drive in FreeDrives)
            {
                CBFreeDrives.Items.Add(drive); // add unused drive letters to the combo box
            }

            CBFreeDrives.SelectedIndex = 0;

            CBMode.SelectedValuePath = "Key";
            CBMode.DisplayMemberPath = "Value";
            CBMode.Items.Add(new KeyValuePair<int, string>(0, "webDAV"));
            CBMode.Items.Add(new KeyValuePair<int, string>(1, "AOS"));
            CBMode.SelectedIndex = 0;

            CBKernelCache.SelectedValuePath = "Key";
            CBKernelCache.DisplayMemberPath = "Value";
            CBKernelCache.Items.Add(new KeyValuePair<int, string>((Int32)KernelCacheMode.Disabled, KernelCacheMode.Disabled.ToString()));
            CBKernelCache.Items.Add(new KeyValuePair<int, string>((Int32)KernelCacheMode.MetaDataOnly, KernelCacheMode.MetaDataOnly.ToString()));
            CBKernelCache.Items.Add(new KeyValuePair<int, string>((Int32)KernelCacheMode.DataAndMetaData, KernelCacheMode.DataAndMetaData.ToString()));
            CBKernelCache.SelectedIndex = 2;

            CBFlush.SelectedValuePath = "Key";
            CBFlush.DisplayMemberPath = "Value";
            CBFlush.Items.Add(new KeyValuePair<int, string>((Int32)FlushMode.FlushAtCleanup, FlushMode.FlushAtCleanup.ToString()));
            CBFlush.Items.Add(new KeyValuePair<int, string>((Int32)FlushMode.FlushAtWrite, FlushMode.FlushAtWrite.ToString()));
            CBFlush.SelectedIndex = 1;

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
            if (!String.IsNullOrEmpty(this.AppConfiguration.DriveLetter)) CBFreeDrives.SelectedIndex = CBFreeDrives.Items.IndexOf(this.AppConfiguration.DriveLetter[0]) == -1 ? 0 : CBFreeDrives.Items.IndexOf(this.AppConfiguration.DriveLetter[0]);
            if (!String.IsNullOrEmpty(this.AppConfiguration.ServerURL)) txtURL.Text = this.AppConfiguration.ServerURL;

            var ServerTypeMatchingItem = CBMode.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(this.AppConfiguration.ServerType));
            if (!ServerTypeMatchingItem.Equals(default(KeyValuePair<int, string>))) CBMode.SelectedItem = ServerTypeMatchingItem;

            if (!String.IsNullOrEmpty(this.AppConfiguration.ServerLogin)) txtLogin.Text = this.AppConfiguration.ServerLogin;
            if (!String.IsNullOrEmpty(this.AppConfiguration.ServerPassword)) txtPassword.Password = this.AppConfiguration.ServerPassword;
            Chk_Password.IsChecked = this.AppConfiguration.SavePassword;

            var KernelCacheMatchingItem = CBKernelCache.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(this.AppConfiguration.KernelCacheMode));
            if (!KernelCacheMatchingItem.Equals(default(KeyValuePair<int, string>))) CBKernelCache.SelectedItem = KernelCacheMatchingItem;

            var FlushMatchingItem = CBFlush.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(this.AppConfiguration.FlushMode));
            if (!FlushMatchingItem.Equals(default(KeyValuePair<int, string>))) CBFlush.SelectedItem = FlushMatchingItem;

            var SyncOpsMatchingItem = CBSyncOps.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(Convert.ToInt32(this.AppConfiguration.SyncOps)));
            if (!SyncOpsMatchingItem.Equals(default(KeyValuePair<int, string>))) CBSyncOps.SelectedItem = SyncOpsMatchingItem;

            var PreloadingMatchingItem = CBPreloading.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(Convert.ToInt32(this.AppConfiguration.PreLoading)));
            if (!PreloadingMatchingItem.Equals(default(KeyValuePair<int, string>))) CBPreloading.SelectedItem = PreloadingMatchingItem;

            var MountAsNetworkDriveMatchingItem = CBMountAsNetworkDrive.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(Convert.ToInt32(this.AppConfiguration.MountAsNetworkDrive)));
            if (!MountAsNetworkDriveMatchingItem.Equals(default(KeyValuePair<int, string>))) CBMountAsNetworkDrive.SelectedItem = MountAsNetworkDriveMatchingItem;

            //Look for certificate
            if (this.AppConfiguration.UseClientCertForAuthentication) Chk_UserClientCert.IsChecked = false;
            if (!String.IsNullOrEmpty(this.AppConfiguration.CertSerial) && !String.IsNullOrEmpty(this.AppConfiguration.CertStoreLocation) && !String.IsNullOrEmpty(this.AppConfiguration.CertStoreName))
            {
                var FoundCertificate = Tools.FindCertificate(this.AppConfiguration.CertStoreName, this.AppConfiguration.CertStoreLocation, this.AppConfiguration.CertSerial);
                if (FoundCertificate != null)
                {
                    txt_ClientCertSubject.Text = FoundCertificate.Subject;
                    if (this.AppConfiguration.UseClientCertForAuthentication) Chk_UserClientCert.IsChecked = true;
                }
            }
        }

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
            RegistryKey rkApp = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            if (chk_AutoStart.IsChecked == true)
            {
                if (ApplicationDeployment.IsNetworkDeployed) //If running from Click-Once link, autostart the Click-Once bootstrap
                {
                    rkApp.SetValue("KS2Drive", Environment.GetFolderPath(Environment.SpecialFolder.Programs) + @"\KS2\KS2.WorkflowClient.appref-ms");
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
            rkApp.Close();

            this.AppConfiguration.DriveLetter = CBFreeDrives.SelectedValue.ToString();
            this.AppConfiguration.ServerURL = txtURL.Text;
            this.AppConfiguration.ServerType = (Int32)CBMode.SelectedValue;
            this.AppConfiguration.ServerLogin = txtLogin.Text;
            if (Chk_Password.IsChecked == true)
            {
                this.AppConfiguration.ServerPassword = txtPassword.Password;
            }
            else
            {
                this.AppConfiguration.ServerPassword = null;
            }
            this.AppConfiguration.SavePassword = Chk_Password.IsChecked.Value;
            this.AppConfiguration.KernelCacheMode = Convert.ToInt32(CBKernelCache.SelectedValue);
            this.AppConfiguration.SyncOps = Convert.ToBoolean(Convert.ToInt16(CBSyncOps.SelectedValue));
            this.AppConfiguration.PreLoading = Convert.ToBoolean(Convert.ToInt16(CBPreloading.SelectedValue));
            this.AppConfiguration.FlushMode = Convert.ToInt32(CBFlush.SelectedValue);
            this.AppConfiguration.MountAsNetworkDrive = Convert.ToBoolean(CBMountAsNetworkDrive.SelectedValue);
            this.AppConfiguration.UseClientCertForAuthentication = Chk_UserClientCert.IsChecked.Value;

            try
            {
                this.AppConfiguration.Save();
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Cannot save configuration : {ex.Message}");
            }

            this.AppConfiguration.ServerPassword = txtPassword.Password;
            this.AppConfiguration.IsConfigured = true;

            Tools.LoadProxy(this.AppConfiguration);
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            this.AppConfiguration.ProxyPassword = ProxyPassword.Password;
        }

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