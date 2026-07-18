using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Storage.Memory;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Blazor.Services;

/// <summary>
///     Reads the list of saved sessions from a directory or from the
///     in-memory store as a fallback. Used by the Sessions page and the
///     sidebar's recent-sessions list.
/// </summary>
public sealed class SessionBrowserService
{
    private readonly MemorySessionStore _fallback;
    private readonly ILogger<SessionBrowserService> _logger;
    private string? _directory;

    /// <summary>Construct the browser.</summary>
    /// <param name="fallback">In-memory store used when no directory is configured.</param>
    /// <param name="logger">Logger.</param>
    public SessionBrowserService(MemorySessionStore fallback, ILogger<SessionBrowserService> logger)
    {
        _fallback = fallback;
        _logger = logger;
    }

    /// <summary>Set the directory to scan for JSONL session files.</summary>
    public void SetDirectory(string? directory)
    {
        _directory = directory;
    }

    /// <summary>Return the list of recent session summaries.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of <see cref="SessionSummary"/> records, newest first.</returns>
    public async Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<SessionSummary>();
        try
        {
            if (!string.IsNullOrEmpty(_directory) && Directory.Exists(_directory))
            {
                foreach (var file in Directory.EnumerateFiles(_directory, "*.jsonl"))
                {
                    var info = new FileInfo(file);
                    list.Add(new SessionSummary(
                        Id: Path.GetFileNameWithoutExtension(file),
                        Path: file,
                        Title: Path.GetFileNameWithoutExtension(file),
                        LastModified: info.LastWriteTimeUtc,
                        SizeBytes: info.Length));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate sessions in {Directory}", _directory);
        }
        // Always include the in-memory fallback sessions so the user sees
        // something even before they configure a directory.
        var memResult = await _fallback.ListAsync(null, ct).ConfigureAwait(false);
        if (memResult.IsSuccess && memResult.Value is { } mem)
        {
            foreach (var s in mem)
            {
                list.Add(new SessionSummary(s.Id, "(memory)", s.Title, s.CreatedAt, 0));
            }
        }

        list.Sort((a, b) => b.LastModified.CompareTo(a.LastModified));
        return list;
    }
}

/// <summary>One session in the browser list.</summary>
public sealed record SessionSummary(string Id, string Path, string Title, DateTimeOffset LastModified, long SizeBytes);

/// <summary>
///     Reads the list of configured LLM providers from the providers/ JSON
///     files shipped with the app. Used by the Providers page.
/// </summary>
public sealed class ProviderBrowserService
{
    private readonly ILogger<ProviderBrowserService> _logger;
    private IReadOnlyList<ProviderSummary> _cache = Array.Empty<ProviderSummary>();

    /// <summary>Construct the browser.</summary>
    /// <param name="logger">Logger.</param>
    public ProviderBrowserService(ILogger<ProviderBrowserService> logger)
    {
        _logger = logger;
    }

    /// <summary>Return the list of known providers, loaded from the providers directory.</summary>
    public Task<IReadOnlyList<ProviderSummary>> ListAsync()
    {
        if (_cache.Count > 0) return Task.FromResult(_cache);

        var list = new List<ProviderSummary>();
        string? providersDir = LocateProvidersDirectory();
        if (providersDir is not null && Directory.Exists(providersDir))
        {
            foreach (var file in Directory.EnumerateFiles(providersDir, "*.json"))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    list.Add(ParseSummary(json, Path.GetFileNameWithoutExtension(file)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read provider file {File}", file);
                }
            }
        }
        _cache = list;
        return Task.FromResult<IReadOnlyList<ProviderSummary>>(_cache);
    }

    private static string? LocateProvidersDirectory()
    {
        // Walk up from AppContext.BaseDirectory looking for a "providers"
        // folder. This handles running from the repo root, the bin/Debug
        // output, or a published single-file exe.
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "providers");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static ProviderSummary ParseSummary(string json, string fallbackId)
    {
        // Minimal regex-based extraction so we don't take a dependency on
        // System.Text.Json DOM (which is reflection-heavy on NativeAOT).
        string id = Extract(json, "id") ?? fallbackId;
        string name = Extract(json, "name") ?? id;
        string baseUrl = Extract(json, "base_url") ?? Extract(json, "baseUrl") ?? string.Empty;
        return new ProviderSummary(id, name, baseUrl, json);
    }

    private static string? Extract(string json, string key)
    {
        int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (i < 0) return null;
        int colon = json.IndexOf(':', i);
        if (colon < 0) return null;
        int q1 = json.IndexOf('"', colon + 1);
        if (q1 < 0) return null;
        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0) return null;
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }
}

/// <summary>One provider in the browser list.</summary>
public sealed record ProviderSummary(string Id, string Name, string BaseUrl, string RawJson);
