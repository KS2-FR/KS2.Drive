using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Globalization;
using System.Windows;

namespace KS2Drive.Tests.UI
{
    [TestClass]
    public class BoolToVisibilityConverterTests
    {
        private readonly BoolToVisibilityConverter _Converter = new BoolToVisibilityConverter();

        [TestMethod]
        public void Convert_True_IsVisible()
        {
            Assert.AreEqual(Visibility.Visible, _Converter.Convert(true, typeof(Visibility), null, CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void Convert_False_IsHiddenNotCollapsed()
        {
            Assert.AreEqual(Visibility.Hidden, _Converter.Convert(false, typeof(Visibility), null, CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void Convert_IgnoresParameterAndCulture()
        {
            Assert.AreEqual(Visibility.Visible, _Converter.Convert(true, typeof(object), "anything", CultureInfo.GetCultureInfo("fr-FR")));
            Assert.AreEqual(Visibility.Hidden, _Converter.Convert(false, null, 42, null));
        }

        [TestMethod]
        public void Convert_NotABool_Throws()
        {
            Assert.ThrowsException<NullReferenceException>(() => _Converter.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture));
            Assert.ThrowsException<InvalidCastException>(() => _Converter.Convert("true", typeof(Visibility), null, CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void ConvertBack_NotSupported()
        {
            Assert.ThrowsException<NotImplementedException>(() => _Converter.ConvertBack(Visibility.Visible, typeof(bool), null, CultureInfo.InvariantCulture));
        }
    }

    [TestClass]
    public class ProxyActivatorTests
    {
        private readonly ProxyActivator _Converter = new ProxyActivator();

        [DataTestMethod]
        [DataRow((short)0, "0", true)]
        [DataRow((short)1, "1", true)]
        [DataRow((short)2, "2", true)]
        [DataRow((short)0, "1", false)]
        [DataRow((short)2, "1", false)]
        [DataRow(1, "1", true)]          //Int32 value
        [DataRow("2", 2, true)]          //String value, Int32 parameter
        [DataRow(2, 2, true)]
        [DataRow(1, 2, false)]
        public void Convert_ChecksEquality(object Value, object Parameter, bool Expected)
        {
            Assert.AreEqual(Expected, _Converter.Convert(Value, typeof(bool), Parameter, CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void Convert_NullValue_IsTreatedAsZero()
        {
            Assert.AreEqual(true, _Converter.Convert(null, typeof(bool), "0", CultureInfo.InvariantCulture));
            Assert.AreEqual(false, _Converter.Convert(null, typeof(bool), "1", CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void Convert_ReturnsABoxedBool()
        {
            Assert.IsInstanceOfType(_Converter.Convert((short)1, typeof(bool), "1", CultureInfo.InvariantCulture), typeof(bool));
            Assert.IsInstanceOfType(_Converter.Convert((short)1, typeof(bool), "2", CultureInfo.InvariantCulture), typeof(bool));
        }

        [TestMethod]
        public void Convert_NonNumericParameter_Throws()
        {
            Assert.ThrowsException<FormatException>(() => _Converter.Convert((short)1, typeof(bool), "abc", CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void ConvertBack_Checked_ReturnsParameter()
        {
            Assert.AreEqual("2", _Converter.ConvertBack(true, typeof(short), "2", CultureInfo.InvariantCulture));
            object Param = 1;
            Assert.AreSame(Param, _Converter.ConvertBack(true, typeof(short), Param, CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void ConvertBack_Unchecked_ReturnsNull()
        {
            Assert.IsNull(_Converter.ConvertBack(false, typeof(short), "2", CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void ConvertBack_NotABool_Throws()
        {
            Assert.ThrowsException<NullReferenceException>(() => _Converter.ConvertBack(null, typeof(short), "2", CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void ConvertThenConvertBack_RoundTrip()
        {
            object Checked = _Converter.Convert((short)2, typeof(bool), "2", CultureInfo.InvariantCulture);
            Assert.AreEqual("2", _Converter.ConvertBack(Checked, typeof(short), "2", CultureInfo.InvariantCulture));
        }
    }

    [TestClass]
    public class ProxyCustomAuthActivatorTests
    {
        private readonly ProxyCustomAuthActivator _Converter = new ProxyCustomAuthActivator();

        [DataTestMethod]
        [DataRow((short)2, true, true)]
        [DataRow((short)2, false, false)]
        [DataRow((short)1, true, false)]
        [DataRow((short)0, true, false)]
        [DataRow((short)0, false, false)]
        [DataRow(2, true, true)]
        [DataRow("2", true, true)]
        [DataRow(3, true, false)]
        public void Convert_EnabledOnlyForUserProxyWithAuthentication(object Mode, bool UseAuthentication, bool Expected)
        {
            Assert.AreEqual(Expected, _Converter.Convert(new object[] { Mode, UseAuthentication }, typeof(bool), null, CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void Convert_ModeNotTwo_DoesNotEvaluateSecondValue()
        {
            //Short-circuit: the second value is only read when the mode is 2
            Assert.AreEqual(false, _Converter.Convert(new object[] { (short)1, null }, typeof(bool), null, CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void Convert_UnsetValue_ReturnsFalse()
        {
            //WPF passes DependencyProperty.UnsetValue while a MultiBinding source is not resolved yet
            Assert.AreEqual(false, _Converter.Convert(new object[] { DependencyProperty.UnsetValue, DependencyProperty.UnsetValue }, typeof(bool), null, CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void Convert_ReturnsABoxedBool()
        {
            Assert.IsInstanceOfType(_Converter.Convert(new object[] { (short)2, true }, typeof(bool), null, CultureInfo.InvariantCulture), typeof(bool));
        }

        [TestMethod]
        public void ConvertBack_NotSupported()
        {
            Assert.ThrowsException<NotImplementedException>(() => _Converter.ConvertBack(true, new[] { typeof(short), typeof(bool) }, null, CultureInfo.InvariantCulture));
        }
    }
}
