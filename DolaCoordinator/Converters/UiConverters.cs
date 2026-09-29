using System;
using System.Linq;
using System.Globalization;
using System.Windows.Data;

namespace DolaCoordinator.Converters;

/// <summary>DateTime (UTC) → "HH:mm dd/MM" giờ máy; null → "—".</summary>
public class UtcToLocalTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime dt) return "—";
        var local = dt.Kind == DateTimeKind.Local ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
        return local.ToString("HH:mm dd/MM", culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Chuỗi rỗng/null → "—".</summary>
public class EmptyToDashConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s) ? s : "—";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Enum ⇄ bool cho RadioButton: IsChecked = (value.ToString() == ConverterParameter).</summary>
public class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null && parameter != null && value.ToString() == parameter.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true && parameter != null ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}

/// <summary>Hiện khi enum == ConverterParameter (hoặc thuộc danh sách "A|B"), ngược lại ẩn.</summary>
public class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null && parameter != null && parameter.ToString()!.Split('|').Contains(value.ToString())
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Đường dẫn file → tên file.</summary>
public class PathToFileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string path ? System.IO.Path.GetFileName(path) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Đường dẫn ảnh → ảnh thu nhỏ (nạp xong đóng file ngay để không khóa ảnh gốc). Lỗi đọc → null.</summary>
public class PathToThumbnailConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !System.IO.File.Exists(path)) return null;
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 96;
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is NotSupportedException or System.IO.IOException or UriFormatException)
        {
            return null; // định dạng không đọc được: chip vẫn hiện tên file
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
