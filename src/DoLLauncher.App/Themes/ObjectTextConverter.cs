using System.Globalization;
using Avalonia.Data.Converters;

namespace DoLLauncher.Themes;

/// <summary>
/// 把任意对象按 ToString 转成文本。模板里 Tag（object）直接 TemplateBinding 到 Text（string）
/// 会抛 InvalidCastException，必须经转换器。
/// </summary>
public sealed class ObjectTextConverter : IValueConverter
{
    public static readonly ObjectTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
