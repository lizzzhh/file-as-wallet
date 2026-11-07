using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using FileAsWallet.Models;
using FileAsWallet.ViewModels;

namespace FileAsWallet;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // DWMWA_USE_IMMERSIVE_DARK_MODE：20（Win10 20H1+）/ 19（更早）
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        ClampToScreen();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);

        KeyDown += OnWindowKeyDown;
        _viewModel.ScrollToItemRequested += OnScrollToItem;
        Closing += (_, _) => _viewModel.FlushCache();
        Opened += async (_, _) =>
        {
            ApplyLightTitleBar();
            // --vanity=8888（也兼容 --suffix= / --prefix=）可预置自定义靓号匹配串
            var cliArgs = Environment.GetCommandLineArgs();
            var vanityArg = cliArgs.FirstOrDefault(a =>
                a.StartsWith("--vanity=", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("--suffix=", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("--prefix=", StringComparison.OrdinalIgnoreCase));

            if (vanityArg is not null)
                _viewModel.VanityPattern = vanityArg[(vanityArg.IndexOf('=') + 1)..];

            // --recursive 可直接以「含子目录」模式启动
            if (Environment.GetCommandLineArgs().Contains("--recursive"))
                _viewModel.Recursive = true;

            await _viewModel.OpenInitialAsync(ParseStartupPath());

            // --sponsor 直接打开赞助窗口（便于单独检查这个对话框）
            if (Environment.GetCommandLineArgs().Contains("--sponsor"))
                new SponsorWindow(SponsorAddress).Show(this);

            if (Environment.GetCommandLineArgs().Contains("--demo"))
            {
                await _viewModel.RunDemoAsync();
                var first = _viewModel.Items.FirstOrDefault(i => i.IsFile);
                if (first is not null) FileList.SelectedItem = first;
            }
        };
    }

    /// <summary>Mica 背景下让系统标题栏跟随浅色主题，避免深色标题栏与浅色界面冲突。</summary>
    private void ApplyLightTitleBar()
    {
        var handle = TryGetPlatformHandle();
        if (handle is null) return;

        var light = 0;
        if (DwmSetWindowAttribute(handle.Handle, DwmUseImmersiveDarkMode, ref light, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle.Handle, DwmUseImmersiveDarkModeLegacy, ref light, sizeof(int));
    }

    /// <summary>避免在小屏幕上窗口超出可用区域。</summary>
    private void ClampToScreen()
    {
        var screen = Screens.Primary;
        if (screen is null) return;

        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var maxWidth = screen.WorkingArea.Width / scale - 24;
        var maxHeight = screen.WorkingArea.Height / scale - 24;

        if (maxWidth > MinWidth) Width = Math.Min(Width, maxWidth);
        if (maxHeight > MinHeight) Height = Math.Min(Height, maxHeight);
    }

    private async void OnPickFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择要扫描的文件夹",
            AllowMultiple = false,
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            await _viewModel.NavigateAsync(path!);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox listBox)
            _viewModel.UpdateSelection(listBox.SelectedItems?.OfType<FileItem>() ?? []);
    }

    /// <summary>赞助地址：点击菜单里的「赞助这个项目」打开赞助窗口。</summary>
    private const string SponsorAddress = "0x8043457D5887a29c864F5628fE5b61dF0d000721";

    private async void OnSponsorClick(object? sender, RoutedEventArgs e)
    {
        var window = new SponsorWindow(SponsorAddress);
        await window.ShowDialog(this);
    }

    /// <summary>赞助窗口里复制地址后，在主窗口给个反馈。</summary>
    public void NotifySponsorCopied() => _viewModel.ShowToast("赞助地址已复制，感谢支持");

    /// <summary>打开缓存所在文件夹。</summary>
    private void OnOpenCacheFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_viewModel.CacheFolderPath);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_viewModel.CacheFolderPath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _viewModel.ShowToast("无法打开缓存目录：" + ex.Message);
        }
    }

    /// <summary>点击列表空白区域时收起详情面板。</summary>
    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual visual && visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
            return;

        FileList.SelectedItems?.Clear();
        _viewModel.ClearSelection();
    }

    private void OnCloseDetailsClick(object? sender, RoutedEventArgs e)
    {
        FileList.SelectedItems?.Clear();
        _viewModel.ClearSelection();
    }

    private async void OnCopySelectedHash(object? sender, RoutedEventArgs e) =>
        await _viewModel.CopyTextAsync(_viewModel.SelectedItem?.Sha256);

    private async void OnCopySelectedEvm(object? sender, RoutedEventArgs e) =>
        await _viewModel.CopyTextAsync(_viewModel.SelectedItem?.EvmAddress);

    private async void OnCopySelectedTron(object? sender, RoutedEventArgs e) =>
        await _viewModel.CopyTextAsync(_viewModel.SelectedItem?.TronAddress);

    private async void OnPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        await _viewModel.NavigateFromInputAsync();
        e.Handled = true;
    }

    private async void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FileList.SelectedItem is not FileItem item) return;

        if (item.IsDirectory)
            await _viewModel.NavigateAsync(item.FullPath);
        else
            OpenWithShell(item.FullPath);
    }

    /// <summary>“下一个靓号”跳转后，选中并滚动到该行。</summary>
    private void OnScrollToItem(FileItem item)
    {
        FileList.SelectedItem = item;
        FileList.ScrollIntoView(item);
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F3 when _viewModel.HasVanity:
                _viewModel.NextVanityCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F5:
                await _viewModel.RefreshAsync();
                e.Handled = true;
                break;
            case Key.Back when _viewModel.CanGoUp:
                await _viewModel.GoUpAsync();
                e.Handled = true;
                break;
            case Key.Left when e.KeyModifiers.HasFlag(KeyModifiers.Alt) && _viewModel.CanGoBack:
                await _viewModel.GoBackAsync();
                e.Handled = true;
                break;
            case Key.Right when e.KeyModifiers.HasFlag(KeyModifiers.Alt) && _viewModel.CanGoForward:
                await _viewModel.GoForwardAsync();
                e.Handled = true;
                break;
            case Key.Escape when _viewModel.HasSelectedItem:
                FileList.SelectedItems?.Clear();
                _viewModel.ClearSelection();
                e.Handled = true;
                break;
        }
    }

    // ───── 单元格点击复制 ─────
    private async void OnCellPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not FileItem item) return;

        var text = (control.Tag as string) switch
        {
            "sha" => item.Sha256,
            "evm" => item.EvmAddress,
            "tron" => item.TronAddress,
            _ => null,
        };

        if (!string.IsNullOrEmpty(text))
            await _viewModel.CopyTextAsync(text);
    }

    // ───── 右键菜单 ─────
    private FileItem? ResolveMenuTarget(object? sender) =>
        (sender as Control)?.DataContext as FileItem ?? _viewModel.Selection.FirstOrDefault();

    private async void OnMenuHash(object? sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) is { IsDirectory: false } item)
            await _viewModel.HashItemAsync(item);
    }

    private async void OnMenuDerive(object? sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) is { IsDirectory: false } item)
            await _viewModel.DeriveItemAsync(item);
    }

    private async void OnMenuCopyHash(object? sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) is { } item)
            await _viewModel.CopyTextAsync(item.Sha256);
    }

    private async void OnMenuCopyEvm(object? sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) is { } item)
            await _viewModel.CopyTextAsync(item.EvmAddress);
    }

    private async void OnMenuCopyTron(object? sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) is { } item)
            await _viewModel.CopyTextAsync(item.TronAddress);
    }

    private async void OnMenuCopyPath(object? sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) is { } item)
            await _viewModel.CopyTextAsync(item.FullPath);
    }

    private void OnMenuReveal(object? sender, RoutedEventArgs e)
    {
        var item = ResolveMenuTarget(sender);
        if (item is null) return;

        try
        {
            if (item.IsDirectory)
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.FullPath}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _viewModel.ShowToast("无法打开资源管理器：" + ex.Message);
        }
    }

    private void OpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _viewModel.ShowToast("无法打开文件：" + ex.Message);
        }
    }

    // ───── 拖放文件夹进入 ─────
    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles();
        if (items is null) return;

        foreach (var item in items)
        {
            var local = item.TryGetLocalPath();
            if (string.IsNullOrEmpty(local)) continue;

            if (Directory.Exists(local))
            {
                await _viewModel.NavigateAsync(local);
                return;
            }

            var parent = Path.GetDirectoryName(local);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                await _viewModel.NavigateAsync(parent);
                return;
            }
        }
    }

    /// <summary>支持用命令行参数直接打开某个目录。</summary>
    private static string? ParseStartupPath() =>
        Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(arg => !arg.StartsWith('-') && Directory.Exists(arg));
}
