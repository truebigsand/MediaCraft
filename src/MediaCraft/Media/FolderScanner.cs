using System.IO;

namespace MediaCraft.Media;

/// <summary>
/// 文件夹递归扫描：只返回白名单内的媒体/字幕文件，跳过隐藏文件与临时文件。
/// </summary>
public static class FolderScanner
{
    /// <summary>递归或单层扫描目录下的媒体文件（结果按路径排序）。</summary>
    public static IReadOnlyList<string> Scan(string folder, bool recursive, CancellationToken cancellationToken = default)
    {
        var results = new List<string>();
        if (!Directory.Exists(folder))
        {
            return results;
        }

        var pending = new Stack<string>();
        pending.Push(folder);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch (Exception)
            {
                // 无权限的目录直接跳过
                continue;
            }

            foreach (var file in files)
            {
                if (IsAcceptable(file))
                {
                    results.Add(file);
                }
            }

            if (!recursive)
            {
                continue;
            }

            try
            {
                foreach (var subdirectory in Directory.EnumerateDirectories(current))
                {
                    var name = Path.GetFileName(subdirectory);
                    if (name.StartsWith('.') || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    pending.Push(subdirectory);
                }
            }
            catch (Exception)
            {
                // 枚举子目录失败不影响已收集的结果
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    /// <summary>是否是列表可接受的媒体文件。</summary>
    public static bool IsAcceptable(string file)
    {
        var name = Path.GetFileName(file);
        if (name.StartsWith('~') || name.StartsWith('.'))
        {
            return false;
        }

        return Ffmpeg.MediaProbe.IsMediaFile(file);
    }
}
