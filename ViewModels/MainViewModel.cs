using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileAsWallet.Models;
using FileAsWallet.Services;

namespace FileAsWallet.ViewModels;

public enum FileSortColumn
{
    Name,
    Modified,
    Size,
}

public partial class MainViewModel : ObservableObject
{
    private readonly List<FileItem> _all = [];
    private readonly List<string> _backHistory = [];
    private readonly List<string> _forwardHistory = [];

    /// <summary>结果缓存（路径 + 大小 + 修改时间 → 哈希与地址）。</summary>
    private readonly ResultCache _cache = new();
    private readonly Task _cacheLoadTask;
    private readonly string _settingsPath;

    /// <summary>本轮是否已经通知过“存在靓号”，避免每命中一条都刷新按钮状态。</summary>
    private bool _hasVanityNotified;

    /// <summary>流式加载时每批追加的条目数，兼顾界面流畅与吞吐。</summary>
    private const int LoadChunkSize = 500;

    public MainViewModel()
    {
        _settingsPath = Path.Combine(_cache.FolderPath, "settings.json");
        LoadSettings();
        _cacheLoadTask = Task.Run(_cache.Load);
    }

    /// <summary>已加载条目数（流式加载过程中实时增长）。</summary>
    [ObservableProperty]
    private int _loadedCount;

    // ───────────── 缓存与跳过大文件 ─────────────

    /// <summary>结果缓存开关：开启后已算过的文件直接复用，跳过重复计算。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CacheInfoText))]
    private bool _cacheEnabled = true;

    /// <summary>跳过超过该体积（MB）的文件，0 表示不跳过。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SkipMenuHeader))]
    private int _skipLargeMb;

    public string CacheInfoText => CacheEnabled
        ? $"缓存条目：{_cache.Count:N0} 条"
        : "缓存已关闭";

    public string SkipMenuHeader => SkipLargeMb <= 0
        ? "跳过大文件：不跳过"
        : $"跳过大文件：超过 {SkipLargeMb} MB";

    partial void OnCacheEnabledChanged(bool value)
    {
        SaveSettings();
        OnPropertyChanged(nameof(CacheInfoText));
    }

    partial void OnSkipLargeMbChanged(int value)
    {
        SaveSettings();
        OnPropertyChanged(nameof(SkipMenuHeader));
    }

    private sealed class UserSettings
    {
        public bool CacheEnabled { get; set; } = true;
        public int SkipLargeMb { get; set; }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_settingsPath));
            if (settings is null) return;
#pragma warning disable MVVMTK0034 // 初始化时直接写字段，避免触发保存/通知
            _cacheEnabled = settings.CacheEnabled;
            _skipLargeMb = settings.SkipLargeMb;
