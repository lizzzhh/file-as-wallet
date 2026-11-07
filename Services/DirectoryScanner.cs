using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FileAsWallet.Services;

public sealed record FileEntryInfo(
    string Name,
    string FullPath,
    string RelativePath,
    long Size,
    DateTimeOffset Modified,
    bool IsDirectory);

public static class DirectoryScanner
{
    /// <summary>流式读取目录：调用方可以边读边显示，不再有固定条目上限。</summary>
    public static IEnumerable<FileEntryInfo> Enumerate(string root, bool recursive)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"目录不存在：{root}");

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
            MatchType = MatchType.Simple,
            ReturnSpecialDirectories = false,
        };

        foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
        {
            FileEntryInfo? entry = null;
            try
            {
                var isDir = info is DirectoryInfo;

                // 递归模式下文件夹本身不再单独占一行，只显示其中的内容
                if (recursive && isDir) continue;

                entry = new FileEntryInfo(
                    Name: info.Name,
                    FullPath: info.FullName,
                    RelativePath: Path.GetRelativePath(root, info.FullName),
                    Size: isDir ? 0 : ((FileInfo)info).Length,
                    Modified: info.LastWriteTime,
                    IsDirectory: isDir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (entry is not null) yield return entry;
        }
    }

    public static string? GetParent(string path)
    {
        try
        {
            var parent = Directory.GetParent(path);
            return parent?.FullName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static IEnumerable<string> GetRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.IsReady)
                yield return drive.RootDirectory.FullName;
        }
    }
}
