using DevelopmentHub.Api.Models.Dao;
using DevelopmentHub.Api.Models.Dtos;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace DevelopmentHub.Api.Services;

public class AzureDevOpsContributorStatsProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<AzureDevOpsContributorStatsProvider> logger) : IContributorStatsProvider
{
  private const int PageSize = 100;
  private const int MaxPullRequestsPerRepo = 2000;

  public string ProviderId => "azureDevOps";

  public async Task<List<RepositoryRefDto>> ListRepositoriesAsync(
      UserConfigDao userConfig,
      CancellationToken cancellationToken = default)
  {
    var cfg = GetSettings(userConfig);
    if (!IsConfigured(cfg))
    {
      logger.LogWarning("Azure DevOps is not fully configured. Skipping repository listing.");
      return [];
    }

    var client = CreateAuthorizedClient(cfg.Pat);
    var url = $"https://dev.azure.com/{cfg.Organization}/{cfg.Project}/_apis/git/repositories?api-version=7.1";

    try
    {
      var response = await client.GetStringAsync(url, cancellationToken);
      var values = JsonNode.Parse(response)?["value"]?.AsArray();
      if (values is null) return [];

      return values
          .Where(v => v is not null)
          .Select(v => new RepositoryRefDto
          {
            ProviderId = ProviderId,
            Id = v!["id"]?.GetValue<string>() ?? string.Empty,
            Name = v["name"]?.GetValue<string>() ?? string.Empty,
            FullName = $"{cfg.Project}/{v["name"]?.GetValue<string>()}",
          })
          .Where(r => !string.IsNullOrEmpty(r.Id))
          .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
          .ToList();
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Failed to list Azure DevOps repositories");
      return [];
    }
  }

  public async Task<List<ContributorStatDto>> GetContributorStatsAsync(
      UserConfigDao userConfig,
      IReadOnlyCollection<string> repositoryIds,
      DateTime? since,
      DateTime? until,
      CancellationToken cancellationToken = default)
  {
    var cfg = GetSettings(userConfig);
    if (!IsConfigured(cfg) || repositoryIds.Count == 0)
      return [];

    var client = CreateAuthorizedClient(cfg.Pat);
    var aggregate = new Dictionary<string, ContributorAccumulator>(StringComparer.OrdinalIgnoreCase);

    var perRepoResults = await Task.WhenAll(
        repositoryIds.Select(repoId =>
            FetchRepoPullRequestsAsync(client, cfg, repoId, since, until, cancellationToken)));

    foreach (var pr in perRepoResults.SelectMany(prs => prs))
      Accumulate(aggregate, pr);

    var ordered = aggregate.Values
        .OrderByDescending(c => c.AuthoredCount)
        .ThenByDescending(c => c.ReviewedCount)
        .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    // Azure DevOps avatar URLs require PAT authentication, which the WebView cannot
    // provide for <img> requests. Fetch them server-side and inline as data URIs.
    var avatars = await Task.WhenAll(
        ordered.Select(a => FetchAvatarDataUriAsync(client, a.AvatarUrl, cancellationToken)));

    return ordered
        .Select((a, i) => new ContributorStatDto
        {
          Login = a.Login,
          DisplayName = a.DisplayName,
          AvatarUrl = avatars[i],
          AuthoredCount = a.AuthoredCount,
          ReviewedCount = a.ReviewedCount,
        })
        .ToList();
  }

  private async Task<string> FetchAvatarDataUriAsync(
      HttpClient client,
      string avatarUrl,
      CancellationToken cancellationToken)
  {
    if (string.IsNullOrWhiteSpace(avatarUrl))
      return string.Empty;

    try
    {
      using var response = await client.GetAsync(avatarUrl, cancellationToken);
      if (!response.IsSuccessStatusCode)
        return string.Empty;

      var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/png";
      if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        return string.Empty;

      var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
      if (bytes.Length == 0)
        return string.Empty;

      return $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
    }
    catch (Exception ex)
    {
      logger.LogDebug(ex, "Failed to fetch Azure DevOps avatar from {Url}", avatarUrl);
      return string.Empty;
    }
  }

