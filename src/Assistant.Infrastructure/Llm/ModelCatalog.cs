using Assistant.Application.Common;

namespace Assistant.Infrastructure.Llm;

/// <summary>Read-only view over the configured LLM_MODELS, built once from LlmConfig at DI
/// composition time. M3a has exactly one tier ("smart"); the candidate chain is LLM_MODELS' own
/// order, regardless of the tier argument -- the parameter exists so later tiers need no signature
/// change (spec 3.1).</summary>
public class ModelCatalog
{
    private readonly LlmConfig _config;

    public ModelCatalog(LlmConfig config)
    {
        _config = config;
    }

    public IReadOnlyList<ModelCatalogEntry> Models => _config.Models;

    public bool TryGetByName(string name, out ModelCatalogEntry? entry)
    {
        entry = _config.Models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        return entry is not null;
    }

    /// <summary>Preferred model first (if set and known), then the rest of the chain in order, so a
    /// chat pinned to one model still falls back when it runs out (spec 3.1).</summary>
    public IReadOnlyList<ModelCatalogEntry> GetCandidateOrder(string tier, string? preferredModel)
    {
        var chain = _config.Models;

        if (preferredModel is null || !TryGetByName(preferredModel, out var preferred))
        {
            return chain;
        }

        return new[] { preferred! }.Concat(chain.Where(m => m.Name != preferred!.Name)).ToArray();
    }
}
