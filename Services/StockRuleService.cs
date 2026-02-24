using Azure.Data.Tables;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace meli_znube_integration.Services;

public class StockRuleService
{
    private readonly TableClient _tableClient;
    private readonly TableClient _skuIndexClient;

    public StockRuleService(IConfiguration configuration)
    {
        var connectionString = EnvVars.GetRequiredString(EnvVars.Keys.AzureStorageConnectionString);
        var tableName = EnvVars.GetRequiredString(EnvVars.Keys.StockRulesTableName);
        var skuIndexTableName = EnvVars.GetString(EnvVars.Keys.StockSkuIndexTableName, "StockSkuIndex");

        _tableClient = new TableClient(connectionString, tableName);
        _tableClient.CreateIfNotExists();

        _skuIndexClient = new TableClient(connectionString, skuIndexTableName);
        _skuIndexClient.CreateIfNotExists();
    }

    /// <summary>Normalizes SKU for use as partition key in StockSkuIndex (e.g. upper case, trimmed).</summary>
    public static string NormalizeSku(string? sku) => (sku ?? "").Trim().ToUpperInvariant();

    /// <summary>Sanitizes a value for use as PartitionKey or RowKey in Azure Table Storage.
    /// Removes/replaces forbidden characters: / \ # ? and control chars (U+0000–U+001F). Caps length at 1024.</summary>
    public static string SanitizeTableKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        const int maxKeyLength = 1024;
        var sanitized = new char[key.Length];
        var written = 0;
        for (var i = 0; i < key.Length && written < maxKeyLength; i++)
        {
            var c = key[i];
            if (c == '/' || c == '\\' || c == '#' || c == '?' || (c >= '\u0000' && c <= '\u001F'))
                c = '_';
            sanitized[written++] = c;
        }
        return new string(sanitized, 0, written);
    }

    public async Task SaveRuleAsync(StockRuleDto ruleDto)
    {
        // Map DTO -> Entity
        var sellerId = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);
        var entity = new StockRuleEntity
        {
            PartitionKey = sellerId,
            RowKey = ruleDto.TargetItemId,
            RuleType = ruleDto.RuleType,
            IsIncomplete = ruleDto.IsIncomplete,
            DefaultPackQuantity = ruleDto.DefaultPackQuantity,
            TargetItemId = ruleDto.TargetItemId,
            TargetTitle = ruleDto.TargetTitle,
            TargetThumbnail = ruleDto.TargetThumbnail,
            TargetSku = ruleDto.TargetSku ?? "",
            ComponentsJson = JsonSerializer.Serialize(ruleDto.Components ?? new List<RuleComponentDto>()),
        };

        entity.Mappings = ruleDto.Mappings.Select(m => new RuleVariantMapping
        {
            TargetVariantId = m.TargetVariantId,
            TargetSku = m.TargetSku,
            PackQuantity = m.PackQuantity,
            Strategy = m.Strategy ?? "Explicit",
            MatchSize = m.MatchSize,
            SourceMatches = m.SourceMatches.Select(sm => new RuleSourceMatch
            {
                SourceItemId = sm.SourceItemId,
                SourceVariantId = sm.SourceVariantId,
                SourceSku = sm.SourceSku,
                Quantity = sm.Quantity
            }).ToList()
        }).ToList();

        var isFullRule = string.Equals(ruleDto.RuleType, "FULL", StringComparison.OrdinalIgnoreCase)
            && (ruleDto.Components == null || ruleDto.Components.Count == 0);

        // 1. Check if rule exists to clean up old index
        var existingRuleKey = entity.RowKey;
        var existingRule = await GetRuleEntityAsync(sellerId, existingRuleKey);
        if (existingRule != null)
        {
            await DeleteIndexEntriesAsync(existingRule);
            await DeleteSkuIndexEntriesByTargetAsync(existingRule.TargetItemId);
        }

        // 2. Upsert the main rule
        await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);

        // 3. Maintain indexes
        if (isFullRule)
        {
            // FULL: maintain StockSkuIndex (no StockSourceIndex entries)
            foreach (var mapping in ruleDto.Mappings)
            {
                var normalizedSku = NormalizeSku(mapping.TargetSku);
                if (string.IsNullOrEmpty(normalizedSku)) continue;
                var skuIndexEntity = new StockSkuIndexEntity
                {
                    PartitionKey = SanitizeTableKey(normalizedSku),
                    RowKey = SanitizeTableKey(entity.TargetItemId),
                    RuleType = entity.RuleType,
                    Timestamp = DateTimeOffset.UtcNow
                };
                await _skuIndexClient.UpsertEntityAsync(skuIndexEntity, TableUpdateMode.Replace);
            }
        }
        else
        {
            // PACK/COMBO: maintain StockSourceIndex (component-based)
            foreach (var component in ruleDto.Components ?? new List<RuleComponentDto>())
            {
                var indexEntity = new StockSourceIndexEntity
                {
                    PartitionKey = SanitizeTableKey(component.SourceItemId),
                    RowKey = SanitizeTableKey(entity.TargetItemId),
                    RuleType = entity.RuleType,
                    Timestamp = DateTimeOffset.UtcNow
                };
                await _tableClient.UpsertEntityAsync(indexEntity, TableUpdateMode.Replace);
            }
        }
    }

    public async Task DeleteRuleAsync(string sellerId, string targetItemId)
    {
        var rule = await GetRuleEntityAsync(sellerId, targetItemId);
        if (rule != null)
        {
            await DeleteIndexEntriesAsync(rule);
            await DeleteSkuIndexEntriesByTargetAsync(targetItemId);
            await _tableClient.DeleteEntityAsync(sellerId, targetItemId);
        }
    }

    public async Task<List<StockRuleDto>> GetRulesBySellerAsync(string? sellerId = null)
    {
        sellerId ??= EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);

        var rules = new List<StockRuleDto>();
        var query = _tableClient.QueryAsync<StockRuleEntity>(filter: $"PartitionKey eq '{sellerId}'");

        await foreach (var rule in query)
        {
            rules.Add(MapToDto(rule));
        }

        return rules;
    }

    // Only used internally or by advanced indexers if needed. 
    // If public consumers need DTOs, usage of this method in WebhookNotificationFunction needs to be checked.
    // However, WebhookNotificationFunction uses GetAffectedRulesBySourceAsync which returns StockSourceIndexEntity.
    // StockSourceIndexEntity is a lightweight pointer, so it is fine to return Entity.
    public async Task<List<StockSourceIndexEntity>> GetAffectedRulesBySourceAsync(string sourceItemId)
    {
        var indexes = new List<StockSourceIndexEntity>();
        var pk = SanitizeTableKey(sourceItemId).Replace("'", "''");
        var query = _tableClient.QueryAsync<StockSourceIndexEntity>(filter: $"PartitionKey eq '{pk}'");

        await foreach (var index in query)
        {
            indexes.Add(index);
        }

        return indexes;
    }

    /// <summary>Gets FULL rules that reference the given SKU (via StockSkuIndex). Used by webhook/worker for SKU-based notifications.</summary>
    public async Task<List<StockRuleDto>> GetFullRulesBySkuAsync(string sellerId, string sku)
    {
        var normalizedSku = NormalizeSku(sku);
        if (string.IsNullOrEmpty(normalizedSku)) return new List<StockRuleDto>();

        var pk = SanitizeTableKey(normalizedSku).Replace("'", "''");
        var targetItemIds = new List<string>();
        var query = _skuIndexClient.QueryAsync<StockSkuIndexEntity>(filter: $"PartitionKey eq '{pk}'");
        await foreach (var index in query)
        {
            targetItemIds.Add(index.RowKey);
        }

        var rules = new List<StockRuleDto>();
        foreach (var targetItemId in targetItemIds.Distinct())
        {
            var rule = await GetRuleAsync(sellerId, targetItemId);
            if (rule != null && string.Equals(rule.RuleType, "FULL", StringComparison.OrdinalIgnoreCase))
                rules.Add(rule);
        }
        return rules;
    }

    public async Task<StockRuleDto?> GetRuleAsync(string sellerId, string targetItemId)
    {
        var entity = await GetRuleEntityAsync(sellerId, targetItemId);
        return entity == null ? null : MapToDto(entity);
    }
    
    // Internal helper to get raw entity
    private async Task<StockRuleEntity?> GetRuleEntityAsync(string sellerId, string targetItemId)
    {
        try
        {
            var response = await _tableClient.GetEntityAsync<StockRuleEntity>(sellerId, targetItemId);
            return response.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private async Task DeleteIndexEntriesAsync(StockRuleEntity rule)
    {
        if (string.IsNullOrEmpty(rule.ComponentsJson)) return;

        try 
        {
            var components = JsonSerializer.Deserialize<List<RuleComponentDto>>(rule.ComponentsJson);
            if (components != null)
            {
                foreach (var component in components)
                {
                    await _tableClient.DeleteEntityAsync(SanitizeTableKey(component.SourceItemId), SanitizeTableKey(rule.TargetItemId));
                }
            }
        }
        catch
        {
            // Ignore deserialization errors or missing components during cleanup
        }
    }

    private async Task DeleteSkuIndexEntriesByTargetAsync(string targetItemId)
    {
        var rk = SanitizeTableKey(targetItemId).Replace("'", "''");
        var query = _skuIndexClient.QueryAsync<StockSkuIndexEntity>(filter: $"RowKey eq '{rk}'");
        await foreach (var index in query)
        {
            await _skuIndexClient.DeleteEntityAsync(index.PartitionKey, index.RowKey);
        }
    }

    private StockRuleDto MapToDto(StockRuleEntity entity)
    {
        return new StockRuleDto
        {
            TargetItemId = entity.TargetItemId,
            TargetTitle = entity.TargetTitle,
            TargetThumbnail = entity.TargetThumbnail,
            TargetSku = entity.TargetSku,
            RuleType = entity.RuleType,
            IsIncomplete = entity.IsIncomplete,
            DefaultPackQuantity = entity.DefaultPackQuantity,
            Components = string.IsNullOrEmpty(entity.ComponentsJson)
                ? new List<RuleComponentDto>()
                : JsonSerializer.Deserialize<List<RuleComponentDto>>(entity.ComponentsJson) ?? new List<RuleComponentDto>(),
            Mappings = entity.Mappings.Select(m => new VariantMappingDto
            {
                TargetVariantId = m.TargetVariantId,
                TargetSku = m.TargetSku,
                PackQuantity = m.PackQuantity,
                Strategy = m.Strategy ?? "Explicit",
                MatchSize = m.MatchSize,
                SourceMatches = m.SourceMatches.Select(sm => new RuleSourceMatchDto
                {
                    SourceItemId = sm.SourceItemId,
                    SourceVariantId = sm.SourceVariantId,
                    SourceSku = sm.SourceSku,
                    Quantity = sm.Quantity
                }).ToList()
            }).ToList()
        };
    }
}
