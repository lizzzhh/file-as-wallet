using System;
using System.Globalization;
using System.IO;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FileAsWallet.Services;

namespace FileAsWallet.Models;

public enum ItemState
{
    Pending,
    Hashing,
    Hashed,
    Derived,
    Failed,
}

public partial class FileItem : ObservableObject
{
    private static readonly IBrush ValueBrush = new SolidColorBrush(Color.Parse("#344054"));
    private static readonly IBrush PlaceholderBrush = new SolidColorBrush(Color.Parse("#C4C9D2"));
    private static readonly IBrush FailBrush = new SolidColorBrush(Color.Parse("#B42318"));

    private static readonly IBrush PendingDot = new SolidColorBrush(Color.Parse("#D0D5DD"));
    private static readonly IBrush BusyDot = new SolidColorBrush(Color.Parse("#F79009"));
    private static readonly IBrush HashedDot = new SolidColorBrush(Color.Parse("#0EA5A5"));
    private static readonly IBrush DoneDot = new SolidColorBrush(Color.Parse("#6172F3"));
    private static readonly IBrush FailDot = new SolidColorBrush(Color.Parse("#F04438"));

    private static readonly IBrush FolderBadgeBg = new SolidColorBrush(Color.Parse("#EFF6FF"));
    private static readonly IBrush FolderBadgeFg = new SolidColorBrush(Color.Parse("#1D5FBF"));
    private static readonly IBrush FileBadgeBg = new SolidColorBrush(Color.Parse("#F2F4F7"));
    private static readonly IBrush FileBadgeFg = new SolidColorBrush(Color.Parse("#667085"));

    /// <summary>荧光黄，用于标记靓号。</summary>
    private static readonly IBrush VanityHighlight = new SolidColorBrush(Color.Parse("#FDE047"));
    private static readonly IBrush VanityGlow = new SolidColorBrush(Color.Parse("#FEF9C3"));

    public FileItem(FileEntryInfo info)
    {
        Name = info.Name;
        FullPath = info.FullPath;
        RelativePath = info.RelativePath;
        Size = info.Size;
        Modified = info.Modified;
        IsDirectory = info.IsDirectory;
    }

    public string Name { get; }
    public string FullPath { get; }
    public string RelativePath { get; }
    public long Size { get; }
    public DateTimeOffset Modified { get; }
    public bool IsDirectory { get; }
    public bool IsFile => !IsDirectory;

