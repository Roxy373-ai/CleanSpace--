using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CleanSpace.Services;

public static class AppIconService
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Load(string? displayIcon, string? installLocation)
    {
        var path = ResolvePath(displayIcon, installLocation);
        if (path is null) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;
        var loaded = LoadCore(path);
        if (loaded is not null) Cache.TryAdd(path, loaded);
        return loaded;
    }

    private static string? ResolvePath(string? displayIcon, string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(displayIcon) == false)
        {
            var value = Environment.ExpandEnvironmentVariables(displayIcon.Trim());
            if (value.StartsWith('"'))
            {
                var end = value.IndexOf('"', 1);
                if (end > 1) value = value[1..end];
            }
            else
            {
                var comma = value.LastIndexOf(',');
                if (comma > 2 && int.TryParse(value[(comma + 1)..], out _)) value = value[..comma];
            }
            value = value.Trim().Trim('"');
            if (File.Exists(value)) return Path.GetFullPath(value);
        }

        if (string.IsNullOrWhiteSpace(installLocation) || Directory.Exists(installLocation) == false) return null;
        try
        {
            return Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => new FileInfo(path).Length)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static ImageSource? LoadCore(string path)
    {
        var info = new ShFileInfo();
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiSmallIcon);
        if (result == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, null);
            image.Freeze();
            return image;
        }
        catch { return null; }
        finally { DestroyIcon(info.Icon); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
