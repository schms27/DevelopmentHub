namespace DevelopmentHub.Api.Models.Dtos;

/// <summary>A repository that can be selected for a contributor-stats report.</summary>
public class RepositoryRefDto
{
  public string ProviderId { get; set; } = string.Empty;
  public string Id { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string FullName { get; set; } = string.Empty;
}

/// <summary>Aggregated pull-request contribution counts for a single person.</summary>
public class ContributorStatDto
{
  public string Login { get; set; } = string.Empty;
  public string DisplayName { get; set; } = string.Empty;
  public string AvatarUrl { get; set; } = string.Empty;
  public int AuthoredCount { get; set; }
  public int ReviewedCount { get; set; }
}

/// <summary>Request body for POST /api/contributors/stats.</summary>
public class ContributorStatsRequestDto
{
  public string Provider { get; set; } = string.Empty;
  public List<string> RepositoryIds { get; set; } = [];
  public DateTime? Since { get; set; }
  public DateTime? Until { get; set; }
}

/// <summary>Result of a contributor-stats report.</summary>
public class ContributorStatsResultDto
{
  public string ProviderId { get; set; } = string.Empty;
  public List<RepositoryRefDto> Repositories { get; set; } = [];
  public List<ContributorStatDto> Contributors { get; set; } = [];
  public DateTime FetchedAt { get; set; }
}
