using EdsMediaTagger.Ollama;
using System.Diagnostics;

namespace EdsMediaTagger;

public record TagResult(string FilePath, string[] Tags, TimeSpan Elapsed);

public static class Extensions
{
    private static readonly HashSet<string> ImageExts =
        [".jpg", ".jpeg", ".png", ".tiff", ".tif"];

    private static readonly HashSet<string> VideoExts =
        [".mp4"];

    public static bool IsImage(this string path) =>
        ImageExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsVideo(this string path) =>
        VideoExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsMedia(this string path) =>
        path.IsImage() || path.IsVideo();

    public static string ShortPath(this string path) =>
        Path.Combine(Path.GetFileName(Path.GetDirectoryName(path)!), Path.GetFileName(path));
}

public class MediaTagger(OllamaApi api) : IDisposable
{
    public Dictionary<string, string> FilePathErrors { get; } = [];
    public bool OverwriteExistingTags { get; set; } = false;

    public async Task TagImageAsync(string filePath, CancellationToken ct = default)
    {
        if (OverwriteExistingTags == false)
        {
            var isTagged = await ExifTool.IsAlreadyTagged(filePath);
            if (isTagged)
            {
                Console.WriteLine($"  File '{filePath.ShortPath()}' is already tagged");
                return;
            }
        }

        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var tags = await api.GetTagsForImage(bytes, ct);
        if (tags.Length == 0)
            throw new Exception("Output is 0 tags");

        await ExifTool.WriteTagsAsync(filePath, tags);
        Console.WriteLine($"  Wrote {tags.Length} tags ({string.Join(";", tags)}) to '{filePath.ShortPath()}'");
    }

    public async Task TagVideoAsync(string filePath, CancellationToken ct = default)
    {
        if (OverwriteExistingTags == false)
        {
            var isTagged = await ExifTool.IsAlreadyTagged(filePath);
            if (isTagged)
            {
                Console.WriteLine($"  File '{filePath.ShortPath()}' is already tagged");
                return;
            }
        }

        var frames = await ExtractFramesAsync(filePath, frameCount: 4, ct);
        try
        {
            var allTags = new List<string>();
            for (int i = 0; i < frames.Length; i++)
            {
                var bytes = await File.ReadAllBytesAsync(frames[i], ct);
                var frameTags = await api.GetTagsForImage(bytes, ct);
                allTags.AddRange(frameTags);
            }

            // Deduplicate tags, keeping most frequent first
            var tags = allTags
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .Take(20)
                .ToArray();
            if (tags.Length == 0)
                throw new Exception("Output is 0 tags");

            await ExifTool.WriteTagsAsync(filePath, allTags);
            Console.WriteLine($"  Wrote {tags.Length} tags ({string.Join(";", tags)}) to '{filePath.ShortPath()}'");
        }
        finally
        {
            // Clean up temp frames
            foreach (var frame in frames)
                try { File.Delete(frame); } catch { /* best effort */ }
        }
    }

    private static async Task<string[]> ExtractFramesAsync(
        string videoPath, int frameCount, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"edsmediatagger");
        Directory.CreateDirectory(tempDir);

        // Use ffprobe to get duration
        var duration = await GetVideoDurationAsync(videoPath, ct);
        var frames = new List<string>();

        for (int i = 0; i < frameCount; i++)
        {
            var position = duration.TotalSeconds * (i + 1) / (frameCount + 1);
            var outputPath = Path.Combine(tempDir, $"frame_{i:D2}.jpg");

            var args = $"-ss {position:F3} -i \"{videoPath}\" -frames:v 1 -q:v 2 \"{outputPath}\" -y";
            await RunProcessAsync("ffmpeg", args, ct);

            if (File.Exists(outputPath))
                frames.Add(outputPath);
        }

        if (frames.Count == 0)
            throw new InvalidOperationException($"Failed to extract any frames from: {videoPath}");

        return [.. frames];
    }

    private static async Task<TimeSpan> GetVideoDurationAsync(string videoPath, CancellationToken ct)
    {
        var args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"";
        var output = await RunProcessAsync("ffprobe", args, ct);

        if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);

        throw new Exception("Unable to get video duration");
    }

    private static async Task<string> RunProcessAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {exe}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new Exception($"{exe} failed (exit {process.ExitCode}): {stderr.Trim()}");

        return stdout;
    }

    public async Task ProcessDirectoryAsync(string directory, CancellationToken ct = default)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => f.IsMedia())
            .OrderBy(f => f)
            .ToArray();

        if (files.Length == 0)
        {
            Console.WriteLine($"  No media files found in directory '{directory}'.");
            return;
        }

        Console.WriteLine($"  Processing {files.Length} media file(s) in: '{directory}'...");
        Console.WriteLine(new string('─', 60));

        int index = 0;
        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();
            index++;

            var relativePath = Path.GetRelativePath(directory, filePath);
            Console.WriteLine($"  Processing [{index}/{files.Length}]: '{relativePath}'");
            try
            {
                if (filePath.IsVideo())
                    await TagVideoAsync(filePath, ct);
                else if (filePath.IsImage())
                    await TagImageAsync(filePath, ct);
                else
                    throw new NotImplementedException($"  Unsupported file type: {Path.GetExtension(filePath)}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                FilePathErrors.Add(relativePath, ex.Message);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"  ERROR: FILE='{relativePath}'; MESSAGE='{ex.Message}'");
                Console.ResetColor();
            }
        }

        Console.WriteLine($"\n{new string('─', 60)}");
    }

    public async Task ProcessFileAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            FilePathErrors.Add(filePath, $"File not found");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"  ERROR: FILE='{filePath.ShortPath()}'; MESSAGE='File not found'");
            Console.ResetColor();
            return;
        }

        Console.WriteLine($"  Processing '{filePath.ShortPath()}'...");
        try
        {
            if (filePath.IsVideo())
                await TagVideoAsync(filePath, ct);
            else if (filePath.IsImage())
                await TagImageAsync(filePath, ct);
            else
                throw new NotImplementedException($"  Unsupported file type: {Path.GetExtension(filePath)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FilePathErrors.Add(filePath, ex.Message);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"  ERROR: FILE='{filePath.ShortPath()}'; MESSAGE='{ex.Message}'");
            Console.ResetColor();
        }
    }

    public void Dispose()
    {
        ((IDisposable)api).Dispose();
    }
}
