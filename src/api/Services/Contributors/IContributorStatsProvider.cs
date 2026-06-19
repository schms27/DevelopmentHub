using DevelopmentHub.Api.Models.Dao;
using DevelopmentHub.Api.Models.Dtos;

namespace DevelopmentHub.Api.Services;

/// <summary>
/// Fetches repositories and aggregated pull-request contributor statistics
/// for a single provider (Azure DevOps, GitHub, …).
/// </summary>
public interface IContributorStatsProvider
{
  string ProviderId { get; }

  /// <summary>Lists the repositories available for selection in the configured org/project.</summary>
  Task<List<RepositoryRefDto>> ListRepositoriesAsync(
      UserConfigDao userConfig,
      CancellationToken cancellationToken = default);

  /// <summary>Aggregates authored/reviewed PR counts per contributor for the given repositories.</summary>
  Task<List<ContributorStatDto>> GetContributorStatsAsync(
      UserConfigDao userConfig,
      IReadOnlyCollection<string> repositoryIds,
      DateTime? since,
      DateTime? until,
      CancellationToken cancellationToken = default);
}
