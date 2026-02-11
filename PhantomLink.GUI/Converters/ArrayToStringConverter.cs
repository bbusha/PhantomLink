using System;
using System.Windows.Data;
using System.Globalization;

namespace PhantomLink.GUI.Converters
{
    public class ArrayToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Array array)
            {
                return string.Join(", ", array);
            }
            
            return value?.ToString() ?? "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
