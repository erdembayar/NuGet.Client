// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using NuGet.Common;
using NuGet.PackageManagement.VisualStudio;
using NuGet.Protocol.Core.Types;
using NuGet.VisualStudio;
using Mvs = Microsoft.VisualStudio.Shell;
using Resx = NuGet.PackageManagement.UI;
using Task = System.Threading.Tasks.Task;

namespace NuGet.PackageManagement.UI
{
    /// <summary>
    /// Interaction logic for InfiniteScrollList.xaml
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1001")]
    public partial class InfiniteScrollList : UserControl
    {
        private readonly LoadingStatusIndicator _loadingStatusIndicator = new LoadingStatusIndicator();

        public event SelectionChangedEventHandler SelectionChanged;

        public delegate void UpdateButtonClickEventHandler(PackageItemListViewModel[] selectedPackages);
        public event UpdateButtonClickEventHandler UpdateButtonClicked;

        /// <summary>
        /// This exists only to facilitate unit testing.
        /// It is triggered at <see cref="RepopulatePackageList(PackageItemListViewModel, IPackageItemLoader, CancellationToken) " />, just before it is finished
        /// </summary>
        internal event EventHandler LoadItemsCompleted;

        private CancellationTokenSource _loadCts;
        private IPackageItemLoader _loader;
        private INuGetUILogger _logger;
        private Task<SearchResult<IPackageSearchMetadata>> _initialSearchResultTask;
        private readonly Lazy<JoinableTaskFactory> _joinableTaskFactory;

        private const string LogEntrySource = "NuGet Package Manager";

        // The count of packages that are selected
        private int _selectedCount;

        public bool IsBrowseTab
        {
            get { return (bool)GetValue(IsBrowseTabProperty); }
            set { SetValue(IsBrowseTabProperty, value); }
        }

        public static readonly DependencyProperty IsBrowseTabProperty =
            DependencyProperty.Register("IsBrowseTab", typeof(bool), typeof(InfiniteScrollList), new PropertyMetadata(default(bool)));


        public InfiniteScrollList()
            : this(new Lazy<JoinableTaskFactory>(() => NuGetUIThreadHelper.JoinableTaskFactory))
        {
        }

        internal InfiniteScrollList(Lazy<JoinableTaskFactory> joinableTaskFactory)
        {
            if (joinableTaskFactory == null)
            {
                throw new ArgumentNullException(nameof(joinableTaskFactory));
            }

            _joinableTaskFactory = joinableTaskFactory;

            InitializeComponent();

            _listBrowse.ItemsLock = ReentrantSemaphore.Create(
                initialCount: 1,
                joinableTaskContext: _joinableTaskFactory.Value.Context,
                mode: ReentrantSemaphore.ReentrancyMode.Stack);

            _listInstalled.ItemsLock = ReentrantSemaphore.Create(
                initialCount: 1,
                joinableTaskContext: _joinableTaskFactory.Value.Context,
                mode: ReentrantSemaphore.ReentrancyMode.Stack);

            BindingOperations.EnableCollectionSynchronization(ItemsBrowse, _listBrowse.ItemsLock);
            BindingOperations.EnableCollectionSynchronization(ItemsInstalled, _listInstalled.ItemsLock);

            DataContext = this;
            CheckBoxesEnabled = false;

            _loadingStatusIndicator.PropertyChanged += LoadingStatusIndicator_PropertyChanged;
        }

        private void LoadingStatusIndicator_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            _joinableTaskFactory.Value.Run(async delegate
            {
                await _joinableTaskFactory.Value.SwitchToMainThreadAsync();
                if (e.PropertyName == nameof(LoadingStatusIndicator.Status)
                    && _ltbLoading.Text != _loadingStatusIndicator.LocalizedStatus)
                {
                    _ltbLoading.Text = _loadingStatusIndicator.LocalizedStatus;
                }
            });
        }

        public bool CheckBoxesEnabled { get; set; }

        public bool IsSolution { get; set; }

        public ObservableCollection<object> ItemsBrowse { get; } = new ObservableCollection<object>();
        public ObservableCollection<object> ItemsInstalled { get; } = new ObservableCollection<object>();

