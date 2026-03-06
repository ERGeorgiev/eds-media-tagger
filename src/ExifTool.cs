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

        // QuickTime:Category is what Windows Explorer reads as "Tags"
        args.Add($"-QuickTime:Category={string.Join(";", tags)}");
        // Also write XMP:Subject for non-Windows tools (Lightroom, digiKam, etc.)
        foreach (var tag in tags)
            args.Add($"-XMP-dc:Subject={tag}");

        // This is what Windows Explorer reads as "Tags"
        args.Add($"-Microsoft:Category={string.Join(";", tags)}");

        args.Add("-overwrite_original");
        args.Add(filePath);

        args.Insert(0, "-f");
        args.Insert(1, "-m");

        await ExecuteAsync(args);
    }

    private static async Task<string> ExecuteAsync(IReadOnlyList<string> args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "exiftool",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            }
        };

        // Use -@ - to read args from stdin, with UTF-8 filename charset
        process.StartInfo.ArgumentList.Add("-charset");
        process.StartInfo.ArgumentList.Add("filename=utf8");
        process.StartInfo.ArgumentList.Add("-@");
        process.StartInfo.ArgumentList.Add("-");

        process.Start();

        // Write args to stdin as UTF-8, one per line
        await using (var writer = new StreamWriter(
            process.StandardInput.BaseStream, new UTF8Encoding(false)))
        {
            foreach (var arg in args)
                await writer.WriteLineAsync(arg);
        }

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
