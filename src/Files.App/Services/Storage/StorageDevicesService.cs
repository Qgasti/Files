// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using OwlCore.Storage.System.IO;
using System.IO;
using Windows.Storage;

namespace Files.App.Services
{
	public sealed class RemovableDrivesService : IRemovableDrivesService
	{
		private static readonly TimeSpan DriveInitializationTimeout = TimeSpan.FromSeconds(5);

		public IStorageDeviceWatcher CreateWatcher()
		{
			return new WindowsStorageDeviceWatcher();
		}

		public async IAsyncEnumerable<IFolder> GetDrivesAsync()
		{
			var driveTasks = DriveInfo.GetDrives()
				.Select(InitializeDriveWithTimeoutAsync)
				.ToArray();
			var drives = await Task.WhenAll(driveTasks);

			foreach (var drive in drives)
			{
				if (drive is not null)
					yield return drive;
			}
		}

		private static async Task<IFolder?> InitializeDriveWithTimeoutAsync(DriveInfo drive)
		{
			var driveName = drive.Name;
			using var timeoutCts = new CancellationTokenSource();
			var initializationTask = Task.Run(() => InitializeDriveAsync(drive, timeoutCts.Token));

			if (await Task.WhenAny(initializationTask, Task.Delay(DriveInitializationTimeout)) != initializationTask)
			{
				timeoutCts.Cancel();
				_ = initializationTask.ContinueWith(
					task => App.Logger.LogDebug(task.Exception, "Drive initialization completed with an error after timing out for {Drive}.", driveName),
					CancellationToken.None,
					TaskContinuationOptions.OnlyOnFaulted,
					TaskScheduler.Default);
				App.Logger.LogWarning("Drive initialization timed out after {TimeoutSeconds} seconds for {Drive}; skipping it during startup.", DriveInitializationTimeout.TotalSeconds, driveName);
				return null;
			}

			return await initializationTask;
		}

		private static async Task<IFolder?> InitializeDriveAsync(DriveInfo drive, CancellationToken cancellationToken)
		{
			try
			{
				if (!drive.IsReady)
					return null;

				cancellationToken.ThrowIfCancellationRequested();
				var driveLabel = DriveHelpers.GetExtendedDriveLabel(drive);
				var pCloudDrivePath = App.AppModel.PCloudDrivePath;

				// Filter out cloud drives from the plain Drives section.
				if (driveLabel.Equals("Google Drive") || drive.Name.Equals(pCloudDrivePath))
					return null;

				var res = await FilesystemTasks.Wrap(() => StorageFolder.GetFolderFromPathAsync(drive.Name).AsTask(cancellationToken));
				if (!res)
				{
					App.Logger.LogWarning($"{res.ErrorCode}: Attempting to add the device, {drive.Name},"
						+ " failed at the StorageFolder initialization step. This device will be ignored.");
					return null;
				}

				cancellationToken.ThrowIfCancellationRequested();
				using var thumbnail = await DriveHelpers.GetThumbnailAsync(res.Result);
				cancellationToken.ThrowIfCancellationRequested();

				var type = DriveHelpers.GetDriveType(drive);
				var driveItem = await DriveItem.CreateFromPropertiesAsync(res.Result, drive.Name.TrimEnd('\\'), driveLabel, type, thumbnail);

				App.Logger.LogInformation($"Drive added: {driveItem.Path}, {driveItem.Type}");
				return driveItem;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return null;
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Drive initialization failed for {Drive}; skipping it during startup.", drive.Name);
				return null;
			}
		}

		public Task<IFolder?> GetPrimaryDriveAsync()
		{
			var cDrivePath = $@"{Constants.UserEnvironmentPaths.SystemDrivePath}\";
			if (!Directory.Exists(cDrivePath))
			{
				App.Logger.LogWarning($"Primary system drive '{cDrivePath}' could not be found.");
				return Task.FromResult<IFolder?>(null);
			}

			return Task.FromResult<IFolder?>(new SystemFolder(cDrivePath));
		}

		public async Task UpdateDrivePropertiesAsync(IFolder drive)
		{
			var rootModified = await FilesystemTasks.Wrap(() => StorageFolder.GetFolderFromPathAsync(drive.Id).AsTask());
			if (rootModified && drive is DriveItem matchingDriveEjected)
			{
				_ = MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() =>
				{
					matchingDriveEjected.Root = rootModified.Result;
					matchingDriveEjected.Text = rootModified.Result.DisplayName;
					return matchingDriveEjected.UpdatePropertiesAsync();
				});
			}
		}
	}
}
