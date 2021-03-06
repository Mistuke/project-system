﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

using Microsoft.VisualStudio.LanguageServices.ProjectSystem;
using Microsoft.VisualStudio.Threading;

namespace Microsoft.VisualStudio.ProjectSystem.LanguageServices
{
    /// <summary>
    ///     Hosts a <see cref="IWorkspaceProjectContext"/> and handles the interaction between the project system and the language service.
    /// </summary>
    [Export(typeof(ILanguageServiceHost))]
    [AppliesTo(ProjectCapability.CSharpOrVisualBasicOrFSharpLanguageService)]
    internal partial class LanguageServiceHost : OnceInitializedOnceDisposedAsync, ILanguageServiceHost
    {
#pragma warning disable CA2213 // OnceInitializedOnceDisposedAsync are not tracked corretly by the IDisposeable analyzer
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(initialCount: 1);
#pragma warning restore CA2213
        private readonly IUnconfiguredProjectCommonServices _commonServices;
        private readonly Lazy<IProjectContextProvider> _contextProvider;
        private readonly IProjectAsynchronousTasksService _tasksService;
        private readonly IActiveConfiguredProjectSubscriptionService _activeConfiguredProjectSubscriptionService;
        private readonly IActiveProjectConfigurationRefreshService _activeProjectConfigurationRefreshService;
        private readonly LanguageServiceHandlerManager _languageServiceHandlerManager;

        private readonly List<IDisposable> _evaluationSubscriptionLinks;
        private readonly List<IDisposable> _designTimeBuildSubscriptionLinks;
        private readonly HashSet<ProjectConfiguration> _projectConfigurationsWithSubscriptions;

        /// <summary>
        /// Current AggregateWorkspaceProjectContext - accesses to this field must be done with a lock on <see cref="_gate"/>.
        /// Note that at any given time, we can have only a single non-disposed aggregate project context.
        /// Otherwise, we can end up with an invalid state of multiple workspace project contexts for the same configured project.
        /// </summary>
        private AggregateWorkspaceProjectContext _currentAggregateProjectContext;

        /// <summary>
        /// Current TargetFramework for non-cross targeting project - accesses to this field must be done with a lock on <see cref="_gate"/>.
        /// </summary>
        private string _currentTargetFramework;


        [ImportingConstructor]
        public LanguageServiceHost(IUnconfiguredProjectCommonServices commonServices,
                                   Lazy<IProjectContextProvider> contextProvider,
                                   [Import(ExportContractNames.Scopes.UnconfiguredProject)]IProjectAsynchronousTasksService tasksService,
                                   IActiveConfiguredProjectSubscriptionService activeConfiguredProjectSubscriptionService,
                                   IActiveProjectConfigurationRefreshService activeProjectConfigurationRefreshService,
                                   LanguageServiceHandlerManager languageServiceHandlerManager)
            : base(commonServices.ThreadingService.JoinableTaskContext)
        {
            _commonServices = commonServices;
            _contextProvider = contextProvider;
            _tasksService = tasksService;
            _activeConfiguredProjectSubscriptionService = activeConfiguredProjectSubscriptionService;
            _activeProjectConfigurationRefreshService = activeProjectConfigurationRefreshService;
            _languageServiceHandlerManager = languageServiceHandlerManager;
            _evaluationSubscriptionLinks = new List<IDisposable>();
            _designTimeBuildSubscriptionLinks = new List<IDisposable>();
            _projectConfigurationsWithSubscriptions = new HashSet<ProjectConfiguration>();
        }

        public object HostSpecificErrorReporter => _currentAggregateProjectContext?.HostSpecificErrorReporter;

        public IWorkspaceProjectContext ActiveProjectContext => _currentAggregateProjectContext?.ActiveProjectContext;

        public object HostSpecificEditAndContinueService => _currentAggregateProjectContext?.ENCProjectConfig;

        [ProjectAutoLoad(ProjectLoadCheckpoint.ProjectFactoryCompleted)]
        [AppliesTo(ProjectCapability.CSharpOrVisualBasicOrFSharpLanguageService)]
        private Task OnProjectFactoryCompletedAsync()
        {
            return InitializeAsync();
        }

        protected async override Task InitializeCoreAsync(CancellationToken cancellationToken)
        {
            if (IsDisposing || IsDisposed)
                return;

            // Don't initialize if we're unloading
            _tasksService.UnloadCancellationToken.ThrowIfCancellationRequested();

            // Update project context and subscriptions.
            await UpdateProjectContextAndSubscriptionsAsync().ConfigureAwait(false);
        }

