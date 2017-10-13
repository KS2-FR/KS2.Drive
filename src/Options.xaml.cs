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
    public partial class Options : Window
    {
        public Configuration AppConfiguration { get; set; }

        public Options()
        {
            this.DataContext = this;
            this.AppConfiguration = ((App)Application.Current).AppConfiguration;
            InitializeComponent();
        }

        private void bt_Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Tools.LoadProxy(this.AppConfiguration);
                this.AppConfiguration.Save();
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Cannot save configuration : {ex.Message}");
            }
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            this.AppConfiguration.ProxyPassword = ProxyPassword.Password;
        }
    }
}
