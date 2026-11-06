using System.Globalization;
using System.Text;

namespace FreshStart.Core.Diagnostics;

public static class ConsoleEncoding
{
    private static readonly Lazy<Encoding> Oem = new(CreateOem);

    /// <summary>Windows 系统工具通常输出 OEM 代码页（中文系统通常为 CP936）。</summary>
    public static Encoding OemCodePage => Oem.Value;

    private static Encoding CreateOem()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(
                CultureInfo.CurrentCulture.TextInfo.OEMCodePage,
                EncoderFallback.ReplacementFallback,
                DecoderFallback.ReplacementFallback);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }
}