        private ICollectionView ItemsInstalledCollectionView
        {
            get
            {
                return CollectionViewSource.GetDefaultView(ItemsInstalled);
            }
        }

        /// <summary>
        /// Count of Items (excluding Loading indicator) that are currently shown after applying any UI filtering.
        /// </summary>
        private int FilteredInstalledItemsCount
        {
            get
            {
                return InstalledPackageItemsFiltered.Count();
            }
        }

        /// <summary>
        /// All loaded Items (excluding Loading indicator) regardless of filtering.
        /// </summary>
        public IEnumerable<PackageItemListViewModel> PackageItemsInstalled => ItemsInstalled.OfType<PackageItemListViewModel>().ToArray();

        /// <summary>
        /// Installed Items (excluding Loading indicator) that are currently shown after applying any UI filtering.
        /// </summary>
        public IEnumerable<PackageItemListViewModel> InstalledPackageItemsFiltered
        {
            get
            {
                if (ItemsInstalledCollectionView.Filter != null)
                {
                    return ItemsInstalledCollectionView.OfType<PackageItemListViewModel>();
                }
                else
                {
                    return PackageItemsInstalled;
                }
            }
        }

        public PackageItemListViewModel SelectedPackageItem => CurrentlyShownListBox.SelectedItem as PackageItemListViewModel;

        public int SelectedIndex => CurrentlyShownListBox.SelectedIndex;

        public Guid? OperationId => _loader?.State.OperationId;

        // Load items using the specified loader
        internal async Task LoadItemsAsync(
            IPackageItemLoader loader,
            string loadingMessage,
            INuGetUILogger logger,
            Task<SearchResult<IPackageSearchMetadata>> searchResultTask,
            ItemFilter filterToRender,
            CancellationToken token)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            if (string.IsNullOrEmpty(loadingMessage))
            {
                throw new ArgumentException(Strings.Argument_Cannot_Be_Null_Or_Empty, nameof(loadingMessage));
            }

            if (searchResultTask == null)
            {
                throw new ArgumentNullException(nameof(searchResultTask));
            }

            token.ThrowIfCancellationRequested();

            _loader = loader;
            _logger = logger;
            _initialSearchResultTask = searchResultTask;
            _loadingStatusIndicator.Reset(loadingMessage);
            _loadingStatusBar.Visibility = Visibility.Hidden;
            _loadingStatusBar.Reset(loadingMessage, loader.IsMultiSource);

            var selectedPackageItem = SelectedPackageItem;

            InfiniteScrollListBox currentListBox = filterToRender == ItemFilter.All ? _listBrowse : _listInstalled;
            ObservableCollection<object> currentItems = filterToRender == ItemFilter.All ? ItemsBrowse : ItemsInstalled;
            
            await currentListBox.ItemsLock.ExecuteAsync(() =>
            {
                ClearPackageList(currentItems);
                return Task.CompletedTask;
            });

            _selectedCount = 0;

