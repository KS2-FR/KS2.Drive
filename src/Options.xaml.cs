using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace KS2Drive
{
    public partial class Options : Window, INotifyPropertyChanged
    {
        public Configuration AppConfiguration { get; set; }

        public Options()
        {
            this.DataContext = this;

            if (((App)Application.Current).AppConfiguration != null)
            {
                this.AppConfiguration = ((App)Application.Current).AppConfiguration;
            }
            else
            {
                this.AppConfiguration = new Configuration() { UseHTTPProxy = false, UseProxyAuthentication = false, Login = "", Password = "", ProxyURL = "" };
            }

            InitializeComponent();
        }

        #region INotifyPropertyChanged Implementation

        public event PropertyChangedEventHandler PropertyChanged;

        public void NotifyPropertyChanged(string propName)
        {
            if (this.PropertyChanged != null)
                this.PropertyChanged(this, new PropertyChangedEventArgs(propName));
        }


        #endregion

        private void bt_Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Tools.LoadProxy(this.AppConfiguration);
                File.WriteAllText(((App)Application.Current).ConfigurationFilePath,Tools.Protect(JsonConvert.SerializeObject(this.AppConfiguration)));
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Cannot save configuration : {ex.Message}");
            }
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            this.AppConfiguration.Password = ProxyPassword.Password;
        }
    }
}
