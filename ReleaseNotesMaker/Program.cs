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

namespace ReleaseNotesMaker
{
    class Program
    {
        static GitHubClient client;

        static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                DisplayHelp();
                return 1;
            }

            string repoURL = args[0];
            string milestone = args[1];

            string[] repoUrlSegments = repoURL.Split('/');
            if (repoUrlSegments.Length > 2)
            {
                DisplayHelp();
                return 1;
            }

            client = new GitHubClient(new ProductHeaderValue("ReleaseNotesMaker"));
            AddClientCredentials(client);

            string owner = repoUrlSegments[0];
            string repoName = repoUrlSegments.Length > 1 ? repoUrlSegments[1] : null;

            try
            {
                if (repoName != null)
                {
                    PublishReleaseForMilestone(owner, repoName, milestone).Wait();
                }
                else
                {
                    PublishReleasesForMilestone(owner, milestone).Wait();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return 1;
            }

            return 0;
        }

        private static void DisplayHelp()
        {
            Console.WriteLine("Usage: [repoURL] [milestone]");
            Console.WriteLine("Sample:");
            Console.WriteLine("{0}.exe signalr/signalr 2.1.1", typeof(Program).Assembly.GetName().Name);
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

        private static async Task<IDictionary<string, Release>> PublishReleasesForMilestone(string org, string milestone)
        {
            var repos = await client.Repository.GetAllForOrg(org);
            Console.WriteLine("Found ({0}) repositories in {1}", repos.Count, org);
            var releases = new Dictionary<string, Release>();
            foreach (var repo in repos.Where(repo => !repo.Fork))
            {
                Release release = await PublishReleaseForMilestone(repo.Owner.Login, repo.Name, milestone);
                if (release != null)
                {
                    releases.Add(repo.Name, release);
                }
            }

            if (!releases.Values.Any(release => release.Draft))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("All {0} {1} releases are now public!", org, milestone);
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Not all {0} {1} releases are public.", org, milestone);
                Console.ResetColor();
            }

            Repository homeRepo = repos.First(repo => repo.Name == "Home");
            Release rollupRelease = await PublishRollupRelease(homeRepo.Owner.Login, homeRepo.Name, "master", milestone, releases);
            releases.Add(homeRepo.Name, rollupRelease);

            return releases;
        }


        private static async Task<Release> PublishReleaseForMilestone(string owner, string repoName, string milestone)
        {
            Console.WriteLine("{0}/{1}", owner, repoName);

            var milestones = await GetMilestonesForRepository(owner, repoName);
            Milestone matchingMilestone = milestones.FirstOrDefault(m => m.Title.EndsWith(milestone));

            Release release = null;
            if (matchingMilestone != null)
            {
                string releaseNotes = await BuildReleaseNotesForMilestone(owner, repoName, matchingMilestone);
                string releaseName = matchingMilestone.Title;
                release = await CreateOrUpdateRelease(owner, repoName, releaseName, "release", releaseNotes);
            }
            return release;
        }

        private static async Task<Release> PublishRollupRelease(string owner, string repoName, string branch, string milestone, IDictionary<string, Release> releases)
        {
            Console.Write("{0}/{1}", owner, repoName);
            string rollupReleaseNotes = BuildRollupReleaseNotes(milestone, releases);
            Release rollupRelease = await CreateOrUpdateRelease(owner, repoName, milestone, branch, rollupReleaseNotes);
            return rollupRelease;
        }

