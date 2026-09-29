namespace Assistant.Application.Manager;

/// <summary>One code, generated once per process at startup, never persisted. Logged to the
/// console (Program.cs) only while the platform hasn't been claimed yet.</summary>
public interface IClaimCodeProvider
{
    string Code { get; }
}
