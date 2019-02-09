using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Octokit;
using System.Globalization;
using System.Net;

namespace ReleaseNotesMaker
{
    class Program
    {
        static GitHubClient client;

        static int Main(string[] args)
        {
            if (args.Length < 2 || args.Length > 3)
            {
                DisplayHelp();
                return 1;
            }

            string repoURL = args[0];
            string tagName = args[1];
            string milestone = tagName;
            bool publish = false;

            if (args.Length > 2)
            {
                publish = args.Last() == "--publish";
                
                if (!args[2].StartsWith("-"))
                {
                    milestone = args[2];
                }
            }

            string[] repoUrlSegments = repoURL.Split('/');
            if (repoUrlSegments.Length != 2)
            {
                DisplayHelp();
                return 1;
            }

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            client = new GitHubClient(new ProductHeaderValue("ReleaseNotesMaker"));
            AddClientCredentials(client);

            string owner = repoUrlSegments[0];
            string repoName = repoUrlSegments.Length > 1 ? repoUrlSegments[1] : null;

            try
            {
                PublishReleaseForMilestone(owner, repoName, tagName, milestone, publish).Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return 1;
            }

            return 0;
        }

        private static void DisplayHelp()
        {
            Console.WriteLine("Usage: [repoURL] [tag] [milestone?] [--publish]");
            Console.WriteLine("Sample:");
            Console.WriteLine("{0}.exe aspnet/signalr 2.1.1", typeof(Program).Assembly.GetName().Name);
        }

        private static void AddClientCredentials(GitHubClient client)
        {
            var user = Environment.GetEnvironmentVariable("GITHUB_USER");
            var password = Environment.GetEnvironmentVariable("GITHUB_PASSWORD");
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

            if (token != null)
            {
                client.Credentials = new Credentials(token);
            }
            else if (user != null && password != null)
            {
                client.Credentials = new Credentials(user, password);
            }
        }

        private static async Task<IEnumerable<Issue>> GetKnownIssues(string owner, string milestone)
        {
            var issueRequest = new RepositoryIssueRequest() { Labels = { "release-note" }, State = ItemStateFilter.Open };
            var releaseIssues = await client.Issue.GetAllForRepository(owner, "Release", issueRequest);
            return releaseIssues.Where(issue => issue.Milestone != null && issue.Milestone.Title.EndsWith(milestone.ToLower()));
        }


        private static async Task<Release> PublishReleaseForMilestone(string owner, string repoName, string tagName, string milestone, bool publish)
        {
            Console.WriteLine("{0}/{1}", owner, repoName);
 
            Milestone matchingMilestone = await GetMilestone(owner, repoName, milestone);
            if (matchingMilestone == null)
            {
                throw new InvalidOperationException($"Milestone {milestone} not found in repo {owner}/{repoName}");
            }

            Release release = null;
            if (matchingMilestone != null)
            {
                string releaseNotes = await BuildReleaseNotesForMilestone(owner, repoName, matchingMilestone);
                string releaseName = GetReleaseName(tagName);
                release = await CreateOrUpdateRelease(owner, repoName, tagName, releaseName, releaseNotes, publish);
            }
            return release;
        }

        private static string GetReleaseName(string tagName)
        {
            var suffix = "-final";
            return tagName.EndsWith(suffix) ? tagName.Substring(0, tagName.Length - suffix.Length) : tagName;
        }

        private static async Task<Release> PublishRollupRelease(string owner, string repoName, string tagName, string milestone, IDictionary<string, Release> releases, IEnumerable<Issue> knownIssues, bool publish)
        {
            Console.Write("{0}/{1}", owner, repoName);
            string rollupReleaseNotes = BuildRollupReleaseNotes(milestone, releases, knownIssues);
            Release rollupRelease = await CreateOrUpdateRelease(owner, repoName, tagName, milestone, rollupReleaseNotes, publish);
            return rollupRelease;
        }

        private static bool IsPrerelease(string releaseName)
        {
            return releaseName.StartsWith("0") || releaseName.Contains("-");
        }

