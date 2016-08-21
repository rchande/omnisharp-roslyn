using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using OmniSharp.Mef;
using OmniSharp.Models;
using OmniSharp.Razor.Workers.Diagnostics;
using OmniSharp.Services;

namespace OmniSharp.Razor.Services
{
    [OmniSharpHandler(OmnisharpEndpoints.Diagnostics, RazorLanguage.Razor)]
    public class DiagnosticsService : RequestHandler<DiagnosticsRequest, DiagnosticsResponse>
    {
        private readonly RazorDiagnosticService _diagnostics;
        private readonly DiagnosticEventForwarder _forwarder;
        private readonly OmnisharpWorkspace _workspace;

        [ImportingConstructor]
        public DiagnosticsService(OmnisharpWorkspace workspace, DiagnosticEventForwarder forwarder, RazorDiagnosticService diagnostics)
        {
            _forwarder = forwarder;
            _workspace = workspace;
            _diagnostics = diagnostics;
        }

        public Task<DiagnosticsResponse> Handle(DiagnosticsRequest request)
        {
            if (!_forwarder.IsEnabled)
            {
                _forwarder.IsEnabled = true;
            }

            var documents = request.FileName != null
                ? new [] { request.FileName }
                : _workspace.CurrentSolution.Projects.SelectMany(project => project.Documents.Select(x => x.FilePath));

            _diagnostics.QueueDiagnostics(documents.ToArray());

            return Task.FromResult(new DiagnosticsResponse());
        }
    }
}
