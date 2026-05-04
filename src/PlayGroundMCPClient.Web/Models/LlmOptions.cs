namespace PlayGroundMCPClient.Web.Models;

/// LLM provider settings. Currently OpenAI direct (api.openai.com) — only
/// model + key needed.
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Model) &&
        !string.IsNullOrWhiteSpace(ApiKey);
}
