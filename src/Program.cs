using EdsMediaTagger;
using EdsMediaTagger.Helpers;
using EdsMediaTagger.Ollama;
using System.Diagnostics;
using System.Reflection;

Console.WriteLine();
Console.WriteLine("================================================");
Console.WriteLine($"  Ed's Media Tagger v{Assembly.GetEntryAssembly()?.GetName().Version?.ToString(2)}");
Console.WriteLine("  Drop file/folder on the .exe to process.");
Console.WriteLine("================================================");
Console.WriteLine();

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

if (args.Length == 0 || string.IsNullOrEmpty(args[0]))
{
    Console.WriteLine("No valid path arguments found. Press any key to exit...");
    ConsoleHelper.FlushInput();
    Console.ReadKey();
    return;
}

Console.WriteLine("  Models:");
string modelChoice = "gemma3n:e4b";
var models = OllamaDefaults.RecommendedModelsVram.Keys.ToArray();
for (int i = 0; i < models.Length; i++)
{
    Console.WriteLine($"    {i}. {models[i]} ({OllamaDefaults.RecommendedModelsVram[models[i]]})");
}
Console.WriteLine($"  Recommended Model: {modelChoice}");
Console.Write($"  Model Choice (0-{models.Length - 1}): ");
if (int.TryParse(Console.ReadLine(), out var modelChoiceIndex) == false)
{
    Console.WriteLine("Invalid model choice. Press any key to exit...");
    ConsoleHelper.FlushInput();
    Console.ReadKey();
    return;
}
modelChoice = models[modelChoiceIndex];
Console.WriteLine($"  Model Choice: {modelChoice}");

var path = args[0];
var api = new OllamaApi();
if (await api.SetModel(modelChoice) == false)
{
    Console.WriteLine("Model not set. Press any key to exit...");
    ConsoleHelper.FlushInput();
    Console.ReadKey();
    return;
}
Console.WriteLine();


var tagger = new MediaTagger(api);
Console.WriteLine("  Overwrite existing tags? (Y/n): ");
if (ConsoleHelper.AskYesNo())
{
    tagger.OverwriteExistingTags = true;
    Console.WriteLine("  Tags will be overwritten.");
}

try
{
    if (Directory.Exists(path))
        await tagger.ProcessDirectoryAsync(path, cts.Token);
    else if (File.Exists(path))
        await tagger.ProcessFileAsync(path, cts.Token);
    else
        Console.WriteLine($"  Path not found: {path}");

    Console.WriteLine($"  Done!");
    if (tagger.FilePathErrors.Count > 0)
    {
        Console.WriteLine($"  Printing Errors...");
        foreach (var item in tagger.FilePathErrors)
        {
            Console.WriteLine($"    ERROR FOR FILE '{item.Key.ShortPath()}': '{item.Value}'");
        }
    }

    Console.WriteLine($"  Press any key to exit...");
    ConsoleHelper.FlushInput();
    Console.ReadKey();
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
}

tagger.Dispose();

// ToDo: Check for tags before writing with 
// Overwrite Tags? Ask

// ToDo: seems like its using primarily the CPU, tho GPU is at max mem but unused.