        private static async Task<Release> CreateOrUpdateRelease(
            string owner, 
            string repoName, 
            string tagName,
            string releaseName, 
            string releaseBody, 
            bool publish)
        {
            Release release = await GetRelease(owner, repoName, releaseName);
            bool isPrerelease = IsPrerelease(releaseName);

            if (release != null)
            {
                Console.WriteLine($"{repoName} {release.Name} {(release.Draft ? "draft" : "public")} release already exists ({release.HtmlUrl})");

                if (release.Body != releaseBody || release.Prerelease != isPrerelease || release.TagName != tagName)
                {
                    if (ShouldUpdateRelease(repoName, release.Name))
                    {
                        release = await UpdateReleaseDescription(owner, repoName, release, releaseBody, isPrerelease, tagName);
                    }
                }
                else
                {
                    Console.WriteLine("{0} {1} release is up to date.", repoName, release.Name);
                }
                
                if (publish && release.Draft)
                { 
                    release = await PublishRelease(owner, repoName, release);
                }

                if (!publish && !release.Draft && ShouldUnpublishRelease(repoName, releaseName))
                {
                    release = await UnpublishRelease(owner, repoName, release);
                }
            }
            else
            {
                Console.WriteLine("{0} {1} release does not exist.", repoName, releaseName);
                release = await CreateRelease(owner, repoName, tagName, releaseName, releaseBody, isPrerelease, publish);
            }
            return release;
        }

        private static async Task<Release> GetRelease(string owner, string repoName, string releaseName)
        {
            var releases = await client.Repository.Release.GetAll(owner, repoName);
            return releases.FirstOrDefault(release => release.Name == releaseName);
        }

        private static bool ShouldCreateDraftRelease(string repoName, string releaseName)
        {
            return YesNoPrompt(String.Format("Create {0} {1} draft release?", repoName, releaseName));
        }

        private static bool ShouldCreateRollupRelease(string org, string milestone)
        {
            return YesNoPrompt("Create {0} {1} rollup release in the Home repo?");
        }

        private static bool ShouldUpdateRelease(string repoName, string releaseName)
        {
            return YesNoPrompt(String.Format("Update {0} {1} release?", repoName, releaseName));
        }

        private static bool ShouldPublishRelease(string repoName, string releaseName)
        {
            return YesNoPrompt(String.Format("Publish {0} {1} release?", repoName, releaseName));
        }

        private static bool ShouldUnpublishRelease(string repoName, string releaseName)
        {
            return YesNoPrompt(String.Format("The {0} {1} release is public. Do you want to hide it?", repoName, releaseName));
        }

        private static bool YesNoPrompt(string yesNoQuestion)
        {
            Console.Write("{0} [Y/N]: N", yesNoQuestion);
            Console.CursorLeft = Console.CursorLeft - 1;
            string yesNo = Console.ReadLine();
            return String.Compare(yesNo, "Y", true) == 0;
        }

        private static async Task<Release> CreateRelease(string owner, string repoName, string tagName, string releaseName, string description, bool isPrerelease, bool publish)
        {
            var newRelease = new NewRelease(tagName)
            {
                Name = releaseName,
                Body = description,
                Prerelease = isPrerelease,
                Draft = !publish
            };

            var release = await client.Repository.Release.Create(owner, repoName, newRelease);
            Console.WriteLine($"Created {(publish ? "public" : "draft")} release {releaseName}: {release.HtmlUrl}");
            return release;
        }

        private static async Task<Release> UpdateReleaseDescription(string owner, string repoName, Release release, string description, bool isPrelease, string tagName)
        {
            var updatedRelease = await UpdateRelease(owner, repoName, release, update => {
                update.Body = description;
                update.Prerelease = isPrelease;
                update.TagName = tagName;
            });
            Console.WriteLine($"Release updated: {updatedRelease.HtmlUrl}");
            return updatedRelease;
        }

        private static async Task<Release> PublishRelease(string owner, string repoName, Release release)
        {
            var updatedRelease = await UpdateRelease(owner, repoName, release, update => update.Draft = false);
            Console.WriteLine($"Release published: {updatedRelease.HtmlUrl}");
            return updatedRelease;
        }

        private static async Task<Release> UnpublishRelease(string owner, string repoName, Release release)
        {
            var updatedRelease = await UpdateRelease(owner, repoName, release, update => update.Draft = true);
            Console.WriteLine($"Release unpublished: {updatedRelease.HtmlUrl}");
            return updatedRelease;
        }

        private static async Task<Release> UpdateRelease(string owner, string repoName, Release release, Action<ReleaseUpdate> updateRelease)
        {
            ReleaseUpdate releaseUpdate = release.ToUpdate();
            updateRelease(releaseUpdate);
            var updatedRelease = await client.Repository.Release.Edit(owner, repoName, release.Id, releaseUpdate);
            return updatedRelease;
        }

