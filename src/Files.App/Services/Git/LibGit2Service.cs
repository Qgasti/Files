using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Sentry;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Files.App.Services.Git;

internal sealed partial class LibGit2Service // : IVersionControl
{
	private const string GIT_RESOURCE_NAME = "Files:https://github.com";
	private const string GIT_RESOURCE_USERNAME = "Personal Access Token";
	private const string CLIENT_ID_SECRET = Constants.AutomatedWorkflowInjectionKeys.GitHubClientId;

	private const int END_OF_ORIGIN_PREFIX = 7;
	private const int MAX_NUMBER_OF_BRANCHES = 30;
	private const int MAX_RECENT_FETCH_REPOSITORIES = 64;
	private const int AUTOMATIC_FETCH_COOLDOWN_SECONDS = 30;

	private static readonly SemaphoreSlim GitOperationSemaphore = new(1, 1);
	private static readonly SemaphoreSlim GitFetchSemaphore = new(1, 1);
	private static readonly ConcurrentDictionary<string, long> _lastSuccessfulFetchTimestamps = new(StringComparer.OrdinalIgnoreCase);
	private static readonly FetchOptions _fetchOptions = new() { Prune = true };
	private static readonly PullOptions _pullOptions = new();
	private static readonly string _clientId = AppLifecycleHelper.AppEnvironment is AppEnvironment.Dev
		? string.Empty
		: CLIENT_ID_SECRET;

	private bool _isExecutingGitAction;
	private int _activeFetchOperations;
	private int _pendingFetchCompletionNotification;

	private static readonly StatusCenterViewModel StatusCenterViewModel = Ioc.Default.GetRequiredService<StatusCenterViewModel>();
	private static readonly ILogger _logger = Ioc.Default.GetRequiredService<ILogger<App>>();
	private static readonly IDialogService _dialogService = Ioc.Default.GetRequiredService<IDialogService>();

