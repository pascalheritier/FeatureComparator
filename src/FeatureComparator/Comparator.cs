using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using NLog.Extensions.Logging;
using Redmine.Net.Api;
using Redmine.Net.Api.Types;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FeatureComparator
{
    internal class Comparator
    {
        #region Consts

        private const string GitRepositoryExtension = ".git";
        private const string RemoteBranchPrefix = "origin/";

        private const string CompareFromFolderName = "from";
        private const string CompareToFolderName = "to";

        #endregion

        #region Members

        private ILogger _logger;
        private string _repoCloneTmpDirectory;
        private string _username;
        private string _password;
        private AppConfiguration _appConfiguration;
        private RedmineManager _manager;
        private Dictionary<string, Issue> _redmineIssueDictionary = new();
        private Dictionary<string, Repository> _gitRepositoryDictionary = new();

        #endregion

        #region Constructor
        public Comparator(AppConfiguration appConfiguration, ILoggerFactory loggerFactory)
        {
            _appConfiguration = appConfiguration;
            _logger = loggerFactory.CreateLogger<Comparator>();
            _repoCloneTmpDirectory = appConfiguration.GitConfiguration.RepoCloneTmpDirectory;
            _username = appConfiguration.GitConfiguration.Username;
            _password = appConfiguration.GitConfiguration.PAT;
            _manager = new RedmineManager(appConfiguration.RedmineConfiguration.ServerUrl, appConfiguration.RedmineConfiguration.ApiKey);
        }

        #endregion

        #region Run

        public void Run()
        {
            try
            {
                // Get credentials
                if (_password is null)
                    _password = AskUserPassword(_username);

                var credentials = new UsernamePasswordCredentials
                {
                    Username = _username,
                    Password = _password
                };

                // pull or clone git repository
                foreach (GitRepositoryComparison gitRepository in _appConfiguration.GitConfiguration.GitRepositoryComparisons)
                {
                    // pull compareFrom branch
                    string repositoryCompareFromName = GetRepositoryCompareFromName(gitRepository.RepositoryName);
                    Repository repositoryCompareFrom = CloneOrPullRepository(
                        gitRepository.RepositoryName,
                        gitRepository.CompareFrom.BranchName,
                        GetRepositoryGitTempPath(repositoryCompareFromName),
                        GetRepositoryUrl(gitRepository.RepositoryName),
                        credentials);
                    _gitRepositoryDictionary.Add(repositoryCompareFromName, repositoryCompareFrom);

                    // pull compareTo branch
                    string repositoryCompareToName = GetRepositoryCompareToName(gitRepository.RepositoryName);
                    Repository repositoryCompareTo = CloneOrPullRepository(
                        gitRepository.RepositoryName,
                        gitRepository.CompareFrom.BranchName,
                        GetRepositoryGitTempPath(repositoryCompareToName),
                        GetRepositoryUrl(gitRepository.RepositoryName),
                        credentials);
                    _gitRepositoryDictionary.Add(repositoryCompareToName, repositoryCompareTo);
                }

                Dictionary<string, IEnumerable<Issue>> missingFeaturesDictionary = new(); // features that are missing in the compareTo repository
                Dictionary<string, IEnumerable<Issue>> unplannedMissingFeaturesDictionary = new(); // features that are missing in the compareTo repository and are unplanned for future development
                Dictionary<string, IEnumerable<string>> unknownFeaturesDictionary = new(); // features merged in the compareFrom repository but that have no Redmine equivalence

                // compare features in each repository
                foreach (GitRepositoryComparison gitRepository in _appConfiguration.GitConfiguration.GitRepositoryComparisons)
                {
                    CompareFeaturesInRepository(gitRepository, out IEnumerable<Issue> missingFeatures, out IList<string> unknownFeatures);
                    missingFeaturesDictionary.Add(gitRepository.RepositoryName, missingFeatures);
                    unknownFeaturesDictionary.Add(gitRepository.RepositoryName, unknownFeatures);
                }

                // generate file
                GenerateComparisonNote(_appConfiguration.RedmineConfiguration.ComparisonNoteFileName, missingFeaturesDictionary, unknownFeaturesDictionary);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error while accessing git repository: {ex.ToString()}");
            }
        }

        private void CompareFeaturesInRepository(GitRepositoryComparison gitRepository, out IEnumerable<Issue> missingFeatures, out IList<string> unknownFeatures)
        {
            _logger.LogInformation($"Start comparing features for git repo '{gitRepository.RepositoryName}', from branch '{gitRepository.CompareFrom.BranchName}' to branch '{gitRepository.CompareTo.BranchName}':");

            using Repository repositoryCompareFrom = GetRepositoryCompareFrom(gitRepository.RepositoryName);
            using Repository repositoryCompareTo = GetRepositoryCompareTo(gitRepository.RepositoryName);

            // get merge commits
            IEnumerable<Commit> commitsCompareFrom = GetMergingCommits(gitRepository.RepositoryName, gitRepository.CommitStartSha, gitRepository.CompareFrom.BranchName, repositoryCompareFrom);
            IEnumerable<Commit> commitsCompareTo = GetMergingCommits(gitRepository.RepositoryName, gitRepository.CommitStartSha, gitRepository.CompareTo.BranchName, repositoryCompareTo);

            IList<Issue> featuresCompareFrom = GetFeatures(GetRepositoryCompareFromName(gitRepository.RepositoryName), commitsCompareFrom, out unknownFeatures);
            IList<Issue> featuresCompareTo = GetFeatures(GetRepositoryCompareToName(gitRepository.RepositoryName), commitsCompareTo, out _);

            missingFeatures = featuresCompareFrom.Where(issueFrom => !featuresCompareTo.Any(issueTo => issueTo.Id == issueFrom.Id));

            _logger.LogInformation($"End comparing features for git repo '{gitRepository.RepositoryName}', from branch '{gitRepository.CompareFrom.BranchName}' to branch '{gitRepository.CompareTo.BranchName}':");
        }

        private Repository GetRepositoryCompareFrom(string gitRepositoryName)
        {
            string repositoryFromName = GetRepositoryCompareFromName(gitRepositoryName);
            return GetRepository(repositoryFromName);
        }

        private Repository GetRepositoryCompareTo(string gitRepositoryName)
        {
            string repositoryToName = GetRepositoryCompareToName(gitRepositoryName);
            return GetRepository(repositoryToName);
        }

        private Repository GetRepository(string gitRepositoryNameKey)
        {
            if (!_gitRepositoryDictionary.ContainsKey(gitRepositoryNameKey))
                throw new NotFoundException("Dictionaries not properly initialized");
            return _gitRepositoryDictionary[gitRepositoryNameKey];
        }

        private IEnumerable<Commit> GetMergingCommits(string gitRepositoryName, string gitCommitStartSha, string gitBranchName, Repository gitRepository)
        {
            var branch = gitRepository.Branches.FirstOrDefault(_B => _B.FriendlyName.Contains(gitBranchName));
            if (branch == null)
            {
                _logger.LogError($"Repository '{gitRepositoryName}': Branch '{gitBranchName}' not found in repository {GetRepositoryUrl(gitRepositoryName)}.");
                return Enumerable.Empty<Commit>();
            }

            CommitFilter filter = new CommitFilter()
            {
                SortBy = CommitSortStrategies.Time,
                IncludeReachableFrom = branch
            };

            IEnumerable<Commit> commits = gitRepository.Commits
                .QueryBy(filter)
                .Where(c => c.Parents.Count() > 1
                && c.Message.Contains($"into '{gitBranchName}'"));

            Commit? startCommit = gitRepository.Commits.FirstOrDefault(_C => _C.Sha == gitCommitStartSha);
            if (startCommit == null)
            {
                _logger.LogError($"Repository '{gitRepositoryName}': Could not find start commit with SHA {gitCommitStartSha}, which will be skipped during generation.");
                return Enumerable.Empty<Commit>();
            }

            return commits.Where(_C => _C.Author.When >= startCommit.Author.When);
        }

        private IList<Issue> GetFeatures(string gitRepositoryName, IEnumerable<Commit> sortedCommits, out IList<string> unkownFeatures)
        {
            List<Issue> features = new();
            unkownFeatures = new List<string>();
            foreach (Commit commit in sortedCommits)
            {
                if (!TryGetRedmineIssueNumber(commit.Message, out string redmineIssueId, out string mergeMessage))
                {
                    _logger.LogWarning($"Repository '{gitRepositoryName}': Could not find redmine issue for merge commit: {commit.Message}.");
                    unkownFeatures.Add(commit.MessageShort);
                    continue;
                }

                if (!features.Any(_I => _I.Id.ToString() == redmineIssueId))
                {
                    Issue? foundIssue = null;
                    // check if already retrieved from redmine otherwise get it online
                    if (_redmineIssueDictionary.ContainsKey(redmineIssueId))
                    {
                        foundIssue = _redmineIssueDictionary[redmineIssueId];
                    }
                    else if (this.TryGetIssueFromOpenIssues(redmineIssueId, out foundIssue))
                    {
                        _redmineIssueDictionary.Add(redmineIssueId, foundIssue!);
                    }
                    else if (this.TryGetIssueFromClosedIssues(redmineIssueId, out foundIssue))
                    {
                        _redmineIssueDictionary.Add(redmineIssueId, foundIssue!);
                    }
                    else
                    {
                        _logger.LogError($"Repository '{gitRepositoryName}': Could not find Redmine issue #{redmineIssueId}");
                    }

                    if (foundIssue is not null)
                    {
                        features.Add(foundIssue);
                        _logger.LogInformation($"- {commit.Author.When}| {this.IssueToString(foundIssue)}");
                    }
                }
            }
            return features;
        }

        #region Git helpers

        private Repository CloneOrPullRepository(
            string gitRepoName,
            string branchName,
            string repoCloneTmpPath,
            string repoUrl,
            UsernamePasswordCredentials credentials)
        {
            Repository repository;
            if (Directory.Exists(repoCloneTmpPath))
            {
                // pull
                repository = new Repository(repoCloneTmpPath);
                Signature signature = new(new Identity(credentials.Username, $"{credentials.Username}@site.com"), DateTimeOffset.Now);
                PullOptions pullOptions = new PullOptions
                {
                    FetchOptions = new FetchOptions
                    {
                        Prune = true,
                        TagFetchMode = TagFetchMode.Auto,
                        CredentialsProvider = (_url, _user, _cred) => credentials
                    },
                    MergeOptions = new MergeOptions
                    {
                        FailOnConflict = true,
                    }
                };
                Branch? localBranch = repository.Branches.FirstOrDefault(b => b.FriendlyName == branchName);
                Branch? trackedRemoteBranch = repository.Branches.FirstOrDefault(b => b.FriendlyName == RemoteBranchPrefix + branchName);
                if (trackedRemoteBranch is null || !trackedRemoteBranch.IsRemote)
                    throw new NotFoundException($"Could not find remote branch '{branchName}' in repository {gitRepoName}.");

                if (localBranch is null || !localBranch.IsCurrentRepositoryHead)
                {
                    _logger.LogInformation($"Checking out branch '{branchName}' in repository {gitRepoName}...");
                    if (localBranch is null)
                        localBranch = repository.CreateBranch(branchName, trackedRemoteBranch.Tip);

                    Branch branch = Commands.Checkout(repository, localBranch);
                    _logger.LogInformation($"Branch '{branchName}' checked out in repository {gitRepoName}.");
                }

                if (!localBranch.IsTracking)
                    repository.Branches.Update(localBranch, b => b.TrackedBranch = trackedRemoteBranch.CanonicalName);

                _logger.LogInformation($"Pulling latest commits for branch '{branchName}' in repository '{gitRepoName}'...");
                Commands.Pull(repository, signature, pullOptions);
                _logger.LogInformation($"Latest commits for branch '{branchName}' pulled in repository '{gitRepoName}'.");
            }
            else
            {
                // clone
                Directory.CreateDirectory(repoCloneTmpPath);
                CloneOptions cloneOptions = new()
                {
                    CredentialsProvider = (_url, _user, _cred) => credentials,
                    BranchName = branchName
                };
                _logger.LogInformation($"Cloning repository '{gitRepoName}'...");
                Repository.Clone(repoUrl, repoCloneTmpPath, cloneOptions);
                repository = new Repository(repoCloneTmpPath);
                _logger.LogInformation($"Clone of repository '{gitRepoName}' done, checked out branch '{branchName}'.");

            }
            return repository;
        }

        private string AskUserPassword(string username)
        {
            Console.WriteLine(Environment.NewLine);
            Console.WriteLine($"--------------------------------");
            Console.WriteLine("Please enter your credentials:");
            Console.WriteLine($"Username: {username}");
            Console.WriteLine("Password:");
            string password = GetHiddenInput();
            Console.WriteLine($"--------------------------------");
            return password;
        }

        /// <summary>
        /// Hide user input while he is typing it.
        /// </summary>
        /// <returns></returns>
        private string GetHiddenInput()
        {
            string hiddenInput = string.Empty;
            ConsoleKey key;
            do
            {
                var keyInfo = Console.ReadKey(intercept: true);
                key = keyInfo.Key;

                if (key == ConsoleKey.Backspace && hiddenInput.Length > 0)
                {
                    Console.Write("\b \b");
                    hiddenInput = hiddenInput[0..^1];
                }
                else if (!char.IsControl(keyInfo.KeyChar))
                {
                    Console.Write("*");
                    hiddenInput += keyInfo.KeyChar;
                }
            } while (key != ConsoleKey.Enter);
            return hiddenInput;
        }

        private string GetRepositoryUrl(string gitRepoName)
        {
            return _appConfiguration.GitConfiguration.RepositoryUrlPrefix + gitRepoName + GitRepositoryExtension;
        }

        private string GetRepositoryGitTempPath(string gitRepoName)
        {
            return Path.Combine(_repoCloneTmpDirectory, gitRepoName);
        }

        private string GetRepositoryCompareFromName(string gitRepoName)
        {
            return Path.Combine(gitRepoName, CompareFromFolderName);
        }

        private string GetRepositoryCompareToName(string gitRepoName)
        {
            return Path.Combine(gitRepoName, CompareToFolderName);
        }

        #endregion

        #region Redmine helpers

        private bool TryGetRedmineIssueNumber(string commitMessage, out string redmineIssueId, out string mergeMessage)
        {
            redmineIssueId = string.Empty;
            mergeMessage = string.Empty;
            Regex regex = new("#([0-9]+)");
            Match match = regex.Match(commitMessage);
            if (match.Success)
            {
                if (match.Groups.Count > 1)
                {
                    redmineIssueId = match.Groups.Values.ElementAt(1)?.Value;
                    return true;
                }
            }
            return false;
        }

        private bool TryGetIssueFromOpenIssues(string targetIssueId, out Issue? foundIssue)
        {
            var parameters = new NameValueCollection
            {
                { RedmineKeys.ISSUE_ID, targetIssueId }
            };
            return TryGetIssue(parameters, out foundIssue);
        }

        private bool TryGetIssueFromClosedIssues(string targetIssueId, out Issue? foundIssue)
        {
            var parameters = new NameValueCollection
            {
                { RedmineKeys.ISSUE_ID, targetIssueId },
                { RedmineKeys.STATUS_ID, "closed" }
            };
            return TryGetIssue(parameters, out foundIssue);
        }

        private bool TryGetIssue(NameValueCollection parameters, out Issue? foundIssue)
        {
            foundIssue = null;
            try
            {
                foundIssue = _manager.GetObjects<Issue>(parameters)?.FirstOrDefault();
                if (foundIssue is not null)
                    return true;
            }
            catch
            {
                // silent failure, found issue is null
            }
            return false;
        }

        private string IssueToString(Issue foundIssue)
        {
            return $" - {foundIssue.Tracker.Name} #{foundIssue.Id}: {foundIssue.Subject}";
        }

        private void GenerateComparisonNote(
            string comparisonNotefileName,
            IDictionary<string, IEnumerable<Issue>> missingFeaturesDictionary,
            IDictionary<string, IEnumerable<string>> unknownFeaturesDictionary)
        {
            if (System.IO.File.Exists(comparisonNotefileName))
                System.IO.File.Delete(comparisonNotefileName);

            using (FileStream fs = System.IO.File.Create(comparisonNotefileName))
            {
                string content;
                foreach (string gitRepoName in missingFeaturesDictionary.Keys)
                {
                    content = $"## {gitRepoName.FirstCharToUpper()}";
                    content += Environment.NewLine;
                    content += $"- Missing features:";
                    content += Environment.NewLine;
                    foreach (Issue redmineIssue in missingFeaturesDictionary[gitRepoName].OrderBy(_I => _I.Tracker.Name))
                    {
                        content += this.IssueToString(redmineIssue);
                        content += Environment.NewLine;
                    }
                    content += $"- Unknown features:";
                    content += Environment.NewLine;
                    foreach (string unknownFeature in unknownFeaturesDictionary[gitRepoName])
                    {
                        content += $" - {unknownFeature}";
                        content += Environment.NewLine;
                    }
                    content += Environment.NewLine;

                    byte[] info = new UTF8Encoding(true).GetBytes(content);
                    fs.Write(info, 0, info.Length);
                }
            }
        }

        #endregion

        #endregion
    }
}
