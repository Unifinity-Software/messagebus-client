using System.Text.RegularExpressions;
using MessageBus.Contracts.Messaging;

namespace MessageBus.Contracts.Runner;

/// <summary>
/// Estimates what a run cost from its usage and a model's list price: the service for project and role totals, the
/// runner for a role's most cost per run while the run goes on (the original issue). Every figure is an estimate: list prices, no
/// discounts; cached reads at the cached-input price when there is one (else as normal input); cache writes at the
/// cache-write price (the original issue), the five-minute rate unless the agent said a write was for an hour, else as normal input.
/// </summary>
public static partial class RunCosts
{
    /// <summary>
    /// The run's cost: the agent's own estimate when it reported one, otherwise tokens times <paramref name="price"/>,
    /// otherwise unknown (null). Never zero for a model without a price.
    /// </summary>
    public static decimal? Estimate(Usage? usage, TokenPrice? price)
    {
        if (usage is null) return null;
        if (usage.EstimatedCostUsd is { } reported) return reported;
        if (price is null) return null;
        // Counts a CLI reports apart are capped so no part of the input goes negative.
        var tokensIn = Math.Max(0, usage.TokensIn);
        var cached = Math.Clamp(usage.CachedTokensIn, 0, tokensIn);
        var writes = Math.Clamp(usage.CacheWriteTokensIn, 0, tokensIn - cached);
        var hourWrites = Math.Clamp(usage.CacheWrite1hTokensIn, 0, writes);
        var writePrice = price.CacheWriteUsdPerMillion ?? price.InputUsdPerMillion;
        var cost = (tokensIn - cached - writes) * price.InputUsdPerMillion / 1_000_000m
                 + cached * (price.CachedInputUsdPerMillion ?? price.InputUsdPerMillion) / 1_000_000m
                 + (writes - hourWrites) * writePrice / 1_000_000m
                 + hourWrites * (price.CacheWrite1hUsdPerMillion ?? writePrice) / 1_000_000m
                 + usage.TokensOut * price.OutputUsdPerMillion / 1_000_000m;
        return decimal.Round(cost, 6);
    }

    /// <summary>
    /// The price for a model name, ignoring case. Matches the name exactly, or with a date or version suffix
    /// (<c>claude-sonnet-4-6-20260115</c>, <c>model@20260115</c>) or a context-size tag (<c>claude-opus-5-5[1m]</c>)
    /// removed. No partial matches: an older model is not priced as a newer one.
    /// </summary>
    public static T? PriceFor<T>(IReadOnlyDictionary<string, T> prices, string? model) where T : class
    {
        if (string.IsNullOrWhiteSpace(model) || prices.Count == 0) return null;
        var name = model.Trim();
        for (var i = 0; i < 3; i++)
        {
            if (prices.TryGetValue(name, out var price)) return price;
            foreach (var (key, value) in prices)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
            }
            var trimmed = Suffix().Replace(name, "");
            if (trimmed == name || trimmed.Length == 0) return null;
            name = trimmed;
        }
        return null;
    }

    [GeneratedRegex(@"(\[[^\]]*\])$|(-\d{8})$|(@[\w.-]+)$")]
    private static partial Regex Suffix();
}