#pragma warning restore MVVMTK0034
        }
        catch (IOException) { }
        catch (JsonException) { }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(_cache.FolderPath);
            File.WriteAllText(_settingsPath,
                JsonSerializer.Serialize(new UserSettings { CacheEnabled = CacheEnabled, SkipLargeMb = SkipLargeMb }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>清空缓存（菜单调用）。</summary>
    [RelayCommand]
    private void ClearCache()
    {
        _cache.Clear();
        OnPropertyChanged(nameof(CacheInfoText));
        ShowToast("缓存已清空");
    }

    /// <summary>菜单选择跳过大文件的阈值，0 表示不跳过。</summary>
    [RelayCommand]
    private void SetSkipLarge(string? megabytes)
    {
        SkipLargeMb = int.TryParse(megabytes, out var mb) ? mb : 0;
    }

    public string CacheFolderPath => _cache.FolderPath;

    /// <summary>把内存里还没落盘的结果写到缓存文件（退出时调用）。</summary>
    public void FlushCache() => _cache.Flush();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoot))]
    [NotifyPropertyChangedFor(nameof(IsRootEmpty))]
    [NotifyPropertyChangedFor(nameof(CanGoUp))]
    private string? _currentPath;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>地址栏文本，可直接输入路径跳转。</summary>
    [ObservableProperty]
    private string _pathInput = string.Empty;

    /// <summary>用户自定义的靓号模式，可匹配前缀或尾号，长度不限。</summary>
    [ObservableProperty]
    private string _vanityPattern = string.Empty;

    /// <summary>自定义匹配只作用于前缀（与尾号都不勾时表示不限定位置）。</summary>
    [ObservableProperty]
    private bool _vanityMatchPrefix;

    /// <summary>自定义匹配只作用于尾号（与前缀都不勾时表示不限定位置）。</summary>
    [ObservableProperty]
    private bool _vanityMatchSuffix;

    [ObservableProperty]
    private bool _recursive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isProgressVisible;

    [ObservableProperty]
    private string _summaryText = "0 个项目";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameArrow))]
    [NotifyPropertyChangedFor(nameof(ModifiedArrow))]
    [NotifyPropertyChangedFor(nameof(SizeArrow))]
    private FileSortColumn _sortColumn = FileSortColumn.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameArrow))]
    [NotifyPropertyChangedFor(nameof(ModifiedArrow))]
    [NotifyPropertyChangedFor(nameof(SizeArrow))]
    private bool _sortDescending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToast))]
    private string? _toast;

    [ObservableProperty]
    private int _selectionCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedItem))]
    [NotifyPropertyChangedFor(nameof(CanProcessSelected))]
    [NotifyPropertyChangedFor(nameof(CanDeriveSelected))]
    [NotifyPropertyChangedFor(nameof(SelectedIsDirectory))]
    private FileItem? _selectedItem;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    public ObservableCollection<FileItem> Items { get; } = [];

    /// <summary>当前选中的条目，由视图同步进来。</summary>
    public List<FileItem> Selection { get; } = [];

    public bool HasRoot => !string.IsNullOrEmpty(CurrentPath);
    public bool IsRootEmpty => string.IsNullOrEmpty(CurrentPath);
    public bool CanInteract => !IsBusy;
    public bool IsEmpty => Items.Count == 0;
    public bool HasItems => Items.Count > 0;
    public bool HasToast => !string.IsNullOrEmpty(Toast);
    public bool HasSelectedItem => SelectedItem is not null;
    public bool CanProcessSelected => SelectedItem is { IsDirectory: false };
    public bool SelectedIsDirectory => SelectedItem?.IsDirectory == true;
    public bool CanGoUp => !string.IsNullOrEmpty(CurrentPath) && DirectoryScanner.GetParent(CurrentPath!) is not null;

    private string Arrow(FileSortColumn column) =>
        SortColumn == column ? (SortDescending ? "▼" : "▲") : string.Empty;

    public string NameArrow => Arrow(FileSortColumn.Name);
    public string ModifiedArrow => Arrow(FileSortColumn.Modified);
    public string SizeArrow => Arrow(FileSortColumn.Size);

    partial void OnSearchTextChanged(string value) => Rebuild();

    partial void OnCurrentPathChanged(string? value) => PathInput = value ?? string.Empty;

    partial void OnVanityPatternChanged(string value) => ApplyVanity();

    partial void OnVanityMatchPrefixChanged(bool value)
    {
        OnPropertyChanged(nameof(VanityUnrestricted));
        ApplyVanity();
    }

    partial void OnVanityMatchSuffixChanged(bool value)
    {
        OnPropertyChanged(nameof(VanityUnrestricted));
        ApplyVanity();
    }

    /// <summary>两个都不限定 = 前后缀均可。</summary>
    public bool VanityUnrestricted => !VanityMatchPrefix && !VanityMatchSuffix;

    [RelayCommand]
    private void SetVanityAny()
    {
        VanityMatchPrefix = false;
        VanityMatchSuffix = false;
    }

    [RelayCommand]
    private void ToggleVanityPrefix()
    {
        var next = !VanityMatchPrefix;
        VanityMatchSuffix = false;
        VanityMatchPrefix = next;
    }

    [RelayCommand]
    private void ToggleVanitySuffix()
    {
        var next = !VanityMatchSuffix;
        VanityMatchPrefix = false;
        VanityMatchSuffix = next;
    }

    /// <summary>按当前规则重新计算所有条目的靓号标记。</summary>
    public void ApplyVanity()
    {
        var pattern = VanityPattern;
        var matchPrefix = VanityMatchPrefix;
        var matchSuffix = VanityMatchSuffix;

        foreach (var item in _all)
        {
            if (item.HasWallet)
            {
                var result = VanityAnalyzer.Evaluate(
                    item.EvmAddress, item.TronAddress, pattern, matchPrefix, matchSuffix);
                item.SetVanity(result.Evm, result.Tron, result.Reason);
            }
            else
            {
                item.SetVanity(false, false, null);
            }
        }

        VanityCursorText = string.Empty;
        _hasVanityNotified = false;
        OnPropertyChanged(nameof(HasVanity));
        UpdateSummary();
    }

    // ───────────── 靓号跳转 ─────────────

    /// <summary>请求视图把某个条目滚动到可见区域。</summary>
    public event Action<FileItem>? ScrollToItemRequested;

    /// <summary>当前列表中是否存在靓号（决定“下一个靓号”按钮是否可用）。</summary>
    public bool HasVanity => Items.Any(i => i.IsVanity);

    /// <summary>形如 “2/5”，表示当前停在第几个靓号。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVanityCursor))]
    private string _vanityCursorText = string.Empty;

    public bool HasVanityCursor => !string.IsNullOrEmpty(VanityCursorText);

    /// <summary>循环跳到下一个命中靓号的条目，并把它滚动到可见处。</summary>
    [RelayCommand]
    private void NextVanity()
    {
        var hits = Items.Where(i => i.IsVanity).ToList();
        if (hits.Count == 0)
        {
            ShowToast("当前列表没有命中的靓号地址");
            return;
        }

        var current = SelectedItem;
        var index = current is null ? -1 : hits.IndexOf(current);

        if (index >= 0)
        {
            index = (index + 1) % hits.Count;
        }
        else
        {
            // 没选中或选中的不是靓号：取当前行之后最近的，没有就回到第一个
            index = 0;
            var anchor = current is null ? -1 : Items.IndexOf(current);
            for (var i = 0; i < hits.Count; i++)
            {
                if (Items.IndexOf(hits[i]) > anchor)
                {
                    index = i;
                    break;
                }
            }
        }

        var target = hits[index];
        SelectedItem = target;
        VanityCursorText = $"{index + 1}/{hits.Count}";
        ShowToast($"第 {index + 1} / {hits.Count} 个靓号 · {target.VanityReason}");
        ScrollToItemRequested?.Invoke(target);
    }

    /// <summary>地址栏回车跳转。</summary>
    public async Task NavigateFromInputAsync()
    {
        var target = PathInput.Trim().Trim('"');
        if (target.Length == 0) return;
        await NavigateAsync(target);
        if (CurrentPath is not null && !string.Equals(CurrentPath, target, StringComparison.OrdinalIgnoreCase))
            PathInput = CurrentPath;
    }

    partial void OnRecursiveChanged(bool value)
    {
        if (HasRoot) _ = NavigateAsync(CurrentPath!, recordHistory: false);
    }

    public async Task OpenInitialAsync(string? preferredPath)
    {
        // 缓存读完再开始扫描，避免边读边等
        await _cacheLoadTask;
        OnPropertyChanged(nameof(CacheInfoText));

        var target = preferredPath;
        if (string.IsNullOrEmpty(target) || !Directory.Exists(target))
            target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!Directory.Exists(target))
            target = DirectoryScanner.GetRoots().FirstOrDefault();

        if (!string.IsNullOrEmpty(target))
            await NavigateAsync(target);
    }

    public async Task NavigateAsync(string path, bool recordHistory = true)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ShowToast("目录不存在或无法访问");
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "正在读取…";

            var recursive = Recursive;

            if (recordHistory && !string.IsNullOrEmpty(CurrentPath) &&
                !string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _backHistory.Add(CurrentPath!);
                _forwardHistory.Clear();
            }

            CurrentPath = path;
            _all.Clear();
            Items.Clear();
            LoadedCount = 0;
            Selection.Clear();
            SelectionCount = 0;
            SelectedItem = null;

            await LoadStreamingAsync(path, recursive);

            Rebuild();
            UpdateNavigationState();

            StatusText = $"已载入 {_all.Count:N0} 个项目";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            ShowToast("读取目录失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>后台逐批扫描、前台逐批追加，超大目录也能边读边看。</summary>
    private async Task LoadStreamingAsync(string path, bool recursive)
    {
        var channel = Channel.CreateBounded<List<FileEntryInfo>>(
            new BoundedChannelOptions(4) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });

        var producer = Task.Run(async () =>
        {
            try
            {
                var batch = new List<FileEntryInfo>(LoadChunkSize);
                foreach (var entry in DirectoryScanner.Enumerate(path, recursive))
                {
                    batch.Add(entry);
                    if (batch.Count < LoadChunkSize) continue;

                    await channel.Writer.WriteAsync(batch);
                    batch = new List<FileEntryInfo>(LoadChunkSize);
                }

                if (batch.Count > 0) await channel.Writer.WriteAsync(batch);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        });

        var fromCache = 0;

        await foreach (var batch in channel.Reader.ReadAllAsync())
        {
            foreach (var entry in batch)
            {
                var item = new FileItem(entry);

                // 命中缓存就直接把结果填好，界面上立刻是“已派生”
                if (CacheEnabled && !item.IsDirectory &&
                    _cache.TryGet(item.FullPath, item.Size, item.Modified.UtcTicks, out var cached))
                {
                    item.RestoreFromCache(cached);

                    var vanity = VanityAnalyzer.Evaluate(
                        cached.Evm, cached.Tron, VanityPattern, VanityMatchPrefix, VanityMatchSuffix);
                    item.SetVanity(vanity.Evm, vanity.Tron, vanity.Reason);

                    fromCache++;
                }

                _all.Add(item);
                Items.Add(item);
            }

            LoadedCount = _all.Count;
            StatusText = fromCache > 0
                ? $"正在读取… 已载入 {LoadedCount:N0} 项（{fromCache:N0} 项来自缓存）"
                : $"正在读取… 已载入 {LoadedCount:N0} 项";
        }

        await producer;
    }

    private void UpdateNavigationState()
    {
        CanGoBack = _backHistory.Count > 0;
        CanGoForward = _forwardHistory.Count > 0;
        OnPropertyChanged(nameof(CanGoUp));
    }

    [RelayCommand]
    public async Task GoBackAsync()
    {
        if (_backHistory.Count == 0) return;
        var target = _backHistory[^1];
        _backHistory.RemoveAt(_backHistory.Count - 1);
        if (CurrentPath is not null) _forwardHistory.Add(CurrentPath);
        await NavigateAsync(target, recordHistory: false);
        UpdateNavigationState();
    }

    [RelayCommand]
    public async Task GoForwardAsync()
    {
        if (_forwardHistory.Count == 0) return;
        var target = _forwardHistory[^1];
        _forwardHistory.RemoveAt(_forwardHistory.Count - 1);
        if (CurrentPath is not null) _backHistory.Add(CurrentPath);
        await NavigateAsync(target, recordHistory: false);
        UpdateNavigationState();
    }

    [RelayCommand]
    public async Task GoUpAsync()
    {
        var parent = CurrentPath is null ? null : DirectoryScanner.GetParent(CurrentPath);
        if (parent is null) return;
        await NavigateAsync(parent, recordHistory: false);
        UpdateNavigationState();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (HasRoot) await NavigateAsync(CurrentPath!, recordHistory: false);
    }

    [RelayCommand]
    private void SortBy(string? column)
    {
        if (!Enum.TryParse<FileSortColumn>(column, out var parsed)) return;

        if (SortColumn == parsed)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = parsed;
            SortDescending = false;
        }

        Rebuild();
    }

    private void Rebuild()
    {
        var filter = SearchText.Trim();

        IEnumerable<FileItem> query = _all;
        if (filter.Length > 0)
        {
            query = query.Where(i =>
                i.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                i.RelativePath.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
        }

        // 文件夹始终排在前面，和资源管理器一致
        var ordered = query.OrderBy(i => i.IsDirectory ? 0 : 1);
        ordered = SortColumn switch
        {
            FileSortColumn.Modified => SortDescending
                ? ordered.ThenByDescending(i => i.Modified)
                : ordered.ThenBy(i => i.Modified),
            FileSortColumn.Size => SortDescending
                ? ordered.ThenByDescending(i => i.Size)
                : ordered.ThenBy(i => i.Size),
            _ => SortDescending
                ? ordered.ThenByDescending(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                : ordered.ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        Items.Clear();
        foreach (var item in ordered)
            Items.Add(item);

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasItems));
        _hasVanityNotified = false;
        OnPropertyChanged(nameof(HasVanity));
        VanityCursorText = string.Empty;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var files = _all.Count(i => !i.IsDirectory);
        var dirs = _all.Count - files;
        var derived = _all.Count(i => i.HasWallet);

        var parts = new List<string> { $"{files} 个文件" };
        if (dirs > 0) parts.Add($"{dirs} 个文件夹");
        if (derived > 0) parts.Add($"{derived} 个已派生地址");

        var vanity = _all.Count(i => i.IsVanity);
        if (vanity > 0) parts.Add($"{vanity} 个靓号");

        SummaryText = string.Join(" · ", parts);
        OnPropertyChanged(nameof(HasVanity));

        if (SelectionCount > 0)
            StatusText = $"已选择 {SelectionCount} 项";
    }

    public void UpdateSelection(IEnumerable<FileItem> selection)
    {
        Selection.Clear();
        Selection.AddRange(selection);
        SelectionCount = Selection.Count;
        SelectedItem = SelectionCount == 1 ? Selection[0] : null;

        StatusText = SelectionCount switch
        {
            0 => $"{_all.Count} 个项目",
            1 => Selection[0].FullPath,
            _ => $"已选择 {SelectionCount} 项",
        };
    }

    /// <summary>点击空白处 / 按 Esc 时收起详情面板。</summary>
    public void ClearSelection()
    {
        Selection.Clear();
        SelectionCount = 0;
        SelectedItem = null;
        StatusText = $"{_all.Count} 个项目";
    }

    /// <summary>待处理条目：有选中就用选中项，否则按当前列表顺序（排序 + 搜索过滤后的可见顺序）。</summary>
    private List<FileItem> ResolveTargets(bool includeDirectories = false)
    {
        var pool = Selection.Count > 0 ? Selection.AsEnumerable() : Items;
        return pool.Where(i => includeDirectories || !i.IsDirectory).ToList();
    }

    [RelayCommand]
    private async Task HashAsync()
    {
        if (IsBusy)
        {
            ShowToast("正在读取目录，请稍候再计算");
            return;
        }

        var targets = ResolveTargets();
        if (targets.Count == 0)
        {
            ShowToast("没有可计算的文件");
            return;
        }

        var explicitSelection = Selection.Count > 0;
        var pending = explicitSelection
            ? targets
            : targets.Where(i => !i.HasHash || i.State == ItemState.Failed).ToList();

        if (pending.Count == 0)
        {
            ShowToast("所有文件都已计算过 SHA-256");
            return;
        }

        await RunBatchAsync(pending, "计算 SHA-256", HashOneAsync, CancellationToken.None);
        UpdateSummary();
        StatusText = $"{pending.Count:N0} 个文件已完成 SHA-256";
    }

    // ───────────── 地址生成（可停止） ─────────────

    private CancellationTokenSource? _deriveCts;

    /// <summary>正在批量生成地址。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeriveButtonText))]
    [NotifyPropertyChangedFor(nameof(CanDeriveSelected))]
    private bool _isDeriving;

    public string DeriveButtonText => IsDeriving ? "停止生成" : "派生地址";

    /// <summary>详情面板的按钮：选中文件时可点，生成过程中也可以点（用来停止）。</summary>
    public bool CanDeriveSelected => IsDeriving || SelectedItem is { IsDirectory: false };

    // 允许并发执行：否则命令执行期间 CanExecute 为 false，按钮会被禁用，就没法点“停止生成”了
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DeriveOrStopAsync()
    {
        // 正在生成 → 点一下就是停止
        if (IsDeriving)
        {
            _deriveCts?.Cancel();
            ShowToast("正在停止…");
            return;
        }

        if (IsBusy)
        {
            ShowToast("正在读取目录，请稍候再派生");
            return;
        }

        var targets = ResolveTargets();
        if (targets.Count == 0)
        {
            ShowToast("没有可派生的文件");
            return;
        }

        var explicitSelection = Selection.Count > 0;
        var pending = explicitSelection
            ? targets
            : targets.Where(i => !i.HasWallet).ToList();

        // 可选的“跳过大文件”
        var skipped = 0;
        if (SkipLargeMb > 0)
        {
            var limit = (long)SkipLargeMb * 1024 * 1024;
            var before = pending.Count;
            pending = pending.Where(i => i.Size <= limit).ToList();
            skipped = before - pending.Count;
        }

        if (pending.Count == 0)
        {
            ShowToast(skipped > 0
                ? $"待处理的都是超过 {SkipLargeMb} MB 的大文件，已跳过 {skipped:N0} 个"
                : "所有文件都已派生过地址");
            return;
        }

        _deriveCts = new CancellationTokenSource();
        IsDeriving = true;

        try
        {
            var done = await RunBatchAsync(pending, "派生地址", async item =>
            {
                if (!item.HasHash && !await HashOneAsync(item)) return false;
                return await DeriveOneAsync(item);
            }, _deriveCts.Token);

            UpdateSummary();

            var skipNote = skipped > 0 ? $"，跳过 {skipped:N0} 个大文件" : string.Empty;

            StatusText = _deriveCts.IsCancellationRequested
                ? $"已停止：本次完成 {done:N0} 个，剩余 {pending.Count - done:N0} 个下次继续{skipNote}"
                : $"{pending.Count:N0} 个地址已生成{skipNote}";
        }
        finally
        {
            _cache.Flush();
            IsDeriving = false;
            _deriveCts.Dispose();
            _deriveCts = null;
        }
    }

    private async Task<int> RunBatchAsync(
        IReadOnlyList<FileItem> targets,
        string title,
        Func<FileItem, Task<bool>> work,
        CancellationToken token)
    {
        if (targets.Count == 0) return 0;

        IsBusy = true;
        IsProgressVisible = true;
        Progress = 0;

        var done = 0;
        try
        {
            foreach (var item in targets)
            {
                if (token.IsCancellationRequested) break;

                StatusText = $"{title}：{item.Name}（{done + 1}/{targets.Count:N0}）";
                await work(item);
                done++;
                Progress = (double)done / targets.Count;
            }
        }
        finally
        {
            IsProgressVisible = false;
            IsBusy = false;
            Progress = 0;
        }

        return done;
    }

    private async Task<bool> HashOneAsync(FileItem item)
    {
        item.State = ItemState.Hashing;
        item.ErrorMessage = null;
        try
        {
            var path = item.FullPath;
            item.Sha256 = await Task.Run(() => FileHasher.ComputeSha256Async(path));
            item.State = ItemState.Hashed;
            return true;
        }
        catch (Exception ex)
        {
            item.ErrorMessage = ex switch
            {
                UnauthorizedAccessException => "没有读取权限",
                FileNotFoundException => "文件已被移动或删除",
                IOException io => $"读取失败：{io.Message}",
                _ => ex.Message,
            };
            item.State = ItemState.Failed;
            return false;
        }
    }

    private async Task<bool> DeriveOneAsync(FileItem item)
    {
        if (string.IsNullOrEmpty(item.Sha256)) return false;
        try
        {
            var hash = item.Sha256!;
            var pattern = VanityPattern;
            var matchPrefix = VanityMatchPrefix;
            var matchSuffix = VanityMatchSuffix;
            var derived = await Task.Run(() => WalletService.Derive(hash));
            item.EvmAddress = derived.EvmAddress;
            item.TronAddress = derived.TronAddress;
            item.State = ItemState.Derived;
            item.ErrorMessage = null;

            var vanity = VanityAnalyzer.Evaluate(
                derived.EvmAddress, derived.TronAddress, pattern, matchPrefix, matchSuffix);
            item.SetVanity(vanity.Evm, vanity.Tron, vanity.Reason);

            // 边算边写缓存，下次打开就不用重算
            if (CacheEnabled)
            {
                _cache.Put(new CacheEntry(
                    item.FullPath, item.Size, item.Modified.UtcTicks,
                    hash, derived.EvmAddress, derived.TronAddress));
            }

            // 计算过程中出现靓号时，让「下一个靓号」按钮立刻可用
            if (item.IsVanity && !_hasVanityNotified)
            {
                _hasVanityNotified = true;
                OnPropertyChanged(nameof(HasVanity));
            }

            return true;
        }
        catch (Exception ex)
        {
            item.ErrorMessage = ex.Message;
            item.State = ItemState.Failed;
            return false;
        }
    }

    /// <summary>供右键菜单 / 单元格点击使用。</summary>
    public async Task HashItemAsync(FileItem item)
    {
        await RunBatchAsync([item], "计算 SHA-256", HashOneAsync, CancellationToken.None);
        UpdateSummary();
    }

    public async Task DeriveItemAsync(FileItem item)
    {
        await RunBatchAsync([item], "派生地址", async target =>
        {
            if (!target.HasHash && !await HashOneAsync(target)) return false;
            return await DeriveOneAsync(target);
        }, CancellationToken.None);
        UpdateSummary();
    }

    /// <summary>演示模式：对当前目录所有文件计算 SHA-256 并派生地址。</summary>
    public async Task RunDemoAsync()
    {
        if (!HasRoot || _all.Count == 0) return;

        var files = _all.Where(i => !i.IsDirectory).ToList();
        if (files.Count == 0) return;

        await RunBatchAsync(files, "计算 SHA-256", HashOneAsync, CancellationToken.None);
        await RunBatchAsync(files, "派生地址", DeriveOneAsync, CancellationToken.None);
        UpdateSummary();
        StatusText = $"演示完成：{files.Count} 个文件已生成地址";
    }

    [RelayCommand]
    private Task CopyAsync(string? text) => CopyTextAsync(text);

    public async Task CopyTextAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "—") return;
        await SetClipboardAsync(text);
        ShowToast("已复制到剪贴板");
    }

    private static async Task SetClipboardAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } window
            && window.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private CancellationTokenSource? _toastCts;

    public void ShowToast(string message)
    {
        Toast = message;
        _toastCts?.Cancel();
        var cts = new CancellationTokenSource();
        _toastCts = cts;
        _ = Task.Delay(1800, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                Dispatcher.UIThread.Post(() => Toast = null);
        }, TaskScheduler.Default);
    }
}
