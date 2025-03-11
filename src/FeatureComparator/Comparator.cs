using ExcelDataReader;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Redmine.Net.Api;
using Redmine.Net.Api.Types;
using System.Collections.Specialized;
using System.Data;
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

        public const string GitRepoNameIdentifier = "## ";

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
                    // pull compareFrom branches
                    foreach (string compareFromBranchName in gitRepository.CompareFrom.BranchesName)
                    {
                        string repositoryCompareFromName = GetRepositoryCompareFromName(gitRepository.RepositoryName, compareFromBranchName);
                        Repository repositoryCompareFrom = CloneOrPullRepository(
                            gitRepository.RepositoryName,
                            compareFromBranchName,
                            GetRepositoryGitTempPath(repositoryCompareFromName, compareFromBranchName),
                            GetRepositoryUrl(gitRepository.RepositoryName),
                            credentials);
                        _gitRepositoryDictionary.Add(repositoryCompareFromName, repositoryCompareFrom);
                    }

                    // pull compareTo branches
                    foreach (string compareToBranchName in gitRepository.CompareTo.BranchesName)
                    {
                        string repositoryCompareToName = GetRepositoryCompareToName(gitRepository.RepositoryName, compareToBranchName);
                        Repository repositoryCompareTo = CloneOrPullRepository(
                            gitRepository.RepositoryName,
                            compareToBranchName,
                            GetRepositoryGitTempPath(repositoryCompareToName, compareToBranchName),
                            GetRepositoryUrl(gitRepository.RepositoryName),
                            credentials);
                        _gitRepositoryDictionary.Add(repositoryCompareToName, repositoryCompareTo);
                    }
                }

                Dictionary<string, IEnumerable<Issue>> missingFeaturesDictionary = new(); // features that are missing in the compareTo repository
                Dictionary<string, IEnumerable<Issue>> unplannedMissingFeaturesDictionary = new(); // features that are missing in the compareTo repository and are unplanned for future development
                Dictionary<string, IEnumerable<string>> unknownFeaturesDictionary = new(); // features merged in the compareFrom repository but that have no Redmine equivalence

                // compare features in each repository
                foreach (GitRepositoryComparison gitRepository in _appConfiguration.GitConfiguration.GitRepositoryComparisons)
                {
                    CompareFeaturesInRepository(gitRepository, out IEnumerable<Issue> missingFeatures, out List<string> unknownFeatures);
                    IEnumerable<Issue> unplannedMissingIssues = FindUnplannedFeatures(missingFeatures);
                    missingFeaturesDictionary.Add(gitRepository.RepositoryName, missingFeatures);
                    unknownFeaturesDictionary.Add(gitRepository.RepositoryName, unknownFeatures);
                    unplannedMissingFeaturesDictionary.Add(gitRepository.RepositoryName, unplannedMissingIssues);
                }

                // filter out features that are already in the existing comparison file
                 FilterOutExistingEntries(_appConfiguration.ComparisonFileConfiguration.FilePath, unplannedMissingFeaturesDictionary, unknownFeaturesDictionary);

                // generate comparison file
                GenerateComparisonNote(_appConfiguration.RedmineConfiguration.ComparisonNoteFileName, unplannedMissingFeaturesDictionary, unknownFeaturesDictionary);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error while accessing git repository: {ex.ToString()}");
            }
        }

        private void CompareFeaturesInRepository(GitRepositoryComparison gitRepository, out IEnumerable<Issue> missingFeatures, out List<string> unknownFeatures)
        {
            _logger.LogInformation($"Start comparing features for git repo '{gitRepository.RepositoryName}', from branches '{string.Join(", ", gitRepository.CompareFrom.BranchesName)}' to branches '{string.Join(", ", gitRepository.CompareTo.BranchesName)}':");

            // get merge commits
            List<Commit> commitsCompareFrom = new();
            List<Commit> commitsCompareTo = new();
            List<Issue> featuresCompareFrom = new();
            List<Issue> featuresCompareTo = new();
            unknownFeatures = new List<string>();
            foreach (string compareFromBranch in gitRepository.CompareFrom.BranchesName)
            {
                using Repository repositoryCompareFrom = GetRepositoryCompareFrom(gitRepository.RepositoryName, compareFromBranch);
                commitsCompareFrom.AddRange(GetMergingCommits(
                    gitRepository.RepositoryName,
                    gitRepository.CommitStartSha,
                    compareFromBranch,
                    repositoryCompareFrom));
                featuresCompareFrom.AddRange(GetFeatures(GetRepositoryCompareFromName(gitRepository.RepositoryName, compareFromBranch), commitsCompareFrom, out IList<string> _unknownFeatures));
                unknownFeatures.AddRange(_unknownFeatures);
            }
            featuresCompareFrom = featuresCompareFrom.DistinctBy(_C => _C.Id).ToList(); // keep only one feature sample per comparison
            unknownFeatures = unknownFeatures.Distinct().ToList();

            foreach (string compareToBranch in gitRepository.CompareTo.BranchesName)
            {
                using Repository repositoryCompareTo = GetRepositoryCompareTo(gitRepository.RepositoryName, compareToBranch);
                commitsCompareTo.AddRange(GetMergingCommits(
                    gitRepository.RepositoryName,
                    gitRepository.CommitStartSha,
                    compareToBranch,
                    repositoryCompareTo));
                featuresCompareTo.AddRange(GetFeatures(GetRepositoryCompareToName(gitRepository.RepositoryName, compareToBranch), commitsCompareTo, out _));
            }
            featuresCompareTo = featuresCompareTo.DistinctBy(_C => _C.Id).ToList(); // keep only one feature sample per comparison

            missingFeatures = featuresCompareFrom.Where(issueFrom => !featuresCompareTo.Any(issueTo => issueTo.Id == issueFrom.Id));

            _logger.LogInformation($"End comparing features for git repo '{gitRepository.RepositoryName}', from branches '{string.Join(", ", gitRepository.CompareFrom.BranchesName)}' to branches '{string.Join(", ", gitRepository.CompareTo.BranchesName)}':");
        }

        private IEnumerable<Issue> FindUnplannedFeatures(IEnumerable<Issue> missingFeatures)
        {
            List<Issue> unplannedFeatures = new();
            foreach (Issue missingFeature in missingFeatures)
            {
                // there should be a planned task (child of the issue) with a specific subject for any missing feature
                IssueChild? plannedTask = missingFeature.Children?.FirstOrDefault(_childIssue => _appConfiguration.RedmineConfiguration.PlannedFeatureSubjects.Any(_S => _childIssue.Subject.Contains(_S)));
                if (plannedTask is not null)
                {
                    // the planned task must obviously be an open issue, so we have to fetch more details
                    if (TryGetIssueFromOpenIssues(plannedTask.Id.ToString(), out Issue? detailedPlannedTask))
                        if (detailedPlannedTask.Status.Id == 1)
                            continue; // this is a planned feature, so we can skip it
                }
                // if we found no open planned task, then it is an unplanned feature
                unplannedFeatures.Add(missingFeature);
            }
            return unplannedFeatures;
        }

        private Repository GetRepositoryCompareFrom(string gitRepositoryName, string branchName)
        {
            string repositoryFromName = GetRepositoryCompareFromName(gitRepositoryName, branchName);
            return GetRepository(repositoryFromName);
        }

        private Repository GetRepositoryCompareTo(string gitRepositoryName, string branchName)
        {
            string repositoryToName = GetRepositoryCompareToName(gitRepositoryName, branchName);
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
                _logger.LogError($"Repository '{gitRepositoryName}', branch name '{gitBranchName}': Could not find start commit with SHA {gitCommitStartSha}, which will be skipped during generation.");
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

        private string GetRepositoryGitTempPath(string gitRepoName, string branchName)
        {
            return Path.Combine(_repoCloneTmpDirectory, gitRepoName, branchName);
        }

        private string GetRepositoryCompareFromName(string gitRepoName, string branchName)
        {
            return Path.Combine(gitRepoName, CompareFromFolderName, branchName);
        }

        private string GetRepositoryCompareToName(string gitRepoName, string branchName)
        {
            return Path.Combine(gitRepoName, CompareToFolderName, branchName);
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
                { RedmineKeys.INCLUDE, RedmineKeys.CHILDREN }
            };
            return TryGetIssue(targetIssueId, parameters, out foundIssue);
        }

        private bool TryGetIssueFromClosedIssues(string targetIssueId, out Issue? foundIssue)
        {
            var parameters = new NameValueCollection
            {
                { RedmineKeys.STATUS_ID, RedmineKeys.IS_CLOSED },
                { RedmineKeys.INCLUDE, RedmineKeys.CHILDREN }
            };
            return TryGetIssue(targetIssueId, parameters, out foundIssue);
        }

        private bool TryGetIssue(string targetIssueId, NameValueCollection? parameters, out Issue? foundIssue)
        {
            foundIssue = null;
            try
            {
                foundIssue = _manager.GetObject<Issue>(targetIssueId, parameters);
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
                    content = $"{GitRepoNameIdentifier}{gitRepoName.FirstCharToUpper()}";
                    content += Environment.NewLine;
                    content += $"- Missing features:";
                    content += Environment.NewLine;
                    foreach (Issue redmineIssue in missingFeaturesDictionary[gitRepoName]
                        .OrderByDescending(_I => _I.Id)
                        .OrderBy(_I => _I.Tracker.Name))
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

        #region Comparison file helpers

        private void FilterOutExistingEntries(
            string comparisonFilePath,
            IDictionary<string, IEnumerable<Issue>> missingFeaturesDictionary,
            IDictionary<string, IEnumerable<string>> unknownFeaturesDictionary)
        {
            if (comparisonFilePath is null)
                return;

            // get missing features already registered in file
            IDictionary<string, List<string>> existingFeatures = GetFileContent(comparisonFilePath);
            // filter out registered missing features from dictionaries
            foreach (string gitRepoName in missingFeaturesDictionary.Keys)
            {
                missingFeaturesDictionary[gitRepoName] = missingFeaturesDictionary[gitRepoName].Where(_I => !existingFeatures[gitRepoName].Any(_F => _F.Contains(_I.Id.ToString()))).ToArray();
                unknownFeaturesDictionary[gitRepoName] = unknownFeaturesDictionary[gitRepoName].Where(_U => !existingFeatures[gitRepoName].Any(_F => _F.Contains(_U))).ToArray();
            }
        }

        private IDictionary<string, List<string>> GetFileContent(string comparisonFilePath)
        {
            Dictionary<string, List<string>> comparisonFileContent = new();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using (var stream = System.IO.File.Open(comparisonFilePath, FileMode.Open, FileAccess.Read))
            {
                using (var reader = ExcelReaderFactory.CreateReader(stream))
                {
                    DataSet result = reader.AsDataSet(); // Convert to DataSet
                    DataTable table = result.Tables[0]; // Read first worksheet

                    string currentGitRepoName = null;
                    foreach (DataRow row in table.Rows)
                    {
                        string? currentRowItem = row.ItemArray[0]?.ToString(); // only the first column is of interest to us
                        if (currentRowItem is null)
                            continue;
                        if (currentRowItem.Contains(GitRepoNameIdentifier))
                        {
                            currentGitRepoName = GetGitRepoNameFromCell(currentRowItem);
                            continue;
                        }
                        if (currentGitRepoName is null)
                            continue; // we do not register item since we have not found any repo yet
                        if (comparisonFileContent.ContainsKey(currentGitRepoName))
                            comparisonFileContent[currentGitRepoName].Add(currentRowItem);
                        else
                            comparisonFileContent.Add(currentGitRepoName, new List<string> { currentRowItem });
                    }
                }
            }
            return comparisonFileContent;
        }

        private string GetGitRepoNameFromCell(string input)
        {
            string pattern = @$"(?<={GitRepoNameIdentifier})\w+"; // Regex to extract text after the identifier
            Match match = Regex.Match(input, pattern);
            return  match.Value.ToLower();
        }

        #endregion

        #endregion
    }
}
