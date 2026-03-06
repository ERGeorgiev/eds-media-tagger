namespace EdsMediaTagger.Ollama;

public static class OllamaDefaults
{
    /// <summary>
    /// The best-performing multimodal models currently available in the Ollama library.
    /// Includes support for Image, Video (via frames), and native Audio (Gemma 3).
    /// </summary>
    public static readonly string[] RecommendedModels =
    {
        "moondream",       // Tiny/Fast (1.6B)
        "qwen2-vl:2b",     // Video optimized (2B)
        "gemma3:4b",       // Native Multimodal (4B)
        "gemma3n:e4b",     // Laptop/Edge optimized (8.4B)
        "llama3.2-vision", // High-accuracy Vision (11B)
        "gemma3:12b"       // Complex Multimodal (12B)
    };

    /// <summary>
    /// Accurate VRAM requirements for multimodal models (4-bit quantization).
    /// Includes: Model weights + Vision Projector + ~2048 Context Buffer.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RecommendedModelsVram = new Dictionary<string, string>
    {
        // 1.6B: Tiny footprint. Fits easily on 4GB cards.
        { "moondream", "2.2 GB" }, 
        
        // 2B: Specialized for video frames. 
        { "qwen2-vl:2b", "3.2 GB" }, 
        
        // 4B: Base weights ~3.3GB. Needs ~5.1GB total for active vision processing.
        { "gemma3:4b", "5.1 GB" }, 
        
        // 8.4B (Raw): Effective footprint is low, but the blob is ~5.6GB-7.5GB.
        // Requires ~7.2GB to remain fully on the GPU during inference.
        { "gemma3n:e4b", "7.2 GB" }, 
        
        // 11B: Weights are ~7.8GB. Projector and KV cache push this to ~9.8GB.
        { "llama3.2-vision", "9.8 GB" }, 
        
        // 12.2B: Weight file is ~8.2GB. To avoid the 0.1% GPU usage bottleneck,
        // you need ~10.5GB of total available VRAM.
        { "gemma3:12b", "10.5 GB" }
    };

    public static readonly string PromptToGetTags = """
        Analyze this image carefully and respond with ONLY a JSON object in this exact format, with no markdown, no code fences, no explanation:
        {
          "tags": ["tag1", "tag2", "tag3"]
        }
        Tags should be specific, searchable keywords covering: objects, people, animals, scenes, locations, colours, activities, mood, style, time of day. Include between 8 and 20 tags.
        """;
}
