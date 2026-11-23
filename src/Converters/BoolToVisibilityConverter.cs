using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace KS2Drive
{
    /// <summary>
    /// One-way converter from <see cref="bool"/> to <see cref="Visibility"/>.
    /// Unlike the built-in converter, <c>false</c> maps to <see cref="Visibility.Hidden"/> (space is kept), not Collapsed.
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        /// <summary>
        /// Returns <see cref="Visibility.Visible"/> when <paramref name="value"/> is <c>true</c>, otherwise <see cref="Visibility.Hidden"/>.
        /// </summary>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if ((bool)value) return Visibility.Visible;
            return Visibility.Hidden;
        }

        /// <summary>
        /// Not supported (one-way converter).
        /// </summary>
        /// <exception cref="NotImplementedException">Always thrown.</exception>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
