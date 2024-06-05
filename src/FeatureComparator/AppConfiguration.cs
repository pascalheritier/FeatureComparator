﻿namespace FeatureComparator
{
    internal class AppConfiguration
    {
        public GitConfiguration GitConfiguration { get; set; } = new();
        public RedmineConfiguration RedmineConfiguration { get; set; } = new();
    }

    internal class RedmineConfiguration
    {
        public string ServerUrl { get; set; } = null!;
        public string ApiKey { get; set; } = null!;
        public string TargetUserId { get; set; } = null!;
        public string ComparisonNoteFileName { get; set; } = null!;
    }

    internal class GitConfiguration
    {
        public string Username { get; set; } = null!;
        public string PAT { get; set; } = null!;
        public string RepositoryUrlPrefix { get; set; } = null!;
        public string RepoCloneTmpDirectory { get; set; } = null!;
        public List<GitRepositoryComparison> GitRepositoryComparisons { get; set; } = null!;
    }

    internal class GitRepositoryComparison
    {
        public string RepositoryName { get; set; } = null!;
        public string CommitStartSha { get; set; } = null!;
        public BranchComparison CompareFrom { get; set; } = null!;
        public BranchComparison CompareTo { get; set; } = null!;
    }

    internal class BranchComparison
    {
        public string BranchName { get; set; } = null!;
    }
}
