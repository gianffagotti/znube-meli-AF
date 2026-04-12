namespace meli_znube_integration.Common;

/// <summary>
/// Tipos de regla de stock soportados. Centraliza constantes para evitar literales repetidos (PACK, COMBO, FULL).
/// </summary>
public static class StockRuleTypes
{
    public const string Full = "FULL";
    public const string Pack = "PACK";
    public const string Combo = "COMBO";

    /// <summary>Tipos válidos para validación en API y reglas.</summary>
    public static readonly IReadOnlySet<string> ValidRuleTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { Full, Pack, Combo };

    public static bool IsValid(string? ruleType) =>
        !string.IsNullOrWhiteSpace(ruleType) && ValidRuleTypes.Contains(ruleType.Trim());

    public static bool IsPackOrCombo(string? ruleType) =>
        string.Equals(ruleType, Pack, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ruleType, Combo, StringComparison.OrdinalIgnoreCase);

    public static bool IsFull(string? ruleType) =>
        string.Equals(ruleType, Full, StringComparison.OrdinalIgnoreCase);
}
