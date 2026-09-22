using DevelopmentHub.Api.Models.Dao;
using DevelopmentHub.Api.Models.Dtos;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevelopmentHub.Api.Services;

/// <summary>
/// Contributor statistics backed by the GitHub GraphQL API. A single query per
/// repository page returns pull requests, their reviews and (for coverage) the
/// changed files, which keeps the request count far below the REST equivalent
/// of two extra calls per pull request.
/// </summary>
public class GitHubContributorStatsProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<GitHubContributorStatsProvider> logger) : IContributorStatsProvider
{
  private const string GraphQlUrl = "https://api.github.com/graphql";
  private const int RepoPageSize = 100;
  private const int PullRequestPageSize = 50;
  private const int FilesPerPullRequest = 100;
  private const int MaxPullRequestsPerRepo = 2000;
  private const int MaxConcurrentRepoQueries = 4;

  public string ProviderId => "github";

  public async Task<List<RepositoryRefDto>> ListRepositoriesAsync(
      UserConfigDao userConfig,
      CancellationToken cancellationToken = default)
  {
    var pat = GetPat(userConfig);
    if (string.IsNullOrWhiteSpace(pat))
    {
      logger.LogWarning("GitHub is not configured (no PAT). Skipping repository listing.");
      return [];
    }

    var client = CreateAuthorizedClient(pat);
    var repos = new List<RepositoryRefDto>();
    string? cursor = null;

    const string query = """
        query($after: String, $first: Int!) {
          viewer {
            repositories(
              first: $first,
              after: $after,
              affiliations: [OWNER, COLLABORATOR, ORGANIZATION_MEMBER],
              orderBy: { field: NAME, direction: ASC }
            ) {
              pageInfo { hasNextPage endCursor }
              nodes { name owner { login } }
            }
          }
        }
        """;

    try
    {
      while (true)
      {
        var payload = await PostGraphQlAsync(
            client,
            query,
            new Dictionary<string, object?> { ["after"] = cursor, ["first"] = RepoPageSize },
            cancellationToken);

        var connection = payload?["data"]?["viewer"]?["repositories"];
        var nodes = connection?["nodes"]?.AsArray();
        if (nodes is null) break;

        foreach (var node in nodes)
        {
          if (node is null) continue;
          var name = node["name"]?.GetValue<string>() ?? string.Empty;
          var owner = node["owner"]?["login"]?.GetValue<string>() ?? string.Empty;
          if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(owner)) continue;

          // "owner/name" doubles as the identifier the later queries need.
          repos.Add(new RepositoryRefDto
          {
            ProviderId = ProviderId,
            Id = $"{owner}/{name}",
            Name = name,
            FullName = $"{owner}/{name}",
          });
        }

        if (connection?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() != true) break;
        cursor = connection["pageInfo"]?["endCursor"]?.GetValue<string>();
        if (string.IsNullOrEmpty(cursor)) break;
      }
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Failed to list GitHub repositories");
      return repos;
    }