        private static async Task<Release> CreateOrUpdateRelease(string owner, string repoName, string releaseName, string branch, string releaseBody)
        {
            Release release = await GetRelease(owner, repoName, releaseName);

            if (release != null)
            {
                Console.WriteLine("{0} {1} {2} release already exists ({3})", repoName, release.Name, release.Draft ? "draft" : "public", release.HtmlUrl);
                if (release.Body != releaseBody)
                {
                    if (ShouldUpdateRelease(repoName, release.Name))
                    {
                        release = await UpdateReleaseDescription(owner, repoName, release, releaseBody);
                    }
                }

                Console.WriteLine("{0} {1} release is up to date.", repoName, release.Name);
                if (release.Draft)
                {
                    if (ShouldPublishRelease(repoName, releaseName))
                    {
                        release = await PublishRelease(owner, repoName, release);
                    }
                }
                else
                {
                    if (ShouldUnpublishRelease(repoName, releaseName))
                    {
                        release = await UnpublishRelease(owner, repoName, release);
                    }
                }
            }
            else
            {
                Console.WriteLine("{0} {1} release does not exist.", repoName, releaseName);
                if (ShouldCreateDraftRelease(repoName, releaseName))
                {
                    release = await CreateDraftRelease(owner, repoName, releaseName, releaseName, branch, releaseBody);
                }
            }
            return release;
        }

        private static async Task<Release> GetRelease(string owner, string repoName, string releaseName)
        {
            var releases = await client.Release.GetAll(owner, repoName);
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

        private static async Task<Release> CreateDraftRelease(string owner, string repoName, string tagName, string releaseName, string branch, string description)
        {
            var releaseUpdate = new ReleaseUpdate(tagName)
            {
                TargetCommitish = branch,
                Name = releaseName,
                Body = description,
                Prerelease = true,
                Draft = true
            };

            return await client.Release.Create(owner, repoName, releaseUpdate);
        }

        private static async Task<Release> UpdateReleaseDescription(string owner, string repoName, Release release, string description)
        {
            return await UpdateRelease(owner, repoName, release, update => update.Body = description);
        }

        private static async Task<Release> PublishRelease(string owner, string repoName, Release release)
        {
            return await UpdateRelease(owner, repoName, release, update => update.Draft = false);
        }

        private static async Task<Release> UnpublishRelease(string owner, string repoName, Release release)
        {
            return await UpdateRelease(owner, repoName, release, update => update.Draft = true);
        }

        private static async Task<Release> UpdateRelease(string owner, string repoName, Release release, Action<ReleaseUpdate> updateRelease)
        {
            ReleaseUpdate releaseUpdate = release.ToUpdate();
            updateRelease(releaseUpdate);
            return await client.Release.Edit(owner, repoName, release.Id, releaseUpdate);
        }

        private static async Task<List<Milestone>> GetMilestonesForRepository(string owner, string repoName)
        {
            var openMilestones = await client.Issue.Milestone.GetForRepository(owner, repoName);
            List<Milestone> milestones = new List<Milestone>(openMilestones);

            var milestoneRequest = new MilestoneRequest() { State = ItemState.Closed };
            var closedMilestones = await client.Issue.Milestone.GetForRepository(owner, repoName, milestoneRequest);
            milestones.AddRange(closedMilestones);

            return milestones;
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

        private static string BuildRollupReleaseNotes(string milestone, IDictionary<string, Release> releases)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine(String.Format("### ASP.NET 5 {0} Release Notes", CultureInfo.CurrentCulture.TextInfo.ToTitleCase(milestone)));
            
            sb.AppendLine(String.Format("You can find details on the new features and bug fixes in {0} for the following components on their corresponding release pages:", milestone));
            foreach (var release in releases.OrderBy(release => release.Key))
            {
                sb.AppendLine(String.Format("- [{0}]({1})", release.Key, release.Value.HtmlUrl));
            }
            sb.AppendLine();
            sb.AppendLine("### Known Issues");
            sb.AppendLine("There are no known issues at this time.");

            string rollupReleaseNotes = sb.ToString();
            Console.WriteLine(rollupReleaseNotes);
            return rollupReleaseNotes;

        }

        private static async Task<IReadOnlyList<Issue>> GetClosedIssuesInMilestone(string owner, string repoName, Milestone milestone)
        {
            var issueRequest = new RepositoryIssueRequest() { State = ItemState.Closed, Milestone = milestone.Number.ToString() };
            var issues = await client.Issue.GetForRepository(owner, repoName, issueRequest);
            return issues;
        }

        private static string Categorize(IEnumerable<Label> labels)
        {
            if (labels.Any(l => l.Name.Contains("Done")))
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
