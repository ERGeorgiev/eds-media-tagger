using EdsMediaTagger.Helpers;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatRole = OllamaSharp.Models.Chat.ChatRole;

namespace EdsMediaTagger.Ollama;

file record TagResponse(
    [property: JsonPropertyName("tags")] string[] Tags);

public class OllamaApi(string ollamaBaseUrl = "http://localhost:11434") : IDisposable
{
    private readonly JsonSerializerOptions _tagResponseSerializerOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly OllamaApiClient _client = new(new Uri(ollamaBaseUrl));

    public async Task<bool> SetModel(string modelId)
    {
        if (await EnsureModelInstalled(modelId) == false)
            return false;
        _client.SelectedModel = modelId;
        return true;
    }

    private async Task<string[]> GetInstalledModelsAsync()
    {
        var response = await _client.ListLocalModelsAsync();
        return response.Select(m => m.ModelName).OfType<string>().Where(s => string.IsNullOrEmpty(s) == false).ToArray();
    }

    private async Task<bool> EnsureModelInstalled(string modelId)
    {
        var installed = await GetInstalledModelsAsync();
        if (installed.Contains(modelId) == false)
        {
            Console.WriteLine($"Model '{modelId}' not installed. Download? (Y/n): ");
            if (ConsoleHelper.AskYesNo() == false)
                return false;

            try
            {
                await foreach (var response in _client.PullModelAsync(modelId))
                {
                    if (response == null) 
                        continue;
                    Console.WriteLine($"Model download progress: {response.Percent:0}%");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Pull failed: {e.Message}");
                throw;
            }
        }

        return true;
    }

    public async Task<string[]> GetTagsForImage(byte[] imageBytes, CancellationToken ct = default)
    {
        var base64 = Convert.ToBase64String(imageBytes);
        var request = new ChatRequest
        {
            Model = _client.SelectedModel,
            Stream = false,
            Messages =
            [
                new Message
                {
                    Role = ChatRole.User,
                    Content = OllamaDefaults.PromptToGetTags,
                    Images = [base64]
                }
            ],
            Options = new RequestOptions
            {
                NumPredict = 512,
                NumGpu = 99,
            }
        };

        var fullResponse = new StringBuilder();
        await foreach (var chunk in _client.ChatAsync(request, ct))
        {
            if (string.IsNullOrEmpty(chunk?.Message?.Content) == false)
            {
                fullResponse.Append(chunk.Message.Content);
            }
        }

        return ParseTags(fullResponse.ToString());

        string[] ParseTags(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return [];

            // Strip markdown fences if the model ignores instructions
            var json = content.Trim();
            if (json.StartsWith("```"))
            {
                var firstNewline = json.IndexOf('\n');
                var lastFence = json.LastIndexOf("```");
                if (firstNewline > 0 && lastFence > firstNewline)
                    json = json[(firstNewline + 1)..lastFence].Trim();
            }

            var result = JsonSerializer.Deserialize<TagResponse>(json, _tagResponseSerializerOptions);
            return result?.Tags ?? [];
        }
    }

    public void Dispose()
    {
        ((IDisposable)_client).Dispose();
    }
}
