using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Harbor.Build.Meta;
using Nuke.Common.IO;
namespace Harbor.Build.Components;
/// <summary>
///     Uploads archive assets to a GitHub release using the GitHub REST API
///     (v3). Requires <c>GH_TOKEN</c> environment variable to be set with a
///     personal access token having <c>repo</c> scope. If <c>GH_TOKEN</c> is
///     absent, the upload is skipped with a warning (does not throw).
///     In dry-run mode the asset list is reported and no network call happens.
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
    ///     Construct an uploader. <paramref name="userAgent" /> is sent as the
    ///     <c>User-Agent</c> header (GitHub requires it).
    /// </summary>
    public GitHubReleaseUploader(string userAgent = "harbor-nuke-build")
    {
        _userAgent = userAgent;
    }
    /// <summary>
    ///     Uploads <paramref name="assets" /> to the GitHub release identified
    ///     by <paramref name="tag" /> in the <paramref name="repo" /> (e.g.
    ///     <c>harbor-sh/harbor</c>). If <paramref name="dryRun" /> is set,
    ///     lists what would be uploaded and returns without any network call.
    ///     If <c>GH_TOKEN</c> is missing, logs a warning and returns without
    ///     throwing.
    /// </summary>
    /// <returns>
    ///     <c>true</c> if all assets uploaded successfully (or skipped);
    ///     <c>false</c> if any upload failed.
    /// </returns>
    public async Task<bool> UploadAsync(
        string tag,
        IReadOnlyList<AbsolutePath> assets,
        string repo,
        BuildOutput output,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        if (assets.Count == 0)
        {
            output.Info("github-release", "No assets to upload — skipping.");
            return true;
        }
        string? token = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            output.Warn("github-release", "GH_TOKEN not set — skipping upload. " +
                                          "Set GH_TOKEN with a PAT having 'repo' scope to enable.");
            return true;
        }
        if (dryRun)
        {
            output.Info("github-release",
                $"dry-run: would upload {assets.Count} asset(s) to {repo} release {tag}:");
            foreach (var asset in assets)
            {
                long bytes = File.Exists(asset) ? new FileInfo(asset).Length : 0;
                output.Info("github-release", $"  asset {asset.Name} ({bytes} bytes)");
            }
            return true;
        }
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        long releaseId = await GetOrCreateReleaseAsync(http, repo, tag, ct);
        output.Info("github-release", $"Uploading {assets.Count} asset(s) to release {releaseId}");
        bool allOk = true;
        foreach (var asset in assets)
        {
            try
            {
                await UploadAssetAsync(http, repo, releaseId, asset, ct);
                output.Info("github-release", $"uploaded {asset.Name}");
            }
            catch (Exception ex)
            {
                output.Error("github-release", $"FAILED {asset.Name}: {ex.Message}");
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
                string json = await getResp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("id").GetInt64();
            }
        }

        // 2. Create the release.
        Console.Error.WriteLine($"  [github-release] Creating release for tag {tag} in {repo}");
        var createUri = new Uri($"{ApiBaseUrl}/repos/{repo}/releases");
        string body = JsonSerializer.Serialize(new
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
        string createJson = await createResp.Content.ReadAsStringAsync(ct);
        using var createDoc = JsonDocument.Parse(createJson);
        return createDoc.RootElement.GetProperty("id").GetInt64();
    }

    private async Task UploadAssetAsync(HttpClient http, string repo, long releaseId, AbsolutePath asset, CancellationToken ct)
    {
        string name = Uri.EscapeDataString(asset.Name);
        var uploadUri = new Uri($"https://uploads.github.com/repos/{repo}/releases/{releaseId}/assets?name={name}");
        await using var fs = File.OpenRead(asset);
        using var content = new StreamContent(fs);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await http.PostAsync(uploadUri, content, ct);
        resp.EnsureSuccessStatusCode();
    }
}
