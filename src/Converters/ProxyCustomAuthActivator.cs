using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;

namespace KS2Drive
{
    /// <summary>
    /// Multi-value converter that enables the custom proxy credential fields only when
    /// the proxy mode is 2 (user-defined proxy) and proxy authentication is requested.
    /// </summary>
    public class ProxyCustomAuthActivator : IMultiValueConverter
    {
        /// <summary>
        /// Returns <c>true</c> when <c>values[0]</c> (proxy mode) equals 2 and <c>values[1]</c> (authentication required) is <c>true</c>.
        /// </summary>
        public object Convert(object[] values,
                                Type targetType,
                                object parameter,
                                System.Globalization.CultureInfo culture)
        {
            //While a binding source is not resolved yet, WPF passes DependencyProperty.UnsetValue (or null): treat it as "not active"
            if (values == null || values.Length < 2) return false;
            if (!(values[1] is bool AuthenticationRequired) || !AuthenticationRequired) return false;
            try
            {
                return System.Convert.ToInt32(values[0]) == 2;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Not supported (one-way converter).
        /// </summary>
        /// <exception cref="NotImplementedException">Always thrown.</exception>
        public object[] ConvertBack(object value,
                                    Type[] targetTypes,
                                    object parameter,
                                    System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
