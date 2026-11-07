using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FileAsWallet.Services;

/// <summary>一条已算好的结果：文件身份（路径 + 大小 + 修改时间）决定它是否仍然有效。</summary>
public sealed record CacheEntry(
    string Path,
    long Size,
    long ModifiedTicks,
    string Sha256,
    string Evm,
    string Tron);

/// <summary>
/// 结果缓存：把「路径 + 大小 + 修改时间」映射到 SHA-256 与两个地址。
/// 存成 JSON Lines（每行一条），便于追加写入，也方便人工查看/清理。
/// </summary>
public sealed class ResultCache
{
    private sealed class Row
    {
        [JsonPropertyName("p")] public string? P { get; set; }
        [JsonPropertyName("s")] public long S { get; set; }
        [JsonPropertyName("m")] public long M { get; set; }
        [JsonPropertyName("h")] public string? H { get; set; }
        [JsonPropertyName("e")] public string? E { get; set; }
        [JsonPropertyName("t")] public string? T { get; set; }
    }

    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pending = [];
    private readonly object _gate = new();

    private int _dirty;

    public string FolderPath { get; }
    public string FilePath { get; }
    public int Count { get { lock (_gate) return _entries.Count; } }

    public ResultCache()
    {
        FolderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileAsWallet");
        FilePath = Path.Combine(FolderPath, "result-cache.jsonl");
    }

    /// <summary>读取缓存文件（在后台线程调用）。</summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;

            foreach (var line in File.ReadLines(FilePath, Encoding.UTF8))
            {
                if (line.Length == 0) continue;
                Row? row;
                try { row = JsonSerializer.Deserialize<Row>(line); }
                catch (JsonException) { continue; }

                if (row?.P is null || row.H is null || row.E is null || row.T is null) continue;
                _entries[row.P] = new CacheEntry(row.P, row.S, row.M, row.H, row.E, row.T);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public bool TryGet(string path, long size, long modifiedTicks, out CacheEntry entry)
    {
        if (_entries.TryGetValue(path, out var found) &&
            found.Size == size && found.ModifiedTicks == modifiedTicks)
        {
            entry = found;
            return true;
        }

        entry = null!;
        return false;
    }

    public void Put(CacheEntry entry)
    {
        bool flushNeeded;
        lock (_gate)
        {
            _entries[entry.Path] = entry;
            _pending.Add(JsonSerializer.Serialize(new Row
            {
                P = entry.Path,
                S = entry.Size,
                M = entry.ModifiedTicks,
                H = entry.Sha256,
                E = entry.Evm,
                T = entry.Tron,
            }));

            flushNeeded = ++_dirty >= 1000;
            if (flushNeeded) _dirty = 0;
        }

        // 每 1000 条落一次盘，避免每算一个文件都写磁盘
        if (flushNeeded) _ = Task.Run(Flush);
    }

    /// <summary>把待写入的条目追加到缓存文件。</summary>
    public void Flush()
    {
        List<string> batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            batch = [.. _pending];
            _pending.Clear();
        }

        try
        {
            Directory.CreateDirectory(FolderPath);
            File.AppendAllLines(FilePath, batch, Encoding.UTF8);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>清空缓存（删文件 + 清内存）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _pending.Clear();
        }

        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public long FileSizeBytes()
    {
        try { return File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0; }
        catch (IOException) { return 0; }
    }
}
