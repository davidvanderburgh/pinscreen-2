using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

// Usage:
// Pinscreen2.Updater <appDir> <zipPath> <exeToLaunch>
//  - Waits briefly for parent process to exit
//  - Extracts zip to temp and copies into appDir (overwrite)
//  - Launches exeToLaunch and exits

static int Main(string[] args)
{
    try
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: Pinscreen2.Updater <appDir> <zipPath> <exeToLaunch>");
            return 2;
        }
        var appDir = Path.GetFullPath(args[0]);
        var zipPath = Path.GetFullPath(args[1]);
        var exeToLaunch = args[2];

        // Basic validation
        if (!Directory.Exists(appDir))
        {
            Console.Error.WriteLine($"appDir not found: {appDir}");
            return 3;
        }
        if (!File.Exists(zipPath))
        {
            Console.Error.WriteLine($"zip not found: {zipPath}");
            return 4;
        }

        // Wait a short time for the main app to exit and release locks
        Thread.Sleep(800);

        // Extract to staging
        var staging = Path.Combine(Path.GetTempPath(), "pinscreen2-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

        // Copy into appDir (preserve user config under appdata; we only copy app folder here)
        CopyRecursive(staging, appDir);

        // Cleanup staging
        try { Directory.Delete(staging, recursive: true); } catch { }

        // Launch the app
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.IsPathRooted(exeToLaunch) ? exeToLaunch : Path.Combine(appDir, exeToLaunch),
                WorkingDirectory = appDir,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Failed to relaunch app: " + ex.Message);
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.ToString());
        return 1;
    }
}

static void CopyRecursive(string source, string dest)
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
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
    }
}
