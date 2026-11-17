using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tavern.Dialogue;
using Tavern.Social;
using Xybrid;

/// <summary>
/// Runs the Gemma structured-judgment baseline on the shared dialogue model through
/// <see cref="XybridModelService.RunBackgroundAsync"/>: greedy, a few dozen tokens, constrained by a GBNF
/// grammar converted from the judge's JSON schema. Uses its own ConversationContext ("table-judge") that
/// never receives turns, so NPC dialogue histories are untouched.
/// </summary>
public sealed class XybridGrammarRunner : IGrammarTextRunner, IDisposable
{
    private readonly XybridModelService _service;
    private readonly Dictionary<string, string> _grammars = new Dictionary<string, string>();
    private ConversationContext _context;
    private string _contextSystem;

    public XybridGrammarRunner(XybridModelService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public string ModelLabel => (_service.ModelId ?? "dialogue LLM") + " via Xybrid " + (_service.SdkVersion ?? "");

    public async Task<GrammarRunResult> RunAsync(string system, string user, string jsonSchema, int maxTokens,
        CancellationToken cancellationToken)
    {
        if (!_service.IsReady)
            return new GrammarRunResult { Error = "XybridModelService not ready" };

        if (!_grammars.TryGetValue(jsonSchema, out string grammar))
        {
            grammar = XybridClient.JsonSchemaToGbnf(jsonSchema);
            _grammars[jsonSchema] = grammar;
        }

        if (_context == null || _contextSystem != system)
        {
            _context?.Dispose();
            _context = new ConversationContext("table-judge");
            _context.SetSystem(system);
            _contextSystem = system;
        }

        using (GenerationConfig config = GenerationConfig.Greedy())
        {
            config.SetMaxTokens(maxTokens);
            config.SetGrammar(grammar);
            XybridModelService.BackgroundRunResult r =
                await _service.RunBackgroundAsync(user, _context, config, cancellationToken);
            return new GrammarRunResult
            {
                Success = r.Success,
                Text = r.Text,
                Error = r.Error,
                QueueWaitMs = r.QueueWaitMs,
                InferenceMs = r.InferenceMs,
            };
        }
    }

    public void Dispose()
    {
        _context?.Dispose();
        _context = null;
    }
}
