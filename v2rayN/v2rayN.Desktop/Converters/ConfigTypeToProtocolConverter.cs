using Avalonia.Data.Converters;
using ServiceLib.Enums;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Converters;

/// <summary>
/// Maps a <see cref="EConfigType"/> to the Incy protocol-chip token (upper-case, e.g. "VLESS").
/// Registered app-wide as <c>{StaticResource ConfigTypeToProtocol}</c>.
/// </summary>
public class ConfigTypeToProtocolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is EConfigType t ? ProfileDisplay.Protocol(t) : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