    return repos
        .OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
        .ToList();
  }

  public async Task<List<ContributorStatDto>> GetContributorStatsAsync(
      UserConfigDao userConfig,
      IReadOnlyCollection<string> repositoryIds,
      DateTime? since,
      DateTime? until,
      CancellationToken cancellationToken = default)
  {
    var pat = GetPat(userConfig);
    if (string.IsNullOrWhiteSpace(pat) || repositoryIds.Count == 0)
      return [];

    var client = CreateAuthorizedClient(pat);
    var aggregate = new Dictionary<string, ContributorAccumulator>(StringComparer.OrdinalIgnoreCase);

    var perRepo = await FetchAllRepositoriesAsync(
        client, repositoryIds, since, until, includeFiles: false, cancellationToken);

    foreach (var (_, pullRequests) in perRepo)
      foreach (var pr in pullRequests)
        Accumulate(aggregate, pr);

    return aggregate.Values
        .OrderByDescending(c => c.AuthoredCount)
        .ThenByDescending(c => c.ReviewedCount)
        .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
        .Select(a => new ContributorStatDto
        {
          Login = a.Login,
          DisplayName = a.DisplayName,
          // GitHub avatar URLs are public, so the WebView can load them directly.
          AvatarUrl = a.AvatarUrl,
          AuthoredCount = a.AuthoredCount,
          ReviewedCount = a.ReviewedCount,
        })
        .ToList();
  }

  public async Task<List<ContributorCoverageDto>> GetRepositoryCoverageAsync(
      UserConfigDao userConfig,
      IReadOnlyCollection<RepositoryRefDto> repositories,
      IReadOnlyCollection<string> contributors,
      DateTime? since,
      DateTime? until,
      CancellationToken cancellationToken = default)
  {
    var pat = GetPat(userConfig);
    if (string.IsNullOrWhiteSpace(pat) || repositories.Count == 0)
      return [];

    var client = CreateAuthorizedClient(pat);

    var filter = contributors
        .Where(c => !string.IsNullOrWhiteSpace(c))
        .Select(c => c.Trim())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var byId = repositories.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
    var aggregate = new Dictionary<string, CoverageAccumulator>(StringComparer.OrdinalIgnoreCase);

    var perRepo = await FetchAllRepositoriesAsync(
        client, byId.Keys, since, until, includeFiles: true, cancellationToken);

    foreach (var (repoId, pullRequests) in perRepo)
    {
      if (!byId.TryGetValue(repoId, out var repo)) continue;
      foreach (var pr in pullRequests)
        AccumulateCoverage(aggregate, repo, pr, filter);
    }

    return aggregate.Values
        .OrderByDescending(c => c.TotalAuthored)
        .ThenByDescending(c => c.TotalReviewed)
        .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
        .Select(a => new ContributorCoverageDto
        {
          Login = a.Login,
          DisplayName = a.DisplayName,
          AvatarUrl = a.AvatarUrl,
          TotalAuthored = a.TotalAuthored,
          TotalReviewed = a.TotalReviewed,
          TotalFilesAdded = a.TotalFilesAdded,
          TotalFilesEdited = a.TotalFilesEdited,
          TotalFilesDeleted = a.TotalFilesDeleted,
          Repositories = a.Repositories.Values
              .OrderByDescending(r => r.AuthoredCount + r.ReviewedCount)
              .ThenBy(r => r.RepositoryName, StringComparer.OrdinalIgnoreCase)
              .ToList(),
        })
        .ToList();
  }

  // ── GraphQL fetching ────────────────────────────────────────────────────

  private async Task<List<(string RepositoryId, List<JsonNode> PullRequests)>> FetchAllRepositoriesAsync(
      HttpClient client,
      IEnumerable<string> repositoryIds,
      DateTime? since,
      DateTime? until,
      bool includeFiles,
      CancellationToken cancellationToken)
  {
    using var throttle = new SemaphoreSlim(MaxConcurrentRepoQueries);

    var tasks = repositoryIds.Select(async repoId =>
    {
      await throttle.WaitAsync(cancellationToken);
      try
      {
        var prs = await FetchRepoPullRequestsAsync(
            client, repoId, since, until, includeFiles, cancellationToken);
        return (repoId, prs);
      }
      finally
      {
        throttle.Release();
      }
    });

    var results = await Task.WhenAll(tasks);
    return results.ToList();
  }

  private async Task<List<JsonNode>> FetchRepoPullRequestsAsync(
      HttpClient client,
      string repositoryId,
      DateTime? since,
      DateTime? until,
      bool includeFiles,
      CancellationToken cancellationToken)
  {
    var collected = new List<JsonNode>();

    var slash = repositoryId.IndexOf('/');
    if (slash <= 0 || slash == repositoryId.Length - 1)
    {
      logger.LogWarning("Skipping GitHub repository with unexpected id {RepositoryId}", repositoryId);
      return collected;
    }

    var owner = repositoryId[..slash];
    var name = repositoryId[(slash + 1)..];
    var query = BuildPullRequestQuery(includeFiles);
    string? cursor = null;

    try
    {
      while (collected.Count < MaxPullRequestsPerRepo)
      {
        var payload = await PostGraphQlAsync(
            client,
            query,
            new Dictionary<string, object?>
            {
              ["owner"] = owner,
              ["name"] = name,
              ["after"] = cursor,
              ["first"] = PullRequestPageSize,
            },
            cancellationToken);

        var connection = payload?["data"]?["repository"]?["pullRequests"];
        var nodes = connection?["nodes"]?.AsArray();
        if (nodes is null || nodes.Count == 0) break;

        // Newest first, so once a page falls entirely before the window we stop.
        var reachedWindowStart = false;
        foreach (var node in nodes)
        {
          if (node is null) continue;
          var createdAt = node["createdAt"]?.GetValue<DateTime>();
          if (createdAt is null) continue;

          if (since.HasValue && createdAt.Value.ToUniversalTime() < since.Value.ToUniversalTime())
          {
            reachedWindowStart = true;
            continue;
          }

          if (until.HasValue && createdAt.Value.ToUniversalTime() > until.Value.ToUniversalTime())
            continue;

          collected.Add(node);
        }

        if (reachedWindowStart) break;
        if (connection?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() != true) break;
        cursor = connection["pageInfo"]?["endCursor"]?.GetValue<string>();
        if (string.IsNullOrEmpty(cursor)) break;
      }
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to fetch GitHub pull requests for repository {RepositoryId}", repositoryId);
    }

    return collected;
  }

  private static string BuildPullRequestQuery(bool includeFiles)
  {
    var files = includeFiles
        ? $"files(first: {FilesPerPullRequest}) {{ nodes {{ changeType }} }}"
        : string.Empty;

    return $$"""
        query($owner: String!, $name: String!, $after: String, $first: Int!) {
          repository(owner: $owner, name: $name) {
            pullRequests(
              first: $first,
              after: $after,
              states: [OPEN, CLOSED, MERGED],
              orderBy: { field: CREATED_AT, direction: DESC }
            ) {
              pageInfo { hasNextPage endCursor }
              nodes {
                number
                createdAt
                author { login avatarUrl ... on User { name } }
                reviews(first: 50) {
                  nodes { state author { login avatarUrl ... on User { name } } }
                }
                {{files}}
              }
            }
          }
        }
        """;
  }

  private async Task<JsonNode?> PostGraphQlAsync(
      HttpClient client,
      string query,
      IDictionary<string, object?> variables,
      CancellationToken cancellationToken)
  {
    var body = JsonSerializer.Serialize(new { query, variables });
    using var content = new StringContent(body, Encoding.UTF8, "application/json");
    using var response = await client.PostAsync(GraphQlUrl, content, cancellationToken);

    var json = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
      logger.LogWarning(
          "GitHub GraphQL request failed. Status={Status} Body={Body}",
          (int)response.StatusCode,
          Truncate(json));
      return null;
    }

    var payload = JsonNode.Parse(json);

    // GraphQL reports partial failures with HTTP 200 and an "errors" array.
    var errors = payload?["errors"]?.AsArray();
    if (errors is not null && errors.Count > 0)
      logger.LogWarning("GitHub GraphQL returned errors: {Errors}", Truncate(errors.ToJsonString()));

    return payload;
  }

  private static string Truncate(string value) =>
      value.Length <= 500 ? value : value[..500] + "…";

  // ── Aggregation ─────────────────────────────────────────────────────────

  private static void Accumulate(
      Dictionary<string, ContributorAccumulator> aggregate,
      JsonNode pr)
  {
    var author = pr["author"];
    if (author is not null && TryReadIdentity(author, out var login, out var displayName, out var avatarUrl))
      GetOrAdd(aggregate, login, displayName, avatarUrl).AuthoredCount++;

    foreach (var reviewer in DistinctReviewers(pr))
      GetOrAdd(aggregate, reviewer.Login, reviewer.DisplayName, reviewer.AvatarUrl).ReviewedCount++;
  }

  private static void AccumulateCoverage(
      Dictionary<string, CoverageAccumulator> aggregate,
      RepositoryRefDto repo,
      JsonNode pr,
      HashSet<string> filter)
  {
    var author = pr["author"];
    if (author is not null && TryReadIdentity(author, out var login, out var displayName, out var avatarUrl))
    {
      var acc = GetOrAddCoverage(aggregate, login, displayName, avatarUrl, filter);
      if (acc is not null)
      {
        acc.TotalAuthored++;
        var contrib = GetRepoContribution(acc, repo);
        contrib.AuthoredCount++;

        var (added, edited, deleted) = CountFileChanges(pr);
        contrib.FilesAdded += added;
        contrib.FilesEdited += edited;
        contrib.FilesDeleted += deleted;
        acc.TotalFilesAdded += added;
        acc.TotalFilesEdited += edited;
        acc.TotalFilesDeleted += deleted;
      }
    }

    foreach (var reviewer in DistinctReviewers(pr))
    {
      var acc = GetOrAddCoverage(
          aggregate, reviewer.Login, reviewer.DisplayName, reviewer.AvatarUrl, filter);
      if (acc is null) continue;
      acc.TotalReviewed++;
      GetRepoContribution(acc, repo).ReviewedCount++;
    }
  }

  /// <summary>
  /// One review credit per person per pull request, matching the Azure DevOps
  /// provider, which counts a reviewer once regardless of how often they voted.
  /// Pending (not yet submitted) reviews do not count.
  /// </summary>
  public static List<(string Login, string DisplayName, string AvatarUrl)> DistinctReviewers(JsonNode pr)
  {
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var reviewers = new List<(string, string, string)>();

    var nodes = pr["reviews"]?["nodes"]?.AsArray();
    if (nodes is null) return reviewers;

    foreach (var review in nodes)
    {
      if (review is null) continue;
      var state = review["state"]?.GetValue<string>() ?? string.Empty;
      if (string.Equals(state, "PENDING", StringComparison.OrdinalIgnoreCase)) continue;

      var author = review["author"];
      if (author is null) continue;
      if (!TryReadIdentity(author, out var login, out var displayName, out var avatarUrl)) continue;
      if (!seen.Add(login)) continue;

      reviewers.Add((login, displayName, avatarUrl));
    }

    return reviewers;
  }

  /// <summary>
  /// Maps GitHub's per-file change types onto the added/edited/deleted counts the
  /// report uses. Renames, copies and mode changes count as edits.
  /// </summary>
  public static (int Added, int Edited, int Deleted) CountFileChanges(JsonNode pr)
  {
    var nodes = pr["files"]?["nodes"]?.AsArray();
    if (nodes is null) return (0, 0, 0);

    var added = 0;
    var edited = 0;
    var deleted = 0;

    foreach (var file in nodes)
    {
      var changeType = file?["changeType"]?.GetValue<string>();
      if (string.IsNullOrEmpty(changeType)) continue;

      switch (changeType.ToUpperInvariant())
      {
        case "ADDED":
          added++;
          break;
        case "DELETED":
          deleted++;
          break;
        default:
          edited++;
          break;
      }
    }

    return (added, edited, deleted);
  }

  private static bool TryReadIdentity(
      JsonNode identity,
      out string login,
      out string displayName,
      out string avatarUrl)
  {
    login = identity["login"]?.GetValue<string>() ?? string.Empty;
    var name = identity["name"]?.GetValue<string>();
    displayName = string.IsNullOrWhiteSpace(name) ? login : name;
    avatarUrl = identity["avatarUrl"]?.GetValue<string>() ?? string.Empty;
    return !string.IsNullOrWhiteSpace(login);
  }

  private static ContributorAccumulator GetOrAdd(
      Dictionary<string, ContributorAccumulator> aggregate,
      string login,
      string displayName,
      string avatarUrl)
  {
    if (!aggregate.TryGetValue(login, out var acc))
    {
      acc = new ContributorAccumulator
      {
        Login = login,
        DisplayName = displayName,
        AvatarUrl = avatarUrl,
      };
      aggregate[login] = acc;
    }

    return acc;
  }

  private static CoverageAccumulator? GetOrAddCoverage(
      Dictionary<string, CoverageAccumulator> aggregate,
      string login,
      string displayName,
      string avatarUrl,
      HashSet<string> filter)
  {
    if (filter.Count > 0 && !filter.Contains(login) && !filter.Contains(displayName))
      return null;

    if (!aggregate.TryGetValue(login, out var acc))
    {
      acc = new CoverageAccumulator
      {
        Login = login,
        DisplayName = displayName,
        AvatarUrl = avatarUrl,
      };
      aggregate[login] = acc;
    }

    return acc;
  }

  private static RepositoryContributionDto GetRepoContribution(CoverageAccumulator acc, RepositoryRefDto repo)
  {
    if (!acc.Repositories.TryGetValue(repo.Id, out var contrib))
    {
      contrib = new RepositoryContributionDto
      {
        RepositoryId = repo.Id,
        RepositoryName = repo.Name,
      };
      acc.Repositories[repo.Id] = contrib;
    }

    return contrib;
  }

  private static string GetPat(UserConfigDao userConfig) =>
      userConfig.GetProviderSetting("github", "pat");

  private HttpClient CreateAuthorizedClient(string pat)
  {
    var client = httpClientFactory.CreateClient("GitHub");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
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

  private sealed class CoverageAccumulator
  {
    public string Login { get; init; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public int TotalAuthored { get; set; }
    public int TotalReviewed { get; set; }
    public int TotalFilesAdded { get; set; }
    public int TotalFilesEdited { get; set; }
    public int TotalFilesDeleted { get; set; }
    public Dictionary<string, RepositoryContributionDto> Repositories { get; } =
        new(StringComparer.OrdinalIgnoreCase);
  }
}
