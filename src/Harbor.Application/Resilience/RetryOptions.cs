namespace Harbor.Application.Resilience;

public sealed record RetryOptions(int MaxAttempts, TimeSpan BaseDelay, bool UseJitter);