// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.WinUI;
using Files.App.Controls;
using Files.App.UserControls.Selection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using SortDirection = Files.App.Data.Enums.SortDirection;

namespace Files.App.Views.Layouts
{
	/// <summary>
	/// Represents the browser page of Details View
	/// </summary>
	public sealed partial class DetailsLayoutPage : BaseGroupableLayoutPage
	{
		// Constants

		private const int TAG_TEXT_BLOCK = 1;

		// Fields

		private ListedItem? _nextItemToSelect;

		/// <summary>
		/// This reference is used to prevent unnecessary icon reloading by only reloading icons when their
		/// size changes, even if the layout size changes (since some layout sizes share the same icon size).
		/// </summary>
		private uint currentIconSize;

		private DispatcherQueueTimer? _autoFitColumnsTimer;
		private double _interactiveMoveStartWidth = double.NaN;
		private static readonly TimeSpan AutoFitColumnsDebounceInterval = TimeSpan.FromMilliseconds(250);

		// Properties

		protected override ListViewBase ListViewBase => FileList;
		protected override SemanticZoom RootZoom => RootGridZoom;

		public ColumnsViewModel ColumnsViewModel { get; } = new();

		private RelayCommand<string>? UpdateSortOptionsCommand { get; set; }

		public ScrollViewer? ContentScroller { get; private set; }

		private double maxWidthForRenameTextbox;
		public double MaxWidthForRenameTextbox
		{
			get => maxWidthForRenameTextbox;
			set
			{
				if (value != maxWidthForRenameTextbox)
				{
					maxWidthForRenameTextbox = value;
					NotifyPropertyChanged(nameof(MaxWidthForRenameTextbox));
				}
			}
		}

		/// <summary>
		/// Row height for items in the Details View
		/// </summary>
		public int RowHeight
		{
			get => LayoutSizeKindHelper.GetDetailsViewRowHeight((DetailsViewSizeKind)UserSettingsService.LayoutSettingsService.DetailsViewSize);
		}


		// Constructor

		public DetailsLayoutPage() : base()
		{
			InitializeComponent();
			DataContext = this;
			var selectionRectangle = RectangleSelection.Create(FileList, SelectionRectangle, FileList_SelectionChanged);
			selectionRectangle.SelectionStarted += SelectionRectangle_SelectionStarted;
			selectionRectangle.SelectionEnded += SelectionRectangle_SelectionEnded;

			UpdateSortOptionsCommand = new RelayCommand<string>(x =>
			{
				if (!Enum.TryParse<SortOption>(x, out var val))
					return;
				if (FolderSettings.DirectorySortOption == val)
				{
					FolderSettings.DirectorySortDirection = (SortDirection)(((int)FolderSettings.DirectorySortDirection + 1) % 2);
				}
				else
				{
					FolderSettings.DirectorySortOption = val;
					FolderSettings.DirectorySortDirection = SortDirection.Ascending;
				}
			});
		}

		// Methods

		protected override void ItemManipulationModel_ScrollIntoViewInvoked(object? sender, ListedItem e)
		{
			FileList.ScrollIntoView(e);
			ContentScroller?.ChangeView(null, FileList.Items.IndexOf(e) * RowHeight, null, true); // Scroll to index * item height
		}

		protected override void ItemManipulationModel_ScrollToTopInvoked(object? sender, EventArgs e)
		{
			ContentScroller?.ChangeView(null, 0, null, true);
		}

		protected override void ItemManipulationModel_FocusSelectedItemsInvoked(object? sender, EventArgs e)
		{
			if (SelectedItems?.Any() ?? false)
			{
				FileList.ScrollIntoView(SelectedItems.Last());
				ContentScroller?.ChangeView(null, FileList.Items.IndexOf(SelectedItems.Last()) * RowHeight, null, false);
				(FileList.ContainerFromItem(SelectedItems.Last()) as ListViewItem)?.Focus(FocusState.Keyboard);
			}
		}

		protected override void ItemManipulationModel_AddSelectedItemInvoked(object? sender, ListedItem e)
		{
			if (NextRenameIndex != 0)
			{
				_nextItemToSelect = e;
				FileList.LayoutUpdated += FileList_LayoutUpdated;
			}
			else if (FileList?.Items.Contains(e) ?? false)
				FileList!.SelectedItems.Add(e);
		}

		protected override void ItemManipulationModel_RemoveSelectedItemInvoked(object? sender, ListedItem e)
		{
			if (FileList?.Items.Contains(e) ?? false)
				FileList.SelectedItems.Remove(e);
		}

		protected override void OnNavigatedTo(NavigationEventArgs eventArgs)
		{
			if (eventArgs.Parameter is NavigationArguments navArgs)
				navArgs.FocusOnNavigation = true;

			base.OnNavigatedTo(eventArgs);

			currentIconSize = LayoutSizeKindHelper.GetIconSize(FolderLayoutModes.DetailsView);

			if (FolderSettings?.ColumnsViewModel is not null)
			{
				// Don't assign the columns view model directly, instead update each property individually using the Update method.
				// This is done to workaround a bug where CsWinRT doesn't properly track the memory of the object so that
				// an invalid memory access can occur when the object is moved.
				// See https://github.com/microsoft/CsWinRT/issues/1834.
				ColumnsViewModel.DateCreatedColumn.Update(FolderSettings.ColumnsViewModel.DateCreatedColumn);
				ColumnsViewModel.DateDeletedColumn.Update(FolderSettings.ColumnsViewModel.DateDeletedColumn);
				ColumnsViewModel.DateModifiedColumn.Update(FolderSettings.ColumnsViewModel.DateModifiedColumn);
				ColumnsViewModel.IconColumn.Update(FolderSettings.ColumnsViewModel.IconColumn);
				ColumnsViewModel.ItemTypeColumn.Update(FolderSettings.ColumnsViewModel.ItemTypeColumn);
				ColumnsViewModel.NameColumn.Update(FolderSettings.ColumnsViewModel.NameColumn);
				ColumnsViewModel.PathColumn.Update(FolderSettings.ColumnsViewModel.PathColumn);
				ColumnsViewModel.OriginalPathColumn.Update(FolderSettings.ColumnsViewModel.OriginalPathColumn);
				ColumnsViewModel.SizeColumn.Update(FolderSettings.ColumnsViewModel.SizeColumn);
				ColumnsViewModel.StatusColumn.Update(FolderSettings.ColumnsViewModel.StatusColumn);
				ColumnsViewModel.TagColumn.Update(FolderSettings.ColumnsViewModel.TagColumn);
				ColumnsViewModel.GitStatusColumn.Update(FolderSettings.ColumnsViewModel.GitStatusColumn);
				ColumnsViewModel.GitLastCommitDateColumn.Update(FolderSettings.ColumnsViewModel.GitLastCommitDateColumn);
				ColumnsViewModel.GitLastCommitMessageColumn.Update(FolderSettings.ColumnsViewModel.GitLastCommitMessageColumn);
				ColumnsViewModel.GitCommitAuthorColumn.Update(FolderSettings.ColumnsViewModel.GitCommitAuthorColumn);
				ColumnsViewModel.GitLastCommitShaColumn.Update(FolderSettings.ColumnsViewModel.GitLastCommitShaColumn);
			}

			ParentShellPageInstance.ShellViewModel.EnabledGitProperties = GetEnabledGitProperties(ColumnsViewModel);

			FolderSettings.LayoutModeChangeRequested += FolderSettings_LayoutModeChangeRequested;
			FolderSettings.SortDirectionPreferenceUpdated += FolderSettings_SortDirectionPreferenceUpdated;
			FolderSettings.SortOptionPreferenceUpdated += FolderSettings_SortOptionPreferenceUpdated;
			ParentShellPageInstance.ShellViewModel.PageTypeUpdated += FilesystemViewModel_PageTypeUpdated;
			ParentShellPageInstance.ShellViewModel.ItemLoadStatusChanged += ShellViewModel_ItemLoadStatusChanged;
			UserSettingsService.LayoutSettingsService.PropertyChanged += LayoutSettingsService_PropertyChanged;
			FileList.Items.VectorChanged += FileListItems_VectorChanged;
			MainWindow.Instance.InteractiveMoveStarted += MainWindow_InteractiveMoveStarted;
			MainWindow.Instance.InteractiveMoveCompleted += MainWindow_InteractiveMoveCompleted;

			var parameters = (NavigationArguments)eventArgs.Parameter;
			if (parameters.IsLayoutSwitch)
				_ = ReloadItemIconsAsync();

			FilesystemViewModel_PageTypeUpdated(null, new PageTypeUpdatedEventArgs()
			{
				IsTypeCloudDrive = InstanceViewModel?.IsPageTypeCloudDrive ?? false,
				IsTypeRecycleBin = InstanceViewModel?.IsPageTypeRecycleBin ?? false,
				IsTypeGitRepository = InstanceViewModel?.IsGitRepository ?? false,
				IsTypeSearchResults = InstanceViewModel?.IsPageTypeSearchResults ?? false
			});

			RootGrid_SizeChanged(null, null);

			SetItemContainerStyle();
		}

		protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
		{
			base.OnNavigatingFrom(e);
			FolderSettings.LayoutModeChangeRequested -= FolderSettings_LayoutModeChangeRequested;
			FolderSettings.SortDirectionPreferenceUpdated -= FolderSettings_SortDirectionPreferenceUpdated;
			FolderSettings.SortOptionPreferenceUpdated -= FolderSettings_SortOptionPreferenceUpdated;
			ParentShellPageInstance.ShellViewModel.PageTypeUpdated -= FilesystemViewModel_PageTypeUpdated;
			ParentShellPageInstance.ShellViewModel.ItemLoadStatusChanged -= ShellViewModel_ItemLoadStatusChanged;
			UserSettingsService.LayoutSettingsService.PropertyChanged -= LayoutSettingsService_PropertyChanged;
			FileList.Items.VectorChanged -= FileListItems_VectorChanged;
			MainWindow.Instance.InteractiveMoveStarted -= MainWindow_InteractiveMoveStarted;
			MainWindow.Instance.InteractiveMoveCompleted -= MainWindow_InteractiveMoveCompleted;
			_autoFitColumnsTimer?.Stop();
		}

		public override void Dispose()
		{
			Bindings.StopTracking();
			FolderSettings.LayoutModeChangeRequested -= FolderSettings_LayoutModeChangeRequested;
			FolderSettings.SortDirectionPreferenceUpdated -= FolderSettings_SortDirectionPreferenceUpdated;
			FolderSettings.SortOptionPreferenceUpdated -= FolderSettings_SortOptionPreferenceUpdated;
			ParentShellPageInstance.ShellViewModel.PageTypeUpdated -= FilesystemViewModel_PageTypeUpdated;
			ParentShellPageInstance.ShellViewModel.ItemLoadStatusChanged -= ShellViewModel_ItemLoadStatusChanged;
			UserSettingsService.LayoutSettingsService.PropertyChanged -= LayoutSettingsService_PropertyChanged;
			FileList.Items.VectorChanged -= FileListItems_VectorChanged;
			MainWindow.Instance.InteractiveMoveStarted -= MainWindow_InteractiveMoveStarted;
			MainWindow.Instance.InteractiveMoveCompleted -= MainWindow_InteractiveMoveCompleted;
			_autoFitColumnsTimer?.Stop();
			base.Dispose();
		}

