using GitDashBackend.Accessors.Interfaces;
using GitDashBackend.Domain.DTOs;
using Octokit;
using System.Linq;

namespace GitDashBackend.Accessors.Implementations;

public class GitHubAccessor : IGitHubAccessor    
{
    private readonly ILogger<GitHubAccessor> _logger;

    public GitHubAccessor(ILogger<GitHubAccessor> logger)
    {
        _logger = logger;
    }

    private GitHubClient CreateClient(string token)
    {
        return new GitHubClient(new ProductHeaderValue("GitDashBackend"))
        {
            Credentials = new Credentials(token)
        };
    }

    public async Task<RepositoriesDto> GetUserRepositoriesAsync(string token)
    {
        _logger.LogInformation("Fetching repositories from GitHub API");

        var client = CreateClient(token);
        var repos = await client.Repository.GetAllForCurrent();

        //var result = new List<RepositoryDto>();
        RepositoriesDto repositoriesDto = new ();

        foreach (var repo in repos)
        {
            // Busca as linguagens do repositório
            var languages = await client.Repository.GetAllLanguages(repo.Owner.Login, repo.Name);

            repositoriesDto.repositories.Add(new RepositoryDto
            {
                FullName = repo.FullName,
                Description = repo.Description ?? string.Empty,
                IsPrivate = repo.Private,
                Starred = repo.StargazersCount,
                Forked = repo.ForksCount,
                Languages = languages.Select(l => l.Name).ToList()
            });

            repositoriesDto.count++;
        }

        return repositoriesDto;
    }

    public async Task<CommitsDto> GetRepositoryCommitsByFullNameAsync(string token, string fullName)
    {
        _logger.LogInformation("Fetching commits for {FullName}", fullName);

        var (owner, repo) = ParseFullName(fullName);

        var client = CreateClient(token);
        var commits = await client.Repository.Commit.GetAll(owner, repo);

        var commitsDto = new CommitsDto();
        
        foreach (var commit in commits)
        {
            commitsDto.commits.Add(new CommitDto
            {
                Message = commit.Commit.Message,
                AuthorName = commit.Commit.Author.Name,
                AuthorEmail = commit.Commit.Author.Email,
                Date = commit.Commit.Author.Date.DateTime,
                Additions = commit.Stats?.Additions ?? 0,
                Deletions = commit.Stats?.Deletions ?? 0,
                TotalChanges = (commit.Stats?.Additions ?? 0) + (commit.Stats?.Deletions ?? 0)
            });
            
            commitsDto.count++;
        }

        return commitsDto;
    }
    