        Task ILanguageServiceHost.InitializeAsync(CancellationToken cancellationToken)
        {
            return InitializeAsync(cancellationToken);
        }

        private async Task OnProjectChangedCoreAsync(IProjectVersionedValue<IProjectSubscriptionUpdate> e, RuleHandlerType handlerType)
        {
            if (IsDisposing || IsDisposed)
                return;

            await _tasksService.LoadedProjectAsync(async () =>
            {
                await HandleAsync(e, handlerType).ConfigureAwait(false);
            });

            // If "TargetFrameworks" property has changed, we need to refresh the project context and subscriptions.
            if (HasTargetFrameworksChanged(e))
            {
                await UpdateProjectContextAndSubscriptionsAsync().ConfigureAwait(false);
            }
        }

        private async Task UpdateProjectContextAndSubscriptionsAsync()
        {
            var previousProjectContext = _currentAggregateProjectContext;
            var newProjectContext = await UpdateProjectContextAsync().ConfigureAwait(false);

            if (previousProjectContext != newProjectContext)
            {
                // Add subscriptions for the new configured projects in the new project context.
                await AddSubscriptionsAsync(newProjectContext).ConfigureAwait(false);
            }
        }

        private Task<T> ExecuteWithinLockAsync<T>(Func<Task<T>> task)
        {
            return _gate.ExecuteWithinLockAsync(JoinableCollection, JoinableFactory, task);
        }

        private Task ExecuteWithinLockAsync(Func<Task> task)
        {
            return _gate.ExecuteWithinLockAsync(JoinableCollection, JoinableFactory, task);
        }

        /// <summary>
        /// Ensures that <see cref="_currentAggregateProjectContext"/> is updated for the latest target frameworks from the project properties
        /// and returns this value.
        /// </summary>
        private async Task<AggregateWorkspaceProjectContext> UpdateProjectContextAsync()
        {
            // Ensure that only single thread is attempting to create a project context.
            AggregateWorkspaceProjectContext previousContextToDispose = null;
            return await ExecuteWithinLockAsync(async () =>
            {
                await _commonServices.ThreadingService.SwitchToUIThread();

                string newTargetFramework = null;
                var projectProperties = await _commonServices.ActiveConfiguredProjectProperties.GetConfigurationGeneralPropertiesAsync().ConfigureAwait(false);

                // Check if we have already computed the project context.
                if (_currentAggregateProjectContext != null)
                {
                    // For non-cross targeting projects, we can use the current project context if the TargetFramework hasn't changed.
                    // For cross-targeting projects, we need to verify that the current project context matches latest frameworks targeted by the project.
                    // If not, we create a new one and dispose the current one.

                    if (!_currentAggregateProjectContext.IsCrossTargeting)
                    {
                        newTargetFramework = (string)await projectProperties.TargetFramework.GetValueAsync().ConfigureAwait(false);
                        if (StringComparers.PropertyValues.Equals(_currentTargetFramework, newTargetFramework))
                        {
                            return _currentAggregateProjectContext;
                        }

                        // Dispose the old workspace project context for the previous target framework.
                        await DisposeAggregateProjectContextAsync(_currentAggregateProjectContext).ConfigureAwait(false);
                    }
                    else
                    {
                        // Check if the current project context is up-to-date for the current active and known project configurations.
                        var activeProjectConfiguration = _commonServices.ActiveConfiguredProject.ProjectConfiguration;
                        var knownProjectConfigurations = await _commonServices.Project.Services.ProjectConfigurationsService.GetKnownProjectConfigurationsAsync().ConfigureAwait(false);
                        if (knownProjectConfigurations.All(c => c.IsCrossTargeting()) &&
                            _currentAggregateProjectContext.HasMatchingTargetFrameworks(activeProjectConfiguration, knownProjectConfigurations))
                        {
                            return _currentAggregateProjectContext;
                        }

                        previousContextToDispose = _currentAggregateProjectContext;
                    }
                }
                else
                {
                    newTargetFramework = (string)await projectProperties.TargetFramework.GetValueAsync().ConfigureAwait(false);
                }

                // Force refresh the CPS active project configuration (needs UI thread).
                await _commonServices.ThreadingService.SwitchToUIThread();
                await _activeProjectConfigurationRefreshService.RefreshActiveProjectConfigurationAsync().ConfigureAwait(false);

                // Create new project context.
                _currentAggregateProjectContext = await _contextProvider.Value.CreateProjectContextAsync().ConfigureAwait(false);
                _currentTargetFramework = newTargetFramework;

                // Dispose the old project context, if one exists.
                if (previousContextToDispose != null)
                {
                    await DisposeAggregateProjectContextAsync(previousContextToDispose).ConfigureAwait(false);
                }

                return _currentAggregateProjectContext;
            }).ConfigureAwait(false);
        }

