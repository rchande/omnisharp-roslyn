﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using OmniSharp.Models.Diagnostics;

namespace OmniSharp.Roslyn.CSharp.Services.Diagnostics
{
    [Shared]
    [Export(typeof(RoslynAnalyzerService))]
    public class RoslynAnalyzerService
    {
        private readonly ImmutableArray<DiagnosticAnalyzer> _analyzers;
        private readonly ILogger<RoslynAnalyzerService> _logger;
        private ConcurrentDictionary<string, Project> _projects = new ConcurrentDictionary<string, Project>();

        [ImportingConstructor]
        public RoslynAnalyzerService(OmniSharpWorkspace workspace, ExternalFeaturesHostServicesProvider hostServices, ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<RoslynAnalyzerService>();
            _analyzers = hostServices.Assemblies.SelectMany(assembly =>
                {
                    try
                    {
                        _logger.LogInformation($"Loading analyzers from assembly: {assembly.Location}");

                        return assembly.GetTypes()
                            .Where(x => typeof(DiagnosticAnalyzer).IsAssignableFrom(x))
                            .Where(x => !x.IsAbstract)
                            .Select(Activator.CreateInstance)
                            .Where(x => x != null)
                            .Cast<DiagnosticAnalyzer>();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        _logger.LogError(
                            $"Tried to load analyzers from extensions, loader error occurred {ex} : {ex.LoaderExceptions}");
                        return Enumerable.Empty<DiagnosticAnalyzer>();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            $"Unexpected error during analyzer loading {ex}");
                        return Enumerable.Empty<DiagnosticAnalyzer>();
                    }
                }
            ).ToImmutableArray();

            //workspace.WorkspaceChanged += OnWorkspaceChanged;

            Task.Run(() => Worker(CancellationToken.None));
        }

        private async Task Worker(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var work = _projects;
                Console.WriteLine("#1");
                // Yeye here we may mis update because of concurrency but lets leave it at this point.
                _projects = new ConcurrentDictionary<string, Project>();

                var result = await Task.WhenAll(work.Select(async x => await Analyze(x.Value)));

                if (result.SelectMany(x => x).Any())
                    CurrentDiagnosticResults = result.SelectMany(x => x).ToImmutableArray();

                Console.WriteLine("#2");

                await Task.Delay(1000, token);
            }
        }

        public ImmutableArray<DiagnosticLocation> CurrentDiagnosticResults { get; private set; } = ImmutableArray<DiagnosticLocation>.Empty;


        private void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs changeEvent)
        {
            if (changeEvent.Kind == WorkspaceChangeKind.DocumentChanged)
            {
                QueueForAnalysis(new[] { changeEvent.NewSolution.GetDocument(changeEvent.DocumentId).Project });
            }
        }

        public void QueueForAnalysis(IEnumerable<Project> projects)
        {
            projects.ToList().ForEach(project =>
            {
                Console.WriteLine($"Queue project {project.Name}");
                _projects.TryAdd(project.Id.ToString(), project);
            });
        }

        private async Task<IEnumerable<DiagnosticLocation>> Analyze(Project project)
        {
            if (_analyzers.Length == 0)
                return ImmutableArray<DiagnosticLocation>.Empty;

            var compiled = await project.GetCompilationAsync();
            var analysis = await compiled.WithAnalyzers(_analyzers).GetAnalysisResultAsync(CancellationToken.None);
            analysis.GetAllDiagnostics().ToList().ForEach(x => _logger.LogInformation(x.GetMessage()));
            return analysis.GetAllDiagnostics().Select(x => AsDiagnosticLocation(x, project));
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