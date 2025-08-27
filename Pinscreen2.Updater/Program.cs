using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Diagnostics;

// Usage:
// Pinscreen2.Updater <appDir> <zipPath> <exeToLaunch> [parentPid]
//  - Waits briefly for parent process (if provided) to exit
//  - Extracts zip to temp and copies into appDir (overwrite)
//  - Launches exeToLaunch and exits

static int Main(string[] args)
{
    try
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: Pinscreen2.Updater <appDir> <zipPath> <exeToLaunch> [parentPid]");
            return 2;
        }
        var appDir = Path.GetFullPath(args[0]);
        var zipPath = Path.GetFullPath(args[1]);
        var exeToLaunch = args[2];
        var parentPid = args.Length >= 4 ? args[3] : string.Empty;

        string GetUserLogDir()
        {
            try
            {
                var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(baseDir))
                {
                    var dir = Path.Combine(baseDir, "Pinscreen2");
                    Directory.CreateDirectory(dir);
                    return dir;
                }
            }
            catch { }
            return appDir;
        }

        var appLogPath = Path.Combine(appDir, "update-log.txt");
        var userLogPath = Path.Combine(GetUserLogDir(), "update-log.txt");

        void Log(string message)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            try { File.AppendAllText(appLogPath, line); } catch { }
            try { File.AppendAllText(userLogPath, line); } catch { }
            try { Console.WriteLine(message); } catch { }
        }

        // Basic validation
        if (!Directory.Exists(appDir))
        {
            Log($"ERROR: appDir not found: {appDir}");
            return 3;
        }
        if (!File.Exists(zipPath))
        {
            Log($"ERROR: zip not found: {zipPath}");
            return 4;
        }

        // Wait for parent process to exit if provided
        if (!string.IsNullOrWhiteSpace(parentPid) && int.TryParse(parentPid, out var pid))
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                Log($"Waiting for parent process PID {pid} to exit...");
                proc.WaitForExit(5000);
            }
            catch { }
        }

        // Wait a short time for the main app to exit and release locks
        Log("Waiting for main app to exit...");
        Thread.Sleep(800);

        // Extract to staging
        var staging = Path.Combine(Path.GetTempPath(), "pinscreen2-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        Log($"Extracting update to staging: {staging}");
        ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

        // Determine current updater filename to avoid overwriting self
        string runningUpdaterName = string.Empty;
        try { runningUpdaterName = Path.GetFileName(Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty); } catch { }

        // Copy into appDir (preserve user config under appdata; we only copy app folder here)
        Log($"Copying files into app directory: {appDir}");
        CopyRecursive(staging, appDir, runningUpdaterName);

        // Cleanup staging
        try { Directory.Delete(staging, recursive: true); } catch (Exception ex) { Log($"Warning: failed to delete staging: {ex.Message}"); }

        // Launch the app
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.IsPathRooted(exeToLaunch) ? exeToLaunch : Path.Combine(appDir, exeToLaunch),
                WorkingDirectory = appDir,
                UseShellExecute = true
            };
            Log($"Relaunching app: {startInfo.FileName}");
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Log("ERROR: Failed to relaunch app: " + ex.Message);
        }
        Log("Update complete.");
        return 0;
    }
    catch (Exception ex)
    {
        try
        {
            // Attempt to write detailed error and show it
            var appDir = args.Length > 0 ? Path.GetFullPath(args[0]) : AppContext.BaseDirectory;
            var appLogPath = Path.Combine(appDir, "update-log.txt");
            File.AppendAllText(appLogPath, ex + Environment.NewLine);
            TryOpenLog(appLogPath);
            Thread.Sleep(2000);
        }
        catch { }
        return 1;
    }
}

static void CopyRecursive(string source, string dest, string skipFileName)
{
    foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(source, dir);
        Directory.CreateDirectory(Path.Combine(dest, rel));
    }
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(source, file);
        var target = Path.Combine(dest, rel);
        var targetName = Path.GetFileName(target);
        if (!string.IsNullOrWhiteSpace(skipFileName) && string.Equals(targetName, skipFileName, StringComparison.OrdinalIgnoreCase))
        {
            continue; // avoid overwriting the running updater
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
    }
}

static void TryOpenLog(string logPath)
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("notepad.exe", '"' + logPath + '"') { UseShellExecute = false });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", '"' + logPath + '"');
        }
        else
        {
            Process.Start("xdg-open", '"' + logPath + '"');
        }
    }
    catch { }
}
