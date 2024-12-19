using System.Text;
using System.Text.Encodings.Web;
using NuGet.Versioning;
using Octokit;

var con = Console.Out;
string owner = Environment.GetEnvironmentVariable("CHANGELOGGEN_OWNER") ?? throw new Exception("Missing CHANGELOGGEN_OWNER envvar");

var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? throw new Exception("Missing GITHUB_TOKEN envvar");
var client = new GitHubClient(new ProductHeaderValue("net.exyll.ChangelogGen"))
{
    Credentials = new Credentials(token)
};

var rateLimits = await client.RateLimit.GetRateLimits();
var coreLimits = rateLimits.Resources.Core;

if (coreLimits.Remaining == 0)
{
    await con.WriteLineAsync($"Rate limit exceeded, resets in {coreLimits.Reset - DateTime.Now}");
    return;
}

await con.WriteLineAsync("Fetching repositories...");
var repos = await client.Repository.GetAllForOrg(owner);

foreach (var repo in repos)
{
    await con.WriteLineAsync($"Processing {repo.Name}...");
    var reponame = repo.Name;

    var releases = await client.Repository.Release.GetAll(owner, reponame);
    releases = releases.Where(x => !x.Prerelease).ToList();

    var newer2 = releases
        .Where(x => NuGetVersion.TryParse(x.TagName, out var _))
        .Select(x => (Release: x, Version: NuGetVersion.Parse(x.TagName)))
        .Where(x => !x.Version.IsPrerelease)
        .OrderByDescending(x => x.Version)
        .ToList();

    var majors = newer2.GroupBy(x => x.Version.Major);

    var sb = new StringBuilder();

    await con.WriteLineAsync("Fetching milestones...");
    var milestones = await client.Issue.Milestone.GetAllForRepository(owner, reponame, new MilestoneRequest { State = ItemStateFilter.All });

    foreach (var major in majors)
    {
        sb.Clear();
        var fn = $"{reponame}.v{major.Key}.md";

        if (File.Exists(fn))
        {
            await con.WriteLineAsync($"Skipping, file {fn} already exists");
            continue;
        }

        sb.AppendLine($"# {reponame} version {major.Key}\n");

        var now = DateTime.Now;
        foreach (var i in major)
        {
            var age = now - i.Release.PublishedAt.Value;
            sb.AppendFormat($"- [{i.Version}](#{i.Version.ToString().Replace(".", "")}) - {i.Release.PublishedAt.Value.Date:D}\n");
        }

        foreach (var i in major)
        {
            try
            {
                await con.WriteLineAsync($"Processing version {i.Version}...");

                sb.AppendFormat($"\n\n## {i.Version}\n\nReleased [**{i.Release.PublishedAt.Value.Date:D}**]({i.Release.HtmlUrl})\n");

                var milestone = milestones.SingleOrDefault(x => x.Title == i.Version.ToString());

                if (milestone == null)
                {
                    continue;
                }

                await con.WriteLineAsync("Fetching issues...");
                var issues = await client.Issue.GetAllForRepository(owner, reponame, new RepositoryIssueRequest { State = ItemStateFilter.All, Milestone = milestone.Number.ToString() });
                issues = issues.Where(x => x.Milestone != null).ToList();
                var milestoneIssues = issues.Where(i => i.Milestone?.Id == milestone.Id);

                var groups = milestoneIssues.GroupBy(x => x.Labels.FirstOrDefault()?.Name);

                if (groups.Any())
                {
                    foreach (var g in groups)
                    {
                        sb.AppendFormat($"\n\n#### {g.Key ?? "Other"}s\n\n");
                        foreach (var issue in g)
                        {
                            var title = HtmlEncoder.Default.Encode(issue.Title);
                            sb.AppendFormat($"- [#{issue.Number}]({issue.HtmlUrl}) {title}\n");
                        }
                    }
                }
                else
                {
                    sb.AppendFormat($"\n{i.Release.Body}\n");
                }
            }
            catch (Exception e)
            {
                await con.WriteLineAsync("\tFailed! " + e.Message);
            }
        }

        await con.WriteLineAsync($"Writing {fn}...");
        File.WriteAllText(fn, sb.ToString());
    }
}