            // triggers the package list loader
            await LoadItemsAsync(currentListBox, currentItems, selectedPackageItem, token);
        }

        /// <summary>
        /// Keep the previously selected package after a search.
        /// Otherwise, select the first on the search if none was selected before.
        /// </summary>
        /// <param name="selectedItem">Previously selected item</param>
        internal void UpdateSelectedItem(PackageItemListViewModel selectedItem)
        {
            if (selectedItem != null)
            {
                // select the the previously selected item if it still exists.
                selectedItem = InstalledPackageItemsFiltered
                    .FirstOrDefault(item => item.Id.Equals(selectedItem.Id, StringComparison.OrdinalIgnoreCase));
            }

            // select the first item if none was selected before
            CurrentlyShownListBox.SelectedItem = selectedItem ?? InstalledPackageItemsFiltered.FirstOrDefault();
        }

        private InfiniteScrollListBox CurrentlyShownListBox
        {
            get
            {
                if (IsBrowseTab)
                {
                    return _listBrowse;
                }
                else
                {
                    return _listInstalled;
                }
            }
        }

        private ObservableCollection<object> CurrentlyShownItems
        {
            get
            {
                if (IsBrowseTab)
                {
                    return ItemsBrowse;
                }
                else
                {
                    return ItemsInstalled;
                }
            }
        }

        private async Task LoadItemsAsync(InfiniteScrollListBox currentListBox,
            ObservableCollection<object> currentItems, PackageItemListViewModel selectedPackageItem, CancellationToken token)
        {
            // If there is another async loading process - cancel it.
            var loadCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Interlocked.Exchange(ref _loadCts, loadCts)?.Cancel();

            await RepopulatePackageListAsync(currentListBox, currentItems, selectedPackageItem, _loader, loadCts);
        }

        private async Task RepopulatePackageListAsync(InfiniteScrollListBox currentListBox,
            ObservableCollection<object> currentItems, PackageItemListViewModel selectedPackageItem, IPackageItemLoader currentLoader, CancellationTokenSource loadCts)
        {
            await TaskScheduler.Default;

            var addedLoadingIndicator = false;

            try
            {
                // add Loading... indicator if not present
                if (!currentItems.Contains(_loadingStatusIndicator))
                {
                    currentItems.Add(_loadingStatusIndicator);
                    addedLoadingIndicator = true;
                }

                await LoadItemsCoreAsync(currentListBox, currentItems, currentLoader, loadCts.Token);

                await _joinableTaskFactory.Value.SwitchToMainThreadAsync();

                if (selectedPackageItem != null)
                {
                    UpdateSelectedItem(selectedPackageItem);
                }
            }
            catch (OperationCanceledException) when (!loadCts.IsCancellationRequested)
            {
                loadCts.Cancel();
                loadCts.Dispose();
                currentLoader.Reset();

                await _joinableTaskFactory.Value.SwitchToMainThreadAsync();

                // The user cancelled the login, but treat as a load error in UI
                // So the retry button and message is displayed
                // Do not log to the activity log, since it is not a NuGet error
                _logger.Log(new LogMessage(LogLevel.Error, Resx.Resources.Text_UserCanceled));

                _loadingStatusIndicator.SetError(Resx.Resources.Text_UserCanceled);

                _loadingStatusBar.SetCancelled();
                _loadingStatusBar.Visibility = Visibility.Visible;
            }
            catch (Exception ex) when (!loadCts.IsCancellationRequested)
            {
                loadCts.Cancel();
                loadCts.Dispose();
                currentLoader.Reset();

                // Write stack to activity log
                Mvs.ActivityLog.LogError(LogEntrySource, ex.ToString());

                await _joinableTaskFactory.Value.SwitchToMainThreadAsync();

                var errorMessage = ExceptionUtilities.DisplayMessage(ex);
                _logger.Log(new LogMessage(LogLevel.Error, errorMessage));

                _loadingStatusIndicator.SetError(errorMessage);

                _loadingStatusBar.SetError();
                _loadingStatusBar.Visibility = Visibility.Visible;
            }
            finally
            {
                if (_loadingStatusIndicator.Status != LoadingStatus.NoItemsFound
                    && _loadingStatusIndicator.Status != LoadingStatus.ErrorOccurred)
                {
                    // Ideally, after a search, it should report its status, and
                    // do not keep the LoadingStatus.Loading forever.
                    // This is a workaround.
                    var emptyListCount = addedLoadingIndicator ? 1 : 0;
                    if (currentItems.Count == emptyListCount)
                    {
                        _loadingStatusIndicator.Status = LoadingStatus.NoItemsFound;
                    }
                    else
                    {
                        currentItems.Remove(_loadingStatusIndicator);
                    }
                }
            }

            UpdateCheckBoxStatus();

            LoadItemsCompleted?.Invoke(this, EventArgs.Empty);
        }

        internal void FilterInstalledDataItems(ItemFilter itemFilter, CancellationToken token)
        {
            switch (itemFilter)
            {
                case ItemFilter.Installed:
                    ItemsInstalledCollectionView.Filter = null;
                    break;
                case ItemFilter.UpdatesAvailable:
                    ItemsInstalledCollectionView.Filter = (item) => item == _loadingStatusIndicator || (item as PackageItemListViewModel).IsUpdateAvailable;
                    break;
                case ItemFilter.Consolidate:
                    ItemsInstalledCollectionView.Filter = null; //TODO: setup
                    break;
                default: break;
            }

            UpdateCheckBoxStatus();
            LoadItemsCompleted?.Invoke(this, EventArgs.Empty);
        }

        private async Task LoadItemsCoreAsync(InfiniteScrollListBox currentListBox,
            ObservableCollection<object> currentItems, IPackageItemLoader currentLoader, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var loadedItems = await LoadNextPageAsync(currentListBox, currentItems, currentLoader, token);
            token.ThrowIfCancellationRequested();

            // multiple loads may occur at the same time as a result of multiple instances,
            // makes sure we update using the relevant one.
            if (currentLoader == _loader)
            {
                UpdatePackageList(currentListBox, currentItems,  loadedItems, refresh: false);
            }

            token.ThrowIfCancellationRequested();

            await _joinableTaskFactory.Value.RunAsync(async () =>
            {
                await _joinableTaskFactory.Value.SwitchToMainThreadAsync();

                _loadingStatusBar.ItemsLoaded = currentLoader.State.ItemsCount;
            });

            token.ThrowIfCancellationRequested();

            // keep waiting till completion
            await WaitForCompletionAsync(currentListBox, currentItems, currentLoader, token);

            token.ThrowIfCancellationRequested();

            if (currentLoader == _loader
                && !loadedItems.Any()
                && currentLoader.State.LoadingStatus == LoadingStatus.Ready)
            {
                UpdatePackageList(currentListBox, currentItems, currentLoader.GetCurrent(), refresh: false);
            }

            token.ThrowIfCancellationRequested();
        }

        private async Task<IEnumerable<PackageItemListViewModel>> LoadNextPageAsync(InfiniteScrollListBox currentListBox,
            ObservableCollection<object> currentItems, IPackageItemLoader currentLoader, CancellationToken token)
        {
            var progress = new Progress<IItemLoaderState>(
                s => HandleItemLoaderStateChange(currentListBox, currentItems, currentLoader, s));

            // if searchResultTask is in progress then just wait for it to complete
            // without creating new load task
            if (_initialSearchResultTask != null)
            {
                token.ThrowIfCancellationRequested();

                // update initial progress
                var cleanState = SearchResult.Empty<IPackageSearchMetadata>();
                await currentLoader.UpdateStateAndReportAsync(cleanState, progress, token);

                var results = await _initialSearchResultTask;

                token.ThrowIfCancellationRequested();

                // update state and progress
                await currentLoader.UpdateStateAndReportAsync(results, progress, token);

                _initialSearchResultTask = null;
            }
            else
            {
                // trigger loading
                await currentLoader.LoadNextAsync(progress, token);
            }

            await WaitForInitialResultsAsync(currentLoader, progress, token);

            return currentLoader.GetCurrent();
        }

        private async Task WaitForCompletionAsync(InfiniteScrollListBox currentListBox,
            ObservableCollection<object> currentItems, IItemLoader<PackageItemListViewModel> currentLoader, CancellationToken token)
        {
            var progress = new Progress<IItemLoaderState>(
                s => HandleItemLoaderStateChange(currentListBox, currentItems, currentLoader, s));

            // run to completion
            while (currentLoader.State.LoadingStatus == LoadingStatus.Loading)
            {
                token.ThrowIfCancellationRequested();
                await currentLoader.UpdateStateAsync(progress, token);
            }
        }

        private async Task WaitForInitialResultsAsync(
            IItemLoader<PackageItemListViewModel> currentLoader,
            IProgress<IItemLoaderState> progress,
            CancellationToken token)
        {
            while (currentLoader.State.LoadingStatus == LoadingStatus.Loading &&
                currentLoader.State.ItemsCount == 0)
            {
                token.ThrowIfCancellationRequested();
                await currentLoader.UpdateStateAsync(progress, token);
            }
        }

        /// <summary>
        /// Shows the Loading status bar, if necessary. Also, it inserts the Loading... indicator, if necesary
        /// </summary>
        /// <param name="loader">Current loader</param>
        /// <param name="state">Progress reported by the <c>Progress</c> callback</param>
        private void HandleItemLoaderStateChange(InfiniteScrollListBox currentListBox,
            ObservableCollection<object> currentItems, IItemLoader<PackageItemListViewModel> loader, IItemLoaderState state)
        {
            _joinableTaskFactory.Value.Run(async () =>
            {
                await _joinableTaskFactory.Value.SwitchToMainThreadAsync();

                if (loader == _loader)
                {
                    _loadingStatusBar.UpdateLoadingState(state);

                    // decide when to show status bar
                    var desiredVisibility = EvaluateStatusBarVisibility(loader, state);

                    if (_loadingStatusBar.Visibility != Visibility.Visible
                        && desiredVisibility == Visibility.Visible)
                    {
                        _loadingStatusBar.Visibility = desiredVisibility;
                    }

                    _loadingStatusIndicator.Status = state.LoadingStatus;

                    if (!currentItems.Contains(_loadingStatusIndicator))
                    {
                        await currentListBox.ItemsLock.ExecuteAsync(() =>
                        {
                            currentItems.Add(_loadingStatusIndicator);
                            return Task.CompletedTask;
                        });
                    }
                }
            });
        }

        private Visibility EvaluateStatusBarVisibility(IItemLoader<PackageItemListViewModel> loader, IItemLoaderState state)
        {
            var statusBarVisibility = Visibility.Hidden;

            if (state.LoadingStatus == LoadingStatus.Cancelled
                || state.LoadingStatus == LoadingStatus.ErrorOccurred)
            {
                statusBarVisibility = Visibility.Visible;
            }

            if (loader.IsMultiSource)
            {
                var hasMore = _loadingStatusBar.ItemsLoaded != 0 && state.ItemsCount > _loadingStatusBar.ItemsLoaded;
                if (hasMore)
                {
                    statusBarVisibility = Visibility.Visible;
                }

                if (state.LoadingStatus == LoadingStatus.Loading && state.ItemsCount > 0)
                {
                    statusBarVisibility = Visibility.Visible;
                }
            }

            return statusBarVisibility;
        }

        /// <summary>
        /// Appends <c>packages</c> to the internal list
        /// </summary>
        /// <param name="packages">Packages collection to add</param>
        /// <param name="refresh">Clears list if set to <c>true</c></param>
        private void UpdatePackageList(InfiniteScrollListBox listBoxToUpdate, ObservableCollection<object> collectionToUpdate,
            IEnumerable<PackageItemListViewModel> packages, bool refresh)
        {
            _joinableTaskFactory.Value.Run(async () =>
            {
                // Synchronize updating Items list
                await listBoxToUpdate.ItemsLock.ExecuteAsync(() =>
                {
                    // remove the loading status indicator if it's in the list
                    bool removed = collectionToUpdate.Remove(_loadingStatusIndicator);

                    if (refresh)
                    {
                        ClearPackageList(collectionToUpdate);
                    }

                    // add newly loaded items
                    foreach (var package in packages)
                    {
                        package.PropertyChanged += Package_PropertyChanged;
                        collectionToUpdate.Add(package);
                        _selectedCount = package.Selected ? _selectedCount + 1 : _selectedCount;
                    }

                    if (removed)
                    {
                        collectionToUpdate.Add(_loadingStatusIndicator);
                    }

                    return Task.CompletedTask;
                });
            });
        }

        /// <summary>
        /// Clear <c>Items</c> list and removes the event handlers for each element
        /// </summary>
        private void ClearPackageList(ObservableCollection<object> itemsToClear)
        {
            foreach (var package in PackageItemsInstalled)
            {
                package.PropertyChanged -= Package_PropertyChanged;
            }

            itemsToClear.Clear();
            _loadingStatusBar.ItemsLoaded = 0;
        }

        public void UpdatePackageStatus(PackageCollectionItem[] installedPackages)
        {
            // Only update PackageStatus of Installed items shown in the current UI Filter.
            foreach (var package in InstalledPackageItemsFiltered)
            {
                package.UpdatePackageStatus(installedPackages);
            }
        }

        private void Package_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var package = sender as PackageItemListViewModel;
            if (e.PropertyName == nameof(package.Selected))
            {
                if (package.Selected)
                {
                    _selectedCount++;
                }
                else
                {
                    _selectedCount--;
                }

                UpdateCheckBoxStatus();
            }
        }

        // Update the status of the _selectAllPackages check box and the Update button.
        private void UpdateCheckBoxStatus()
        {
            // The current tab is not "Updates".
            if (!CheckBoxesEnabled)
            {
                _updateButtonContainer.Visibility = Visibility.Collapsed;
                return;
            }

            //Are any packages shown with the current filter?
            int packageCount = FilteredInstalledItemsCount;

            _updateButtonContainer.Visibility =
                packageCount > 0 ?
                Visibility.Visible :
                Visibility.Collapsed;

            if (_selectedCount == 0)
            {
                _selectAllPackages.IsChecked = false;
                _updateButton.IsEnabled = false;
            }
            else if (_selectedCount < packageCount)
            {
                _selectAllPackages.IsChecked = null;
                _updateButton.IsEnabled = true;
            }
            else
            {
                _selectAllPackages.IsChecked = true;
                _updateButton.IsEnabled = true;
            }
        }

        public PackageItemListViewModel SelectedItem
        {
            get
            {
                return CurrentlyShownListBox.SelectedItem as PackageItemListViewModel;
            }
            internal set
            {
                CurrentlyShownListBox.SelectedItem = value;
            }
        }

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0
                && e.AddedItems[0] is LoadingStatusIndicator)
            {
                // make the loading object not selectable
                if (e.RemovedItems.Count > 0)
                {
                    CurrentlyShownListBox.SelectedItem = e.RemovedItems[0];
                }
                else
                {
                    CurrentlyShownListBox.SelectedIndex = -1;
                }
            }
            else
            {
                if (SelectionChanged != null)
                {
                    SelectionChanged(this, e);
                }
            }
        }

        private void BrowseScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_loader?.State.LoadingStatus == LoadingStatus.Ready && e.VerticalChange > 0)
            {
                var scrollViewer = e.OriginalSource as ScrollViewer;
                if (scrollViewer != null)
                {
                    var first = scrollViewer.VerticalOffset;
                    var last = scrollViewer.ViewportHeight + first;
                    if (scrollViewer.ViewportHeight > 0 && last >= ItemsBrowse.Count)
                    {
                        NuGetUIThreadHelper.JoinableTaskFactory.RunAsync(() =>
                            LoadItemsAsync(CurrentlyShownListBox, CurrentlyShownItems, selectedPackageItem: null, token: CancellationToken.None)
                        );
                    }
                }
            }
        }

        private void SelectAllPackagesCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            foreach (var item in CurrentlyShownListBox.Items)
            {
                var package = item as PackageItemListViewModel;

                // note that item could be the loading indicator, thus we need to check
                // for null here.
                if (package != null)
                {
                    package.Selected = true;
                }
            }
        }

        private void SelectAllPackagesCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            foreach (var item in CurrentlyShownListBox.Items)
            {
                var package = item as PackageItemListViewModel;
                if (package != null)
                {
                    package.Selected = false;
                }
            }
        }

        private void _updateButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedPackages = InstalledPackageItemsFiltered.Where(p => p.Selected).ToArray();
            UpdateButtonClicked(selectedPackages);
        }

        private void List_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // toggle the selection state when user presses the space bar
            var package = CurrentlyShownListBox.SelectedItem as PackageItemListViewModel;
            if (package != null && e.Key == Key.Space)
            {
                package.Selected = !package.Selected;
                e.Handled = true;
            }
        }

        private void _loadingStatusBar_ShowMoreResultsClick(object sender, RoutedEventArgs e)
        {
            var packageItems = _loader?.GetCurrent() ?? Enumerable.Empty<PackageItemListViewModel>();
            UpdatePackageList(CurrentlyShownListBox, CurrentlyShownItems, packageItems, refresh: true);
            _loadingStatusBar.ItemsLoaded = _loader?.State.ItemsCount ?? 0;

            var desiredVisibility = EvaluateStatusBarVisibility(_loader, _loader.State);
            if (_loadingStatusBar.Visibility != desiredVisibility)
            {
                _loadingStatusBar.Visibility = desiredVisibility;
            }
        }

        private void _loadingStatusBar_DismissClick(object sender, RoutedEventArgs e)
        {
            _loadingStatusBar.Visibility = Visibility.Hidden;
        }

        public void ResetLoadingStatusIndicator()
        {
            _loadingStatusIndicator.Reset(string.Empty);
        }
    }
}
