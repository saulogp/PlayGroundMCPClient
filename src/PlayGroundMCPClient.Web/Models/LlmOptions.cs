namespace PlayGroundMCPClient.Web.Models;

/// Which LLM endpoint to talk to.
public enum LlmProvider
{
    /// OpenAI direct (api.openai.com). Needs Model + ApiKey.
    OpenAI,
    /// Azure OpenAI (*.openai.azure.com). Needs Endpoint + Deployment (Model) +
    /// ApiKey, and uses an api-version on the request.
    AzureOpenAI
}

/// LLM provider settings. OpenAI direct only needs Model + ApiKey; Azure OpenAI
/// also needs Endpoint and (optionally) ApiVersion.
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public LlmProvider Provider { get; set; } = LlmProvider.OpenAI;

    /// OpenAI model id, or — for Azure — the deployment name.
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";

    /// Azure OpenAI resource endpoint (e.g. https://my-res.openai.azure.com/).
    public string Endpoint { get; set; } = "";

    /// Azure OpenAI api-version (e.g. 2024-10-21). Empty = SDK default.
    public string ApiVersion { get; set; } = "";

    public bool IsConfigured => Provider switch
    {
        LlmProvider.AzureOpenAI =>
            !string.IsNullOrWhiteSpace(Model) &&
            !string.IsNullOrWhiteSpace(ApiKey) &&
            !string.IsNullOrWhiteSpace(Endpoint),
        _ =>
            !string.IsNullOrWhiteSpace(Model) &&
            !string.IsNullOrWhiteSpace(ApiKey)
    };
}
