using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;

// Runs from the update cache on the Windows inbox .NET Framework, so no files
// in the application's self-contained .NET runtime stay mapped during install.
internal static class Program
{
    private const string Updating = @"Local\XingMengYu.WristMusic.Updating";
    private const string Prefix = "WristMusic-win-x64/";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string source, string target, int flags);

    private static void ReplaceFile(string source, string target)
    {
        // Rename within the destination directory, including on exFAT volumes.
        if (!MoveFileEx(source, target, 1 | 8))
            throw new IOException("无法替换 " + Path.GetFileName(target),
                Marshal.GetHRForLastWin32Error());
    }

    private static void RetryLocks(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (IOException e)
            {
                int code = e.HResult & 65535;
                if ((code != 32 && code != 33) || attempt >= 16) throw;
                Thread.Sleep(250);
            }
        }
    }

    private static void VerifyVersion(string root, Version expected)
    {
        var actual = AssemblyName.GetAssemblyName(Path.Combine(root, "WristMusic.dll")).Version;
        if (actual.ToString(3) != expected.ToString(3))
            throw new IOException("安装后的版本仍为 " + actual.ToString(3) + "，预期为 " + expected.ToString(3));
    }

    private sealed class Change
    {
        public string Target, Backup;
        public bool Installed;
    }

    private static void InstallZip(Stream package, string root, Version version)
    {
        var changes = new List<Change>();
        using (var zip = new ZipArchive(package, ZipArchiveMode.Read, true))
        {
            var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith("/")) continue;
                if (!name.StartsWith(Prefix, StringComparison.Ordinal)) throw new InvalidDataException("免安装包的目录结构不正确");
                string relative = name.Substring(Prefix.Length);
                if (relative.Contains(':') || relative.Split('/').Any(s => s.Length == 0 || s == "." || s == ".."))
                    throw new InvalidDataException("免安装包内有无效路径");
                files.Add(relative, entry);
            }
            foreach (var name in new[] { "WristMusic.exe", "WristMusic.dll", "WristMusic.deps.json", "coreclr.dll" })
                if (!files.ContainsKey(name)) throw new InvalidDataException("免安装包缺少 " + name);
            try
            {
                foreach (var file in files)
                {
                    string target = Path.GetFullPath(Path.Combine(root, file.Key));
                    for (string node = target; !string.Equals(node, root, StringComparison.OrdinalIgnoreCase); node = Path.GetDirectoryName(node))
                        if (node == null || ((File.Exists(node) || Directory.Exists(node)) && (File.GetAttributes(node) & FileAttributes.ReparsePoint) != 0))
                            throw new IOException("更新目标包含重定向路径");
                    string dir = Path.GetDirectoryName(target);
                    Directory.CreateDirectory(dir);
                    string temp = Path.Combine(dir, Path.GetRandomFileName());
                    using (var input = file.Value.Open())
                    {
                        try
                        {
                            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                input.CopyTo(output);
                            var change = new Change { Target = target };
                            if (File.Exists(target))
                            {
                                string backup = Path.Combine(dir, Path.GetRandomFileName());
                                RetryLocks(() => File.Copy(target, backup));
                                change.Backup = backup;
                            }
                            changes.Add(change);
                            RetryLocks(() => ReplaceFile(temp, target));
                            change.Installed = true;
                        }
                        finally { File.Delete(temp); }
                    }
                }
                VerifyVersion(root, version);
            }
            catch
            {
                var errors = new List<Exception>();
                foreach (var change in Enumerable.Reverse(changes))
                {
                    try
                    {
                        if (change.Installed && change.Backup != null) RetryLocks(() => ReplaceFile(change.Backup, change.Target));
                        else if (change.Installed) RetryLocks(() => File.Delete(change.Target));
                        else if (change.Backup != null) File.Delete(change.Backup);
                    }
                    catch (Exception error) { errors.Add(error); }
                }
                if (errors.Count > 0) throw new IOException("还原旧文件失败：" + errors[0].Message, errors[0]);
                throw;
            }
        }
        foreach (var change in changes)
            if (change.Backup != null)
            {
                try { File.Delete(change.Backup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
    }

    private static int Main(string[] args)
    {
        if (args.Length != 8) return 2;
        using (var identity = WindowsIdentity.GetCurrent())
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 3;
        string root = args[0], package = args[1], cache = Path.GetDirectoryName(package);
        string errorFile = Path.Combine(cache, "error.txt");
        bool restart = false, success = false, corrupt = false;
        using (var mutex = new Mutex(false, Updating))
        {
            try { if (!mutex.WaitOne(0)) return 4; }
            catch (AbandonedMutexException) { }
            try
            {
                using (var parent = Process.GetProcessById(int.Parse(args[3])))
                using (var ready = EventWaitHandle.OpenExisting(args[4]))
                using (var proceed = EventWaitHandle.OpenExisting(args[4] + ".proceed"))
                using (var input = File.OpenRead(package))
                {
                    var version = Version.Parse(args[5]);
                    // Confirm write access before asking the application to exit.
                    using (new FileStream(Path.Combine(root, Path.GetRandomFileName()), FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                    ready.Set();
                    if (!proceed.WaitOne(15000)) return 5;
                    if (!parent.WaitForExit(120000)) throw new IOException("旧程序未完全退出，请关闭腕间音乐后重试");
                    restart = true;
                    using (var sha = SHA256.Create())
                        if (BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "") != args[2])
                        {
                            corrupt = true;
                            throw new InvalidDataException("下载文件校验失败，请重新检测更新");
                        }
                    input.Position = 0;
                    if (args[6] == "setup")
                    {
                        var start = new ProcessStartInfo(package, "/CURRENTUSER /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=\"" + root + "\"")
                            { UseShellExecute = false, WorkingDirectory = cache };
                        using (var installer = Process.Start(start))
                        {
                            installer.WaitForExit();
                            if (installer.ExitCode != 0) throw new IOException("安装程序退出码 " + installer.ExitCode);
                        }
                        VerifyVersion(root, version);
                    }
                    else InstallZip(input, root, version);
                }
                success = true;
                File.Delete(Path.Combine(cache, "pending.json"));
                File.Delete(package);
                File.Delete(errorFile);
            }
            catch (Exception e)
            {
                File.WriteAllText(errorFile, e.Message);
                if (corrupt) File.Delete(package);
            }
            finally { mutex.ReleaseMutex(); }
        }
        if (restart)
        {
            string flags = args[7] == "steamvr" ? "--steamvr-startup " : "";
            if (!success) flags += "--update-retry-later";
            try
            {
                RetryLocks(() => Process.Start(new ProcessStartInfo(Path.Combine(root, "WristMusic.exe"), flags)
                    { WorkingDirectory = root, UseShellExecute = false }).Dispose());
            }
            catch (Exception e) { File.WriteAllText(errorFile, "重新启动失败：" + e.Message); return 1; }
        }
        return success ? 0 : 1;
    }
}
