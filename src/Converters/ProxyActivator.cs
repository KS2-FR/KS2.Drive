using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;

namespace KS2Drive
{
    /// <summary>
    /// Two-way converter that binds an integer proxy mode to a radio button / checkbox:
    /// the control is checked when the bound value equals the converter parameter.
    /// </summary>
    public class ProxyActivator : IValueConverter
    {
        /// <summary>
        /// Returns <c>true</c> when <paramref name="value"/> and <paramref name="parameter"/> represent the same integer.
        /// </summary>
        /// <param name="value">Current proxy mode value.</param>
        /// <param name="parameter">Proxy mode represented by the bound control.</param>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (System.Convert.ToInt32(value) == System.Convert.ToInt32(parameter)) return true;
            return false;
        }

        /// <summary>
        /// Returns <paramref name="parameter"/> (the control's proxy mode) when the control becomes checked, otherwise <c>null</c>.
        /// </summary>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if ((bool)value) return parameter;
            return null;
        }
    }
}