        private static async Task<Milestone> GetMilestone(string owner, string repoName, string milestone)
        {
            var milestones = await client.Issue.Milestone.GetAllForRepository(owner, repoName, new MilestoneRequest { State = ItemStateFilter.All });
            return milestones.FirstOrDefault(m => m.Title.EndsWith(milestone.ToLower())); ;
        }

        private static async Task<string> BuildReleaseNotesForMilestone(string owner, string repoName, Milestone milestone)
        {
            var issues = await GetClosedIssuesInMilestone(owner, repoName, milestone);

            var issueGroups = from issue in issues
                              let category = Categorize(issue.Labels)
                              where !String.IsNullOrEmpty(category)
                              group issue by category into g
                              orderby g.Key descending
                              select g;

            Console.WriteLine(String.Format("Found ({0}) issues in {1}", issueGroups.Sum(g => g.Count()), milestone.Title));
            Console.WriteLine();

            StringBuilder sb = new StringBuilder();
            foreach (var g in issueGroups)
            {
                if (String.IsNullOrEmpty(g.Key))
                {
                    continue;
                }

                sb.AppendLine(String.Format("### {0}", g.Key));
                sb.AppendLine();

                foreach (var issue in g)
                {
                    sb.AppendLine(String.Format("* {0} ([#{1}]({2}))", issue.Title, issue.Number, issue.HtmlUrl));
                }

                sb.AppendLine();
            }

            string releaseNotes = sb.ToString();
            Console.WriteLine(releaseNotes);
            return releaseNotes;
        }

        private static string BuildRollupReleaseNotes(string milestone, IDictionary<string, Release> releases, IEnumerable<Issue> knownIssues)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine(String.Format("### ASP.NET Core {0} Release Notes", milestone));
            sb.AppendLine();
            sb.AppendLine(String.Format("We are pleased to [announce](http://blogs.msdn.com/webdev) the release of ASP.NET Core {0}!", milestone));
            sb.AppendLine();
            sb.AppendLine(String.Format("You can find details on the new features and bug fixes in {0} for the following components on their corresponding release pages:", milestone));
            var releasesWithIssues = releases.Where(release => !String.IsNullOrWhiteSpace(release.Value.Body));
            foreach (var release in releasesWithIssues.OrderBy(release => release.Key))
            {
                sb.AppendLine(String.Format("- [{0}]({1})", release.Key, release.Value.HtmlUrl));
            }
            sb.AppendLine();
            sb.AppendLine("### <a name=\"breaking-changes\"></a>Breaking Changes");
            sb.AppendLine(String.Format("- For a list of the breaking changes for this release please refer to the issues in the [Announcements](https://github.com/aspnet/announcements/issues?q=is%3Aissue+milestone%3A{0}) repo.", milestone.ToLower()));
            sb.AppendLine();
            sb.AppendLine("### <a name=\"known-issues\"></a>Known Issues");
            foreach (var knownIssue in knownIssues)
            {
                sb.AppendLine(CreateKnownIssueListItem(knownIssue));
            }        

            string rollupReleaseNotes = sb.ToString();
            Console.WriteLine(rollupReleaseNotes);
            return rollupReleaseNotes;

        }

        private static string CreateKnownIssueListItem(Issue issue)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(String.Format("- **{0}**", issue.Title));
            sb.AppendLine();
            sb.Append("  ");
            sb.AppendLine(issue.Body.Replace("\n", "\n  "));
            return sb.ToString();
        }

        private static async Task<IReadOnlyList<Issue>> GetClosedIssuesInMilestone(string owner, string repoName, Milestone milestone)
        {
            var issueRequest = new RepositoryIssueRequest() { State = ItemStateFilter.Closed, Milestone = milestone.Number.ToString() };
            var issues = await client.Issue.GetAllForRepository(owner, repoName, issueRequest);
            return issues;
        }

        private static string Categorize(IEnumerable<Label> labels)
        {
            if (labels.Any(l => l.Name.Contains("Done") || l.Name.Contains("closed-fixed")))
            {
                if (labels.Any(l => l.Name.Contains("bug")))
                {
                    return "Bugs Fixed";
                }
                if (labels.Any(l => l.Name.Contains("feature")) || labels.Any(l => l.Name.Contains("enhancement")))
                {
                    return "Features";
                }
            }

            return String.Empty;
        }
    }
}