	public bool IsExecutingGitAction
	{
		get => _isExecutingGitAction;
		internal set // TODO: Make set method private again when move finished
		{
			if (_isExecutingGitAction != value)
			{
				_isExecutingGitAction = value;
				IsExecutingGitActionChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExecutingGitAction)));
			}
		}
	}

	public event PropertyChangedEventHandler? IsExecutingGitActionChanged;
	public event EventHandler? GitFetchCompleted;

	public string? GetGitRepositoryPath(string? path, string root)
	{
		if (string.IsNullOrEmpty(root))
			return null;

		if (root.EndsWith('\\'))
			root = root.Substring(0, root.Length - 1);

		if (string.IsNullOrWhiteSpace(path) ||
			path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
			path.Equals("Home", StringComparison.OrdinalIgnoreCase) ||
			ShellStorageFolder.IsShellPath(path))
		{
			return null;
		}

		try
		{
			var candidate = path;
			while (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase))
			{
				if (HasRepositoryMarker(candidate))
					return candidate;

				var parentDir = PathNormalization.GetParentDir(candidate);
				if (parentDir == candidate)
					break;

				candidate = parentDir;
			}

			return null;
		}
		catch (Exception ex) when (ex is LibGit2SharpException or EncoderFallbackException)
		{
			_logger.LogWarning(ex.Message);

			return null;
		}
	}

	public string GetOriginRepositoryName(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || !IsRepoValid(path))
			return string.Empty;

		using var repository = new Repository(path);
		var repositoryUrl = repository.Network.Remotes.FirstOrDefault()?.Url;

		if (string.IsNullOrEmpty(repositoryUrl))
			return string.Empty;

		var repositoryName = repositoryUrl.Split('/').Last();
		return repositoryName[..repositoryName.LastIndexOf(".git")];
	}

	public async Task<BranchItem[]> GetBranchNames(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return [];

		var (result, returnValue) = await DoGitOperationAsync<(GitOperationResult, BranchItem[])>(() =>
		{
			var branches = Array.Empty<BranchItem>();
			if (!IsRepoValid(path))
				return (GitOperationResult.GenericError, branches);

			var result = GitOperationResult.Success;
			try
			{
				using var repository = new Repository(path);

				branches = GetValidBranches(repository.Branches)
					.OrderByDescending(b => b.Tip?.Committer.When)
					.GroupBy(b => b.IsRemote)
					.SelectMany(g => g.Take(MAX_NUMBER_OF_BRANCHES))
					.OrderByDescending(b => b.IsCurrentRepositoryHead)
					.Select(b => new BranchItem(b.FriendlyName, b.IsCurrentRepositoryHead, b.IsRemote, TryGetTrackingDetails(b)?.AheadBy ?? 0, TryGetTrackingDetails(b)?.BehindBy ?? 0))
					.ToArray();
			}
			catch (Exception)
			{
				result = GitOperationResult.GenericError;
			}

			return (result, branches);
		});

		return returnValue;
	}

	public async Task<BranchItem?> GetRepositoryHead(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return null;

		var (_, returnValue) = await DoGitOperationAsync<(GitOperationResult, BranchItem?)>(() =>
		{
			BranchItem? head = null;
			try
			{
				using var repository = new Repository(path);
				var branch = GetValidBranches(repository.Branches).FirstOrDefault(b => b.IsCurrentRepositoryHead);
				if (branch is not null)
					head = new BranchItem(
						branch.FriendlyName,
						branch.IsCurrentRepositoryHead,
						branch.IsRemote,
						TryGetTrackingDetails(branch)?.AheadBy ?? 0,
						TryGetTrackingDetails(branch)?.BehindBy ?? 0
					);
			}
			catch
			{
				return (GitOperationResult.GenericError, head);
			}

			return (GitOperationResult.Success, head);
		}, true);

		return returnValue;
	}

	public async Task<bool> Checkout(string? repositoryPath, string? branch)
	{
		SentrySdk.Metrics.EmitCounter("Triggered git checkout", 1);

		if (string.IsNullOrWhiteSpace(repositoryPath) || !IsRepoValid(repositoryPath))
			return false;

		using var repository = new Repository(repositoryPath);
		var checkoutBranch = repository.Branches[branch];
		if (checkoutBranch is null)
			return false;

		var options = new CheckoutOptions();
		var isBringingChanges = false;

		IsExecutingGitAction = true;

		if (repository.Index.Conflicts.Any())
		{
			var dialog = DynamicDialogFactory.GetFor_GitMergeConflicts(checkoutBranch.FriendlyName, repository.Head.FriendlyName);
			await dialog.ShowAsync();

			var resolveConflictOption = (GitCheckoutOptions)dialog.ViewModel.AdditionalData;

			switch (resolveConflictOption)
			{
				case GitCheckoutOptions.None:
					IsExecutingGitAction = false;
					return false;
				case GitCheckoutOptions.AbortMerge:
					repository.Reset(ResetMode.Hard);
					break;
			}
		}
		else if (repository.RetrieveStatus().IsDirty)
		{
			var dialog = DynamicDialogFactory.GetFor_GitCheckoutConflicts(checkoutBranch.FriendlyName, repository.Head.FriendlyName);
			await dialog.ShowAsync();

			var resolveConflictOption = (GitCheckoutOptions)dialog.ViewModel.AdditionalData;

			switch (resolveConflictOption)
			{
				case GitCheckoutOptions.None:
					IsExecutingGitAction = false;
					return false;
				case GitCheckoutOptions.DiscardChanges:
					options.CheckoutModifiers = CheckoutModifiers.Force;
					break;
				case GitCheckoutOptions.BringChanges:
				case GitCheckoutOptions.StashChanges:
					var signature = repository.Config.BuildSignature(DateTimeOffset.Now);
					if (signature is null)
					{
						IsExecutingGitAction = false;
						return false;
					}

					repository.Stashes.Add(signature);

					isBringingChanges = resolveConflictOption is GitCheckoutOptions.BringChanges;
					break;
			}
		}

		var result = await DoGitOperationAsync<GitOperationResult>(() =>
		{
			try
			{
				if (checkoutBranch.IsRemote)
					CheckoutRemoteBranch(repository, checkoutBranch);
				else
					LibGit2Sharp.Commands.Checkout(repository, checkoutBranch, options);

				if (isBringingChanges)
				{
					var lastStashIndex = repository.Stashes.Count() - 1;
					repository.Stashes.Pop(lastStashIndex, new StashApplyOptions());
				}
			}
			catch (Exception)
			{
				return GitOperationResult.GenericError;
			}

			return GitOperationResult.Success;
		});

		IsExecutingGitAction = false;

		return result is GitOperationResult.Success;
	}

	public async Task CreateNewBranchAsync(string repositoryPath, string activeBranch)
	{
		SentrySdk.Metrics.EmitCounter("Triggered create git branch", 1);

		var viewModel = new AddBranchDialogViewModel(repositoryPath, activeBranch);
		var loadBranchesTask = viewModel.LoadBranches();
		var dialog = _dialogService.GetDialog(viewModel);

		await loadBranchesTask;
		var result = await dialog.TryShowAsync();

		if (result != DialogResult.Primary)
			return;

		using var repository = new Repository(repositoryPath);

		IsExecutingGitAction = true;

		if (repository.Head.FriendlyName.Equals(viewModel.NewBranchName) ||
			await Checkout(repositoryPath, viewModel.BasedOn))
		{
			repository.CreateBranch(viewModel.NewBranchName);

			if (viewModel.Checkout)
				await Checkout(repositoryPath, viewModel.NewBranchName);
		}

		IsExecutingGitAction = false;
	}

	public async Task DeleteBranchAsync(string? repositoryPath, string? activeBranch, string? branchToDelete)
	{
		SentrySdk.Metrics.EmitCounter("Triggered delete git branch", 1);

		if (string.IsNullOrWhiteSpace(repositoryPath) ||
			string.IsNullOrWhiteSpace(activeBranch) ||
			string.IsNullOrWhiteSpace(branchToDelete) ||
			activeBranch.Equals(branchToDelete, StringComparison.OrdinalIgnoreCase) ||
			!IsRepoValid(repositoryPath))
		{
			return;
		}

		var dialog = DynamicDialogFactory.GetFor_DeleteGitBranchConfirmation(branchToDelete);
		await dialog.TryShowAsync();
		if (!(dialog.ViewModel.AdditionalData as bool? ?? false))
			return;

		IsExecutingGitAction = true;

		await DoGitOperationAsync<GitOperationResult>(() =>
		{
			try
			{
				using var repository = new Repository(repositoryPath);
				repository.Branches.Remove(branchToDelete);
			}
			catch (Exception)
			{
				return GitOperationResult.GenericError;
			}

			return GitOperationResult.Success;
		});

		IsExecutingGitAction = false;
	}

	public bool ValidateBranchNameForRepository(string branchName, string repositoryPath)
	{
		if (string.IsNullOrEmpty(branchName) || !IsRepoValid(repositoryPath))
			return false;

		var nameValidator = RegexHelpers.GitBranchName();
		if (!nameValidator.IsMatch(branchName))
			return false;

		using var repository = new Repository(repositoryPath);
		return !repository.Branches.Any(branch =>
			branch.FriendlyName.Equals(branchName, StringComparison.OrdinalIgnoreCase));
	}

	public async Task FetchOriginAsync(string? repositoryPath, CancellationToken cancellationToken = default, bool force = false)
	{
		if (string.IsNullOrWhiteSpace(repositoryPath))
			return;
		if (!force && TryGetRecentFetchAge(repositoryPath, out var recentFetchAge))
		{
			LogSkippedAutomaticFetch(repositoryPath, recentFetchAge);
			return;
		}

		var fetchStartedTimestamp = Stopwatch.GetTimestamp();
		var activeFetchOperations = Interlocked.Increment(ref _activeFetchOperations);
		_logger.LogInformation(
			"Git fetch started for {RepositoryPath}; active operations: {ActiveOperations}.",
			LogPathHelper.GetPathIdentifier(repositoryPath),
			activeFetchOperations);
		if (activeFetchOperations == 1)
		{
			MainWindow.Instance.DispatcherQueue.TryEnqueue(() =>
			{
				IsExecutingGitAction = true;
			});
		}

		var completedSuccessfully = false;
		var fetchSemaphoreAcquired = false;
		var skippedByCooldown = false;
		try
		{
			await GitFetchSemaphore.WaitAsync(cancellationToken);
			fetchSemaphoreAcquired = true;
			if (!force && TryGetRecentFetchAge(repositoryPath, out recentFetchAge))
			{
				skippedByCooldown = true;
				LogSkippedAutomaticFetch(repositoryPath, recentFetchAge);
				return;
			}

			await Task.Run(() =>
			{
				using var repository = new Repository(repositoryPath);
				var signature = repository.Config.BuildSignature(DateTimeOffset.Now);

				var token = CredentialsHelpers.GetPassword(GIT_RESOURCE_NAME, GIT_RESOURCE_USERNAME);
				if (signature is not null && !string.IsNullOrWhiteSpace(token))
				{
					_fetchOptions.CredentialsProvider = (url, user, cred)
						=> new UsernamePasswordCredentials
						{
							Username = signature.Name,
							Password = token
						};
				}

				var result = GitOperationResult.Success;

				foreach (var remote in repository.Network.Remotes)
				{
					if (cancellationToken.IsCancellationRequested)
						return result;

					try
					{
						LibGit2Sharp.Commands.Fetch(
							repository,
							remote.Name,
							remote.FetchRefSpecs.Select(rs => rs.Specification),
							_fetchOptions,
							"git fetch updated a ref");
					}
					catch (Exception ex)
					{
						// An unreachable remote (e.g. a deleted fork answering 401) must not prevent fetching the remaining remotes
						_logger.LogWarning(ex, "Failed to fetch remote {RemoteName} in {RepositoryPath}", remote.Name, LogPathHelper.GetPathIdentifier(repositoryPath));

						if (IsAuthorizationException(ex))
							result = GitOperationResult.AuthorizationError;
					}
				}

				return result;
			});
			completedSuccessfully = true;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to fetch repository {RepositoryPath}", LogPathHelper.GetPathIdentifier(repositoryPath));
		}
		finally
		{
			if (fetchSemaphoreAcquired)
				GitFetchSemaphore.Release();

			if (completedSuccessfully && !cancellationToken.IsCancellationRequested)
			{
				RecordSuccessfulFetch(repositoryPath);
				Interlocked.Exchange(ref _pendingFetchCompletionNotification, 1);
			}

			activeFetchOperations = Interlocked.Decrement(ref _activeFetchOperations);
			if (!skippedByCooldown)
			{
				_logger.LogInformation(
					"Git fetch finished for {RepositoryPath} in {ElapsedMs:F1} ms (successful: {Successful}, canceled: {Canceled}, active operations: {ActiveOperations}).",
					LogPathHelper.GetPathIdentifier(repositoryPath),
					Stopwatch.GetElapsedTime(fetchStartedTimestamp).TotalMilliseconds,
					completedSuccessfully,
					cancellationToken.IsCancellationRequested,
					activeFetchOperations);
			}
			if (activeFetchOperations == 0)
			{
				var raiseCompletion = Interlocked.Exchange(ref _pendingFetchCompletionNotification, 0) == 1;
				MainWindow.Instance.DispatcherQueue.TryEnqueue(() =>
				{
					IsExecutingGitAction = false;
					if (raiseCompletion)
						GitFetchCompleted?.Invoke(null, EventArgs.Empty);
				});
			}
		}
	}

	private static bool TryGetRecentFetchAge(string repositoryPath, out TimeSpan age)
	{
		if (_lastSuccessfulFetchTimestamps.TryGetValue(repositoryPath, out var completedTimestamp))
		{
			age = Stopwatch.GetElapsedTime(completedTimestamp);
			return age < TimeSpan.FromSeconds(AUTOMATIC_FETCH_COOLDOWN_SECONDS);
		}

		age = default;
		return false;
	}

	private static void RecordSuccessfulFetch(string repositoryPath)
	{
		_lastSuccessfulFetchTimestamps[repositoryPath] = Stopwatch.GetTimestamp();
		if (_lastSuccessfulFetchTimestamps.Count <= MAX_RECENT_FETCH_REPOSITORIES)
			return;

		var oldestEntry = _lastSuccessfulFetchTimestamps.MinBy(entry => entry.Value);
		_lastSuccessfulFetchTimestamps.TryRemove(oldestEntry.Key, out _);
	}

	private static void LogSkippedAutomaticFetch(string repositoryPath, TimeSpan age)
	{
		_logger.LogInformation(
			"Git automatic fetch skipped for {RepositoryPath}; last successful fetch was {AgeMs:F1} ms ago.",
			LogPathHelper.GetPathIdentifier(repositoryPath),
			age.TotalMilliseconds);
	}

	private static bool IsRepoValid(string path)
	{
		return SafetyExtensions.IgnoreExceptions(() => Repository.IsValid(path));
	}

	private static bool HasRepositoryMarker(string path)
	{
		var gitMarkerPath = Path.Combine(path, ".git");
		return Directory.Exists(gitMarkerPath) ||
			File.Exists(gitMarkerPath) ||
			File.Exists(Path.Combine(path, "HEAD")) && Directory.Exists(Path.Combine(path, "objects"));
	}

	private static IEnumerable<Branch> GetValidBranches(BranchCollection branches)
	{
		foreach (var branch in branches)
		{
			try
			{
				_ = branch.IsCurrentRepositoryHead;
			}
			catch (LibGit2SharpException)
			{
				continue;
			}

			yield return branch;
		}
	}

	private static BranchTrackingDetails? TryGetTrackingDetails(Branch branch)
	{
		try
		{
			return branch.TrackingDetails;
		}
		catch (LibGit2SharpException)
		{
			return null;
		}
	}

	private static Commit? GetLastCommitForFile(Repository repository, string currentPath)
	{
		foreach (var currentCommit in repository.Commits)
		{
			var currentTreeEntry = currentCommit.Tree[currentPath];
			if (currentTreeEntry == null)
				return null;

			var parentCount = currentCommit.Parents.Take(2).Count();
			if (parentCount == 0)
			{
				return currentCommit;
			}
			else if (parentCount == 1)
			{
				var parentCommit = currentCommit.Parents.Single();

				// Does not consider renames
				var parentPath = currentPath;

				var parentTreeEntry = parentCommit.Tree[parentPath];

				if (parentTreeEntry == null ||
					parentTreeEntry.Target.Id != currentTreeEntry.Target.Id ||
					parentPath != currentPath)
				{
					return currentCommit;
				}
			}
		}

		return null;
	}

	private static void CheckoutRemoteBranch(Repository repository, Branch branch)
	{
		var uniqueName = branch.FriendlyName.Substring(END_OF_ORIGIN_PREFIX);

		// TODO: This is a temp fix to avoid an issue where Files would create many branches in a loop
		if (repository.Branches.Any(b => !b.IsRemote && b.FriendlyName == uniqueName))
			return;

		//var discriminator = 0;
		//while (repository.Branches.Any(b => !b.IsRemote && b.FriendlyName == uniqueName))
		//	uniqueName = $"{branch.FriendlyName}_{++discriminator}";

		var newBranch = repository.CreateBranch(uniqueName, branch.Tip);
		repository.Branches.Update(newBranch, b => b.TrackedBranch = branch.CanonicalName);

		LibGit2Sharp.Commands.Checkout(repository, newBranch);
	}

	private static bool IsAuthorizationException(Exception ex)
	{
		return
			ex.Message.Contains("status code: 401", StringComparison.OrdinalIgnoreCase) ||
			ex.Message.Contains("authentication replays", StringComparison.OrdinalIgnoreCase);
	}

	private static async Task<T?> DoGitOperationAsync<T>(Func<T> payload, bool useSemaphore = false)
	{
		if (useSemaphore)
			await GitOperationSemaphore.WaitAsync();

		try
		{
			return await Task.Run(payload);
		}
		finally
		{
			if (useSemaphore)
				GitOperationSemaphore.Release();
		}
	}

	[GeneratedRegex(@"^(?:https?:\/\/)?(?:www\.)?(?<domain>github|gitlab)\.com\/(?<user>[^\/]+)\/(?<repo>[^\/]+?)(?=\.git|\/|$)(?:\.git)?(?:\/)?", RegexOptions.IgnoreCase)]
	private static partial Regex GitHubRepositoryRegex();
}