		private void LayoutSettingsService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(ILayoutSettingsService.AutoSizeColumnsInDetailsLayout))
			{
				AutoFitColumnsIfEnabled();
			}
			else if (e.PropertyName == nameof(ILayoutSettingsService.DetailsViewSize))
			{
				// Get current scroll position
				var previousOffset = ContentScroller?.VerticalOffset;

				NotifyPropertyChanged(nameof(RowHeight));

				// Update the container style to match the item size
				SetItemContainerStyle();

				// Restore correct scroll position
				ContentScroller?.ChangeView(null, previousOffset, null);

				// Check if icons need to be reloaded
				var newIconSize = LayoutSizeKindHelper.GetIconSize(FolderLayoutModes.DetailsView);
				if (newIconSize != currentIconSize)
				{
					currentIconSize = newIconSize;
					_ = ReloadItemIconsAsync();
				}
			}
			else
			{
				var settings = sender as ILayoutSettingsService;
				var isDefaultPath = FolderSettings?.IsPathUsingDefaultLayout(ParentShellPageInstance?.ShellViewModel.CurrentFolder?.ItemPath);
				if (settings is not null && (isDefaultPath ?? true))
				{
					switch (e.PropertyName)
					{
						case nameof(ILayoutSettingsService.ShowFileTagColumn):
							ColumnsViewModel.TagColumn.UserCollapsed = !settings.ShowFileTagColumn;
							break;
						case nameof(ILayoutSettingsService.ShowSizeColumn):
							ColumnsViewModel.SizeColumn.UserCollapsed = !settings.ShowSizeColumn;
							break;
						case nameof(ILayoutSettingsService.ShowTypeColumn):
							ColumnsViewModel.ItemTypeColumn.UserCollapsed = !settings.ShowTypeColumn;
							break;
						case nameof(ILayoutSettingsService.ShowDateCreatedColumn):
							ColumnsViewModel.DateCreatedColumn.UserCollapsed = !settings.ShowDateCreatedColumn;
							break;
						case nameof(ILayoutSettingsService.ShowDateColumn):
							ColumnsViewModel.DateModifiedColumn.UserCollapsed = !settings.ShowDateColumn;
							break;
					}
				}
			}
		}

		/// <summary>
		/// Sets the item size and spacing
		/// </summary>
		private void SetItemContainerStyle()
		{
			if (UserSettingsService.LayoutSettingsService.DetailsViewSize == DetailsViewSizeKind.Compact)
			{
				// Toggle style to force item size to update
				FileList.ItemContainerStyle = RegularItemContainerStyle;

				// Set correct style
				FileList.ItemContainerStyle = CompactItemContainerStyle;
			}
			else
			{
				// Toggle style to force item size to update
				FileList.ItemContainerStyle = CompactItemContainerStyle;

				// Set correct style
				FileList.ItemContainerStyle = RegularItemContainerStyle;
			}

			// Set the width of the icon column. The value is increased by 4px to account for icon overlays.
			ColumnsViewModel.IconColumn.UserLength = new GridLength(LayoutSizeKindHelper.GetIconSize(FolderLayoutModes.DetailsView) + 4);

			// Compact rows use a -2px ItemContainer margin, so the header checkbox needs an extra
			// left offset to stay aligned with row checkboxes.
			var leftOffset = UserSettingsService.LayoutSettingsService.DetailsViewSize == DetailsViewSizeKind.Compact ? -4 : 0;
			SelectAllCheckbox.Margin = new Thickness(leftOffset, 14, 0, 0);
		}

		private void FileList_LayoutUpdated(object? sender, object e)
		{
			FileList.LayoutUpdated -= FileList_LayoutUpdated;
			TryStartRenameNextItem(_nextItemToSelect!);
			_nextItemToSelect = null;
		}

		private void FolderSettings_SortOptionPreferenceUpdated(object? sender, SortOption e)
		{
			UpdateSortIndicator();
		}

		private void FolderSettings_SortDirectionPreferenceUpdated(object? sender, SortDirection e)
		{
			UpdateSortIndicator();
		}

		private void UpdateSortIndicator()
		{
			NameHeader.ColumnSortOption = FolderSettings.DirectorySortOption == SortOption.Name ? FolderSettings.DirectorySortDirection : null;
			TagHeader.ColumnSortOption = FolderSettings.DirectorySortOption == SortOption.FileTag ? FolderSettings.DirectorySortDirection : null;
			PathHeader.ColumnSortOption = FolderSettings.DirectorySortOption == SortOption.Path ? FolderSettings.DirectorySortDirection : null;
			OriginalPathHeader.ColumnSortOption = FolderSettings.DirectorySortOption == SortOption.OriginalFolder ? FolderSettings.DirectorySortDirection : null;
			DateDeletedHeader.ColumnSortOption = FolderSettings.DirectorySortOption == SortOption.DateDeleted ? FolderSettings.DirectorySortDirection : null;
			DateModifiedHeader.ColumnSortOption = FolderSettings.DirectorySortOption == SortOption.DateModified ? FolderSettings.DirectorySortDirection : null;
			DateCreatedHeader.ColumnSortOption = FolderSettings.DirectorySortOption == SortOption.DateCreated ? FolderSettings.DirectorySortDirection : null;
			FileTypeHeader.ColumnSortOption = FolderSettings.DirectorySortOption == SortOption.FileType ? FolderSettings.DirectorySortDirection : null;
			ItemSizeHeader.ColumnSortOption = FolderSettings.DirectorySortOption == SortOption.Size ? FolderSettings.DirectorySortDirection : null;
			SyncStatusHeader.ColumnSortOption = FolderSettings.DirectorySortOption == SortOption.SyncStatus ? FolderSettings.DirectorySortDirection : null;
		}

		private void FilesystemViewModel_PageTypeUpdated(object? sender, PageTypeUpdatedEventArgs e)
		{
			if (e.IsTypeRecycleBin)
			{
				ColumnsViewModel.OriginalPathColumn.Show();
				ColumnsViewModel.DateDeletedColumn.Show();
			}
			else
			{
				ColumnsViewModel.OriginalPathColumn.Hide();
				ColumnsViewModel.DateDeletedColumn.Hide();
			}

			if (e.IsTypeCloudDrive)
				ColumnsViewModel.StatusColumn.Show();
			else
				ColumnsViewModel.StatusColumn.Hide();

			if (e.IsTypeGitRepository && !e.IsTypeSearchResults)
			{
				ColumnsViewModel.GitCommitAuthorColumn.Show();
				ColumnsViewModel.GitLastCommitDateColumn.Show();
				ColumnsViewModel.GitLastCommitMessageColumn.Show();
				ColumnsViewModel.GitLastCommitShaColumn.Show();
				ColumnsViewModel.GitStatusColumn.Show();
			}
			else
			{
				ColumnsViewModel.GitCommitAuthorColumn.Hide();
				ColumnsViewModel.GitLastCommitDateColumn.Hide();
				ColumnsViewModel.GitLastCommitMessageColumn.Hide();
				ColumnsViewModel.GitLastCommitShaColumn.Hide();
				ColumnsViewModel.GitStatusColumn.Hide();
			}

			if (e.IsTypeSearchResults)
				ColumnsViewModel.PathColumn.Show();
			else
				ColumnsViewModel.PathColumn.Hide();

			UpdateSortIndicator();
		}

		private void FolderSettings_LayoutModeChangeRequested(object? sender, LayoutModeEventArgs e)
		{

		}

		protected override void OnSelectionChanged(SelectionChangedEventArgs e)
		{
			foreach (var item in e.AddedItems)
				SetCheckboxSelectionState(item);

			foreach (var item in e.RemovedItems)
				SetCheckboxSelectionState(item);

			UpdateSelectAllCheckboxState();
		}

		private bool _suppressSelectAllCheckboxEvents;

		private void SelectAllCheckbox_Checked(object sender, RoutedEventArgs e)
		{
			if (_suppressSelectAllCheckboxEvents)
				return;

			ItemManipulationModel.SelectAllItems();
		}

		private void SelectAllCheckbox_Unchecked(object sender, RoutedEventArgs e)
		{
			if (_suppressSelectAllCheckboxEvents)
				return;

			ItemManipulationModel.ClearSelection();
		}

		private void UpdateSelectAllCheckboxState()
		{
			var selectedCount = FileList.SelectedItems.Count;
			bool? newState = selectedCount == 0
				? false
				: selectedCount == FileList.Items.Count ? true : null;

			if (SelectAllCheckbox.IsChecked == newState)
				return;

			_suppressSelectAllCheckboxEvents = true;
			try
			{
				SelectAllCheckbox.IsChecked = newState;
			}
			finally
			{
				_suppressSelectAllCheckboxEvents = false;
			}
		}

		override public void StartRenameItem()
		{
			StartRenameItem("ItemNameTextBox");

			if (FileList.ContainerFromItem(RenamingItem) is not ListViewItem listViewItem)
				return;

			var textBox = listViewItem.FindDescendant("ItemNameTextBox") as TextBox;
			if (textBox is null || textBox.FindParent<Grid>() is null)
				return;

			Grid.SetColumnSpan(textBox.FindParent<Grid>(), 8);
		}

		private void ItemNameTextBox_BeforeTextChanging(TextBox textBox, TextBoxBeforeTextChangingEventArgs args)
		{
			if (IsRenamingItem)
			{
				_ = ValidateItemNameInputTextAsync(textBox, args, (showError) =>
				{
					FileNameTeachingTip.Visibility = showError ? Visibility.Visible : Visibility.Collapsed;
					FileNameTeachingTip.IsOpen = showError;
				});
			}
		}

		protected override void EndRename(TextBox textBox)
		{
			if (textBox is not null && textBox.FindParent<Grid>() is FrameworkElement parent)
				Grid.SetColumnSpan(parent, 1);

			ListViewItem? listViewItem = FileList.ContainerFromItem(RenamingItem) as ListViewItem;

			if (textBox is null || listViewItem is null)
			{
				// Navigating away, do nothing
			}
			else
			{
				TextBlock? textBlock = listViewItem.FindDescendant("ItemName") as TextBlock;
				textBox.Visibility = Visibility.Collapsed;
				textBlock!.Visibility = Visibility.Visible;
			}

			// Unsubscribe from events
			if (textBox is not null)
			{
				textBox!.LostFocus -= RenameTextBox_LostFocus;
				textBox.KeyDown -= RenameTextBox_KeyDown;
			}

			FileNameTeachingTip.IsOpen = false;
			IsRenamingItem = false;

			// Re-focus selected list item
			listViewItem?.Focus(FocusState.Programmatic);
		}

		protected override async void FileList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (ParentShellPageInstance is null || IsRenamingItem)
				return;

			var ctrlPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
			var shiftPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
			var focusedElement = (FrameworkElement)FocusManager.GetFocusedElement(MainWindow.Instance.Content.XamlRoot);
			var isHeaderFocused = DependencyObjectHelpers.FindParent<DataGridHeader>(focusedElement) is not null;
			var isFooterFocused = focusedElement is HyperlinkButton;

			if (ctrlPressed && e.Key is VirtualKey.A)
			{
				e.Handled = true;

				var commands = Ioc.Default.GetRequiredService<ICommandManager>();
				var hotKey = new HotKey(Keys.A, KeyModifiers.Ctrl);

				await commands[hotKey].ExecuteAsync();
			}
			else if (e.Key == VirtualKey.Enter && !e.KeyStatus.IsMenuKeyDown)
			{
				e.Handled = true;

				if (ctrlPressed && !shiftPressed)
				{
					var folders = SelectedItems?.Where(file => file.PrimaryItemAttribute == StorageItemTypes.Folder);
					if (folders?.Any() ?? false)
					{
						foreach (ListedItem folder in folders)
							await NavigationHelpers.OpenPathInNewTab(folder.ItemPath);
					}
				}
				else if (ctrlPressed && shiftPressed)
				{
					var selectedFolders = SelectedItems?.Where(item => item.PrimaryItemAttribute == StorageItemTypes.Folder);
					if (selectedFolders?.Count() == 1)
					{
						NavigationHelpers.OpenInSecondaryPane(ParentShellPageInstance, selectedFolders.First());
					}
				}
				else if (!ctrlPressed && !shiftPressed)
				{
					if (SelectedItems?.Any() ?? false)
					{
						foreach (var selectedItem in SelectedItems)
							await OpenItem(selectedItem);
					}
				}
			}
			else if (e.Key == VirtualKey.Enter && e.KeyStatus.IsMenuKeyDown)
			{
				FilePropertiesHelpers.OpenPropertiesWindow(ParentShellPageInstance);
				e.Handled = true;
			}
			else if (e.Key == VirtualKey.Space)
			{
				e.Handled = true;
			}
			else if (e.KeyStatus.IsMenuKeyDown && (e.Key == VirtualKey.Left || e.Key == VirtualKey.Right || e.Key == VirtualKey.Up))
			{
				// Unfocus the GridView so keyboard shortcut can be handled
				Focus(FocusState.Pointer);
			}
			else if (e.KeyStatus.IsMenuKeyDown && shiftPressed && e.Key == VirtualKey.Add)
			{
				// Unfocus the ListView so keyboard shortcut can be handled (alt + shift + "+")
				Focus(FocusState.Pointer);
			}
			else if (e.Key == VirtualKey.Down)
			{
				// Focus the first item in the file list if Header header has focus,
				// or if there is only one item in the file list (#13774)
				if (isHeaderFocused || FileList.Items.Count == 1)
				{
					var selectIndex = FileList.SelectedIndex < 0 ? 0 : FileList.SelectedIndex;
					if (FileList.ContainerFromIndex(selectIndex) is ListViewItem item)
					{
						// Focus selected list item or first item
						item.Focus(FocusState.Programmatic);
						if (!IsItemSelected)
							FileList.SelectedIndex = 0;
						e.Handled = true;
					}
				}
			}
		}

		protected override bool CanGetItemFromElement(object element)
			=> element is ListViewItem;

		private async Task ReloadItemIconsAsync()
		{
			if (ParentShellPageInstance is null)
				return;

			ParentShellPageInstance.ShellViewModel.CancelExtendedPropertiesLoading();
			var filesAndFolders = ParentShellPageInstance.ShellViewModel.FilesAndFolders.ToList();

			await Task.WhenAll(filesAndFolders.Select(listedItem =>
			{
				listedItem.ItemPropertiesInitialized = false;
				listedItem.ThumbnailPropertiesInitialized = false;
				if (FileList.ContainerFromItem(listedItem) is not null)
					return ParentShellPageInstance.ShellViewModel.LoadExtendedItemPropertiesAsync(listedItem);
				else
					return Task.CompletedTask;
			}));

			if (ParentShellPageInstance.ShellViewModel.EnabledGitProperties is not GitProperties.None)
			{
				await Task.WhenAll(filesAndFolders.Select(item =>
				{
					if (item is IGitItem gitItem)
						return ParentShellPageInstance.ShellViewModel.LoadGitPropertiesAsync(gitItem);

					return Task.CompletedTask;
				}));
			}
		}

		private async void FileList_ItemTapped(object sender, TappedRoutedEventArgs e)
		{
			var clickedItem = e.OriginalSource as FrameworkElement;
			var ctrlPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
			var shiftPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
			var item = clickedItem?.DataContext as ListedItem;
			if (item is null)
			{
				if (IsRenamingItem && RenamingItem is not null)
				{
					ListViewItem? listViewItem = FileList.ContainerFromItem(RenamingItem) as ListViewItem;
					if (listViewItem is not null)
					{
						var textBox = listViewItem.FindDescendant("ItemNameTextBox") as TextBox;
						if (textBox is not null)
							await CommitRenameAsync(textBox);
					}
				}
				else
				{
					// Clear selection when clicking empty area via touch
					// https://github.com/files-community/Files/issues/15051
					if (e.PointerDeviceType == PointerDeviceType.Touch)
						ItemManipulationModel.ClearSelection();
				}
				return;
			}

			// Skip code if the control or shift key is pressed or if the user is using multiselect
			if
			(
				ctrlPressed ||
				shiftPressed ||
				clickedItem is Microsoft.UI.Xaml.Shapes.Rectangle
			)
			{
				e.Handled = true;
				return;
			}

			// Check if the setting to open items with a single click is turned on
			if ((item.PrimaryItemAttribute is StorageItemTypes.File && UserSettingsService.FoldersSettingsService.OpenFilesWithSingleClick.ShouldOpenWithSingleClick(e.PointerDeviceType)) ||
				(item.PrimaryItemAttribute is StorageItemTypes.Folder && UserSettingsService.FoldersSettingsService.OpenFoldersWithSingleClick.ShouldOpenWithSingleClick(e.PointerDeviceType)))
			{
				ResetRenameDoubleClick();
				await Commands.OpenItem.ExecuteAsync();
			}
			else
			{
				if (clickedItem is TextBlock && ((TextBlock)clickedItem).Name == "ItemName")
				{
					CheckRenameDoubleClick(clickedItem.DataContext);
				}
				else if (IsRenamingItem && RenamingItem is not null)
				{
					ListViewItem? listViewItem = FileList.ContainerFromItem(RenamingItem) as ListViewItem;
					if (listViewItem is not null)
					{
						var textBox = listViewItem.FindDescendant("ItemNameTextBox") as TextBox;
						if (textBox is not null)
							await CommitRenameAsync(textBox);
					}
				}
			}
		}

		private async Task OpenItem(ListedItem item)
		{
			if (!Commands.OpenItem.IsExecutable)
			{
				// Fallback if the command is not executable. It occurs only when search is performed from the columns view.
				var itemType = item.PrimaryItemAttribute == StorageItemTypes.Folder ? FilesystemItemType.Directory : FilesystemItemType.File;
				await NavigationHelpers.OpenPath(item.ItemPath, ParentShellPageInstance, itemType);
			}
			else
			{
				await Commands.OpenItem.ExecuteAsync();
			}
		}

		private async void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
		{
			// Skip opening selected items if the double tap doesn't capture an item
			var originalElement = e.OriginalSource as FrameworkElement;
			var dataContext = originalElement?.DataContext;

			// Try to get the item from DataContext or from sender (ListView)
			ListedItem? item = dataContext as ListedItem;
			if (item == null && sender is ListView listView && listView.SelectedItem is ListedItem selectedItem)
				item = selectedItem;

			if (item != null && item.PrimaryItemAttribute == StorageItemTypes.File && !UserSettingsService.FoldersSettingsService.OpenFilesWithSingleClick.ShouldOpenWithSingleClick(e.PointerDeviceType))
				await OpenItem(item);
			else if (item != null && item.PrimaryItemAttribute == StorageItemTypes.Folder && !UserSettingsService.FoldersSettingsService.OpenFoldersWithSingleClick.ShouldOpenWithSingleClick(e.PointerDeviceType))
				await OpenItem(item);
			else if (item == null && UserSettingsService.FoldersSettingsService.DoubleClickToGoUp)
				await Commands.NavigateUp.ExecuteAsync();

			ResetRenameDoubleClick();
		}

		private void StackPanel_Loaded(object sender, RoutedEventArgs e)
		{
			// This is the best way I could find to set the context flyout, as doing it in the styles isn't possible
			// because you can't use bindings in the setters
			DependencyObject item = VisualTreeHelper.GetParent(sender as StackPanel);
			while (item is not ListViewItem)
				item = VisualTreeHelper.GetParent(item);
			if (item is ListViewItem itemContainer)
				itemContainer.ContextFlyout = ItemContextMenuFlyout;
		}

		private void Grid_PointerPressed(object sender, PointerRoutedEventArgs e)
		{
			// This prevents the drag selection rectangle from appearing when resizing the columns
			e.Handled = true;
		}

		private void GridSplitter_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
		{
			UpdateColumnLayout();
		}

		private void GridSplitter_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key == VirtualKey.Left || e.Key == VirtualKey.Right)
			{
				UpdateColumnLayout();
				FolderSettings.ColumnsViewModel = ColumnsViewModel;
			}
		}

		private void UpdateColumnLayout()
		{
			ColumnsViewModel.IconColumn.UserLength = Column2.Width;
			ColumnsViewModel.NameColumn.UserLength = Column3.Width;

			// Git
			ColumnsViewModel.GitStatusColumn.UserLength = GitStatusColumnDefinition.Width;
			ColumnsViewModel.GitLastCommitDateColumn.UserLength = GitLastCommitDateColumnDefinition.Width;
			ColumnsViewModel.GitLastCommitMessageColumn.UserLength = GitLastCommitMessageColumnDefinition.Width;
			ColumnsViewModel.GitCommitAuthorColumn.UserLength = GitCommitAuthorColumnDefinition.Width;
			ColumnsViewModel.GitLastCommitShaColumn.UserLength = GitLastCommitShaColumnDefinition.Width;

			ColumnsViewModel.TagColumn.UserLength = Column4.Width;
			ColumnsViewModel.PathColumn.UserLength = Column5.Width;
			ColumnsViewModel.OriginalPathColumn.UserLength = Column6.Width;
			ColumnsViewModel.DateDeletedColumn.UserLength = Column7.Width;
			ColumnsViewModel.DateModifiedColumn.UserLength = Column8.Width;
			ColumnsViewModel.DateCreatedColumn.UserLength = Column9.Width;
			ColumnsViewModel.ItemTypeColumn.UserLength = Column10.Width;
			ColumnsViewModel.SizeColumn.UserLength = Column11.Width;
			ColumnsViewModel.StatusColumn.UserLength = Column12.Width;
		}

		private void RootGrid_SizeChanged(object? sender, SizeChangedEventArgs? e)
		{
			MaxWidthForRenameTextbox = Math.Max(0, RootGrid.ActualWidth - 80);
			AutoFitColumnsIfEnabled();
		}

		private void MainWindow_InteractiveMoveStarted(object? sender, EventArgs e)
		{
			_interactiveMoveStartWidth = RootGrid.ActualWidth;
		}

		private void MainWindow_InteractiveMoveCompleted(object? sender, EventArgs e)
		{
			var currentWidth = RootGrid.ActualWidth;
			var widthChanged = double.IsFinite(_interactiveMoveStartWidth) &&
				double.IsFinite(currentWidth) &&
				Math.Abs(currentWidth - _interactiveMoveStartWidth) > 0.5;
			_interactiveMoveStartWidth = double.NaN;

			if (widthChanged)
				AutoFitColumnsIfEnabled();
		}

		private void GridSplitter_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
		{
			this.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast));
		}

		private void GridSplitter_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
		{
			FolderSettings.ColumnsViewModel = ColumnsViewModel;
			this.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.Arrow));
		}

		private void GridSplitter_Loaded(object sender, RoutedEventArgs e)
		{
			(sender as UIElement)?.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast));
		}

		private void ToggleMenuFlyoutItem_Click(object sender, RoutedEventArgs e)
		{
			FolderSettings.ColumnsViewModel = ColumnsViewModel;
			ParentShellPageInstance.ShellViewModel.EnabledGitProperties = GetEnabledGitProperties(ColumnsViewModel);
		}

		private void GridSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
		{
			var columnToResize = Grid.GetColumn(sender as Files.App.Controls.GridSplitter) / 2 + 1;
			ResizeColumnToFit(columnToResize);

			e.Handled = true;
		}

		private void SizeAllColumnsToFit_Click(object sender, RoutedEventArgs e)
		{
			_ = Commands.AutoFitColumns.ExecuteAsync();
		}

		public void AutoFitColumns(bool fillAvailableWidth = false)
		{
			var autoFitStartedTimestamp = Stopwatch.GetTimestamp();
			var items = FileList.Items.OfType<ListedItem>().ToList();
			if (items.Count == 0)
				return;
			var previousColumnWidths = CaptureResizableColumnWidths();
			var snapshotCompletedTimestamp = Stopwatch.GetTimestamp();
			var maxItemLengths = CalculateMaxItemLengths(items);
			var aggregationCompletedTimestamp = Stopwatch.GetTimestamp();
			var columnEstimates = MeasureColumnEstimates(maxItemLengths);
			var measurementCompletedTimestamp = Stopwatch.GetTimestamp();

			// Column one is the fixed icon and selection area; resizable content columns start at two.
			int totalColumnCount = ColumnsViewModel.GetType().GetProperties().Count(prop => prop.PropertyType == typeof(DetailsLayoutColumnItem));
			for (int columnIndex = 2; columnIndex <= totalColumnCount; columnIndex++)
				ApplyColumnWidth(columnIndex, maxItemLengths[columnIndex], columnEstimates[columnIndex], false);

			if (fillAvailableWidth)
				FitColumnsToAvailableWidth();
			var columnsAppliedTimestamp = Stopwatch.GetTimestamp();

			var columnsChanged = !previousColumnWidths.SequenceEqual(CaptureResizableColumnWidths());
			if (columnsChanged)
				FolderSettings.ColumnsViewModel = ColumnsViewModel;

			var elapsed = Stopwatch.GetElapsedTime(autoFitStartedTimestamp);
			if (items.Count >= 1024 || elapsed >= TimeSpan.FromMilliseconds(50))
			{
				App.Logger.LogInformation(
					"Details auto-fit processed {ItemCount} items across {ColumnCount} columns in {ElapsedMs:F1} ms (snapshot/aggregate/measure/apply/persist: {SnapshotMs:F1}/{AggregateMs:F1}/{MeasureMs:F1}/{ApplyMs:F1}/{PersistMs:F1} ms, persisted: {Persisted}).",
					items.Count,
					totalColumnCount - 1,
					elapsed.TotalMilliseconds,
					Stopwatch.GetElapsedTime(autoFitStartedTimestamp, snapshotCompletedTimestamp).TotalMilliseconds,
					Stopwatch.GetElapsedTime(snapshotCompletedTimestamp, aggregationCompletedTimestamp).TotalMilliseconds,
					Stopwatch.GetElapsedTime(aggregationCompletedTimestamp, measurementCompletedTimestamp).TotalMilliseconds,
					Stopwatch.GetElapsedTime(measurementCompletedTimestamp, columnsAppliedTimestamp).TotalMilliseconds,
					Stopwatch.GetElapsedTime(columnsAppliedTimestamp).TotalMilliseconds,
					columnsChanged);
			}
		}

		private double[] CaptureResizableColumnWidths()
		{
			var widths = new double[15];
			for (var columnIndex = 2; columnIndex <= 16; columnIndex++)
				widths[columnIndex - 2] = GetDetailsColumn(columnIndex)?.UserLength.Value ?? 0;

			return widths;
		}

		private void FitColumnsToAvailableWidth()
		{
			var availableWidth = FileList.ActualWidth;
			if (!double.IsFinite(availableWidth) || availableWidth <= 0)
				return;

			var visibleColumns = ColumnsViewModel
				.GetType()
				.GetProperties()
				.Where(property => property.PropertyType == typeof(DetailsLayoutColumnItem))
				.Select(property => property.GetValue(ColumnsViewModel) as DetailsLayoutColumnItem)
				.Where(column => column?.Length.Value > 0)
				.Cast<DetailsLayoutColumnItem>()
				.ToArray();
			if (visibleColumns.Length == 0)
				return;

			const double headerHorizontalPadding = 24;
			var usableWidth = Math.Max(0, availableWidth - headerHorizontalPadding);
			var columnsWidth = visibleColumns.Sum(column => column.LengthIncludingGridSplitter.Value);
			var remainingWidth = usableWidth - columnsWidth;

			if (remainingWidth >= 0)
			{
				if (ColumnsViewModel.NameColumn.Length.Value == 0)
					return;

				var nameWidth = Math.Min(
					ColumnsViewModel.NameColumn.UserLength.Value + remainingWidth,
					ColumnsViewModel.NameColumn.NormalMaxLength);
				ColumnsViewModel.NameColumn.UserLength = new GridLength(nameWidth, GridUnitType.Pixel);
				return;
			}

			var resizableColumns = visibleColumns.Where(column => column.IsResizable).ToArray();
			var fixedWidth = visibleColumns.Sum(column =>
				(column.IsResizable ? 0 : column.Length.Value) +
				(column.LengthIncludingGridSplitter.Value - column.Length.Value));
			var minimumWidth = resizableColumns.Sum(column => column.NormalMinLength);
			var flexibleWidth = resizableColumns.Sum(column => Math.Max(0, column.UserLength.Value - column.NormalMinLength));
			var availableFlexibleWidth = Math.Max(0, usableWidth - fixedWidth - minimumWidth);
			var scale = flexibleWidth > 0 ? Math.Min(1, availableFlexibleWidth / flexibleWidth) : 0;

			foreach (var column in resizableColumns)
			{
				var scaledWidth = column.NormalMinLength +
					Math.Max(0, column.UserLength.Value - column.NormalMinLength) * scale;
				column.UserLength = new GridLength(scaledWidth, GridUnitType.Pixel);
			}
		}

		private void AutoFitColumnsIfEnabled()
		{
			if (!UserSettingsService.LayoutSettingsService.AutoSizeColumnsInDetailsLayout)
				return;

			// Trailing debounce so bursts of item adds and extended-property updates during a folder load collapse into one resize.
			if (_autoFitColumnsTimer is null)
			{
				_autoFitColumnsTimer = DispatcherQueue.CreateTimer();
				_autoFitColumnsTimer.IsRepeating = false;
				_autoFitColumnsTimer.Tick += AutoFitColumnsTimer_Tick;
			}

			_autoFitColumnsTimer.Stop();
			_autoFitColumnsTimer.Interval = AutoFitColumnsDebounceInterval;
			_autoFitColumnsTimer.Start();
		}

		private void AutoFitColumnsTimer_Tick(DispatcherQueueTimer sender, object args)
		{
			sender.Stop();
			if (ParentShellPageInstance?.ShellViewModel.IsFolderLoadInProgress is true)
				return;

			AutoFitColumns(true);
		}

		private void ShellViewModel_ItemLoadStatusChanged(object sender, ItemLoadStatusChangedEventArgs e)
		{
			if (e.Status == ItemLoadStatusChangedEventArgs.ItemLoadStatus.Complete)
				AutoFitColumnsIfEnabled();
		}

		private void FileListItems_VectorChanged(IObservableVector<object> sender, IVectorChangedEventArgs e)
		{
			if (ParentShellPageInstance?.ShellViewModel.IsFolderLoadInProgress is true)
				return;

			AutoFitColumnsIfEnabled();
		}

		protected override async Task CommitRenameAsync(TextBox textBox)
		{
			await base.CommitRenameAsync(textBox);
			AutoFitColumnsIfEnabled();
		}

		private static int[] CalculateMaxItemLengths(IReadOnlyList<ListedItem> items)
		{
			var maxItemLengths = new int[17];
			maxItemLengths[16] = 20;

			foreach (var item in items)
			{
				maxItemLengths[2] = Math.Max(maxItemLengths[2], item.Name?.Length ?? 0);
				maxItemLengths[3] = Math.Max(maxItemLengths[3], (item as IGitItem)?.UnmergedGitStatusName?.Length ?? 0);
				maxItemLengths[4] = Math.Max(maxItemLengths[4], (item as IGitItem)?.GitLastCommitDateHumanized?.Length ?? 0);
				maxItemLengths[5] = Math.Max(maxItemLengths[5], (item as IGitItem)?.GitLastCommitMessage?.Length ?? 0);
				maxItemLengths[6] = Math.Max(maxItemLengths[6], (item as IGitItem)?.GitLastCommitAuthor?.Length ?? 0);
				maxItemLengths[7] = Math.Max(maxItemLengths[7], (item as IGitItem)?.GitLastCommitSha?.Length ?? 0);
				maxItemLengths[8] = Math.Max(maxItemLengths[8], item.FileTagsUI?.Sum(tag => tag?.Name?.Length ?? 0) ?? 0);
				maxItemLengths[9] = Math.Max(maxItemLengths[9], item.ItemPath?.Length ?? 0);
				maxItemLengths[10] = Math.Max(maxItemLengths[10], (item as RecycleBinItem)?.ItemOriginalPath?.Length ?? 0);
				maxItemLengths[11] = Math.Max(maxItemLengths[11], (item as RecycleBinItem)?.ItemDateDeleted?.Length ?? 0);
				maxItemLengths[12] = Math.Max(maxItemLengths[12], item.ItemDateModified?.Length ?? 0);
				maxItemLengths[13] = Math.Max(maxItemLengths[13], item.ItemDateCreated?.Length ?? 0);
				maxItemLengths[14] = Math.Max(maxItemLengths[14], item.ItemType?.Length ?? 0);
				maxItemLengths[15] = Math.Max(maxItemLengths[15], item.FileSize?.Length ?? 0);
			}

			return maxItemLengths;
		}

		private double[] MeasureColumnEstimates(IReadOnlyList<int> maxItemLengths)
		{
			var estimates = new double[17];
			estimates[16] = maxItemLengths[16];
			if (maxItemLengths[8] > 0 && GetDetailsColumn(8)?.Length.Value > 0)
				estimates[8] = MeasureTagColumnEstimate(8);

			var textBlocksByColumn = DependencyObjectHelpers
				.FindChildren<TextBlock>(FileList.ItemsPanelRoot)
				.Where(textBlock => !string.IsNullOrEmpty(textBlock.Text))
				.Select(textBlock => (ColumnIndex: GetColumnIndex(textBlock.Name), TextBlock: textBlock))
				.Where(entry => entry.ColumnIndex is >= 2 and <= 15 && entry.ColumnIndex != 8)
				.GroupBy(entry => entry.ColumnIndex);

			foreach (var column in textBlocksByColumn)
			{
				var columnIndex = column.Key;
				if (maxItemLengths[columnIndex] == 0 || GetDetailsColumn(columnIndex)?.Length.Value <= 0)
					continue;

				var widthPerLetter = column
					.OrderByDescending(entry => entry.TextBlock.Text.Length)
					.Take(5)
					.Select(entry =>
					{
						var textBlock = entry.TextBlock;
						var sample = new TextBlock { Text = textBlock.Text, FontSize = textBlock.FontSize, FontFamily = textBlock.FontFamily };
						sample.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
						return sample.DesiredSize.Width / Math.Max(1, textBlock.Text.Length);
					})
					.ToArray();
				if (widthPerLetter.Length == 0)
					continue;

				var weightedAverage = (widthPerLetter.Average() + widthPerLetter.Max()) / 2;
				estimates[columnIndex] = weightedAverage * maxItemLengths[columnIndex];
			}

			return estimates;
		}

		private void ResizeColumnToFit(int columnToResize, IReadOnlyList<ListedItem>? itemSnapshot = null, bool persistChanges = true)
		{
			if (GetDetailsColumn(columnToResize)?.Length.Value <= 0)
				return;

			itemSnapshot ??= FileList.Items.OfType<ListedItem>().ToList();
			if (itemSnapshot.Count == 0)
				return;

			var maxItemLength = columnToResize switch
			{
				2 => itemSnapshot.Select(x => x.Name?.Length ?? 0).Max(), // file name column
				3 => itemSnapshot.Select(x => (x as IGitItem)?.UnmergedGitStatusName?.Length ?? 0).Max(), // git status
				4 => itemSnapshot.Select(x => (x as IGitItem)?.GitLastCommitDateHumanized?.Length ?? 0).Max(), // git
				5 => itemSnapshot.Select(x => (x as IGitItem)?.GitLastCommitMessage?.Length ?? 0).Max(), // git
				6 => itemSnapshot.Select(x => (x as IGitItem)?.GitLastCommitAuthor?.Length ?? 0).Max(), // git
				7 => itemSnapshot.Select(x => (x as IGitItem)?.GitLastCommitSha?.Length ?? 0).Max(), // git
				8 => itemSnapshot.Select(x => x.FileTagsUI?.Sum(x => x?.Name?.Length ?? 0) ?? 0).Max(), // file tag column
				9 => itemSnapshot.Select(x => x.ItemPath?.Length ?? 0).Max(), // path column
				10 => itemSnapshot.Select(x => (x as RecycleBinItem)?.ItemOriginalPath?.Length ?? 0).Max(), // original path column
				11 => itemSnapshot.Select(x => (x as RecycleBinItem)?.ItemDateDeleted?.Length ?? 0).Max(), // date deleted column
				12 => itemSnapshot.Select(x => x.ItemDateModified?.Length ?? 0).Max(), // date modified column
				13 => itemSnapshot.Select(x => x.ItemDateCreated?.Length ?? 0).Max(), // date created column
				14 => itemSnapshot.Select(x => x.ItemType?.Length ?? 0).Max(), // item type column
				15 => itemSnapshot.Select(x => x.FileSize?.Length ?? 0).Max(), // item size column
				16 => 20, // cloud status column
				_ => 0
			};

			if (maxItemLength == 0)
				return;

			var columnSizeToFit = MeasureColumnEstimate(columnToResize, 5, maxItemLength);
			ApplyColumnWidth(columnToResize, maxItemLength, columnSizeToFit, persistChanges);
		}

		private void ApplyColumnWidth(int columnIndex, int maxItemLength, double columnSizeToFit, bool persistChanges)
		{
			var column = GetDetailsColumn(columnIndex);
			if (column is null || column.Length.Value <= 0 || maxItemLength == 0)
				return;

			if (double.IsFinite(columnSizeToFit) && columnSizeToFit > 1)
			{
				if (columnIndex == 2) // file name column
					columnSizeToFit += 20;

				var minFitLength = Math.Max(columnSizeToFit, column.NormalMinLength);
				var maxFitLength = Math.Min(minFitLength + 36, column.NormalMaxLength); // 36 to account for SortIcon & padding

				column.UserLength = new GridLength(maxFitLength, GridUnitType.Pixel);
			}

			if (persistChanges)
				FolderSettings.ColumnsViewModel = ColumnsViewModel;
		}

		private DetailsLayoutColumnItem? GetDetailsColumn(int columnIndex)
			=> columnIndex switch
			{
				2 => ColumnsViewModel.NameColumn,
				3 => ColumnsViewModel.GitStatusColumn,
				4 => ColumnsViewModel.GitLastCommitDateColumn,
				5 => ColumnsViewModel.GitLastCommitMessageColumn,
				6 => ColumnsViewModel.GitCommitAuthorColumn,
				7 => ColumnsViewModel.GitLastCommitShaColumn,
				8 => ColumnsViewModel.TagColumn,
				9 => ColumnsViewModel.PathColumn,
				10 => ColumnsViewModel.OriginalPathColumn,
				11 => ColumnsViewModel.DateDeletedColumn,
				12 => ColumnsViewModel.DateModifiedColumn,
				13 => ColumnsViewModel.DateCreatedColumn,
				14 => ColumnsViewModel.ItemTypeColumn,
				15 => ColumnsViewModel.SizeColumn,
				16 => ColumnsViewModel.StatusColumn,
				_ => null
			};

		private double MeasureColumnEstimate(int columnIndex, int measureItemsCount, int maxItemLength)
		{
			if (columnIndex == 16) // sync status
				return maxItemLength;

			if (columnIndex == 8) // file tag
				return MeasureTagColumnEstimate(columnIndex);

			return MeasureTextColumnEstimate(columnIndex, measureItemsCount, maxItemLength);
		}

		private double MeasureTagColumnEstimate(int columnIndex)
		{
			var grids = DependencyObjectHelpers
				.FindChildren<Grid>(FileList.ItemsPanelRoot)
				.Where(grid => IsCorrectColumn(grid, columnIndex))
				.ToArray();

			return grids
				.Select(grid =>
				{
					var tagWidths = DependencyObjectHelpers
						.FindChildren<StackPanel>(grid)
						.Select(tag => DependencyObjectHelpers
							.FindChildren<TextBlock>(tag)
							.Where(textBlock => !string.IsNullOrEmpty(textBlock.Text))
							.Select(textBlock =>
							{
								var sample = new TextBlock
								{
									Text = textBlock.Text,
									FontSize = textBlock.FontSize,
									FontFamily = textBlock.FontFamily
								};
								sample.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
								return sample.DesiredSize.Width;
							})
							.Sum() + 40)
						.ToArray();

					return tagWidths.Sum() + Math.Max(0, tagWidths.Length - 1) * 4;
				})
				.DefaultIfEmpty(0)
				.Max();
		}

		private double MeasureTextColumnEstimate(int columnIndex, int measureItemsCount, int maxItemLength)
		{
			var tbs = DependencyObjectHelpers
				.FindChildren<TextBlock>(FileList.ItemsPanelRoot)
				.Where(tb => IsCorrectColumn(tb, columnIndex));

			// heuristic: usually, text with more letters are wider than shorter text with wider letters
			// with this, we can calculate avg width using longest text(s) to avoid overshooting the width
			var widthPerLetter = tbs
				.OrderByDescending(x => x.Text.Length)
				.Where(tb => !string.IsNullOrEmpty(tb.Text))
				.Take(measureItemsCount)
				.Select(tb =>
				{
					var sampleTb = new TextBlock { Text = tb.Text, FontSize = tb.FontSize, FontFamily = tb.FontFamily };
					sampleTb.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));

					return sampleTb.DesiredSize.Width / Math.Max(1, tb.Text.Length);
				});

			if (!widthPerLetter.Any())
				return 0;

			// Take weighted avg between mean and max since width is an estimate
			var weightedAvg = (widthPerLetter.Average() + widthPerLetter.Max()) / 2;
			return weightedAvg * maxItemLength;
		}

		private bool IsCorrectColumn(FrameworkElement element, int columnIndex)
			=> GetColumnIndex(element.Name) == columnIndex;

		private static int GetColumnIndex(string elementName)
		{
			return elementName switch
			{
				"ItemName" => 2,
				"ItemGitStatusTextBlock" => 3,
				"ItemGitLastCommitDateTextBlock" => 4,
				"ItemGitLastCommitMessageTextBlock" => 5,
				"ItemGitCommitAuthorTextBlock" => 6,
				"ItemGitLastCommitShaTextBlock" => 7,
				"ItemTagGrid" => 8,
				"ItemPath" => 9,
				"ItemOriginalPath" => 10,
				"ItemDateDeleted" => 11,
				"ItemDateModified" => 12,
				"ItemDateCreated" => 13,
				"ItemType" => 14,
				"ItemSize" => 15,
				"ItemStatus" => 16,
				_ => -1,
			};
		}

		private void FileList_Loaded(object sender, RoutedEventArgs e)
		{
			ContentScroller = FileList.FindDescendant<ScrollViewer>(x => x.Name == "ScrollViewer");
			const double OffsetCorrection = 88; // HeaderGrid (40) + ListViewHeaderItem (44 + 4 margin)

			RootGridZoom.ViewChangeStarted += (_, args) =>
			{
				var scroller = ContentScroller;
				if (args.IsSourceZoomedInView || scroller is null)
					return;
				void OnZoomScrolled(object? s, ScrollViewerViewChangedEventArgs ve)
				{
					scroller.ViewChanged -= OnZoomScrolled; 
					scroller.ChangeView(0, Math.Max(0, scroller.VerticalOffset - OffsetCorrection), null, true);
				}
				scroller.ViewChanged += OnZoomScrolled;
			};
		}

		private void SetDetailsColumnsAsDefault_Click(object sender, RoutedEventArgs e)
		{
			LayoutPreferencesManager.SetDefaultLayoutPreferences(ColumnsViewModel);
		}

		private void ItemSelected_Checked(object sender, RoutedEventArgs e)
		{
			if (sender is CheckBox checkBox && checkBox.DataContext is ListedItem item && !FileList.SelectedItems.Contains(item))
				FileList.SelectedItems.Add(item);
		}

		private void ItemSelected_Unchecked(object sender, RoutedEventArgs e)
		{
			if (sender is not CheckBox checkBox)
				return;

			if (checkBox.DataContext is ListedItem item && FileList.SelectedItems.Contains(item))
				FileList.SelectedItems.Remove(item);

			// Workaround for #17298
			checkBox.IsTabStop = false;
			checkBox.IsEnabled = false;
			checkBox.IsEnabled = true;
			checkBox.IsTabStop = true;
			FileList.Focus(FocusState.Programmatic);
		}

		private new void FileList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
		{
			var selectionCheckbox = (CheckBox)args.ItemContainer.FindDescendant("SelectionCheckbox")!;

			selectionCheckbox.PointerEntered -= SelectionCheckbox_PointerEntered;
			selectionCheckbox.PointerExited -= SelectionCheckbox_PointerExited;
			selectionCheckbox.PointerCanceled -= SelectionCheckbox_PointerCanceled;
			selectionCheckbox.Checked -= ItemSelected_Checked;
			selectionCheckbox.Unchecked -= ItemSelected_Unchecked;

			base.FileList_ContainerContentChanging(sender, args);
			if (args.InRecycleQueue)
				return;

			SetCheckboxSelectionState(args.Item, args.ItemContainer as ListViewItem);

			selectionCheckbox.PointerEntered += SelectionCheckbox_PointerEntered;
			selectionCheckbox.PointerExited += SelectionCheckbox_PointerExited;
			selectionCheckbox.PointerCanceled += SelectionCheckbox_PointerCanceled;
		}

		private void SetCheckboxSelectionState(object item, ListViewItem? lviContainer = null)
		{
			var container = lviContainer ?? FileList.ContainerFromItem(item) as ListViewItem;
			if (container is not null)
			{
				var checkbox = container.FindDescendant("SelectionCheckbox") as CheckBox;
				if (checkbox is not null)
				{
					// Temporarily disable events to avoid selecting wrong items
					checkbox.Checked -= ItemSelected_Checked;
					checkbox.Unchecked -= ItemSelected_Unchecked;

					checkbox.IsChecked = FileList.SelectedItems.Contains(item);

					checkbox.Checked += ItemSelected_Checked;
					checkbox.Unchecked += ItemSelected_Unchecked;
				}
				UpdateCheckboxVisibility(container, checkbox?.IsPointerOver ?? false);
			}
		}

		private void TagItem_Tapped(object sender, TappedRoutedEventArgs e)
		{
			var tagName = ((sender as StackPanel)?.Children[TAG_TEXT_BLOCK] as TextBlock)?.Text;
			if (tagName is null)
				return;

			ParentShellPageInstance?.SubmitSearch(FolderSearch.FormatTagQuery(tagName));
		}

		private void FileTag_PointerEntered(object sender, PointerRoutedEventArgs e)
		{
			VisualStateManager.GoToState((UserControl)sender, "PointerOver", true);
		}

		private void FileTag_PointerExited(object sender, PointerRoutedEventArgs e)
		{
			VisualStateManager.GoToState((UserControl)sender, "Normal", true);
		}

		private async void RemoveTagIcon_Tapped(object sender, TappedRoutedEventArgs e)
		{
			var parent = (sender as FontIcon)?.Parent as StackPanel;
			var tagName = (parent?.Children[TAG_TEXT_BLOCK] as TextBlock)?.Text;

			if (tagName is null || parent?.DataContext is not ListedItem item)
				return;

			var tagId = FileTagsSettingsService.GetTagsByName(tagName).FirstOrDefault()?.Uid;

			if (tagId is not null)
			{
				item.FileTags = item.FileTags
					.Except([tagId])
					.ToArray();

				if (ParentShellPageInstance is not null)
					await ParentShellPageInstance.ShellViewModel.RefreshTagGroups();
			}

			e.Handled = true;
		}

		private void SelectionCheckbox_PointerEntered(object sender, PointerRoutedEventArgs e)
		{
			UpdateCheckboxVisibility((sender as FrameworkElement)!.FindAscendant<ListViewItem>()!, true);
		}

		private void SelectionCheckbox_PointerExited(object sender, PointerRoutedEventArgs e)
		{
			UpdateCheckboxVisibility((sender as FrameworkElement)!.FindAscendant<ListViewItem>()!, false);
		}

		private void SelectionCheckbox_PointerCanceled(object sender, PointerRoutedEventArgs e)
		{
			UpdateCheckboxVisibility((sender as FrameworkElement)!.FindAscendant<ListViewItem>()!, false);
		}

		private void UpdateCheckboxVisibility(object sender, bool isPointerOver)
		{
			if (sender is ListViewItem control && control.FindDescendant<UserControl>() is UserControl userControl)
			{
				// Handle visual states
				// Show checkboxes when items are selected (as long as the setting is enabled)
				// Show checkboxes when hovering of the thumbnail (regardless of the setting to hide them)
				if (UserSettingsService.FoldersSettingsService.ShowCheckboxesWhenSelectingItems && control.IsSelected
					|| isPointerOver)
					VisualStateManager.GoToState(userControl, "ShowCheckbox", true);
				else
					VisualStateManager.GoToState(userControl, "HideCheckbox", true);
			}
		}

		// Workaround for https://github.com/microsoft/microsoft-ui-xaml/issues/170
		private void TextBlock_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs e)
		{
			SetToolTip(sender);
		}

		private void TextBlock_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs e)
		{
			if (sender is TextBlock textBlock)
				SetToolTip(textBlock);
		}

		private void SetToolTip(TextBlock textBlock)
		{
			ToolTipService.SetToolTip(textBlock, textBlock.IsTextTrimmed ? textBlock.Text : null);
		}

		private async void ViewSizeButton_Click(object sender, RoutedEventArgs e)
		{
			if (sender is not FrameworkElement { DataContext: ListedItem item })
				return;

			item.IsCalculatingSize = true;
			var sizeProvider = Ioc.Default.GetRequiredService<Services.SizeProvider.ISizeProvider>();
			var updateTask = Task.Run(() => sizeProvider.UpdateAsync(item.ItemPath, default));

			try
			{
				if (await Task.WhenAny(updateTask, Task.Delay(300)) != updateTask)
					item.ShowCalculatingText = true;
				await updateTask;
			}
			finally
			{
				item.ShowCalculatingText = false;
				item.IsCalculatingSize = false;
			}
		}

		private void FileList_LosingFocus(UIElement sender, LosingFocusEventArgs args)
		{
			// Fixes an issue where clicking an empty space would scroll to the top of the file list
			if (args.NewFocusedElement == FileList)
				args.TryCancel();
		}

		private void FileListHeader_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
		{
			// Fixes an issue where double clicking the column header would navigate back as if clicking on empty space
			e.Handled = true;
		}

		private static GitProperties GetEnabledGitProperties(ColumnsViewModel columnsViewModel)
		{
			var enableStatus = !columnsViewModel.GitStatusColumn.IsHidden && !columnsViewModel.GitStatusColumn.UserCollapsed;
			var enableCommit = !columnsViewModel.GitLastCommitDateColumn.IsHidden && !columnsViewModel.GitLastCommitDateColumn.UserCollapsed
				|| !columnsViewModel.GitLastCommitMessageColumn.IsHidden && !columnsViewModel.GitLastCommitMessageColumn.UserCollapsed
				|| !columnsViewModel.GitCommitAuthorColumn.IsHidden && !columnsViewModel.GitCommitAuthorColumn.UserCollapsed
				|| !columnsViewModel.GitLastCommitShaColumn.IsHidden && !columnsViewModel.GitLastCommitShaColumn.UserCollapsed;
			return (enableStatus, enableCommit) switch
			{
				(true, true) => GitProperties.All,
				(true, false) => GitProperties.Status,
				(false, true) => GitProperties.Commit,
				(false, false) => GitProperties.None
			};
		}
	}
}