    /// <summary>
    /// 从缓存恢复结果。调用时机是条目还没加到列表里，所以直接写字段，
    /// 不触发属性通知，几百上千条一起恢复也不会卡界面。
    /// </summary>
    public void RestoreFromCache(CacheEntry entry)
    {
#pragma warning disable MVVMTK0034 // 有意直接写字段：此时条目还没绑定界面，避免逐条触发通知

        _sha256 = entry.Sha256;
        _evmAddress = entry.Evm;
        _tronAddress = entry.Tron;
        _state = ItemState.Derived;
        _errorMessage = null;
#pragma warning restore MVVMTK0034
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHash))]
    [NotifyPropertyChangedFor(nameof(HasNoHash))]
    [NotifyPropertyChangedFor(nameof(Sha256Text))]
    [NotifyPropertyChangedFor(nameof(Sha256Short))]
    [NotifyPropertyChangedFor(nameof(ValueForeground))]
    private string? _sha256;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWallet))]
    [NotifyPropertyChangedFor(nameof(EvmShort))]
    [NotifyPropertyChangedFor(nameof(ValueForeground))]
    private string? _evmAddress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TronShort))]
    private string? _tronAddress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(Sha256Short))]
    private ItemState _state = ItemState.Pending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Sha256Short))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ValueForeground))]
    [NotifyPropertyChangedFor(nameof(TipText))]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVanity))]
    [NotifyPropertyChangedFor(nameof(EvmCellBackground))]
    private bool _evmVanity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVanity))]
    [NotifyPropertyChangedFor(nameof(TronCellBackground))]
    private bool _tronVanity;

    [ObservableProperty]
    private string? _vanityReason;

    public bool IsVanity => EvmVanity || TronVanity;
    public IBrush EvmCellBackground => EvmVanity ? VanityHighlight : Brushes.Transparent;
    public IBrush TronCellBackground => TronVanity ? VanityHighlight : Brushes.Transparent;
    public IBrush VanityBadgeBackground => VanityGlow;
    public string VanityTip => string.IsNullOrEmpty(VanityReason) ? "靓号地址" : $"靓号：{VanityReason}";

    public void SetVanity(bool evm, bool tron, string? reason)
    {
        if (EvmVanity == evm && TronVanity == tron && VanityReason == reason) return;

        EvmVanity = evm;
        TronVanity = tron;
        VanityReason = reason;
        OnPropertyChanged(nameof(EvmCellBackground));
        OnPropertyChanged(nameof(TronCellBackground));
        OnPropertyChanged(nameof(IsVanity));
        OnPropertyChanged(nameof(VanityTip));
    }

    public bool HasHash => !string.IsNullOrEmpty(Sha256);
    public bool HasNoHash => string.IsNullOrEmpty(Sha256);
    public bool HasWallet => !string.IsNullOrEmpty(EvmAddress);
    public bool HasNoWallet => string.IsNullOrEmpty(EvmAddress);

    public string SizeText => IsDirectory ? "文件夹" : FormatSize(Size);

    public string ModifiedText => Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string BadgeText
    {
        get
        {
            if (IsDirectory) return "文件夹";
            var ext = Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();
            return ext.Length == 0 ? "无扩展名" : "." + ext.ToLowerInvariant();
        }
    }

    public IBrush BadgeBackground => IsDirectory ? FolderBadgeBg : FileBadgeBg;
    public IBrush BadgeForeground => IsDirectory ? FolderBadgeFg : FileBadgeFg;

    public string Sha256Text => State switch
    {
        ItemState.Hashing => "计算中…",
        ItemState.Failed when !string.IsNullOrEmpty(ErrorMessage) => ErrorMessage!,
        _ => string.IsNullOrEmpty(Sha256) ? "—" : Sha256!,
    };

    /// <summary>表格中显示的短哈希：首尾各 8 位，完整值见右侧详情与复制。</summary>
    public string Sha256Short => State switch
    {
        ItemState.Hashing => "计算中…",
        ItemState.Failed when !string.IsNullOrEmpty(ErrorMessage) => ErrorMessage!,
        _ => Shorten(Sha256),
    };

    public string EvmShort => Shorten(EvmAddress, keepHexPrefix: true);
    public string TronShort => Shorten(TronAddress);

    private static string Shorten(string? value, bool keepHexPrefix = false)
    {
        if (string.IsNullOrEmpty(value)) return "—";
        if (value.Length <= 20) return value;

        if (keepHexPrefix && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return $"0x{value[2..10]}…{value[^8..]}";

        return $"{value[..8]}…{value[^8..]}";
    }

    public string EvmText => string.IsNullOrEmpty(EvmAddress) ? "—" : EvmAddress!;
    public string TronText => string.IsNullOrEmpty(TronAddress) ? "—" : TronAddress!;

    public string TipText => string.IsNullOrEmpty(ErrorMessage) ? FullPath : ErrorMessage!;

    public IBrush ValueForeground =>
        State == ItemState.Failed ? FailBrush
        : (HasHash || HasWallet) ? ValueBrush
        : PlaceholderBrush;

    public IBrush StatusBrush => State switch
    {
        ItemState.Hashing => BusyDot,
        ItemState.Hashed => HashedDot,
        ItemState.Derived => DoneDot,
        ItemState.Failed => FailDot,
        _ => PendingDot,
    };

    public void Reset()
    {
        Sha256 = null;
        EvmAddress = null;
        TronAddress = null;
        ErrorMessage = null;
        State = ItemState.Pending;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        } while (value >= 1024 && unit < units.Length - 1);

        return value < 10 ? $"{value:0.##} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
