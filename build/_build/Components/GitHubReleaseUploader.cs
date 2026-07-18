using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nuke.Common;
using Nuke.Common.IO;

namespace Harbor.Build.Components;

/// <summary>
///     Uploads archive assets to a GitHub release using the GitHub REST API
///     (v3). Requires <c>GH_TOKEN</c> environment variable to be set with a
///     personal access token having <c>repo</c> scope. If <c>GH_TOKEN</c> is
///     absent, the upload is skipped with a warning (does not throw).
/// </summary>
/// <remarks>
///     Single responsibility: GitHub release creation + asset upload.
///     Does NOT trigger the build itself — <c>ReleaseTarget</c> is responsible
///     for sequencing publish → archive → upload.
/// </remarks>
public sealed class GitHubReleaseUploader
{
    private const string ApiBaseUrl = "https://api.github.com";
    private readonly string _userAgent;

    /// <summary>
    ///     Construct an uploader. <paramref name="userAgent"/> is sent as the
    ///     <c>User-Agent</c> header (GitHub requires it).
    /// </summary>
    public GitHubReleaseUploader(string userAgent = "harbor-nuke-build")
    {
        _userAgent = userAgent;
    }

    /// <summary>
    ///     Uploads <paramref name="assets"/> to the GitHub release identified
    ///     by <paramref name="tag"/> in the <paramref name="repo"/> (e.g.
    ///     <c>harbor-sh/harbor</c>). If <c>GH_TOKEN</c> is missing, logs a
    ///     warning and returns without throwing.
    /// </summary>
    /// <returns>
    ///     <c>true</c> if all assets uploaded successfully (or skipped);
    ///     <c>false</c> if any upload failed.
    /// </returns>
    public async Task<bool> UploadAsync(
        string tag,
        IReadOnlyList<AbsolutePath> assets,
        string repo,
        CancellationToken ct = default)
    {
        if (assets.Count == 0)
        {
            Console.WriteLine("  [github-release] No assets to upload — skipping.");
            return true;
        }

        var token = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("  [github-release] GH_TOKEN not set — skipping upload. " +
                              "Set GH_TOKEN with a PAT having 'repo' scope to enable.");
            return true;
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var releaseId = await GetOrCreateReleaseAsync(http, repo, tag, ct);
        Console.WriteLine($"  [github-release] Uploading {assets.Count} asset(s) to release {releaseId}");

        var allOk = true;
        foreach (var asset in assets)
        {
            try
            {
                await UploadAssetAsync(http, repo, releaseId, asset, ct);
                Console.WriteLine($"  [github-release]   uploaded {asset.Name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [github-release]   FAILED {asset.Name}: {ex.Message}");
                allOk = false;
            }
        }
        return allOk;
    }

    private async Task<long> GetOrCreateReleaseAsync(HttpClient http, string repo, string tag, CancellationToken ct)
    {
        // 1. Try to fetch the release by tag.
        var getByTagUri = new Uri($"{ApiBaseUrl}/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}");
        using (var getResp = await http.GetAsync(getByTagUri, ct))
        {
            if (getResp.IsSuccessStatusCode)
            {
                var json = await getResp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("id").GetInt64();
            }
        }

        // 2. Create the release.
        Console.WriteLine($"  [github-release] Creating release for tag {tag} in {repo}");
        var createUri = new Uri($"{ApiBaseUrl}/repos/{repo}/releases");
        var body = JsonSerializer.Serialize(new
        {
            tag_name = tag,
            name = tag,
            generate_release_notes = true,
            draft = false,
            prerelease = false
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var createResp = await http.PostAsync(createUri, content, ct);
        createResp.EnsureSuccessStatusCode();
        var createJson = await createResp.Content.ReadAsStringAsync(ct);
        using var createDoc = JsonDocument.Parse(createJson);
        return createDoc.RootElement.GetProperty("id").GetInt64();
    }

    private async Task UploadAssetAsync(HttpClient http, string repo, long releaseId, AbsolutePath asset, CancellationToken ct)
    {
        var name = Uri.EscapeDataString(asset.Name);
        var uploadUri = new Uri($"https://uploads.github.com/repos/{repo}/releases/{releaseId}/assets?name={name}");
        await using var fs = File.OpenRead(asset);
        using var content = new StreamContent(fs);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await http.PostAsync(uploadUri, content, ct);
        resp.EnsureSuccessStatusCode();
    }
}
