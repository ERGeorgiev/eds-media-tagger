using EdsMediaTagger;
using EdsMediaTagger.Helpers;
using System.Diagnostics;

// To Use: Drop folders/files on the .exe

if (Debugger.IsAttached)
{
    var currentDirectory = new DirectoryInfo(Directory.GetCurrentDirectory());
    var solutionDir = currentDirectory.Parent!.Parent!.Parent!.FullName;
    args = [$"{Path.Combine(solutionDir, "TestData")}"];
}

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nCancelling...");
    cts.Cancel();
};

var path = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var tagger = new GemmaMediaTagger(); // optionally: new GemmaMediaTagger("http://localhost:11434")

try
{
    if (Directory.Exists(path))
        await tagger.ProcessDirectoryAsync(path, cts.Token);
    else if (File.Exists(path))
        await tagger.ProcessFileAsync(path, cts.Token);
    else
        Console.WriteLine($"Path not found: {path}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
}

tagger.Dispose();

Console.WriteLine($"Ready to apply tags. Proceed? (Y/n): ");
if (ConsoleHelper.AskYesNo())
{
    tagger.ApplyTags().Wait();
}

Console.WriteLine($"Done! Press any key to exit...");
ConsoleHelper.FlushInput();
Console.ReadKey();