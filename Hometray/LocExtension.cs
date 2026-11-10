using Microsoft.UI.Xaml.Markup;

namespace Hometray;

/// <summary>XAML access to the texts: <c>Text="{local:Loc Key=settings.section.connection}"</c>.</summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    protected override object ProvideValue() => Loc.T(Key);
}
