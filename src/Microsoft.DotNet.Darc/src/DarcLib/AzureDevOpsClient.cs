// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Microsoft.DotNet.DarcLib
{
    public class AzureDevOpsClient : RemoteRepoBase, IGitRepo
    {
        private const string DefaultApiVersion = "5.0-preview.1";

        private static readonly string CommentMarker =
            "\n\n[//]: # (This identifies this comment as a Maestro++ comment)\n";

        private static readonly Regex RepositoryUriPattern = new Regex(
            @"^https://dev\.azure\.com/(?<account>[a-zA-Z0-9]+)/(?<project>[a-zA-Z0-9-]+)/_git/(?<repo>[a-zA-Z0-9-\.]+)");

        private static readonly Regex LegacyRepositoryUriPattern = new Regex(
            @"^https://(?<account>[a-zA-Z0-9]+)\.visualstudio\.com/(?<project>[a-zA-Z0-9-]+)/_git/(?<repo>[a-zA-Z0-9-\.]+)");

        private static readonly Regex PullRequestApiUriPattern = new Regex(
            @"^https://dev\.azure\.com/(?<account>[a-zA-Z0-9]+)/(?<project>[a-zA-Z0-9-]+)/_apis/git/repositories/(?<repo>[a-zA-Z0-9-\.]+)/pullRequests/(?<id>\d+)");

        // Azure DevOps uses this id when creating a new branch as well as when deleting a branch
        private static readonly string BaseObjectId = "0000000000000000000000000000000000000000";

        private readonly ILogger _logger;
        private readonly string _personalAccessToken;
        private readonly JsonSerializerSettings _serializerSettings;

        /// <summary>
        /// Create a new azure devops client.
        /// </summary>
        /// <param name="accessToken">
        ///     PAT for Azure DevOps. This PAT should cover all
        ///     organizations that may be accessed in a single operation.
        /// </param>
        public AzureDevOpsClient(string accessToken, ILogger logger, string temporaryRepositoryPath)
            : base (temporaryRepositoryPath)
        {
            _personalAccessToken = accessToken;
            _logger = logger;
            _serializerSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore
            };
        }

        /// <summary>
        /// Retrieve the contents of a text file in a repo on a specific branch
        /// </summary>
        /// <param name="filePath">Path to file within the repo</param>
        /// <param name="repoUri">Repository url</param>
        /// <param name="branch">Branch or commit</param>
        /// <returns>Content of file</returns>
        public Task<string> GetFileContentsAsync(string filePath, string repoUri, string branch)
        {
            (string accountName, string projectName, string repoName) = ParseRepoUri(repoUri);

            return GetFileContentsAsync(accountName, projectName, repoName, filePath, branch);
        }

        private static readonly List<string> VersionTypes = new List<string>() { "branch", "commit", "tag" };
        /// <summary>
        ///     Retrieve the contents of a text file in a repo on a specific branch
        /// </summary>
        /// <param name="accountName">Azure DevOps account</param>
        /// <param name="projectName">Azure DevOps project</param>
        /// <param name="repoName">Azure DevOps repo</param>
        /// <param name="filePath">Path to file</param>
        /// <param name="branchOrCommit">Branch</param>
        /// <returns>Contents of file as string</returns>
        private async Task<string> GetFileContentsAsync(
            string accountName,
            string projectName,
            string repoName,
            string filePath,
            string branchOrCommit)
        {
            _logger.LogInformation(
                $"Getting the contents of file '{filePath}' from repo '{accountName}/{projectName}/{repoName}' in branch/commit '{branchOrCommit}'...");

            // The AzDO REST API currently does not transparently handle commits vs. branches vs. tags.
            // You really need to know whether you're talking about a commit or branch or tag
            // when you ask the question. Avoid this issue for now by first checking branch (most common)
            // then if it 404s, check commit and then tag.
            HttpRequestException lastException = null;
            foreach (var versionType in VersionTypes)
            {
                try
                {
                    JObject content = await this.ExecuteAzureDevOpsAPIRequestAsync(
                        HttpMethod.Get,
                        accountName,
                        projectName,
                        $"_apis/git/repositories/{repoName}/items?path={filePath}&versionType={versionType}&version={branchOrCommit}&includeContent=true",
                        _logger,
                        // Don't log the failure so users don't get confused by 404 messages popping up in expected circumstances.
                        logFailure: false);
                    return content["content"].ToString();
                }
                catch (HttpRequestException reqEx) when (reqEx.Message.Contains("404 (Not Found)"))
                {
                    // Continue
                    lastException = reqEx;
                }
            }
            _logger.LogError(
                        $"Could not get file contents at {filePath} from {repoName} at branch/commit '{branchOrCommit}'.");
            throw lastException;
        }

        /// <summary>
        /// Create a new branch in a repository
        /// </summary>
        /// <param name="repoUri">Repo to create a branch in</param>
        /// <param name="newBranch">New branch name</param>
        /// <param name="baseBranch">Base of new branch</param>
        public async Task CreateBranchAsync(string repoUri, string newBranch, string baseBranch)
        {
            (string accountName, string projectName, string repoName) = ParseRepoUri(repoUri);

            var azureDevOpsRefs = new List<AzureDevOpsRef>();
            string latestSha = await GetLastCommitShaAsync(accountName, projectName, repoName, baseBranch);

            JObject content = await this.ExecuteAzureDevOpsAPIRequestAsync(
                HttpMethod.Get,
                accountName,
                projectName,
                $"_apis/git/repositories/{repoName}/refs/heads/{newBranch}",
                _logger);

            AzureDevOpsRef azureDevOpsRef;

            // Azure DevOps doesn't fail with a 404 if a branch does not exist, it just returns an empty response object...
            if (content["count"].ToObject<int>() == 0)
            {
                _logger.LogInformation($"'{newBranch}' branch doesn't exist. Creating it...");

                azureDevOpsRef = new AzureDevOpsRef($"refs/heads/{newBranch}", latestSha, BaseObjectId);
                azureDevOpsRefs.Add(azureDevOpsRef);
            }
            else
            {
                _logger.LogInformation(
                    $"Branch '{newBranch}' exists, making sure it is in sync with '{baseBranch}'...");

                string oldSha = await GetLastCommitShaAsync(repoName, $"{newBranch}");

                azureDevOpsRef = new AzureDevOpsRef($"refs/heads/{newBranch}", latestSha, oldSha);
                azureDevOpsRefs.Add(azureDevOpsRef);
            }

            string body = JsonConvert.SerializeObject(azureDevOpsRefs, _serializerSettings);

            await this.ExecuteAzureDevOpsAPIRequestAsync(HttpMethod.Post,
                accountName, projectName, $"_apis/git/repositories/{repoName}/refs", _logger, body);
        }

        public async Task DeleteBranchAsync(string repoUri, string branch)
        {
            (string accountName, string projectName, string repoName) = ParseRepoUri(repoUri);

            string latestSha = await GetLastCommitShaAsync(accountName, projectName, repoName, branch);

            AzureDevOpsRef azureDevOpsRef = new AzureDevOpsRef($"refs/heads/{branch}", BaseObjectId, latestSha);

            string body = JsonConvert.SerializeObject(azureDevOpsRef, _serializerSettings);

            await this.ExecuteAzureDevOpsAPIRequestAsync(HttpMethod.Post,
                accountName, projectName, $"_apis/git/repositories/{repoName}/refs", _logger, body);
        }

        /// <summary>
        ///     Search pull requests matching the specified criteria
        /// </summary>
        /// <param name="repoUri">URI of repo containing the pull request</param>
        /// <param name="pullRequestBranch">Source branch for PR</param>
        /// <param name="status">Current PR status</param>
        /// <param name="keyword">Keyword</param>
        /// <param name="author">Author</param>
        /// <returns>List of pull requests matching the specified criteria</returns>
        public async Task<IEnumerable<int>> SearchPullRequestsAsync(
            string repoUri,
            string pullRequestBranch,
            PrStatus status,
            string keyword = null,
            string author = null)
        {
            (string accountName, string projectName, string repoName) = ParseRepoUri(repoUri);
            var query = new StringBuilder();
            AzureDevOpsPrStatus prStatus;

            switch (status)
            {
                case PrStatus.Open:
                    prStatus = AzureDevOpsPrStatus.Active;
                    break;
                case PrStatus.Closed:
                    prStatus = AzureDevOpsPrStatus.Abandoned;
                    break;
                case PrStatus.Merged:
                    prStatus = AzureDevOpsPrStatus.Completed;
                    break;
                default:
                    prStatus = AzureDevOpsPrStatus.None;
                    break;
            }

            query.Append(
                $"searchCriteria.sourceRefName=refs/heads/{pullRequestBranch}&searchCriteria.status={prStatus.ToString().ToLower()}");

            if (!string.IsNullOrEmpty(keyword))
            {
                _logger.LogInformation(
                    "A keyword was provided but Azure DevOps doesn't support searching for PRs based on keywords and it won't be used...");
            }

            if (!string.IsNullOrEmpty(author))
            {
                query.Append($"&searchCriteria.creatorId={author}");
            }

            JObject content = await this.ExecuteAzureDevOpsAPIRequestAsync(
                HttpMethod.Get,
                accountName,
                projectName,
                $"repositories/{repoName}/pullrequests?{query}",
                _logger);

            JArray values = JArray.Parse(content["value"].ToString());
            IEnumerable<int> prs = values.Select(r => r["pullRequestId"].ToObject<int>());

            return prs;
        }

        /// <summary>
        /// Get the status of a pull request
        /// </summary>
        /// <param name="pullRequestUrl">URI of pull request</param>
        /// <returns>Pull request status</returns>
        public async Task<PrStatus> GetPullRequestStatusAsync(string pullRequestUrl)
        {
            (string accountName, string projectName, string repoName, int id) = ParsePullRequestUri(pullRequestUrl);

            JObject content = await this.ExecuteAzureDevOpsAPIRequestAsync(HttpMethod.Get,
                accountName, projectName, $"_apis/git/repositories/{repoName}/pullRequests/{id}", _logger);

            if (Enum.TryParse(content["status"].ToString(), true, out AzureDevOpsPrStatus status))
            {
                if (status == AzureDevOpsPrStatus.Active)
                {
                    return PrStatus.Open;
                }

                if (status == AzureDevOpsPrStatus.Completed)
                {
                    return PrStatus.Merged;
                }

                if (status == AzureDevOpsPrStatus.Abandoned)
                {
                    return PrStatus.Closed;
                }

                throw new DarcException($"Unhandled Azure DevOPs PR status {status}");
            }

            throw new DarcException($"Failed to parse PR status: {content["status"]}");
            
        }

        /// <summary>
        ///     Retrieve information on a specific pull request
        /// </summary>
        /// <param name="pullRequestUrl">Uri of the pull request</param>
        /// <returns>Information on the pull request.</returns>
        public async Task<PullRequest> GetPullRequestAsync(string pullRequestUrl)
        {
            (string accountName, string projectName, string repoName, int id) = ParsePullRequestUri(pullRequestUrl);

            VssConnection connection = CreateVssConnection(accountName);
            GitHttpClient client = await connection.GetClientAsync<GitHttpClient>();

            GitPullRequest pr = await client.GetPullRequestAsync(projectName, repoName, id);
            // Strip out the refs/heads prefix on BaseBranch and HeadBranch because almost
            // all of the other APIs we use do not support them (e.g. get an item at branch X).
            // At the time this code was written, the API always returned the refs with this prefix,
            // so verify this is the case.
            const string refsHeads = "refs/heads/";
            if (!pr.TargetRefName.StartsWith(refsHeads) || !pr.SourceRefName.StartsWith(refsHeads))
            {
                throw new NotImplementedException("Expected that source and target ref names returned from pull request API include refs/heads");
            }

            return new PullRequest
            {
                Title = pr.Title,
                Description = pr.Description,
                BaseBranch = pr.TargetRefName.Substring(refsHeads.Length),
                HeadBranch = pr.SourceRefName.Substring(refsHeads.Length),
            };
        }

        /// <summary>
        ///     Create a new pull request
        /// </summary>
        /// <param name="repoUri">Repository URI</param>
        /// <param name="pullRequest">Pull request data</param>
        /// <returns>URL of new pull request</returns>
        public async Task<string> CreatePullRequestAsync(string repoUri, PullRequest pullRequest)
        {
            (string accountName, string projectName, string repoName) = ParseRepoUri(repoUri);

            VssConnection connection = CreateVssConnection(accountName);
            GitHttpClient client = await connection.GetClientAsync<GitHttpClient>();

            GitPullRequest createdPr = await client.CreatePullRequestAsync(
                new GitPullRequest
                {
                    Title = pullRequest.Title,
                    Description = pullRequest.Description,
                    SourceRefName = "refs/heads/" + pullRequest.HeadBranch,
                    TargetRefName = "refs/heads/" + pullRequest.BaseBranch
                },
                projectName,
                repoName);

            return createdPr.Url;
        }

        /// <summary>
        ///     Update a pull request with new information
        /// </summary>
        /// <param name="pullRequestUri">Uri of pull request to update</param>
        /// <param name="pullRequest">Pull request info to update</param>
        /// <returns></returns>
        public async Task UpdatePullRequestAsync(string pullRequestUri, PullRequest pullRequest)
        {
            (string accountName, string projectName, string repoName, int id) = ParsePullRequestUri(pullRequestUri);

            VssConnection connection = CreateVssConnection(accountName);
            GitHttpClient client = await connection.GetClientAsync<GitHttpClient>();

            await client.UpdatePullRequestAsync(
                new GitPullRequest
                {
                    Title = pullRequest.Title,
                    Description = pullRequest.Description
                },
                projectName,
                repoName,
                id);
        }

        /// <summary>
        ///     Merge a pull request
        /// </summary>
        /// <param name="pullRequestUrl">Uri of pull request to merge</param>
        /// <param name="parameters">Settings for merge</param>
        /// <returns></returns>
        public async Task MergePullRequestAsync(string pullRequestUrl, MergePullRequestParameters parameters)
        {
            (string accountName, string projectName, string repoName, int id) = ParsePullRequestUri(pullRequestUrl);

            VssConnection connection = CreateVssConnection(accountName);
            GitHttpClient client = await connection.GetClientAsync<GitHttpClient>();

            string commitToMerge = parameters.CommitToMerge;

            // If the commit to merge is empty, look it up first.
            if (string.IsNullOrEmpty(commitToMerge))
            {
                var prInfo = await client.GetPullRequestAsync(repoName, id);
                commitToMerge = prInfo.LastMergeSourceCommit.CommitId;
            }

            await client.UpdatePullRequestAsync(
                new GitPullRequest
                {
                    Status = PullRequestStatus.Completed,
                    CompletionOptions = new GitPullRequestCompletionOptions
                    {
                        SquashMerge = parameters.SquashMerge,
                        DeleteSourceBranch = parameters.DeleteSourceBranch
                    },
                    LastMergeSourceCommit = new GitCommitRef { CommitId = commitToMerge }
                },
                projectName,
                repoName,
                id);
        }

        /// <summary>
        ///     Create a new comment, or update the last comment with an updated message,
        ///     if that comment was created by Darc.
        /// </summary>
        /// <param name="pullRequestUrl">Url of pull request</param>
        /// <param name="message">Message to post</param>
        /// <remarks>
        ///     Search through the pull request comment threads to find one who's *last* comment ends
        ///     in the comment marker. If the comment is found, update it, otherwise append a comment
        ///     to the first thread that has a comment marker for any comment.
        ///     Create a new thread if no comment markers were found.
        /// </remarks>
        public async Task CreateOrUpdatePullRequestCommentAsync(string pullRequestUrl, string message)
        {
            (string accountName, string projectName, string repoName, int id) = ParsePullRequestUri(pullRequestUrl);

            VssConnection connection = CreateVssConnection(accountName);
            GitHttpClient client = await connection.GetClientAsync<GitHttpClient>();

            Comment prComment = new Comment()
            {
                CommentType = CommentType.Text,
                Content = $"{message}{CommentMarker}"
            };

            // Search threads to find ones with comment markers.
            List<GitPullRequestCommentThread> commentThreads = await client.GetThreadsAsync(repoName, id);
            foreach (GitPullRequestCommentThread commentThread in commentThreads)
            {
                // Skip non-active and non-unknown threads.  Threads that are active may appear as unknown.
                if (commentThread.Status != CommentThreadStatus.Active && commentThread.Status != CommentThreadStatus.Unknown)
                {
                    continue;
                }
                List<Comment> comments = await client.GetCommentsAsync(repoName, id, commentThread.Id);
                bool threadHasCommentWithMarker = comments.Any(comment => comment.CommentType == CommentType.Text && comment.Content.EndsWith(CommentMarker));
                if (threadHasCommentWithMarker)
                {
                    // Check if last comment in that thread has the marker.
                    Comment lastComment = comments.Last();
                    if (lastComment.CommentType == CommentType.Text && lastComment.Content.EndsWith(CommentMarker))
                    {
                        // Update comment
                        await client.UpdateCommentAsync(prComment, repoName, id, commentThread.Id, lastComment.Id);
                    }
                    else
                    {
                        // Add a new comment to the end of the thread
                        await client.CreateCommentAsync(prComment, repoName, id, commentThread.Id);
                    }
                    return;
                }
            }

            // No threads found, create a new one with the comment
            var newCommentThread = new GitPullRequestCommentThread()
            {
                Comments = new List<Comment>()
                {
                    prComment
                }
            };
            await client.CreateThreadAsync(newCommentThread, repoName, id);
        }

        /// <summary>
        ///     Retrieve a set of file under a specific path at a commit
        /// </summary>
        /// <param name="repoUri">Repository URI</param>
        /// <param name="commit">Commit to get files at</param>
        /// <param name="path">Path to retrieve files from</param>
        /// <returns>Set of files under <paramref name="path"/> at <paramref name="commit"/></returns>
        public async Task<List<GitFile>> GetFilesAtCommitAsync(string repoUri, string commit, string path)
        {
            var files = new List<GitFile>();

            _logger.LogInformation(
                $"Getting the contents of file/files in '{path}' of repo '{repoUri}' at commit '{commit}'");

            (string accountName, string projectName, string repoName) = ParseRepoUri(repoUri);

            JObject content = await this.ExecuteAzureDevOpsAPIRequestAsync(
                HttpMethod.Get,
                accountName,
                projectName,
                $"repositories/{repoName}/items?scopePath={path}&version={commit}&includeContent=true&versionType=commit&recursionLevel=full",
                _logger);
            List<AzureDevOpsItem> items = JsonConvert.DeserializeObject<List<AzureDevOpsItem>>(Convert.ToString(content["value"]));

            foreach (AzureDevOpsItem item in items)
            {
                if (!item.IsFolder)
                {
                    if (!GitFileManager.DependencyFiles.Contains(item.Path))
                    {
                        string fileContent = await GetFileContentsAsync(accountName, projectName, repoName, item.Path, commit);
                        var gitCommit = new GitFile(item.Path, fileContent);
                        files.Add(gitCommit);
                    }
                }
            }

            _logger.LogInformation(
                $"Getting the contents of file/files in '{path}' of repo '{repoUri}' at commit '{commit}' succeeded!");

            return files;
        }

        /// <summary>
        ///     Get the latest commit in a repo on the specific branch 
        /// </summary>
        /// <param name="repoUri">Repository uri</param>
        /// <param name="branch">Branch to retrieve the latest sha for</param>
        /// <returns>Latest sha.  Throws if no commits were found.</returns>
        public Task<string> GetLastCommitShaAsync(string repoUri, string branch)
        {
            (string accountName, string projectName, string repoName) = ParseRepoUri(repoUri);
            return GetLastCommitShaAsync(accountName, projectName, repoName, branch);
        }

        /// <summary>
        ///     Get the latest commit in a repo on the specific branch
        /// </summary>
        /// <param name="accountName">Azure DevOps account</param>
        /// <param name="projectName">Azure DevOps project</param>
        /// <param name="repoName">Azure DevOps repo</param>
        /// <param name="branch">Branch</param>
        /// <returns>Latest sha. Throws if there were not commits on <paramref name="branch"/></returns>
        private async Task<string> GetLastCommitShaAsync(string accountName, string projectName, string repoName, string branch)
        {
            JObject content = await this.ExecuteAzureDevOpsAPIRequestAsync(
                HttpMethod.Get,
                accountName,
                projectName,
                $"_apis/git/repositories/{repoName}/commits?branch={branch}",
                _logger);
            JArray values = JArray.Parse(content["value"].ToString());

            if (!values.Any())
            {
                throw new DarcException($"No commits found in branch '{branch}' of '{accountName}/{projectName}/{repoName}'");
            }

            return values[0]["commitId"].ToString();
        }

        /// <summary>
        /// Retrieve the list of status checks on a PR.
        /// </summary>
        /// <param name="pullRequestUrl">Uri of pull request</param>
        /// <returns>List of status checks.</returns>
        public async Task<IList<Check>> GetPullRequestChecksAsync(string pullRequestUrl)
        {
            (string accountName, string projectName, string repo, int id) = ParsePullRequestUri(pullRequestUrl);

            string statusesPath = $"_apis/git/repositories/{repo}/pullRequests/{id}/statuses";

            JObject content = await this.ExecuteAzureDevOpsAPIRequestAsync(HttpMethod.Get,
                accountName,
                projectName,
                statusesPath,
                _logger);

            JArray values = JArray.Parse(content["value"].ToString());

            IList<Check> statuses = new List<Check>();
            foreach (JToken status in values)
            {
                if (Enum.TryParse(status["state"].ToString(), true, out AzureDevOpsCheckState state))
                {
                    CheckState checkState;

                    switch (state)
                    {
                        case AzureDevOpsCheckState.Error:
                            checkState = CheckState.Error;
                            break;
                        case AzureDevOpsCheckState.Failed:
                            checkState = CheckState.Failure;
                            break;
                        case AzureDevOpsCheckState.Pending:
                            checkState = CheckState.Pending;
                            break;
                        case AzureDevOpsCheckState.Succeeded:
                            checkState = CheckState.Success;
                            break;
                        default:
                            checkState = CheckState.None;
                            break;
                    }

                    statuses.Add(
                        new Check(
                            checkState,
                            status["context"]["name"].ToString(),
                            $"https://dev.azure.com/{accountName}/{projectName}/{statusesPath}/{status["id"]}"));
                }
            }

            return statuses;
        }

        /// <summary>
        ///     Execute a command on the remote repository.
        /// </summary>
        /// <param name="method">Http method</param>
        /// <param name="accountName">Azure DevOps account name</param>
        /// <param name="projectName">Project name</param>
        /// <param name="requestPath">Path for request</param>
        /// <param name="logger">Logger</param>
        /// <param name="body">Optional body if <paramref name="method"/> is Put or Post</param>
        /// <param name="versionOverride">API version override</param>
        /// <param name="baseAddressSubpath">[baseAddressSubPath]dev.azure.com subdomain to make the request</param>
        /// <returns>Http response</returns>
        private async Task<JObject> ExecuteAzureDevOpsAPIRequestAsync(
            HttpMethod method,
            string accountName,
            string projectName,
            string requestPath,
            ILogger logger,
            string body = null,
            string versionOverride = null,
            bool logFailure = true,
            string baseAddressSubpath = null)
        {
            using (HttpClient client = CreateHttpClient(accountName, projectName, versionOverride, baseAddressSubpath))
            {
                HttpRequestManager requestManager = new HttpRequestManager(client,
                                                                           method,
                                                                           requestPath,
                                                                           logger,
                                                                           body,
                                                                           versionOverride,
                                                                           logFailure);

                using (var response = await requestManager.ExecuteAsync())
                {
                    return JObject.Parse(await response.Content.ReadAsStringAsync());
                }
            }
        }

        /// <summary>
        ///     Ensure that the input string ends with 'shouldEndWith' char. 
        ///     Returns null if input parameter is null.
        /// </summary>
        /// <param name="input">String that must have 'shouldEndWith' at the end.</param>
        /// <param name="shouldEndWith">Character that must be present at end of 'input' string.</param>
        /// <returns>Input string appended with 'shouldEndWith'</returns>
        private string EnsureEndsWith(string input, char shouldEndWith)
        {
            if (input == null) return null;

            return input.TrimEnd(shouldEndWith) + shouldEndWith;
        }

        /// <summary>
        /// Create a new http client for talking to the specified azdo account name and project.
        /// </summary>
        /// <param name="versionOverride">Optional version override for the targeted API version.</param>
        /// <param name="baseAddressSubpath">Optional subdomain for the base address for the API. Should include the final dot.</param>
        /// <param name="accountName">Azure DevOps account</param>
        /// <param name="projectName">Azure DevOps project</param>
        /// <returns>New http client</returns>
        private HttpClient CreateHttpClient(string accountName, string projectName, string versionOverride = null, string baseAddressSubpath = null)
        {
            baseAddressSubpath = EnsureEndsWith(baseAddressSubpath, '.');

            var client = new HttpClient {
                BaseAddress = new Uri($"https://{baseAddressSubpath}dev.azure.com/{accountName}/{projectName}/")
            };

            client.DefaultRequestHeaders.Add(
                "Accept",
                $"application/json;api-version={versionOverride ?? DefaultApiVersion}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes(string.Format("{0}:{1}", "", _personalAccessToken))));

            return client;
        }

        /// <summary>
        /// Create a connection to AzureDevOps using the VSS APIs
        /// </summary>
        /// <param name="accountName">Uri of repository or pull request</param>
        /// <returns>New VssConnection</returns>
        private VssConnection CreateVssConnection(string accountName)
        {
            Uri accountUri = new Uri($"https://dev.azure.com/{accountName}");
            var creds = new VssCredentials(new VssBasicCredential("", _personalAccessToken));
            return new VssConnection(accountUri, creds);
        }

        private (int threadId, int commentId) ParseCommentId(string commentId)
        {
            string[] parts = commentId.Split('-');
            if (parts.Length != 2 || int.TryParse(parts[0], out int threadId) ||
                int.TryParse(parts[1], out int commentIdValue))
            {
                throw new ArgumentException("The comment id '{commentId}' is in an invalid format", nameof(commentId));
            }

            return (threadId, commentIdValue);
        }

        /// <summary>
        /// Parse a repository url into its component parts.
        /// </summary>
        /// <param name="repoUri">Repository url to parse</param>
        /// <returns>Tuple of account, project, repo</returns>
        /// <remarks>
        ///     While we really only want to support dev.azure.com, in some cases
        ///     builds are still being reported from foo.visualstudio.com. This is apparently because
        ///     the url the agent connects to is what determines this property. So for now, support both forms.
        ///     We don't need to support this for PR urls because the repository target urls in the Maestro
        ///     database are restricted to dev.azure.com forms.
        /// </remarks>
        public static (string accountName, string projectName, string repoName) ParseRepoUri(string repoUri)
        {
            Match m = RepositoryUriPattern.Match(repoUri);
            if (!m.Success)
            {
                m = LegacyRepositoryUriPattern.Match(repoUri);
                if (!m.Success)
                {
                    throw new ArgumentException(
                        "Repository URI should be in the form https://dev.azure.com/:account/:project/_git/:repo or " +
                        "https://:account.visualstudio.com/:project/_git/:repo");
                }
            }

            return (m.Groups["account"].Value,
                    m.Groups["project"].Value,
                    m.Groups["repo"].Value);
        }

        /// <summary>
        /// Parse a repository url into its component parts
        /// </summary>
        /// <param name="repoUri">Repository url to parse</param>
        /// <returns>Tuple of account, project, repo, and pr id</returns>
        public static (string accountName, string projectName, string repoName, int id) ParsePullRequestUri(string prUri)
        {
            Match m = PullRequestApiUriPattern.Match(prUri);
            if (!m.Success)
            {
                throw new ArgumentException(
                    @"Pull request URI should be in the form  https://dev.azure.com/:account/:project/_apis/git/repositories/:repo/pullRequests/:id");
            }

            return (m.Groups["account"].Value,
                    m.Groups["project"].Value,
                    m.Groups["repo"].Value,
                    Int32.Parse(m.Groups["id"].Value));
        }

        /// <summary>
        ///     Commit or update a set of files to a repo
        /// </summary>
        /// <param name="filesToCommit">Files to comit</param>
        /// <param name="repoUri">Remote repository URI</param>
        /// <param name="branch">Branch to push to</param>
        /// <param name="commitMessage">Commit message</param>
        /// <returns></returns>
        public Task CommitFilesAsync(List<GitFile> filesToCommit, string repoUri, string branch, string commitMessage)
        {
            return this.CommitFilesAsync(filesToCommit, repoUri, branch, commitMessage, _logger, _personalAccessToken);
        }

        /// <summary>
        ///     Add the informed build as an specific build artifact source to the release definition informed.
        /// </summary>
        /// <param name="accountName">Azure DevOps account name</param>
        /// <param name="projectName">Project name</param>
        /// <param name="releaseDefinition">Release definition to be updated</param>
        /// <param name="build">Build which should be added as source of the release definition.</param>
        /// <returns>AzureDevOpsReleaseDefinition</returns>
        public async Task<AzureDevOpsReleaseDefinition> AddArtifactSourceAsync(string accountName, string projectName, AzureDevOpsReleaseDefinition releaseDefinition, AzureDevOpsBuild build)
        {
            releaseDefinition.Artifacts = new AzureDevOpsArtifact[1] {
                new AzureDevOpsArtifact()
                {
                    Alias = "PrimaryArtifact",
                    Type = "Build",
                    DefinitionReference = new AzureDevOpsArtifactSourceReference()
                    {
                        Definition = new AzureDevOpsIdNamePair()
                        {
                            Id = build.Definition.Id.ToString(),
                            Name = build.Definition.Name
                        },
                        DefaultVersionType = new AzureDevOpsIdNamePair()
                        {
                            Id = "specificVersionType",
                            Name = "Specific version"
                        },
                        DefaultVersionSpecific = new AzureDevOpsIdNamePair()
                        {
                            Id = build.Id.ToString(),
                            Name = build.BuildNumber
                        },
                        Project = new AzureDevOpsIdNamePair()
                        {
                            Id = build.Project.Id.ToString(),
                            Name = build.Project.Name
                        }
                    }
                }
            };

            var _serializerSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore
            };

            var body = JsonConvert.SerializeObject(releaseDefinition, _serializerSettings);

            JObject content = await this.ExecuteAzureDevOpsAPIRequestAsync(
                HttpMethod.Put,
                accountName,
                projectName,
                $"_apis/release/definitions/",
                _logger,
                body,
                versionOverride: "5.0-preview.3",
                baseAddressSubpath: "vsrm.");

            return content.ToObject<AzureDevOpsReleaseDefinition>();
        }

        /// <summary>
        ///     Remove all artifact sources of the release definition informed.
        /// </summary>
        /// <param name="accountName">Azure DevOps account name</param>
        /// <param name="projectName">Project name</param>
        /// <param name="releaseDefinition">Release definition to be modified</param>
        public async void RemoveAllArtifactSourcesAsync(string accountName, string projectName, AzureDevOpsReleaseDefinition releaseDefinition)
        {
            releaseDefinition.Artifacts = new AzureDevOpsArtifact[0];

            var body = JsonConvert.SerializeObject(releaseDefinition);

            JObject content = await this.ExecuteAzureDevOpsAPIRequestAsync(
                HttpMethod.Put,
                accountName,
                projectName,
                $"_apis/release/definitions/",
                _logger,
                body,
                versionOverride: "5.0-preview.3",
                baseAddressSubpath: "vsrm.");
        }

        /// <summary>
        ///     Trigger a new release using the release definition informed. No change is performed
        ///     on the release definition - it is used as is.
        /// </summary>
        /// <param name="accountName">Azure DevOps account name</param>
        /// <param name="projectName">Project name</param>
        /// <param name="releaseDefinition">Release definition to be updated</param>
        public async void StartNewReleaseAsync(string accountName, string projectName, AzureDevOpsReleaseDefinition releaseDefinition)
        {
            var body = $"{{ \"definitionId\": {releaseDefinition.Id} }}";

            JObject content = await this.ExecuteAzureDevOpsAPIRequestAsync(
                HttpMethod.Post,
                accountName,
                projectName,
                $"_apis/release/releases/",
                _logger,
                body,
                versionOverride: "5.0-preview.3",
                baseAddressSubpath: "vsrm.");
        }

        /// <summary>
        ///     Fetches an specific AzDO build based on its ID.
        /// </summary>
        /// <param name="accountName">Azure DevOps account name</param>
        /// <param name="projectName">Project name</param>
        /// <param name="buildId">Id of the build to be retrieved</param>
        /// <returns>AzureDevOpsBuild</returns>
        public async Task<AzureDevOpsBuild> GetBuildAsync(string accountName, string projectName, long buildId)
        {
            JObject content = await this.ExecuteAzureDevOpsAPIRequestAsync(
                HttpMethod.Get,
                accountName,
                projectName,
                $"_apis/build/builds/{buildId}",
                _logger,
                versionOverride: "5.0-preview.3");

            return content.ToObject<AzureDevOpsBuild>();
        }

        /// <summary>
        ///     Fetches an specific AzDO release definition based on its ID.
        /// </summary>
        /// <param name="accountName">Azure DevOps account name</param>
        /// <param name="projectName">Project name</param>
        /// <param name="releaseDefinitionId">Id of the release definition to be retrieved</param>
        /// <returns>AzureDevOpsReleaseDefinition</returns>
        public async Task<AzureDevOpsReleaseDefinition> GetReleaseDefinitionAsync(string accountName, string projectName, long releaseDefinitionId)
        {
            JObject content = await this.ExecuteAzureDevOpsAPIRequestAsync(
                HttpMethod.Get,
                accountName,
                projectName,
                $"_apis/release/definitions/{releaseDefinitionId}",
                _logger,
                versionOverride: "5.0-preview.3",
                baseAddressSubpath: "vsrm.");

            return content.ToObject<AzureDevOpsReleaseDefinition>();
        }
    }
}