    public async Task<CollaboratorsDto> GetRepositoryCollaboratorsAsync(string token, string fullName, string timeRange)
    {
        var (owner, repo) = ParseFullName(fullName);
        _logger.LogInformation("Fetching collaborators for {FullName}", fullName);

        var client = CreateClient(token);

        var collaborators = await client.Repository.Collaborator.GetAll(owner, repo);

        var collaboratorsDto = new CollaboratorsDto();
        
        foreach (var collaborator in collaborators)
        {
            var login = collaborator.Login;

            var commitCount = (await client.Repository.Commit.GetAll(owner, repo, new CommitRequest
            {
                Author = login,
                Since = GetStartDateFromTimeRange(timeRange)
            })).Count;

            var pullRequests = (await client.Search.SearchIssues(new SearchIssuesRequest
            {
                Type = IssueTypeQualifier.PullRequest,
                Author = login,
                Repos = { $"{owner}/{repo}" }
            })).TotalCount;
            
            var issues = (await client.Search.SearchIssues(new SearchIssuesRequest
            {
                Type = IssueTypeQualifier.Issue,
                Author = login,
                Repos = { $"{owner}/{repo}" }
            })).TotalCount;

            collaboratorsDto.collaborators.Add(new CollaboratorDto
            {
                Login = login,
                AvatarUrl = collaborator.AvatarUrl,
                Role = collaborator.Permissions.Admin ? "admin" : collaborator.Permissions.Push ? "write" : "read",
                Commits = commitCount,
                PullRequests = pullRequests,
                Issues = issues
            });
            collaboratorsDto.count++;
        }

        return collaboratorsDto;
    }
    
    
    public async Task<RepoOverviewStatsDto> GetRepositoryStatsAsync(string token, string fullName, string timeRange)
    {
        var (owner, repo) = ParseFullName(fullName);
        _logger.LogInformation("Fetching repository overview for {FullName}", fullName);

        var client = CreateClient(token);
        var overview = new RepoOverviewStatsDto();

        // === KPI METRICS ===
        var sinceDate = GetStartDateFromTimeRange(timeRange);
        
        // Commits (for the selected time range)
        var request = new CommitRequest
        {
            Since = sinceDate
        };

        // This tells Octokit to fetch all pages, not just the first one
        var options = new ApiOptions 
        { 
            PageSize = 100, 
            PageCount = 10 // Adjust based on how much history you really need
        };
        var commits = await client.Repository.Commit.GetAll(owner, repo, request, options);
        overview.Kpis.Commits = commits.Count;

        // Pull Requests merged
        var mergedPrs = await client.Search.SearchIssues(new SearchIssuesRequest
        {
            Type = IssueTypeQualifier.PullRequest,
            Repos = { $"{owner}/{repo}" },
            State = ItemState.Closed,
            Is = new List<IssueIsQualifier> { IssueIsQualifier.Merged },
    
            // Filter by the date the PR was actually merged
            Merged = new DateRange(sinceDate, SearchQualifierOperator.GreaterThanOrEqualTo)
        });
        overview.Kpis.PrsMerged = mergedPrs.TotalCount;

        // Issues closed
        var closedIssues = await client.Search.SearchIssues(new SearchIssuesRequest
        {
            Type = IssueTypeQualifier.Issue,
            Repos = { $"{owner}/{repo}" },
            State = ItemState.Closed, // Ensures they are currently closed
    
            // Filter by the specific date the issue was closed
            Closed = new DateRange(sinceDate, SearchQualifierOperator.GreaterThanOrEqualTo)
        });

        overview.Kpis.IssuesClosed = closedIssues.TotalCount;
        
        // === OPEN WORK ===
        // Open PRs
        var openPrs = await client.Search.SearchIssues(new SearchIssuesRequest
        {
            Type = IssueTypeQualifier.PullRequest,
            Repos = { $"{owner}/{repo}" },
            State = ItemState.Open
        });
        overview.OpenWork.OpenPrs = openPrs.TotalCount;

        // Open Issues
        var openIssues = await client.Search.SearchIssues(new SearchIssuesRequest
        {
            Type = IssueTypeQualifier.Issue,
            Repos = { $"{owner}/{repo}" },
            State = ItemState.Open
        });
        overview.OpenWork.OpenIssues = openIssues.TotalCount;

        // PRs needing review (open PRs with review requests)
        var needsReview = await client.Search.SearchIssues(new SearchIssuesRequest
        {
            Type = IssueTypeQualifier.PullRequest,
            Repos = { $"{owner}/{repo}" },
            State = ItemState.Open,
            //Is = new List<IssueIsQualifier> { IssueIsQualifier.Unreviewed }
        });
        overview.OpenWork.NeedsReview = needsReview.TotalCount;

        // === PEAK ACTIVITY ===
        // Most active day and hour — using punch card stats
        var punchCards = await client.Repository.Statistics.GetPunchCard(owner, repo);
        //var commitsd = await client.Repository.Statistics.GetCommitActivity(owner, repo);
        var punchPoints = punchCards.PunchPoints;
        //punchCards.PunchPoints.
        if (punchPoints != null && punchPoints.Any())
        {
            var maxActivity = punchPoints.OrderByDescending(p => p.CommitCount).First();
            overview.PeakActivity.MostActiveDay = maxActivity.DayOfWeek.StringValue;
            overview.PeakActivity.PeakHourUtc = $"{maxActivity.HourOfTheDay:D2}:00 - {maxActivity.HourOfTheDay + 1:D2}:00"; 
        }


        // Team size
        var collaborators = await client.Repository.Collaborator.GetAll(owner, repo);
        overview.PeakActivity.TeamSize = collaborators.Count;
        
        return overview;
    }

