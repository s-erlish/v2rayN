using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace v2rayN.Desktop.Common;

/// <summary>
/// The <c>{loc:T Key}</c> markup extension. Binds a control property to <c>L.Instance[Key]</c>
/// so the value re-pulls live whenever the language switches (L raises <c>PropertyChanged("Item[]")</c>).
///
/// Because it returns a reflection <see cref="Binding"/> against <c>L.Instance</c>, it is
/// independent of each view's <c>x:DataType</c> / compiled bindings — no per-view DataContext
/// plumbing is needed.
///
/// AXAML:
///   <c>xmlns:loc="clr-namespace:v2rayN.Desktop.Common"</c> on the view root, then e.g.
///   <c>Text="{loc:T Home_NotConnected}"</c>, <c>Header="{loc:T Nav_Home}"</c>,
///   <c>ToolTip.Tip="{loc:T Common_TestLatency}"</c>, <c>Watermark="{loc:T Servers_SearchPlaceholder}"</c>.
/// </summary>
public sealed class T : MarkupExtension
{
    public T()
    {
    }

    public T(string key)
    {
        Key = key;
    }

    /// <summary>The localization key (positional ctor arg in <c>{loc:T Key}</c>).</summary>
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]")
        {
            Source = L.Instance,
            Mode = BindingMode.OneWay,
        };
}
