using MahApps.Metro.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

namespace KS2Drive.About
{
    /// <summary>
    /// "About" dialog showing application information and links.
    /// </summary>
    public partial class About : MetroWindow
    {
        /// <summary>
        /// Initializes the About window.
        /// </summary>
        public About()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Opens the clicked hyperlink in the default browser.
        /// </summary>
        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(e.Uri.ToString());
        }
    }
}