      public async Task<List<int>?> GetCollaboratorWeeklyActivityAsync(string token, string fullName, string username)
    {
        _logger.LogInformation("Fetching weekly activity for {FullName} and {Username}", fullName, username);
        var (owner, repo) = ParseFullName(fullName);
        var client = CreateClient(token);

        // Calculate weekly commits for the last 12 weeks
        var now = DateTime.UtcNow;
        var weeks = new List<int>();
        for (int i = 11; i >= 0; i--)
        {
            var weekStart = now.AddDays(-7 * i).Date.AddDays(-(int)now.DayOfWeek); // início da semana
            var weekEnd = weekStart.AddDays(7);
            var request = new CommitRequest
            {
                Author = username,
                Since = weekStart,
                Until = weekEnd
            };
            try
            {
                var commits = await client.Repository.Commit.GetAll(owner, repo, request);
                weeks.Add(commits.Count);
            }
            catch (NotFoundException)
            {
                return null;
            }
        }
        return weeks;
    }

    public async Task<CollaboratorActivityDto?> GetCollaboratorActivityAsync(string token, string fullName, string username, string range)
    {
        _logger.LogInformation("Fetching collaborator activity for {FullName} and {Username}", fullName, username);
        var (owner, repo) = ParseFullName(fullName);
        var client = CreateClient(token);

        // Determine date range using standard helper
        DateTime until = DateTime.UtcNow;
        DateTime since = GetStartDateFromTimeRange(range);

        // COMMITS
        var commitRequest = new CommitRequest
        {
            Author = username,
            Since = since,
            Until = until
        };
        int commitCount = 0;
        try
        {
            var commits = await client.Repository.Commit.GetAll(owner, repo, commitRequest);
            commitCount = commits.Count;
        }
        catch
        {
            // Requirement: If any number is difficult to get, just skip it and return what is available.
        }

        // PULL REQUESTS
        int prTotal = 0, prMerged = 0;
        try
        {
            var prRequest = new PullRequestRequest
            {
                State = ItemStateFilter.All
            };
            var prs = await client.PullRequest.GetAllForRepository(owner, repo, prRequest);
            var userPrs = prs.Where(pr => pr.User?.Login?.Equals(username, StringComparison.OrdinalIgnoreCase) == true
                && pr.CreatedAt.UtcDateTime >= since && pr.CreatedAt.UtcDateTime <= until).ToList();
            prTotal = userPrs.Count;
            prMerged = userPrs.Count(pr => pr.MergedAt.HasValue && pr.MergedAt.Value.UtcDateTime >= since && pr.MergedAt.Value.UtcDateTime <= until);
        }
        catch
        {
            // Requirement: If any number is difficult to get, just skip it and return what is available.
        }

        // ISSUES
        int issuesTotal = 0, issuesClosed = 0;
        try
        {
            var issueRequest = new RepositoryIssueRequest
            {
                State = ItemStateFilter.All,
                Creator = username,
                Since = since
            };
            var issues = await client.Issue.GetAllForRepository(owner, repo, issueRequest);
            var userIssues = issues.Where(i => i.CreatedAt.UtcDateTime >= since && i.CreatedAt.UtcDateTime <= until).ToList();
            issuesTotal = userIssues.Count;
            issuesClosed = userIssues.Count(i => i.ClosedAt.HasValue && i.ClosedAt.Value.UtcDateTime >= since && i.ClosedAt.Value.UtcDateTime <= until);
        }
        catch
        {
            // Requirement: If any number is difficult to get, just skip it and return what is available.
        }

        // REVIEWS (data limited by the API)
        int reviewsGiven = 0;
        try
        {
            var prs = await client.PullRequest.GetAllForRepository(owner, repo);
            foreach (var pr in prs)
            {
                var reviews = await client.PullRequest.Review.GetAll(owner, repo, pr.Number);
                reviewsGiven += reviews.Count(r => r.User?.Login?.Equals(username, StringComparison.OrdinalIgnoreCase) == true
                    && r.SubmittedAt is DateTimeOffset submittedAt && submittedAt.UtcDateTime >= since && submittedAt.UtcDateTime <= until);
            }
        }
        catch
        {
            // Requirement: If any number is difficult to get, just skip it and return what is available.
        }

        return new CollaboratorActivityDto
        {
            commits = new CommitStats { totalCount = commitCount },
            pullRequests = new PullRequestStats { totalCount = prTotal, mergedCount = prMerged },
            issues = new IssueStats { totalCount = issuesTotal, closedCount = issuesClosed },
            reviews = new ReviewStats { givenCount = reviewsGiven }
        };
    }