  private async Task<List<JsonNode>> FetchRepoPullRequestsAsync(
      HttpClient client,
      AzureDevOpsProviderSettings cfg,
      string repoId,
      DateTime? since,
      DateTime? until,
      CancellationToken cancellationToken)
  {
    var collected = new List<JsonNode>();
    var skip = 0;

    try
    {
      while (skip < MaxPullRequestsPerRepo)
      {
        var url = BuildPullRequestUrl(cfg, repoId, since, until, skip);
        var response = await client.GetStringAsync(url, cancellationToken);
        var values = JsonNode.Parse(response)?["value"]?.AsArray();
        if (values is null || values.Count == 0)
          break;

        collected.AddRange(values.Where(v => v is not null).Select(v => v!));

        if (values.Count < PageSize)
          break;

        skip += PageSize;
      }
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to fetch Azure DevOps pull requests for repository {RepoId}", repoId);
    }

    return collected;
  }

  private static string BuildPullRequestUrl(
      AzureDevOpsProviderSettings cfg,
      string repoId,
      DateTime? since,
      DateTime? until,
      int skip)
  {
    var url =
        $"https://dev.azure.com/{cfg.Organization}/{cfg.Project}/_apis/git/repositories/{repoId}/pullrequests" +
        $"?searchCriteria.status=all&$top={PageSize}&$skip={skip}&api-version=7.1";

    if (since.HasValue || until.HasValue)
      url += "&searchCriteria.queryTimeRangeType=Created";
    if (since.HasValue)
      url += $"&searchCriteria.minTime={Uri.EscapeDataString(since.Value.ToUniversalTime().ToString("o"))}";
    if (until.HasValue)
      url += $"&searchCriteria.maxTime={Uri.EscapeDataString(until.Value.ToUniversalTime().ToString("o"))}";

    return url;
  }

  private static void Accumulate(
      Dictionary<string, ContributorAccumulator> aggregate,
      JsonNode pr)
  {
    var author = pr["createdBy"];
    if (author is not null)
      GetOrAdd(aggregate, author).AuthoredCount++;

    var reviewers = pr["reviewers"]?.AsArray();
    if (reviewers is null) return;

    foreach (var reviewer in reviewers)
    {
      if (reviewer is null) continue;
      var vote = reviewer["vote"]?.GetValue<int>() ?? 0;
      if (vote == 0) continue; // only count reviewers who actually voted
      GetOrAdd(aggregate, reviewer).ReviewedCount++;
    }
  }

  private static ContributorAccumulator GetOrAdd(
      Dictionary<string, ContributorAccumulator> aggregate,
      JsonNode identity)
  {
    var uniqueName = identity["uniqueName"]?.GetValue<string>();
    var displayName = identity["displayName"]?.GetValue<string>() ?? string.Empty;
    var login = !string.IsNullOrWhiteSpace(uniqueName) ? uniqueName : displayName;

    if (!aggregate.TryGetValue(login, out var acc))
    {
      acc = new ContributorAccumulator
      {
        Login = login,
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? login : displayName,
        AvatarUrl = identity["imageUrl"]?.GetValue<string>() ?? string.Empty,
      };
      aggregate[login] = acc;
    }

    return acc;
  }

  private static bool IsConfigured(AzureDevOpsProviderSettings cfg) =>
      !string.IsNullOrWhiteSpace(cfg.Organization) &&
      !string.IsNullOrWhiteSpace(cfg.Project) &&
      !string.IsNullOrWhiteSpace(cfg.Pat);

  private static AzureDevOpsProviderSettings GetSettings(UserConfigDao userConfig) =>
      new()
      {
        Organization = userConfig.GetProviderSetting("azureDevOps", "organization"),
        Project = userConfig.GetProviderSetting("azureDevOps", "project"),
        UserEmail = userConfig.GetProviderSetting("azureDevOps", "userEmail"),
        Pat = userConfig.GetProviderSetting("azureDevOps", "pat"),
      };

  private HttpClient CreateAuthorizedClient(string pat)
  {
    var client = httpClientFactory.CreateClient("AzureDevOps");
    var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    return client;
  }

  private sealed class ContributorAccumulator
  {
    public string Login { get; init; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public int AuthoredCount { get; set; }
    public int ReviewedCount { get; set; }
  }
}
