namespace EdsMediaTagger.Helpers;

internal static class ConsoleHelper
{
    public static bool AskYesNo()
    {
        FlushInput();
        var input = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(input) || input.StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    public static void FlushInput()
    {
        while (Console.KeyAvailable)
            Console.ReadKey(intercept: true);
    }
}