        private async Task DisposeAggregateProjectContextAsync(AggregateWorkspaceProjectContext projectContext)
        {
            await _contextProvider.Value.ReleaseProjectContextAsync(projectContext).ConfigureAwait(false);

            foreach (var innerContext in projectContext.DisposedInnerProjectContexts)
            {
                _languageServiceHandlerManager.OnContextReleased(innerContext);
            }
        }

        private async Task AddSubscriptionsAsync(AggregateWorkspaceProjectContext newProjectContext)
        {
            Requires.NotNull(newProjectContext, nameof(newProjectContext));

            await _commonServices.ThreadingService.SwitchToUIThread();
            await _tasksService.LoadedProjectAsync(() =>
            {
                var watchedEvaluationRules = _languageServiceHandlerManager.GetWatchedRules(RuleHandlerType.Evaluation);
                var watchedDesignTimeBuildRules = _languageServiceHandlerManager.GetWatchedRules(RuleHandlerType.DesignTimeBuild);

                foreach (var configuredProject in newProjectContext.InnerConfiguredProjects)
                {
                    if (_projectConfigurationsWithSubscriptions.Contains(configuredProject.ProjectConfiguration))
                    {
                        continue;
                    }

                    _designTimeBuildSubscriptionLinks.Add(configuredProject.Services.ProjectSubscription.JointRuleSource.SourceBlock.LinkTo(
                        new ActionBlock<IProjectVersionedValue<IProjectSubscriptionUpdate>>(e => OnProjectChangedCoreAsync(e, RuleHandlerType.DesignTimeBuild)),
                        ruleNames: watchedDesignTimeBuildRules, suppressVersionOnlyUpdates: true));

                    _evaluationSubscriptionLinks.Add(configuredProject.Services.ProjectSubscription.ProjectRuleSource.SourceBlock.LinkTo(
                        new ActionBlock<IProjectVersionedValue<IProjectSubscriptionUpdate>>(e => OnProjectChangedCoreAsync(e, RuleHandlerType.Evaluation)),
                        ruleNames: watchedEvaluationRules, suppressVersionOnlyUpdates: true));

                    _projectConfigurationsWithSubscriptions.Add(configuredProject.ProjectConfiguration);
                }

                return Task.CompletedTask;
            });
        }

        private async Task HandleAsync(IProjectVersionedValue<IProjectSubscriptionUpdate> update, RuleHandlerType handlerType)
        {
            // We need to process the update within a lock to ensure that we do not release this context during processing.
            // TODO: Enable concurrent execution of updates themeselves, i.e. two separate invocations of HandleAsync
            //       should be able to run concurrently.
            await ExecuteWithinLockAsync(async () =>
            {
                // TODO: https://github.com/dotnet/roslyn-project-system/issues/353
                await _commonServices.ThreadingService.SwitchToUIThread();

                // Get the inner workspace project context to update for this change.
                var projectContextToUpdate = _currentAggregateProjectContext.GetInnerProjectContext(update.Value.ProjectConfiguration, out bool isActiveContext);
                if (projectContextToUpdate == null)
                {
                    return;
                }

                _languageServiceHandlerManager.Handle(update, handlerType, projectContextToUpdate, isActiveContext);
            }).ConfigureAwait(false);
        }

        private static bool HasTargetFrameworksChanged(IProjectVersionedValue<IProjectSubscriptionUpdate> e)
        {
            return e.Value.ProjectChanges.TryGetValue(ConfigurationGeneral.SchemaName, out IProjectChangeDescription projectChange) &&
                 projectChange.Difference.ChangedProperties.Contains(ConfigurationGeneral.TargetFrameworksProperty);
        }

        protected override async Task DisposeCoreAsync(bool initialized)
        {
            if (initialized)
            {
                DisposeAndClearSubscriptions();

                await ExecuteWithinLockAsync(async () =>
                {
                    if (_currentAggregateProjectContext != null)
                    {
                        await _contextProvider.Value.ReleaseProjectContextAsync(_currentAggregateProjectContext).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
            }
        }

        private void DisposeAndClearSubscriptions()
        {
            foreach (var link in _evaluationSubscriptionLinks.Concat(_designTimeBuildSubscriptionLinks))
            {
                link.Dispose();
            }

            _evaluationSubscriptionLinks.Clear();
            _designTimeBuildSubscriptionLinks.Clear();
            _projectConfigurationsWithSubscriptions.Clear();
        }
    }
}
