using Assistant.Application.Common;

namespace Assistant.Infrastructure.Llm;

/// <summary>Read-only view over the configured LLM_MODELS, built once from LlmConfig at DI
/// composition time. Tier "smart" (and any unknown tier) uses LLM_MODELS' own order; tier "fast"
/// uses LLM_FAST_MODELS in that variable's own order, then the remaining LLM_MODELS entries in chain
/// order, so a fast call still works with no fast model configured or all of them out of limits.</summary>
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

    /// <summary>Preferred model first (if set and known), then the rest of the tier's chain in order,
    /// so a chat pinned to one model still falls back when it runs out (spec 3.1).</summary>
    public IReadOnlyList<ModelCatalogEntry> GetCandidateOrder(string tier, string? preferredModel)
    {
        var chain = string.Equals(tier, LlmConfig.FastTier, StringComparison.OrdinalIgnoreCase)
            ? FastChain()
            : _config.Models;

        if (preferredModel is null || !TryGetByName(preferredModel, out var preferred))
        {
            return chain;
        }

        return new[] { preferred! }.Concat(chain.Where(m => m.Name != preferred!.Name)).ToArray();
    }

    private IReadOnlyList<ModelCatalogEntry> FastChain()
    {
        // Only fast entries that are still in the catalog (DI already drops the others).
        var fast = _config.FastModels
            .Where(f => _config.Models.Any(m => string.Equals(m.Name, f.Name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var rest = _config.Models
            .Where(m => !fast.Any(f => string.Equals(f.Name, m.Name, StringComparison.OrdinalIgnoreCase)));
        return fast.Concat(rest).ToArray();
    }
}
