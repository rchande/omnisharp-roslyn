﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using OmniSharp.Models.Diagnostics;
using OmniSharp.Services;

namespace OmniSharp.Roslyn.CSharp.Services.Diagnostics
{
    [Shared]
    [Export(typeof(RoslynAnalyzerService))]
    public class RoslynAnalyzerService
    {
        private readonly ILogger<RoslynAnalyzerService> _logger;
        private ConcurrentDictionary<string, (DateTime modified, Project project)> _workQueue = new ConcurrentDictionary<string, (DateTime modified, Project project)>();
        private readonly ConcurrentDictionary<string, IEnumerable<DiagnosticLocation>> _results =
            new ConcurrentDictionary<string, IEnumerable<DiagnosticLocation>>();
        private readonly IEnumerable<ICodeActionProvider> providers;

        private int throttlingMs = 1000;

        [ImportingConstructor]
        public RoslynAnalyzerService(
            OmniSharpWorkspace workspace,
            [ImportMany] IEnumerable<ICodeActionProvider> providers,
            ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<RoslynAnalyzerService>();
            this.providers = providers;

            workspace.WorkspaceChanged += OnWorkspaceChanged;

            Task.Run(() => Worker(CancellationToken.None));
            Task.Run(() => QueueForAnalysis(workspace.CurrentSolution.Projects));
        }

        private async Task Worker(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var currentWork = GetThrottledWork();

                var analyzerResults = await Task
                    .WhenAll(currentWork
                        .Select(async x => new
                        {
                            Project = x.Key,
                            Result = await Analyze(x.Value, token)
                        }));

                analyzerResults
                    .ToList()
                    .ForEach(result => _results[result.Project] = result.Result);

                await Task.Delay(500, token);
            }
        }

        private IDictionary<string, Project> GetThrottledWork()
        {
            lock (_workQueue)
            {
                var currentWork = _workQueue
                    .Where(x => x.Value.modified.AddMilliseconds(this.throttlingMs) < DateTime.UtcNow)
                    .ToList();

                currentWork.Select(x => x.Key).ToList().ForEach(key => _workQueue.TryRemove(key, out _));

                return currentWork.ToDictionary(x => x.Key, x => x.Value.project);
            }
        }

        public IEnumerable<DiagnosticLocation> GetCurrentDiagnosticResults() => _results.SelectMany(x => x.Value);

        private void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs changeEvent)
        {
            if (changeEvent.Kind == WorkspaceChangeKind.DocumentChanged)
            {
                var project = changeEvent.NewSolution.GetDocument(changeEvent.DocumentId).Project;
                QueueForAnalysis(new[] { changeEvent.NewSolution.GetDocument(changeEvent.DocumentId).Project });
            }
        }

        private void QueueForAnalysis(IEnumerable<Project> projects)
        {
            projects.ToList().ForEach(project =>
            {
                _workQueue.AddOrUpdate(project.Id.ToString(), (modified: DateTime.UtcNow, project: project), (key, old) => (modified: DateTime.UtcNow, project: project));
            });
        }

        private async Task<IEnumerable<DiagnosticLocation>> Analyze(Project project, CancellationToken token)
        {
            var allAnalyzers = this.providers
                .SelectMany(x => x.CodeDiagnosticAnalyzerProviders)
                .Concat(project.AnalyzerReferences.SelectMany(x => x.GetAnalyzersForAllLanguages()));

            if (!allAnalyzers.Any())
                return ImmutableArray<DiagnosticLocation>.Empty;

            var compiled = await project.GetCompilationAsync(token);

            var analysis = await compiled
                .WithAnalyzers(allAnalyzers.ToImmutableArray())
                .GetAllDiagnosticsAsync(token);

            return analysis.Select(x => AsDiagnosticLocation(x, project));
        }

        private static DiagnosticLocation AsDiagnosticLocation(Diagnostic diagnostic, Project project)
        {
            var span = diagnostic.Location.GetMappedLineSpan();
            return new DiagnosticLocation
            {
                FileName = span.Path,
                Line = span.StartLinePosition.Line,
                Column = span.StartLinePosition.Character,
                EndLine = span.EndLinePosition.Line,
                EndColumn = span.EndLinePosition.Character,
                Text = $"{diagnostic.GetMessage()} ({diagnostic.Id})",
                LogLevel = diagnostic.Severity.ToString(),
                Id = diagnostic.Id,
                Projects = new List<string> { project.Name }
            };
        }
    }
}