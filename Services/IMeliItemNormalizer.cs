using meli_znube_integration.Models;
using meli_znube_integration.Models.Canonical;

namespace meli_znube_integration.Services;

/// <summary>
/// Anti-Corruption Layer contract: translates a raw <see cref="MeliItem"/> DTO (MELI API transport model)
/// into the domain-stable <see cref="CanonicalItem"/>.
///
/// Implementors MUST handle both MELI item schemas:
///   - With variations: each <c>MeliVariation</c> carries its own SKU and stock.
///   - Without variations (root-SKU): SKU and stock are on the root <c>MeliItem</c>.
///
/// Implementations are pure (no I/O). Spec: meli-item-normalizer.
/// </summary>
public interface IMeliItemNormalizer
{
    /// <summary>
    /// Normalizes a raw <see cref="MeliItem"/> DTO into a <see cref="CanonicalItem"/>.
    /// </summary>
    /// <param name="item">Raw MELI item DTO. Must not be null.</param>
    /// <returns>A non-null <see cref="CanonicalItem"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="item"/> is null.</exception>
    CanonicalItem Normalize(MeliItem item);
}
