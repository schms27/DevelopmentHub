using DevelopmentHub.Api.Services;
using System.Text.Json.Nodes;

namespace DevelopmentHub.Tests.Services;

public sealed class GitHubContributorStatsProviderTests
{
    private static JsonNode Pr(string json) => JsonNode.Parse(json)!;

    // ── CountFileChanges ──────────────────────────────────────────────────────

    [Fact]
    public void CountFileChanges_MapsAddedDeletedAndEdits()
    {
        var pr = Pr("""
            {
              "files": { "nodes": [
                { "changeType": "ADDED" },
                { "changeType": "ADDED" },
                { "changeType": "MODIFIED" },
                { "changeType": "DELETED" }
              ]}
            }
            """);

        GitHubContributorStatsProvider.CountFileChanges(pr).Should().Be((2, 1, 1));
    }

    [Theory]
    [InlineData("RENAMED")]
    [InlineData("COPIED")]
    [InlineData("CHANGED")]
    public void CountFileChanges_TreatsOtherChangeTypesAsEdits(string changeType)
    {
        var pr = Pr($$"""{ "files": { "nodes": [ { "changeType": "{{changeType}}" } ] } }""");

        GitHubContributorStatsProvider.CountFileChanges(pr).Should().Be((0, 1, 0));
    }

    [Fact]
    public void CountFileChanges_ReturnsZeros_WhenFilesWereNotRequested()
    {
        GitHubContributorStatsProvider.CountFileChanges(Pr("""{ "number": 7 }""")).Should().Be((0, 0, 0));
    }

    // ── DistinctReviewers ─────────────────────────────────────────────────────

    [Fact]
    public void DistinctReviewers_CountsEachPersonOnce()
    {
        var pr = Pr("""
            {
              "reviews": { "nodes": [
                { "state": "COMMENTED", "author": { "login": "ada", "name": "Ada", "avatarUrl": "a" } },
                { "state": "APPROVED",  "author": { "login": "ada", "name": "Ada", "avatarUrl": "a" } },
                { "state": "APPROVED",  "author": { "login": "linus", "name": "Linus", "avatarUrl": "l" } }
              ]}
            }
            """);

        var reviewers = GitHubContributorStatsProvider.DistinctReviewers(pr);

        reviewers.Select(r => r.Login).Should().Equal("ada", "linus");
    }

    [Fact]
    public void DistinctReviewers_IgnoresPendingReviewsAndDeletedAccounts()
    {
        var pr = Pr("""
            {
              "reviews": { "nodes": [
                { "state": "PENDING",  "author": { "login": "ada", "avatarUrl": "a" } },
                { "state": "APPROVED", "author": null }
              ]}
            }
            """);

        GitHubContributorStatsProvider.DistinctReviewers(pr).Should().BeEmpty();
    }

    [Fact]
    public void DistinctReviewers_FallsBackToLogin_WhenNameIsMissing()
    {
        var pr = Pr("""
            { "reviews": { "nodes": [ { "state": "APPROVED", "author": { "login": "ada", "avatarUrl": "a" } } ] } }
            """);

        var reviewer = GitHubContributorStatsProvider.DistinctReviewers(pr).Single();

        reviewer.DisplayName.Should().Be("ada");
        reviewer.AvatarUrl.Should().Be("a");
    }
}