    public async Task<RepositoryDto?> GetRepositoryByOwnerRepoAsync(string token, string ownerRepo)
    {
        if (string.IsNullOrEmpty(ownerRepo) || !ownerRepo.Contains('/')) return null;
        var (owner, repo) = ParseFullName(ownerRepo);
        var client = CreateClient(token);
        try
        {
            var repoObj = await client.Repository.Get(owner, repo);
            var languages = await client.Repository.GetAllLanguages(owner, repo);
            return new RepositoryDto
            {
                FullName = repoObj.FullName,
                Description = repoObj.Description ?? string.Empty,
                IsPrivate = repoObj.Private,
                Starred = repoObj.StargazersCount,
                Forked = repoObj.ForksCount,
                Languages = languages.Select(l => l.Name).ToList()
            };
        }
        catch (Octokit.NotFoundException)
        {
            return null;
        }
        catch (Octokit.AuthorizationException)
        {
            return null;
        }
    }    

    public async Task<CollaboratorCodeChangesDto?> GetCollaboratorCodeChangesAsync(string token, string fullName, string username, string range)
    {
        // Decode fullName in case it is URL-encoded
        var decodedFullName = Uri.UnescapeDataString(fullName);
        var (owner, repo) = ParseFullName(decodedFullName);
        var client = CreateClient(token);
        DateTime since = GetStartDateFromTimeRange(range);
        DateTime until = DateTime.UtcNow;
        int additions = 0, deletions = 0;
        try
        {
            // Get all pull requests by the collaborator in the date range
            var prRequest = new PullRequestRequest
            {
                State = ItemStateFilter.All
            };
            var allPrs = await client.PullRequest.GetAllForRepository(owner, repo, prRequest);
            var userPrs = allPrs.Where(pr =>
                pr.User?.Login?.Equals(username, StringComparison.OrdinalIgnoreCase) == true &&
                pr.CreatedAt.UtcDateTime >= since && pr.CreatedAt.UtcDateTime <= until
            ).ToList();

            foreach (var pr in userPrs)
            {
                // Fetch PR details to get additions/deletions
                var prDetails = await client.PullRequest.Get(owner, repo, pr.Number);
                additions += prDetails.Additions;
                deletions += prDetails.Deletions;
            }
            return new CollaboratorCodeChangesDto { Additions = additions, Deletions = deletions };
        }
        catch (Octokit.NotFoundException)
        {
            // Return null if repo or collaborator not found
            return null;
        }
        catch (Exception)
        {
            // Return null for any other error
            return null;
        }
    }

    private static DateTime GetStartDateFromTimeRange(string timeRange)
    {
        return timeRange switch
        {
            "1 week" => DateTime.UtcNow.AddDays(-7),
            "1 month" => DateTime.UtcNow.AddMonths(-1),
            "3 months" => DateTime.UtcNow.AddMonths(-3),
            _ => throw new ArgumentException("Invalid time range. Accepted values: '1 week', '1 month', '3 months'")
        };
    }
    
    private (string owner, string repo) ParseFullName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName) || !fullName.Contains('/'))
        {
            throw new ArgumentException($"Invalid repository fullName format: {fullName}. Expected format: 'owner/repo'");
        }

        var parts = fullName.Split('/');
        return (parts[0], parts[1]);
    }

}