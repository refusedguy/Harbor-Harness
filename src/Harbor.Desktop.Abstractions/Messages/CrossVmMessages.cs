namespace Harbor.Desktop.Abstractions.Messages;

/// <summary>Raised when the user picks a model in the provider/model picker.</summary>
public sealed record ModelPickedMessage;

/// <summary>Raised when the onboarding wizard completes (either Finish or Skip).</summary>
public sealed record OnboardingCompletedMessage(bool IsCompleted);
