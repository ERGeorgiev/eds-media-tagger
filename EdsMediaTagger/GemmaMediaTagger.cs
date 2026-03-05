using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatRole = OllamaSharp.Models.Chat.ChatRole;

namespace EdsMediaTagger;

public record TagResult(string FilePath, string[] Tags, TimeSpan Elapsed);

file record TagResponse(
    [property: JsonPropertyName("tags")] string[] Tags);

public static class Extensions
{
    private static readonly HashSet<string> ImageExts =
        [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tiff", ".tif"];

    private static readonly HashSet<string> VideoExts =
        [".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v", ".wmv"];

    public static bool IsImage(this string path) =>
        ImageExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsVideo(this string path) =>
        VideoExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsMedia(this string path) =>
        path.IsImage() || path.IsVideo();
}

public class GemmaMediaTagger : IDisposable
{
    private Dictionary<string, string[]> _fileTags = [];
    private const string Model = "gemma3:12b";


    private const string TagPrompt = """
        Analyze this image carefully and respond with ONLY a JSON object in this exact format, with no markdown, no code fences, no explanation:
        {
          "tags": ["tag1", "tag2", "tag3"]
        }
        Tags should be specific, searchable keywords covering: objects, people, animals, scenes, locations, colours, activities, mood, style, time of day. Include between 8 and 20 tags.
        """;

    private readonly OllamaApiClient _ollama;

    public GemmaMediaTagger(string ollamaBaseUrl = "http://localhost:11434")
    {
        _ollama = new OllamaApiClient(new Uri(ollamaBaseUrl))
        {
            SelectedModel = Model
        };
    }

    public async Task<TagResult> TagImageAsync(string filePath, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var base64 = Convert.ToBase64String(bytes);

        var request = new ChatRequest
        {
            Model = Model,
            Stream = false,
            Messages =
            [
                new Message
                {
                    Role = ChatRole.User,
                    Content = TagPrompt,
                    Images = [base64]
                }
            ],
            Options = new RequestOptions
            {
                NumPredict = 512
            }
        };

        ChatResponseStream? response = null;
        await foreach (var chunk in _ollama.ChatAsync(request, ct))
            response = chunk;

        sw.Stop();
        return ParseResponse(filePath, response?.Message?.Content, sw.Elapsed);
    }

    public async Task<TagResult> TagVideoAsync(string filePath, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        Console.WriteLine($"  Extracting frames from video...");
        var frames = await ExtractFramesAsync(filePath, frameCount: 4, ct);

        try
        {
            var allTags = new List<string>();

            for (int i = 0; i < frames.Length; i++)
            {
                Console.WriteLine($"  Tagging frame {i + 1}/{frames.Length}...");
                var frameResult = await TagImageAsync(frames[i], ct);

                allTags.AddRange(frameResult.Tags);
            }

            // Deduplicate tags, keeping most frequent first
            var mergedTags = allTags
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .Take(20)
                .ToArray();

            sw.Stop();
            return new TagResult(filePath, mergedTags, sw.Elapsed);
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"mediatagger_{Guid.NewGuid():N}");
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

        return TimeSpan.FromSeconds(30); // fallback
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

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return output;
    }

    private static TagResult ParseResponse(string filePath, string? content, TimeSpan elapsed)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new TagResult(filePath, [], elapsed);

        try
        {
            // Strip markdown fences if the model ignores instructions
            var json = content.Trim();
            if (json.StartsWith("```"))
            {
                var firstNewline = json.IndexOf('\n');
                var lastFence = json.LastIndexOf("```");
                if (firstNewline > 0 && lastFence > firstNewline)
                    json = json[(firstNewline + 1)..lastFence].Trim();
            }

            var result = JsonSerializer.Deserialize<TagResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return new TagResult(
                filePath,
                result?.Tags ?? [],
                elapsed);
        }
        catch (JsonException)
        {
            // Fallback: no structured tags, use raw response as description
            return new TagResult(filePath, [], elapsed);
        }
    }

    public async Task ProcessDirectoryAsync(string directory, CancellationToken ct = default)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => f.IsMedia())
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0)
        {
            Console.WriteLine("No media files found.");
            return;
        }

        Console.WriteLine($"Found {files.Count} media file(s) in: {directory}");
        Console.WriteLine(new string('─', 60));

        int index = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            index++;

            var relativePath = Path.GetRelativePath(directory, file);
            Console.WriteLine($"\n[{index}/{files.Count}] {relativePath}");

            try
            {
                var result = file.IsVideo()
                    ? await TagVideoAsync(file, ct)
                    : await TagImageAsync(file, ct);
                _fileTags.Add(file, result.Tags);

                PrintResult(result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  ERROR: {ex.Message}");
                Console.ResetColor();
            }
        }

        Console.WriteLine($"\n{new string('─', 60)}");
        Console.WriteLine("Done.");
    }

    public async Task ProcessFileAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"File not found: {filePath}");
            return;
        }

        if (!filePath.IsMedia())
        {
            Console.WriteLine($"Unsupported file type: {Path.GetExtension(filePath)}");
            return;
        }

        Console.WriteLine($"Processing: {filePath}");

        var result = filePath.IsVideo()
            ? await TagVideoAsync(filePath, ct)
            : await TagImageAsync(filePath, ct);
        _fileTags.Add(filePath, result.Tags);

        PrintResult(result);
    }

    private static void PrintResult(TagResult result)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  Tags ({result.Tags.Length}): {string.Join(", ", result.Tags)}");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  Time: {result.Elapsed.TotalSeconds:F1}s");
        Console.ResetColor();
    }

    public async Task ApplyTags()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  Applying Tags...");
        Console.ResetColor();
        //foreach (var item in _fileTags)
        //{
        //    try
        //    {
        //        await ExifTool.WriteTagsAsync(item.Key, item.Value);
        //        Console.ForegroundColor = ConsoleColor.Green;
        //        Console.WriteLine($"  Wrote tags to {Path.GetFileName(item.Key)}");
        //        Console.ResetColor();
        //    }
        //    catch (Exception)
        //    {
        //        Console.ForegroundColor = ConsoleColor.Red;
        //        Console.WriteLine($"  Failed to write tags to {Path.GetFileName(item.Key)}");
        //        Console.ResetColor();
        //    }
        //}

        await Parallel.ForEachAsync(_fileTags, async (item, ct) =>
        {
            try
            {
                await ExifTool.WriteTagsAsync(item.Key, item.Value);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  Wrote tags to {Path.GetFileName(item.Key)}");
                Console.ResetColor();
            }
            catch (Exception)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  Failed to write tags to {Path.GetFileName(item.Key)}");
                Console.ResetColor();
            }
        });
    }

    public void Dispose()
    {
        ((IDisposable)_ollama).Dispose();
    }
}
