namespace Abacus.Run.Core;

/// <summary>Converts token counts into money.</summary>
public interface IModelPricing
{
    /// <summary>
    /// Cost of one call, or null when the model has no configured price.
    /// </summary>
    /// <remarks>
    /// Null rather than zero, deliberately. A zero cost averages into the drift baseline as a real
    /// observation and drags the mean down, so a genuine cost rise later looks smaller than it is.
    /// "We do not know" and "it was free" are different facts and must stay different.
    /// </remarks>
    decimal? CostOf(string modelId, long inputTokens, long outputTokens);
}

/// <summary>Per-million-token rates for one model.</summary>
public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion);

/// <summary>
/// Config-driven price table, bound from <c>Abacus:Llm:Pricing:&lt;model&gt;</c>. Knows nothing by
/// default, which is the honest starting position for a host that has not been told its rates.
/// </summary>
public sealed class ModelPricing : IModelPricing
{
    private readonly IReadOnlyDictionary<string, ModelPrice> _prices;

    public ModelPricing(IReadOnlyDictionary<string, ModelPrice>? prices = null)
        => _prices = prices ?? new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);

    public decimal? CostOf(string modelId, long inputTokens, long outputTokens)
    {
        if (string.IsNullOrWhiteSpace(modelId) || !_prices.TryGetValue(modelId, out ModelPrice? price))
        {
            return null;
        }

        return (inputTokens * price.InputPerMillion / 1_000_000m)
             + (outputTokens * price.OutputPerMillion / 1_000_000m);
    }
}
