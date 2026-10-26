// Shell.cs — проводник, корзина, ярлыки на рабочем столе, папка «Загрузки», ссылки.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SR2ModManager.Launcher;

internal static class Shell
{
    public static string? Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return null; }
        catch (Exception e) { return e.Message; }
    }

    public static string? OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    public static string? Reveal(string path)
    {
        try
        {
            if (!File.Exists(path)) return OpenFolder(Path.GetDirectoryName(path) ?? path);
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

    /// Удаляет файл в корзину (его можно восстановить).
    public static string? Recycle(string path)
    {
        try
        {
            const uint FO_DELETE = 3;
            const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
            var op = new SHFILEOPSTRUCT { wFunc = FO_DELETE, pFrom = path + "\0\0", fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI };
            int rc = SHFileOperationW(ref op);
            if (rc != 0) return "код ошибки 0x" + rc.ToString("X");
            if (op.fAnyOperationsAborted) return "операция отменена";
            return File.Exists(path) ? "файл занят (возможно, игрой)" : null;
        }
        catch (Exception e) { return e.Message; }
    }

    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    /// Ярлык .lnk через WScript.Shell (PowerShell, как в build.ps1). null — успех.
    public static string? CreateShortcut(string lnk, string target, string args, string workDir, string icon, string description)
    {
        try
        {
            string ps = "$ErrorActionPreference='Stop'; $ws = New-Object -ComObject WScript.Shell; " +
                        $"$l = $ws.CreateShortcut({Q(lnk)}); $l.TargetPath = {Q(target)}; $l.Arguments = {Q(args)}; " +
                        $"$l.WorkingDirectory = {Q(workDir)}; $l.IconLocation = {Q(icon)}; $l.Description = {Q(description)}; $l.Save()";
            string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(20000);
            if (File.Exists(lnk)) return null;
            return string.IsNullOrWhiteSpace(err) ? "ярлык не создан" : err.Trim();
        }
        catch (Exception e) { return e.Message; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    /// Папка «Загрузки» пользователя (в том числе перенесённая на другой диск) или null.
    public static string? DownloadsDir()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0 && p != IntPtr.Zero)
            {
                try
                {
                    string? s = Marshal.PtrToStringUni(p);
                    if (!string.IsNullOrEmpty(s) && Directory.Exists(s)) return s;
                }
                finally { Marshal.FreeCoTaskMem(p); }
            }
        }
        catch { }
        string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(d) ? d : null;
    }

    /// Открыть ссылку в браузере — только http(s).
    public static string? OpenLink(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
            return "это не веб-ссылка: " + url;
        return Open(u.AbsoluteUri);
    }

    public static string SafeFileName(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return sb.ToString().Trim().TrimEnd('.');
    }
}
