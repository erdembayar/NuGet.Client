﻿using NuGet.Client;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.PackagingCore;
using NuGet.ProjectManagement;
using NuGet.Resolver;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NuGet.PackageManagement
{
    /// <summary>
    /// NuGetPackageManager orchestrates a nuget package operation such as an install or uninstall
    /// It is to be called by various NuGet Clients including the custom third-party ones
    /// </summary>
    public class NuGetPackageManager
    {
        /// <summary>
        /// Event to be raised while installing a package
        /// </summary>
        public event EventHandler<PackageOperationEventArgs> PackageInstalling;
        /// <summary>
        /// Event to be raised while installing a package
        /// </summary>
        public event EventHandler<PackageOperationEventArgs> PackageInstalled;
        /// <summary>
        /// Event to be raised while installing a package
        /// </summary>
        public event EventHandler<PackageOperationEventArgs> PackageUninstalling;
        /// <summary>
        /// Event to be raised while installing a package
        /// </summary>
        public event EventHandler<PackageOperationEventArgs> PackageUninstalled;

        private ISourceRepositoryProvider SourceRepositoryProvider { get; set; }

        private ISolutionManager SolutionManager { get; set; }

        private ISettings Settings { get; set; }

        public FolderNuGetProject PackagesFolderNuGetProject { get; set; }

        public SourceRepository PackagesFolderSourceRepository { get; set; }
      
        /// <summary>
        /// To construct a NuGetPackageManager that does not need a SolutionManager like NuGet.exe
        /// </summary>
        public NuGetPackageManager(ISourceRepositoryProvider sourceRepositoryProvider, string packagesFolderPath)
        {
            InitializeMandatory(sourceRepositoryProvider);
            if(packagesFolderPath == null)
            {
                throw new ArgumentNullException("packagesFolderPath");
            }

            InitializePackagesFolderInfo(packagesFolderPath);
        }

        /// <summary>
        /// To construct a NuGetPackageManager with a mandatory SolutionManager lke VS
        /// </summary>
        public NuGetPackageManager(ISourceRepositoryProvider sourceRepositoryProvider, ISettings settings, ISolutionManager solutionManager/*, IPackageResolver packageResolver */)
        {
            InitializeMandatory(sourceRepositoryProvider);
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            if(solutionManager == null)
            {
                throw new ArgumentNullException("solutionManager");
            }

            Settings = settings;
            SolutionManager = solutionManager;

            InitializePackagesFolderInfo(PackagesFolderPathUtility.GetPackagesFolderPath(SolutionManager, Settings));
        }

        private void InitializeMandatory(ISourceRepositoryProvider sourceRepositoryProvider)
        {
            if (sourceRepositoryProvider == null)
            {
                throw new ArgumentNullException("sourceRepositoryProvider");
            }

            SourceRepositoryProvider = sourceRepositoryProvider;
        }

        private void InitializePackagesFolderInfo(string packagesFolderPath)
        {
            PackagesFolderNuGetProject = new FolderNuGetProject(packagesFolderPath);
            PackagesFolderSourceRepository = SourceRepositoryProvider.CreateRepository(new PackageSource(packagesFolderPath));
        }

        /// <summary>
        /// Installs the latest version of the given <param name="packageId"></param> to NuGetProject <param name="nuGetProject"></param>
        /// <param name="resolutionContext"></param> and <param name="nuGetProjectContext"></param> are used in the process
        /// </summary>
        public async Task InstallPackageAsync(NuGetProject nuGetProject, string packageId, ResolutionContext resolutionContext,
            INuGetProjectContext nuGetProjectContext, SourceRepository primarySourceRepository,
            IEnumerable<SourceRepository> secondarySources, CancellationToken token)
        {
            await InstallPackageAsync(nuGetProject, packageId, resolutionContext, nuGetProjectContext,
                new List<SourceRepository>() { primarySourceRepository }, secondarySources, token);
        }

        /// <summary>
        /// Installs the latest version of the given <param name="packageId"></param> to NuGetProject <param name="nuGetProject"></param>
        /// <param name="resolutionContext"></param> and <param name="nuGetProjectContext"></param> are used in the process
        /// </summary>
        public async Task InstallPackageAsync(NuGetProject nuGetProject, string packageId, ResolutionContext resolutionContext,
            INuGetProjectContext nuGetProjectContext, IEnumerable<SourceRepository> primarySources,
            IEnumerable<SourceRepository> secondarySources, CancellationToken token)
        {
            // Step-1 : Get latest version for packageId
            var latestVersion = await GetLatestVersionAsync(packageId, resolutionContext, primarySources, token);

            if (latestVersion == null)
            {
                throw new InvalidOperationException(String.Format(Strings.NoLatestVersionFound, packageId));
            }

            // Step-2 : Call InstallPackageAsync(project, packageIdentity)
            await InstallPackageAsync(nuGetProject, new PackageIdentity(packageId, latestVersion), resolutionContext,
                nuGetProjectContext, primarySources, secondarySources, token);
        }

        /// <summary>
        /// Installs given <param name="packageIdentity"></param> to NuGetProject <param name="nuGetProject"></param>
        /// <param name="resolutionContext"></param> and <param name="nuGetProjectContext"></param> are used in the process
        /// </summary>
        public async Task InstallPackageAsync(NuGetProject nuGetProject, PackageIdentity packageIdentity, ResolutionContext resolutionContext,
            INuGetProjectContext nuGetProjectContext, SourceRepository primarySourceRepository,
            IEnumerable<SourceRepository> secondarySources, CancellationToken token)
        {
            await InstallPackageAsync(nuGetProject, packageIdentity, resolutionContext, nuGetProjectContext,
                new List<SourceRepository>() { primarySourceRepository }, secondarySources, token);
        }

        /// <summary>
        /// Installs given <param name="packageIdentity"></param> to NuGetProject <param name="nuGetProject"></param>
        /// <param name="resolutionContext"></param> and <param name="nuGetProjectContext"></param> are used in the process
        /// </summary>
        public async Task InstallPackageAsync(NuGetProject nuGetProject, PackageIdentity packageIdentity, ResolutionContext resolutionContext,
            INuGetProjectContext nuGetProjectContext, IEnumerable<SourceRepository> primarySources,
            IEnumerable<SourceRepository> secondarySources, CancellationToken token)
        {
            // Step-1 : Call PreviewInstallPackageAsync to get all the nuGetProjectActions
            var nuGetProjectActions = await PreviewInstallPackageAsync(nuGetProject, packageIdentity, resolutionContext,
                nuGetProjectContext, primarySources, secondarySources, token);

            // Step-2 : Execute all the nuGetProjectActions
            await ExecuteNuGetProjectActionsAsync(nuGetProject, nuGetProjectActions, nuGetProjectContext, token);
        }

        public async Task UninstallPackageAsync(NuGetProject nuGetProject, string packageId, UninstallationContext uninstallationContext,
            INuGetProjectContext nuGetProjectContext, CancellationToken token)
        {
            // Step-1 : Call PreviewUninstallPackagesAsync to get all the nuGetProjectActions
            var nuGetProjectActions = await PreviewUninstallPackageAsync(nuGetProject, packageId, uninstallationContext, nuGetProjectContext, token);

            // Step-2 : Execute all the nuGetProjectActions
            await ExecuteNuGetProjectActionsAsync(nuGetProject, nuGetProjectActions, nuGetProjectContext, token);
        }

        /// <summary>
        /// Gives the preview as a list of NuGetProjectActions that will be performed to install <param name="packageId"></param> into <param name="nuGetProject"></param>
        /// <param name="resolutionContext"></param> and <param name="nuGetProjectContext"></param> are used in the process
        /// </summary>
        public async Task<IEnumerable<NuGetProjectAction>> PreviewInstallPackageAsync(NuGetProject nuGetProject, string packageId,
            ResolutionContext resolutionContext, INuGetProjectContext nuGetProjectContext,
            SourceRepository primarySourceRepository, IEnumerable<SourceRepository> secondarySources, CancellationToken token)
        {
            if (nuGetProject == null)
            {
                throw new ArgumentNullException("nuGetProject");
            }

            if (packageId == null)
            {
                throw new ArgumentNullException("packageId");
            }

            if (resolutionContext == null)
            {
                throw new ArgumentNullException("resolutionContext");
            }

            if (nuGetProjectContext == null)
            {
                throw new ArgumentNullException("nuGetProjectContext");
            }
            // Step-1 : Get latest version for packageId
            var latestVersion = await GetLatestVersionAsync(packageId, resolutionContext, primarySourceRepository, token);

            if (latestVersion == null)
            {
                throw new InvalidOperationException(String.Format(CultureInfo.CurrentCulture, Strings.UnknownPackage, packageId));
            }

            // Step-2 : Call InstallPackage(project, packageIdentity)
            return await PreviewInstallPackageAsync(nuGetProject, new PackageIdentity(packageId, latestVersion), resolutionContext,
                nuGetProjectContext, primarySourceRepository, secondarySources, token);
        }

        public async Task<IEnumerable<NuGetProjectAction>> PreviewUpdatePackagesAsync(IEnumerable<string> packageIdsToInstall, NuGetProject nuGetProject,
            ResolutionContext resolutionContext, INuGetProjectContext nuGetProjectContext,
            SourceRepository primarySourceRepository, IEnumerable<SourceRepository> secondarySources,
            CancellationToken token)
        {
            if (packageIdsToInstall == null)
            {
                throw new ArgumentNullException("packageIdsToInstall");
            }

            if (nuGetProject == null)
            {
                throw new ArgumentNullException("nuGetProject");
            }

            if (resolutionContext == null)
            {
                throw new ArgumentNullException("resolutionContext");
            }

            if (nuGetProjectContext == null)
            {
                throw new ArgumentNullException("nuGetProjectContext");
            }

            if (packageIdsToInstall.Any(p => String.IsNullOrEmpty(p)))
            {
                throw new ArgumentException("packageIdsToInstall");
            }

            if (primarySourceRepository == null)
            {
                throw new ArgumentNullException("primarySourceRepository");
            }
            var primarySources = new List<SourceRepository>() { primarySourceRepository };

            if (secondarySources == null)
            {
                secondarySources = SourceRepositoryProvider.GetRepositories().Where(e => e.PackageSource.IsEnabled);
            }

            var projectInstalledPackageReferences = await nuGetProject.GetInstalledPackagesAsync(token);
            var oldListOfInstalledPackages = projectInstalledPackageReferences.Select(p => p.PackageIdentity);

            // Note: resolver needs all the installed packages as targets too. And, metadata should be gathered for the installed packages as well
            var packageTargetIdsForResolver = new HashSet<string>(oldListOfInstalledPackages.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var packageIdToInstall in packageIdsToInstall)
            {
                packageTargetIdsForResolver.Add(packageIdToInstall);
            }

            List<NuGetProjectAction> nuGetProjectActions = new List<NuGetProjectAction>();
            // TODO: these sources should be ordered
            // TODO: search in only the active source but allow dependencies to come from other sources?

            var effectiveSources = GetEffectiveSources(primarySources, secondarySources);

            try
            {
                // Step-1 : Get metadata resources using gatherer
                var targetFramework = nuGetProject.GetMetadata<NuGetFramework>(NuGetProjectMetadataKeys.TargetFramework);
                nuGetProjectContext.Log(MessageLevel.Info, Strings.AttemptingToGatherDependencyInfoForMultiplePackages, targetFramework);
                var availablePackageDependencyInfoWithSourceSet = await ResolverGather.GatherPackageDependencyInfo(resolutionContext,
                    packageIdsToInstall,
                    packageTargetIdsForResolver,
                    targetFramework,
                    primarySources,
                    effectiveSources,
                    token);

                if (!availablePackageDependencyInfoWithSourceSet.Any())
                {
                    throw new InvalidOperationException(Strings.UnableToGatherDependencyInfoForMultiplePackages);
                }

                // Step-2 : Call IPackageResolver.Resolve to get new list of installed packages                
                // TODO: Consider using IPackageResolver once it is extensible
                var packageResolver = new PackageResolver(resolutionContext.DependencyBehavior);
                nuGetProjectContext.Log(MessageLevel.Info, Strings.AttemptingToResolveDependenciesForMultiplePackages);
                IEnumerable<PackageIdentity> newListOfInstalledPackages = packageResolver.Resolve(packageTargetIdsForResolver, availablePackageDependencyInfoWithSourceSet,
                    Enumerable.Empty<PackageReference>(), token);
                if (newListOfInstalledPackages == null)
                {
                    throw new InvalidOperationException(Strings.UnableToResolveDependencyInfoForMultiplePackages);
                }

                nuGetProjectActions = GetProjectActionsForUpdate(newListOfInstalledPackages, oldListOfInstalledPackages, availablePackageDependencyInfoWithSourceSet, nuGetProjectContext);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (AggregateException)
            {
                throw;
            }
            catch (NuGetResolverConstraintException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (String.IsNullOrEmpty(ex.Message))
                {
                    throw new InvalidOperationException(Strings.PackagesCouldNotBeInstalled, ex);
                }
                else
                {
                    throw new InvalidOperationException(ex.Message, ex);
                }
            }
            return nuGetProjectActions;
        }

        public async Task<IEnumerable<NuGetProjectAction>> PreviewUpdatePackagesAsync(IEnumerable<PackageIdentity> packagesToInstall, NuGetProject nuGetProject,
            ResolutionContext resolutionContext, INuGetProjectContext nuGetProjectContext,
            SourceRepository primarySourceRepository, IEnumerable<SourceRepository> secondarySources,
            CancellationToken token)
        {
            if(packagesToInstall == null)
            {
                throw new ArgumentNullException("packagesToInstall");
            }

            if (nuGetProject == null)
            {
                throw new ArgumentNullException("nuGetProject");
            }

            if (resolutionContext == null)
            {
                throw new ArgumentNullException("resolutionContext");
            }

            if (nuGetProjectContext == null)
            {
                throw new ArgumentNullException("nuGetProjectContext");
            }

            if(packagesToInstall.Any(p => p.Version == null))
            {
                throw new ArgumentException("packagesToInstall");
            }

            if (primarySourceRepository == null)
            {
                throw new ArgumentNullException("primarySourceRepository");
            }
            var primarySources = new List<SourceRepository>() { primarySourceRepository };

            if (secondarySources == null)
            {
                secondarySources = SourceRepositoryProvider.GetRepositories().Where(e => e.PackageSource.IsEnabled);
            }

            var projectInstalledPackageReferences = await nuGetProject.GetInstalledPackagesAsync(token);
            var oldListOfInstalledPackages = projectInstalledPackageReferences.Select(p => p.PackageIdentity);

            // Note: resolver needs all the installed packages as targets too. And, metadata should be gathered for the installed packages as well
            var packageTargetsForResolver = new HashSet<PackageIdentity>(oldListOfInstalledPackages, PackageIdentity.Comparer);
            foreach(var packageToInstall in packagesToInstall)
            {
                packageTargetsForResolver.Add(packageToInstall);
            }

            List<NuGetProjectAction> nuGetProjectActions = new List<NuGetProjectAction>();
            // TODO: these sources should be ordered
            // TODO: search in only the active source but allow dependencies to come from other sources?

            var effectiveSources = GetEffectiveSources(primarySources, secondarySources);

            try
            {
                // Step-1 : Get metadata resources using gatherer
                var targetFramework = nuGetProject.GetMetadata<NuGetFramework>(NuGetProjectMetadataKeys.TargetFramework);
                nuGetProjectContext.Log(MessageLevel.Info, Strings.AttemptingToGatherDependencyInfoForMultiplePackages, targetFramework);
                var availablePackageDependencyInfoWithSourceSet = await ResolverGather.GatherPackageDependencyInfo(resolutionContext,
                    packagesToInstall,
                    packageTargetsForResolver,
                    targetFramework,
                    primarySources,
                    effectiveSources,
                    token);

                if (!availablePackageDependencyInfoWithSourceSet.Any())
                {
                    throw new InvalidOperationException(Strings.UnableToGatherDependencyInfoForMultiplePackages);
                }

                // Step-2 : Call IPackageResolver.Resolve to get new list of installed packages                
                // TODO: Consider using IPackageResolver once it is extensible
                var packageResolver = new PackageResolver(resolutionContext.DependencyBehavior);
                nuGetProjectContext.Log(MessageLevel.Info, Strings.AttemptingToResolveDependenciesForMultiplePackages);
                IEnumerable<PackageIdentity> newListOfInstalledPackages = packageResolver.Resolve(packageTargetsForResolver, availablePackageDependencyInfoWithSourceSet, projectInstalledPackageReferences, token);
                if (newListOfInstalledPackages == null)
                {
                    throw new InvalidOperationException(Strings.UnableToResolveDependencyInfoForMultiplePackages);
                }

                nuGetProjectActions = GetProjectActionsForUpdate(newListOfInstalledPackages, oldListOfInstalledPackages, availablePackageDependencyInfoWithSourceSet, nuGetProjectContext);
            }
            catch(InvalidOperationException)
            {
                throw;
            }
            catch (AggregateException)
            {
                throw;
            }
            catch (NuGetResolverConstraintException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if(String.IsNullOrEmpty(ex.Message))
                {
                    throw new InvalidOperationException(Strings.PackagesCouldNotBeInstalled, ex);
                }
                else
                {
                    throw new InvalidOperationException(ex.Message, ex);
                }                
            }
            return nuGetProjectActions;
        }

        // TODO: Convert this to a generic GetProjectActions and use it from Install methods too
        private List<NuGetProjectAction> GetProjectActionsForUpdate(IEnumerable<PackageIdentity> newListOfInstalledPackages,
            IEnumerable<PackageIdentity> oldListOfInstalledPackages,
            HashSet<SourceDependencyInfo> availablePackageDependencyInfoWithSourceSet,
            INuGetProjectContext nuGetProjectContext)
        {
            // Step-3 : Get the list of nuGetProjectActions to perform, install/uninstall on the nugetproject
            // based on newPackages obtained in Step-2 and project.GetInstalledPackages

            List<NuGetProjectAction> nuGetProjectActions = new List<NuGetProjectAction>();
            nuGetProjectContext.Log(MessageLevel.Info, Strings.ResolvingActionsToInstallOrUpdateMultiplePackages);
            var newPackagesToUninstall = oldListOfInstalledPackages
                .Where(op => newListOfInstalledPackages
                    .Where(np => op.Id.Equals(np.Id, StringComparison.OrdinalIgnoreCase) && !op.Version.Equals(np.Version)).Any());
            var newPackagesToInstall = newListOfInstalledPackages.Where(p => !oldListOfInstalledPackages.Contains(p));

            foreach (PackageIdentity newPackageToUninstall in newPackagesToUninstall)
            {
                nuGetProjectActions.Add(NuGetProjectAction.CreateUninstallProjectAction(newPackageToUninstall));
            }

            var comparer = PackageIdentity.Comparer;

            foreach (PackageIdentity newPackageToInstall in newPackagesToInstall)
            {
                // find the package match based on identity
                SourceDependencyInfo sourceDepInfo = availablePackageDependencyInfoWithSourceSet.Where(p => comparer.Equals(p, newPackageToInstall)).SingleOrDefault();

                if (sourceDepInfo == null)
                {
                    // this really should never happen
                    throw new InvalidOperationException(String.Format(Strings.PackageNotFound, newPackageToInstall));
                }

                nuGetProjectActions.Add(NuGetProjectAction.CreateInstallProjectAction(newPackageToInstall, sourceDepInfo.Source));
            }

            return nuGetProjectActions;
        }


        /// <summary>
        /// Gives the preview as a list of NuGetProjectActions that will be performed to install <param name="packageIdentity"></param> into <param name="nuGetProject"></param>
        /// <param name="resolutionContext"></param> and <param name="nuGetProjectContext"></param> are used in the process
        /// </summary>
        public async Task<IEnumerable<NuGetProjectAction>> PreviewInstallPackageAsync(NuGetProject nuGetProject, PackageIdentity packageIdentity,
            ResolutionContext resolutionContext, INuGetProjectContext nuGetProjectContext,
            SourceRepository primarySourceRepository, IEnumerable<SourceRepository> secondarySources, CancellationToken token)
        {
            if (nuGetProject is ProjectManagement.Projects.ProjectKNuGetProjectBase)
            {
                var action = NuGetProjectAction.CreateInstallProjectAction(packageIdentity, primarySourceRepository);
                return new NuGetProjectAction[] { action };
            }

            var primarySources = new List<SourceRepository>() { primarySourceRepository };
            return await PreviewInstallPackageAsync(nuGetProject, packageIdentity, resolutionContext,
                nuGetProjectContext, primarySources, secondarySources, token);
        }

        public async Task<IEnumerable<NuGetProjectAction>> PreviewInstallPackageAsync(NuGetProject nuGetProject, PackageIdentity packageIdentity,
            ResolutionContext resolutionContext, INuGetProjectContext nuGetProjectContext,
            IEnumerable<SourceRepository> primarySources, IEnumerable<SourceRepository> secondarySources,
            CancellationToken token)
        {
            if(nuGetProject == null)
            {
                throw new ArgumentNullException("nuGetProject");
            }

            if (packageIdentity == null)
            {
                throw new ArgumentNullException("packageIdentity");
            }

            if(resolutionContext == null)
            {
                throw new ArgumentNullException("resolutionContext");
            }

            if(nuGetProjectContext == null)
            {
                throw new ArgumentNullException("nuGetProjectContext");
            }

            if (primarySources == null)
            {
                throw new ArgumentNullException("primarySources");
            }

            if (secondarySources == null)
            {
                secondarySources = SourceRepositoryProvider.GetRepositories().Where(e => e.PackageSource.IsEnabled);
            }

            if(!primarySources.Any())
            {
                throw new ArgumentException("primarySources");
            }

            if(packageIdentity.Version == null)
            {
                throw new ArgumentNullException("packageIdentity.Version");
            }

            // TODO: BUGBUG: HACK: Multiple primary repositories is mainly intended for nuget.exe at the moment
            // The following special case for ProjectK is not correct, if they used nuget.exe
            // and multiple repositories in the -Source switch
            if (nuGetProject is ProjectManagement.Projects.ProjectKNuGetProjectBase)
            {
                var action = NuGetProjectAction.CreateInstallProjectAction(packageIdentity, primarySources.First());
                return new NuGetProjectAction[] { action };
            }

            var projectInstalledPackageReferences = await nuGetProject.GetInstalledPackagesAsync(token);
            var oldListOfInstalledPackages = projectInstalledPackageReferences.Select(p => p.PackageIdentity);
            if(oldListOfInstalledPackages.Any(p => p.Equals(packageIdentity)))
            {
                string projectName;
                nuGetProject.TryGetMetadata<string>(NuGetProjectMetadataKeys.Name, out projectName);
                throw new InvalidOperationException(String.Format(NuGet.ProjectManagement.Strings.PackageAlreadyExistsInProject, packageIdentity, projectName ?? String.Empty));
            }

            List<NuGetProjectAction> nuGetProjectActions = new List<NuGetProjectAction>();
            // TODO: these sources should be ordered
            // TODO: search in only the active source but allow dependencies to come from other sources?

            var effectiveSources = GetEffectiveSources(primarySources, secondarySources);
            
            if (resolutionContext.DependencyBehavior != DependencyBehavior.Ignore)
            {
                try
                {
                    var packageTargetsForResolver = new HashSet<PackageIdentity>(oldListOfInstalledPackages, PackageIdentity.Comparer);
                    // Note: resolver needs all the installed packages as targets too. And, metadata should be gathered for the installed packages as well
                    var installedPackageWithSameId = packageTargetsForResolver.Where(p => p.Id.Equals(packageIdentity.Id, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
                    if(installedPackageWithSameId != null)
                    {
                        packageTargetsForResolver.Remove(installedPackageWithSameId);
                    }
                    packageTargetsForResolver.Add(packageIdentity);

                    // Step-1 : Get metadata resources using gatherer
                    var targetFramework = nuGetProject.GetMetadata<NuGetFramework>(NuGetProjectMetadataKeys.TargetFramework);
                    nuGetProjectContext.Log(MessageLevel.Info, Strings.AttemptingToGatherDependencyInfo, packageIdentity, targetFramework);

                    var primaryPackages = new List<PackageIdentity>() { packageIdentity };

                    var availablePackageDependencyInfoWithSourceSet = await ResolverGather.GatherPackageDependencyInfo(resolutionContext,
                        primaryPackages,
                        packageTargetsForResolver,
                        targetFramework,
                        primarySources,
                        effectiveSources,
                        token);

                    if (!availablePackageDependencyInfoWithSourceSet.Any())
                    {
                        throw new InvalidOperationException(String.Format(Strings.UnableToGatherDependencyInfo, packageIdentity));
                    }

                    // Step-2 : Call IPackageResolver.Resolve to get new list of installed packages
                    // TODO: Consider using IPackageResolver once it is extensible
                    var packageResolver = new PackageResolver(resolutionContext.DependencyBehavior);
                    nuGetProjectContext.Log(MessageLevel.Info, Strings.AttemptingToResolveDependencies, packageIdentity, resolutionContext.DependencyBehavior);

                    // Note: resolver prefers installed package versions if the satisfy the dependency version constraints
                    // So, since we want an exact version of a package, create a new list of installed packages where the packageIdentity being installed
                    // is present after removing the one with the same id
                    var preferredPackageReferences = new List<PackageReference>(projectInstalledPackageReferences.Where(pr =>
                        !pr.PackageIdentity.Id.Equals(packageIdentity.Id, StringComparison.OrdinalIgnoreCase)));
                    preferredPackageReferences.Add(new PackageReference(packageIdentity, targetFramework));

                    IEnumerable<PackageIdentity> newListOfInstalledPackages = packageResolver.Resolve(packageTargetsForResolver,
                        availablePackageDependencyInfoWithSourceSet,
                        preferredPackageReferences,
                        token);
                    if (newListOfInstalledPackages == null)
                    {
                        throw new InvalidOperationException(String.Format(Strings.UnableToResolveDependencyInfo, packageIdentity, resolutionContext.DependencyBehavior));
                    }

                    // Step-3 : Get the list of nuGetProjectActions to perform, install/uninstall on the nugetproject
                    // based on newPackages obtained in Step-2 and project.GetInstalledPackages                    

                    nuGetProjectContext.Log(MessageLevel.Info, Strings.ResolvingActionsToInstallPackage, packageIdentity);
                    var newPackagesToUninstall = oldListOfInstalledPackages
                        .Where(op => newListOfInstalledPackages
                            .Where(np => op.Id.Equals(np.Id, StringComparison.OrdinalIgnoreCase) && !op.Version.Equals(np.Version)).Any());
                    var newPackagesToInstall = newListOfInstalledPackages.Where(p => !oldListOfInstalledPackages.Contains(p));

                    foreach (PackageIdentity newPackageToUninstall in newPackagesToUninstall)
                    {
                        nuGetProjectActions.Add(NuGetProjectAction.CreateUninstallProjectAction(newPackageToUninstall));
                    }

                    var comparer = PackageIdentity.Comparer;

                    foreach (PackageIdentity newPackageToInstall in newPackagesToInstall)
                    {
                        // find the package match based on identity
                        SourceDependencyInfo sourceDepInfo = availablePackageDependencyInfoWithSourceSet.Where(p => comparer.Equals(p, newPackageToInstall)).SingleOrDefault();

                        if (sourceDepInfo == null)
                        {
                            // this really should never happen
                            throw new InvalidOperationException(String.Format(Strings.PackageNotFound, packageIdentity));
                        }

                        nuGetProjectActions.Add(NuGetProjectAction.CreateInstallProjectAction(newPackageToInstall, sourceDepInfo.Source));
                    }
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch (AggregateException)
                {
                    throw;
                }
                catch (NuGetResolverException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (String.IsNullOrEmpty(ex.Message))
                    {
                        throw new InvalidOperationException(String.Format(Strings.PackageCouldNotBeInstalled, packageIdentity), ex);
                    }
                    else
                    {
                        throw new InvalidOperationException(ex.Message, ex);
                    }
                }
            }
            else
            {
                var sourceRepository = await GetSourceRepository(packageIdentity, effectiveSources);
                nuGetProjectActions.Add(NuGetProjectAction.CreateInstallProjectAction(packageIdentity, sourceRepository));
            }

            nuGetProjectContext.Log(MessageLevel.Info, Strings.ResolvedActionsToInstallPackage, packageIdentity);
            return nuGetProjectActions;
        }

        private static async Task<SourceRepository> GetSourceRepository(PackageIdentity packageIdentity, IEnumerable<SourceRepository> sourceRepositories)
        {
            foreach (var sourceRepository in sourceRepositories)
            {
                try
                {
                    var metadataResource = await sourceRepository.GetResourceAsync<MetadataResource>();
                    if (metadataResource != null)
                    {
                        if (await metadataResource.Exists(packageIdentity, CancellationToken.None))
                        {
                            return sourceRepository;
                        }
                    }
                }
                catch (Exception)
                {
                    Debug.Fail("Error finding repository");
                }
            }

            throw new InvalidOperationException(String.Format(CultureInfo.CurrentCulture, Strings.UnknownPackageSpecificVersion, packageIdentity.Id, packageIdentity.Version));
        }

        /// <summary>
        /// Gives the preview as a list of NuGetProjectActions that will be performed to uninstall <param name="packageId"></param> into <param name="nuGetProject"></param>
        /// <param name="uninstallationContext"></param> and <param name="nuGetProjectContext"></param> are used in the process
        /// </summary>
        public async Task<IEnumerable<NuGetProjectAction>> PreviewUninstallPackageAsync(NuGetProject nuGetProject, string packageId,
            UninstallationContext uninstallationContext, INuGetProjectContext nuGetProjectContext, CancellationToken token)
        {
            if(nuGetProject == null)
            {
                throw new ArgumentNullException("nuGetProject");
            }

            if(packageId == null)
            {
                throw new ArgumentNullException("packageId");
            }

            if(uninstallationContext == null)
            {
                throw new ArgumentNullException("uninstallationContext");
            }

            if(nuGetProjectContext == null)
            {
                throw new ArgumentNullException("nuGetProjectContext");
            }

            // Step-1: Get the packageIdentity corresponding to packageId and check if it exists to be uninstalled
            var installedPackages = await nuGetProject.GetInstalledPackagesAsync(token);
            PackageReference packageReference = installedPackages
                .Where(pr => pr.PackageIdentity.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            if (packageReference == null || packageReference.PackageIdentity == null)
            {
                throw new ArgumentException(String.Format(Strings.PackageToBeUninstalledCouldNotBeFound,
                    packageId, nuGetProject.GetMetadata<string>(NuGetProjectMetadataKeys.Name)));
            }

            return await PreviewUninstallPackageAsyncPrivate(nuGetProject, packageReference, uninstallationContext, nuGetProjectContext, token);
        }

        /// <summary>
        /// Gives the preview as a list of NuGetProjectActions that will be performed to uninstall <param name="packageId"></param> into <param name="nuGetProject"></param>
        /// <param name="uninstallationContext"></param> and <param name="nuGetProjectContext"></param> are used in the process
        /// </summary>
        public async Task<IEnumerable<NuGetProjectAction>> PreviewUninstallPackageAsync(NuGetProject nuGetProject, PackageIdentity packageIdentity,
            UninstallationContext uninstallationContext, INuGetProjectContext nuGetProjectContext, CancellationToken token)
        {
            if (nuGetProject == null)
            {
                throw new ArgumentNullException("nuGetProject");
            }

            if (packageIdentity == null)
            {
                throw new ArgumentNullException("packageIdentity");
            }

            if (uninstallationContext == null)
            {
                throw new ArgumentNullException("uninstallationContext");
            }

            if (nuGetProjectContext == null)
            {
                throw new ArgumentNullException("nuGetProjectContext");
            }

            // Step-1: Get the packageIdentity corresponding to packageId and check if it exists to be uninstalled
            var installedPackages = await nuGetProject.GetInstalledPackagesAsync(token);
            PackageReference packageReference = installedPackages
                .Where(pr => pr.PackageIdentity.Equals(packageIdentity)).FirstOrDefault();
            if (packageReference == null || packageReference.PackageIdentity == null)
            {
                throw new ArgumentException(String.Format(Strings.PackageToBeUninstalledCouldNotBeFound,
                    packageIdentity.Id, nuGetProject.GetMetadata<string>(NuGetProjectMetadataKeys.Name)));
            }

            return await PreviewUninstallPackageAsyncPrivate(nuGetProject, packageReference, uninstallationContext, nuGetProjectContext, token);
        }

        private async Task<IEnumerable<NuGetProjectAction>> PreviewUninstallPackageAsyncPrivate(NuGetProject nuGetProject, PackageReference packageReference,
            UninstallationContext uninstallationContext, INuGetProjectContext nuGetProjectContext, CancellationToken token)
        {
            if(SolutionManager == null)
            {
                throw new InvalidOperationException(Strings.SolutionManagerNotAvailableForUninstall);
            }

            if (nuGetProject is ProjectManagement.Projects.ProjectKNuGetProjectBase)
            {
                var action = NuGetProjectAction.CreateUninstallProjectAction(packageReference.PackageIdentity);
                return new NuGetProjectAction[] { action };
            }

            // Step-1 : Get the metadata resources from "packages" folder or custom repository path
            var packageIdentity = packageReference.PackageIdentity;
            var packageReferenceTargetFramework = packageReference.TargetFramework;
            nuGetProjectContext.Log(MessageLevel.Info, Strings.AttemptingToGatherDependencyInfo, packageIdentity, packageReferenceTargetFramework);

            // TODO: IncludePrerelease is a big question mark
            var installedPackageIdentities = (await nuGetProject.GetInstalledPackagesAsync(token)).Select(pr => pr.PackageIdentity);
            var dependencyInfoFromPackagesFolder = await GetDependencyInfoFromPackagesFolder(installedPackageIdentities,
                packageReferenceTargetFramework, includePrerelease: true);

            nuGetProjectContext.Log(MessageLevel.Info, Strings.ResolvingActionsToUninstallPackage, packageIdentity);
            // Step-2 : Determine if the package can be uninstalled based on the metadata resources
            var packagesToBeUninstalled = UninstallResolver.GetPackagesToBeUninstalled(packageIdentity, dependencyInfoFromPackagesFolder, installedPackageIdentities, uninstallationContext);

            var nuGetProjectActions = packagesToBeUninstalled.Select(p => NuGetProjectAction.CreateUninstallProjectAction(p));

            nuGetProjectContext.Log(MessageLevel.Info, Strings.ResolvedActionsToUninstallPackage, packageIdentity);
            return nuGetProjectActions;
        }

        private async Task<IEnumerable<PackageDependencyInfo>> GetDependencyInfoFromPackagesFolder(IEnumerable<PackageIdentity> packageIdentities,
            NuGetFramework nuGetFramework,
            bool includePrerelease)
        {
            try
            {
                var dependencyInfoResource = await PackagesFolderSourceRepository.GetResourceAsync<DepedencyInfoResource>();
                return await dependencyInfoResource.ResolvePackages(packageIdentities, nuGetFramework, includePrerelease);
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        /// <summary>
        /// Executes the list of <param name="nuGetProjectActions"></param> on <param name="nuGetProject"></param>, which is likely obtained by calling into PreviewInstallPackageAsync
        /// <param name="nuGetProjectContext"></param> is used in the process
        /// </summary>
        public async Task ExecuteNuGetProjectActionsAsync(NuGetProject nuGetProject, IEnumerable<NuGetProjectAction> nuGetProjectActions,
            INuGetProjectContext nuGetProjectContext, CancellationToken token)
        {
            if (nuGetProject == null)
            {
                throw new ArgumentNullException("nuGetProject");
            }

            if (nuGetProjectActions == null)
            {
                throw new ArgumentNullException("nuGetProjectActions");
            }

            if (nuGetProjectContext == null)
            {
                throw new ArgumentNullException("nuGetProjectContext");
            }

            foreach (NuGetProjectAction nuGetProjectAction in nuGetProjectActions)
            {
                if (nuGetProjectAction.NuGetProjectActionType == NuGetProjectActionType.Uninstall)
                {
                    await ExecuteUninstallAsync(nuGetProject, nuGetProjectAction.PackageIdentity, nuGetProjectContext, token);
                }
                else
                {
                    using (var targetPackageStream = new MemoryStream())
                    {
                        await PackageDownloader.GetPackageStream(nuGetProjectAction.SourceRepository, nuGetProjectAction.PackageIdentity, targetPackageStream, token);
                        await ExecuteInstallAsync(nuGetProject, nuGetProjectAction.PackageIdentity, targetPackageStream, nuGetProjectContext, token);
                    }
                }
            }
        }

        /// <summary>
        /// RestorePackage is only allowed on a folderNuGetProject. In most cases, one will simply use the packagesFolderPath from NuGetPackageManager
        /// to create a folderNuGetProject before calling into this method
        /// </summary>
        public async Task<bool> RestorePackageAsync(PackageIdentity packageIdentity, INuGetProjectContext nuGetProjectContext,
            IEnumerable<SourceRepository> sourceRepositories, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(PackageExistsInPackagesFolder(packageIdentity))
            {
                return false;
            }

            token.ThrowIfCancellationRequested();
            nuGetProjectContext.Log(MessageLevel.Info, String.Format(Strings.RestoringPackage, packageIdentity));
            var enabledSources = (sourceRepositories != null && sourceRepositories.Any()) ? sourceRepositories :
                SourceRepositoryProvider.GetRepositories().Where(e => e.PackageSource.IsEnabled);
            var sourceRepository = await GetSourceRepository(packageIdentity, enabledSources);

            token.ThrowIfCancellationRequested();
            using (var targetPackageStream = new MemoryStream())
            {
                await PackageDownloader.GetPackageStream(sourceRepository, packageIdentity, targetPackageStream, token);
                // If you already downloaded the package, just restore it, don't cancel the operation now
                await PackagesFolderNuGetProject.InstallPackageAsync(packageIdentity, targetPackageStream, nuGetProjectContext, token);
            }

            return true;
        }

        public async Task<bool> CopySatelliteFilesAsync(PackageIdentity packageIdentity, INuGetProjectContext nuGetProjectContext, CancellationToken token)
        {
            return await PackagesFolderNuGetProject.CopySatelliteFilesAsync(packageIdentity, nuGetProjectContext, token);
        }

        public bool PackageExistsInPackagesFolder(PackageIdentity packageIdentity)
        {
            return PackagesFolderNuGetProject.PackageExists(packageIdentity);
        }

        private async Task ExecuteInstallAsync(NuGetProject nuGetProject, PackageIdentity packageIdentity, Stream packageStream,
            INuGetProjectContext nuGetProjectContext, CancellationToken token)
        {
            // TODO: MinClientVersion check should be performed in preview. Can easily avoid a lot of rollback
            MinClientVersionHandler.CheckMinClientVersion(packageStream, packageIdentity);

            var packageOperationEventArgs = new PackageOperationEventArgs(packageIdentity);
            if(PackageInstalling != null)
            {
                PackageInstalling(this, packageOperationEventArgs);
            }
            await nuGetProject.InstallPackageAsync(packageIdentity, packageStream, nuGetProjectContext, token);

            // TODO: Consider using CancelEventArgs instead of a regular EventArgs??
            //if (packageOperationEventArgs.Cancel)
            //{
            //    return;
            //}

            if(PackageInstalled != null)
            {
                PackageInstalled(this, packageOperationEventArgs);
            }
        }

        private async Task ExecuteUninstallAsync(NuGetProject nuGetProject, PackageIdentity packageIdentity,
            INuGetProjectContext nuGetProjectContext, CancellationToken token)
        {
            // Step-1: Raise package uninstalling event
            var packageOperationEventArgs = new PackageOperationEventArgs(packageIdentity);
            if (PackageUninstalling != null)
            {
                PackageUninstalling(this, packageOperationEventArgs);
            }

            // Step-2: Call nuGetProject.UninstallPackage
            await nuGetProject.UninstallPackageAsync(packageIdentity, nuGetProjectContext, token);

            // Step-3: Check if the package directory could be deleted
            if (!(nuGetProject is ProjectManagement.Projects.ProjectKNuGetProjectBase) &&
                !await PackageExistsInAnotherNuGetProject(nuGetProject, packageIdentity, nuGetProjectContext, token))
            {
                DeletePackageDirectory(packageIdentity, nuGetProjectContext);
            }

            // TODO: Consider using CancelEventArgs instead of a regular EventArgs??
            //if (packageOperationEventArgs.Cancel)
            //{
            //    return;
            //}

            // Step-4: Raise PackageUninstalled event
            if (PackageUninstalled != null)
            {
                PackageUninstalled(this, packageOperationEventArgs);
            }
        }

        private async Task<bool> PackageExistsInAnotherNuGetProject(NuGetProject nuGetProject, PackageIdentity packageIdentity,
            INuGetProjectContext nuGetProjectContext, CancellationToken token)
        {
            bool packageExistsInAnotherNuGetProject = false;
            var nuGetProjectName = nuGetProject.GetMetadata<string>(NuGetProjectMetadataKeys.Name);
            foreach (var otherNuGetProject in SolutionManager.GetNuGetProjects())
            {
                if (!otherNuGetProject.GetMetadata<string>(NuGetProjectMetadataKeys.Name).Equals(nuGetProjectName, StringComparison.OrdinalIgnoreCase))
                {
                    packageExistsInAnotherNuGetProject = (await otherNuGetProject.GetInstalledPackagesAsync(token)).Where(pr => pr.PackageIdentity.Equals(packageIdentity)).Any();
                }
            }

            return packageExistsInAnotherNuGetProject;
        }

        private bool DeletePackageDirectory(PackageIdentity packageIdentity, INuGetProjectContext nuGetProjectContext)
        {
            if(packageIdentity == null)
            {
                throw new ArgumentNullException("packageIdentity");
            }

            if(nuGetProjectContext == null)
            {
                throw new ArgumentNullException("nuGetProjectContext");
            }

            // TODO: Handle removing of satellite files from the runtime package also

            // 1. Check if the Package exists at root, if not, return false
            if (!PackagesFolderNuGetProject.PackageExists(packageIdentity))
            {
                nuGetProjectContext.Log(MessageLevel.Warning, NuGet.ProjectManagement.Strings.PackageDoesNotExistInFolder, packageIdentity, PackagesFolderNuGetProject.Root);
                return false;
            }

            nuGetProjectContext.Log(MessageLevel.Info, NuGet.ProjectManagement.Strings.RemovingPackageFromFolder, packageIdentity, PackagesFolderNuGetProject.Root);
            // 2. Delete the package folder and files from the root directory of this FileSystemNuGetProject
            // Remember that the following code may throw System.UnauthorizedAccessException
            Directory.Delete(PackagesFolderNuGetProject.PackagePathResolver.GetInstallPath(packageIdentity), recursive: true);
            nuGetProjectContext.Log(MessageLevel.Info, NuGet.ProjectManagement.Strings.RemovedPackageFromFolder, packageIdentity, PackagesFolderNuGetProject.Root);
            return true;
        }

        public static async Task<NuGetVersion> GetLatestVersionAsync(string packageId, ResolutionContext resolutionContext, SourceRepository primarySourceRepository, CancellationToken token)
        {
            return await GetLatestVersionAsync(packageId, resolutionContext, new List<SourceRepository> { primarySourceRepository }, token);
        }

        private static async Task<NuGetVersion> GetLatestVersionAsync(string packageId, ResolutionContext resolutionContext,
            IEnumerable<SourceRepository> sources, CancellationToken token)
        {
            List<NuGetVersion> latestVersions = new List<NuGetVersion>();
            foreach (var source in sources)
            {
                var metadataResource = await source.GetResourceAsync<MetadataResource>(token);
                if (metadataResource != null)
                {
                    var latestVersion = await metadataResource.GetLatestVersion(packageId,
                        resolutionContext.IncludePrerelease, resolutionContext.IncludeUnlisted, token);
                    if (latestVersion != null)
                    {
                        latestVersions.Add(latestVersion);
                    }
                }
            }

            return latestVersions.Max<NuGetVersion>();
        }

        private IEnumerable<SourceRepository> GetEffectiveSources(IEnumerable<SourceRepository> primarySources, IEnumerable<SourceRepository> secondarySources)
        {
            // Always have to add the packages folder as the primary repository so that
            // dependency info for an installed package that is unlisted from the server is still available :(
            var effectiveSources = new List<SourceRepository>(primarySources);
            effectiveSources.Add(PackagesFolderSourceRepository);
            effectiveSources.AddRange(secondarySources);

            return new HashSet<SourceRepository>(effectiveSources, new SourceRepositoryComparer());
        }
    }

    /// <summary>
    /// The event args class used in raising package operation events
    /// </summary>
    public  class PackageOperationEventArgs : EventArgs
    {
        PackageIdentity PackageIdentity { get; set; }
        /// <summary>
        /// Creates a package operation event args object for given <param name="packageIdentity"></param>
        /// </summary>
        public PackageOperationEventArgs(PackageIdentity packageIdentity)
        {
            PackageIdentity = packageIdentity;
        }
    }
}
