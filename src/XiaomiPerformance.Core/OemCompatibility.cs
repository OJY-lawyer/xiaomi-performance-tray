using System.Diagnostics;

namespace XiaomiPerformance;

/// <summary>Explicitly reviewed installations; new vendor versions require a protocol review.</summary>
public static class OemCompatibility
{
    public static readonly string ManagerBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MI", "XiaomiPCManager");
    public static readonly string ServiceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Timi Personal Computing", "MiService", "4.1.1.648");
    private static readonly string[] ManagerVersions = ["5.8.1.121", "5.8.1.130"];

    public static string ResolveManagerRoot(string executablePath, string? fileVersion, string? product)
    {
        foreach (var version in ManagerVersions)
        {
            var root = Path.Combine(ManagerBase, version);
            if (string.Equals(executablePath, Path.Combine(root, "XiaomiPcManager.exe"), StringComparison.OrdinalIgnoreCase)
                && fileVersion == version && product == "小米电脑管家") return root;
        }
        throw new InvalidOperationException($"管家版本或安装路径尚未核验（{fileVersion ?? "未知版本"}）；已暂停切换，请更新托盘适配。支持 5.8.1.121／5.8.1.130。");
    }

    public static bool MatchesServiceImage(string imagePath)
    {
        // Quoted ImagePath contains the entire executable, not a substring match.
        var text = imagePath.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
        return string.Equals(text, Path.Combine(ServiceRoot, "MiService.exe"), StringComparison.OrdinalIgnoreCase);
    }

    public static void CheckVersionResource(string path, string expectedVersion, string expectedProduct)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("缺少已核验的原厂文件，请先恢复小米电脑管家。", path);
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.FileVersion != expectedVersion || info.ProductName != expectedProduct)
            throw new InvalidOperationException($"原厂组件版本不在已核验范围：{Path.GetFileName(path)}（{info.FileVersion ?? "未知版本"}）。已暂停切换。");
    }
}
