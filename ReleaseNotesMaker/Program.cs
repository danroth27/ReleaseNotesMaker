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

        private static async Task PublishReleasesForMilestone(string org, string milestone)
        {
            var repos = await client.Repository.GetAllForOrg(org);
            Console.WriteLine("Found ({0}) repositories in {1}", repos.Count, org);
            foreach (var repo in repos)
            {
                await PublishReleaseForMilestone(repo.Owner.Login, repo.Name, milestone);
            }
        }

        private static async Task PublishReleaseForMilestone(string owner, string repoName, string milestone)
        {
            Console.WriteLine("{0}/{1}", owner, repoName);

            var milestones = await GetMilestonesForRepository(owner, repoName);
            var matchingMilestones = milestones.Where(m => m.Title.EndsWith(milestone));

            foreach (var matchingMilestone in matchingMilestones)
            {
                string releaseNotes = await BuildReleaseNotesForMilestone(owner, repoName, matchingMilestone);

                if (ShouldCreateDraftRelease())
                {
                    await CreateDraftRelease(owner, repoName, matchingMilestone.Title, matchingMilestone.Title, "release", releaseNotes);
                }
            }
        }

        private static bool ShouldCreateDraftRelease()
        {
            Console.Write("Create draft release? [Y/N]: N");
            Console.CursorLeft = Console.CursorLeft - 1;
            string yesNo = Console.ReadLine();
            return String.Compare(yesNo, "Y", true) == 0;
        }

        private static async Task CreateDraftRelease(string owner, string repoName, string tagName, string releaseName, string branch, string description)
        {
            var releaseUpdate = new ReleaseUpdate(tagName)
            {
                TargetCommitish = branch,
                Name = releaseName,
                Body = description,
                Prerelease = true,
                Draft = true
            };

            var release = await client.Release.Create(owner, repoName, releaseUpdate);
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
