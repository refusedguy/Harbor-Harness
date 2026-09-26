namespace Harbor.E2E.Framework;

public static class E2eHomeInstaller
{
    public static async Task InstallAsync(string tempHome, string baseUri)
    {
        string harborDir = Path.Combine(tempHome, ".harbor");
        string providersDir = Path.Combine(harborDir, "providers");
        Directory.CreateDirectory(providersDir);

        string mockConfigPath = Path.Combine(providersDir, "mock.json");
        string mockConfig = $$"""
                              {
                                "id": "mock",
                                "displayName": "Mock LLM (E2E)",
                                "description": "In-process mock for E2E tests.",
                                "baseUrl": "{{baseUri}}",
                                "apiType": "openai-compatible",
                                "authType": "bearer",
                                "authEnvVar": "MOCK_API_KEY",
                                "models": [
                                  { "id": "test-model", "providerId": "mock", "displayName": "Mock Test Model", "contextWindow": 128000, "maxOutputTokens": 4096, "supportsReasoning": false, "supportsVision": false, "supportsToolUse": true, "pricing": { "inputPerMillion": 0, "outputPerMillion": 0 }, "promptTemplate": "openai" }
                                ]
                              }
                              """;
        await File.WriteAllTextAsync(mockConfigPath, mockConfig).ConfigureAwait(false);

        string harborConfigPath = Path.Combine(harborDir, "config.json");
        string harborConfig = """
                              {
                                "provider": "mock",
                                "model": "mock/test-model",
                                "agent": "code",
                                "onboarded": true
                              }
                              """;
        await File.WriteAllTextAsync(harborConfigPath, harborConfig).ConfigureAwait(false);
    }
}
