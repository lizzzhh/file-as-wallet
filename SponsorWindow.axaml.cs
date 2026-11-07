using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using QRCoder;

namespace FileAsWallet;

public partial class SponsorWindow : Window
{
    private readonly string _address;

    // 供 XAML 设计器使用的无参构造
    public SponsorWindow() : this("0x8043457D5887a29c864F5628fE5b61dF0d000721")
    {
    }

    public SponsorWindow(string address)
    {
        InitializeComponent();
        _address = address;

        AddressText.Text = address;
        QrPath.Data = BuildQrGeometry(address);
    }

    /// <summary>把二维码矩阵转成一条几何路径，避免引入位图或额外依赖。</summary>
    private static Geometry BuildQrGeometry(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);

        var matrix = data.ModuleMatrix;
        var count = matrix.Count;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var y = 0; y < count; y++)
            {
                var row = matrix[y];
                for (var x = 0; x < count; x++)
                {
                    if (!row[x]) continue;

                    var left = x;
                    var top = y;

                    // 合并同一行连续的暗色模块，减少图形数量
                    var right = x;
                    while (right + 1 < count && row[right + 1]) right++;

                    ctx.BeginFigure(new Point(left, top), isFilled: true);
                    ctx.LineTo(new Point(right + 1, top));
                    ctx.LineTo(new Point(right + 1, top + 1));
                    ctx.LineTo(new Point(left, top + 1));
                    ctx.EndFigure(isClosed: true);

                    x = right;
                }
            }
        }

        return geometry;
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        var clipboard = Clipboard;
        if (clipboard is null) return;

        await clipboard.SetTextAsync(_address);
        if (Owner is MainWindow owner)
            owner.NotifySponsorCopied();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
