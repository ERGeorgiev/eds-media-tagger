using System.Diagnostics;
using System.Text;

namespace EdsMediaTagger;

public static class ExifTool
{
    public static async Task WriteTagsAsync(string filePath, IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
            return;

        var args = new List<string>(tags.Count * 3 + 3)
        {
            $"-XPKeywords={string.Join(";", tags)}"
        };

        foreach (var tag in tags)
            args.Add($"-Keywords={tag}");

        foreach (var tag in tags)
            args.Add($"-Subject={tag}");

        args.Add("-overwrite_original");
        args.Add(filePath);

        // QuickTime:Category is what Windows Explorer reads as "Tags"
        args.Add($"-QuickTime:Category={string.Join(";", tags)}");
        // Also write XMP:Subject for non-Windows tools (Lightroom, digiKam, etc.)
        foreach (var tag in tags)
            args.Add($"-XMP-dc:Subject={tag}");

        // This is what Windows Explorer reads as "Tags"
        args.Add($"-Microsoft:Category={string.Join(";", tags)}");

        args.Insert(0, "-charset");
        args.Insert(1, "filename=utf8");

        await ExecuteAsync(args);
    }

    private static async Task<string> ExecuteAsync(IReadOnlyList<string> args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "exiftool",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            }
        };

        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"ExifTool failed (exit code {process.ExitCode}): {error.Trim()}");

        return output;
    }
}
