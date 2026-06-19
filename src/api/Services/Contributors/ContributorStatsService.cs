using DevelopmentHub.Api.Models.Dtos;
using Microsoft.Extensions.Caching.Memory;

namespace DevelopmentHub.Api.Services;

public interface IContributorStatsService
{
  Task<List<RepositoryRefDto>> ListRepositoriesAsync(
      string providerId,
      CancellationToken cancellationToken = default);

  Task<ContributorStatsResultDto> GetContributorStatsAsync(
      ContributorStatsRequestDto request,
      CancellationToken cancellationToken = default);
}

public class ContributorStatsService(
    IEnumerable<IContributorStatsProvider> providers,
    IUserConfigService userConfigService,
    IMemoryCache cache,
    ILogger<ContributorStatsService> logger) : IContributorStatsService
{
  private static readonly TimeSpan ReposCacheDuration = TimeSpan.FromMinutes(10);
  private static readonly TimeSpan StatsCacheDuration = TimeSpan.FromMinutes(5);

  public async Task<List<RepositoryRefDto>> ListRepositoriesAsync(
      string providerId,
      CancellationToken cancellationToken = default)
  {
    var provider = ResolveProvider(providerId);
    if (provider is null) return [];

    var cacheKey = $"contributors.repos.{provider.ProviderId}";
    if (cache.TryGetValue<List<RepositoryRefDto>>(cacheKey, out var cached) && cached is not null)
      return cached;

    var userConfig = await userConfigService.GetAsync();
    var repos = await provider.ListRepositoriesAsync(userConfig, cancellationToken);
    cache.Set(cacheKey, repos, ReposCacheDuration);
    return repos;
  }

  public async Task<ContributorStatsResultDto> GetContributorStatsAsync(
      ContributorStatsRequestDto request,
      CancellationToken cancellationToken = default)
  {
    var provider = ResolveProvider(request.Provider);
    if (provider is null)
      return new ContributorStatsResultDto { ProviderId = request.Provider, FetchedAt = DateTime.UtcNow };

    var repoIds = (request.RepositoryIds ?? [])
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    var cacheKey = BuildStatsCacheKey(provider.ProviderId, repoIds, request.Since, request.Until);
    if (cache.TryGetValue<ContributorStatsResultDto>(cacheKey, out var cached) && cached is not null)
      return cached;

    var userConfig = await userConfigService.GetAsync();
    var allRepos = await provider.ListRepositoriesAsync(userConfig, cancellationToken);
    var selectedRepos = allRepos.Where(r => repoIds.Contains(r.Id, StringComparer.OrdinalIgnoreCase)).ToList();

    var contributors = await provider.GetContributorStatsAsync(
        userConfig, repoIds, request.Since, request.Until, cancellationToken);

    logger.LogInformation(
        "Computed contributor stats. Provider={Provider} Repositories={RepositoryCount} Contributors={ContributorCount}",
        provider.ProviderId, repoIds.Count, contributors.Count);

    var result = new ContributorStatsResultDto
    {
      ProviderId = provider.ProviderId,
      Repositories = selectedRepos,
      Contributors = contributors,
      FetchedAt = DateTime.UtcNow,
    };

    cache.Set(cacheKey, result, StatsCacheDuration);
    return result;
  }

  private IContributorStatsProvider? ResolveProvider(string providerId)
  {
    var provider = providers.FirstOrDefault(p =>
        string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
    if (provider is null)
      logger.LogWarning("No contributor stats provider registered for {ProviderId}", providerId);
    return provider;
  }

  private static string BuildStatsCacheKey(
      string providerId,
      IEnumerable<string> repoIds,
      DateTime? since,
      DateTime? until)
  {
    var repos = string.Join(',', repoIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
    return $"contributors.stats.{providerId}.{repos}.{since?.Ticks ?? 0}.{until?.Ticks ?? 0}";
  }
}
