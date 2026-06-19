using DevelopmentHub.Api.Models.Dtos;
using DevelopmentHub.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace DevelopmentHub.Api.Controllers;

[ApiController]
[Route("api/contributors")]
public class ContributorsController(IContributorStatsService contributorStatsService) : ControllerBase
{
  [HttpGet("repositories")]
  public async Task<ActionResult<List<RepositoryRefDto>>> GetRepositories([FromQuery] string provider)
  {
    if (string.IsNullOrWhiteSpace(provider))
      return BadRequest(new { error = "Query parameter 'provider' is required." });

    var repos = await contributorStatsService.ListRepositoriesAsync(provider, HttpContext.RequestAborted);
    return Ok(repos);
  }

  [HttpPost("stats")]
  public async Task<ActionResult<ContributorStatsResultDto>> GetStats([FromBody] ContributorStatsRequestDto request)
  {
    if (string.IsNullOrWhiteSpace(request.Provider))
      return BadRequest(new { error = "'provider' is required." });
    if (request.RepositoryIds is null || request.RepositoryIds.Count == 0)
      return BadRequest(new { error = "At least one repository must be selected." });

    var result = await contributorStatsService.GetContributorStatsAsync(request, HttpContext.RequestAborted);
    return Ok(result);
  }

  [HttpPost("repository-coverage")]
  public async Task<ActionResult<RepositoryCoverageResultDto>> GetRepositoryCoverage(
      [FromBody] RepositoryCoverageRequestDto request)
  {
    if (string.IsNullOrWhiteSpace(request.Provider))
      return BadRequest(new { error = "'provider' is required." });
    if (request.RepositoryIds is null || request.RepositoryIds.Count == 0)
      return BadRequest(new { error = "At least one repository must be selected." });

    var result = await contributorStatsService.GetRepositoryCoverageAsync(request, HttpContext.RequestAborted);
    return Ok(result);
  }
}
