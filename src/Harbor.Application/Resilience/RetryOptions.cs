namespace Harbor.Core.Resilience;

public sealed record RetryOptions(int MaxAttempts, TimeSpan BaseDelay, bool UseJitter);