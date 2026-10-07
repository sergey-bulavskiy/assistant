namespace Assistant.Infrastructure.Llm;

/// <summary>Explicit opt-in by a provider whose native image path has been verified.</summary>
public interface IImageChatClient
{
    bool SupportsImages { get; }
}
