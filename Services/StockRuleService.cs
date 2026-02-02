using Azure.Data.Tables;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace meli_znube_integration.Services;

public class StockRuleService
{
    private readonly TableClient _tableClient;

    public StockRuleService(IConfiguration configuration)
    {
        var connectionString = EnvVars.GetRequiredString(EnvVars.Keys.AzureStorageConnectionString);
        var tableName = EnvVars.GetRequiredString(EnvVars.Keys.StockRulesTableName);

        _tableClient = new TableClient(connectionString, tableName);
        _tableClient.CreateIfNotExists();
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
            TargetItemId = ruleDto.TargetItemId,
            TargetTitle = ruleDto.TargetTitle,
            TargetThumbnail = ruleDto.TargetThumbnail,
            TargetSku = ruleDto.TargetSku ?? "",
            ComponentsJson = JsonSerializer.Serialize(ruleDto.Components),
            // Mappings are handled by the property setter in Entity which serializes to MappingJson
        };
        
        // Map DTO Mappings -> Entity Mappings
        // We need to map List<VariantMappingDto> to List<RuleVariantMapping>
        entity.Mappings = ruleDto.Mappings.Select(m => new RuleVariantMapping
        {
            TargetVariantId = m.TargetVariantId,
            TargetSku = m.TargetSku,
            SourceMatches = m.SourceMatches.Select(sm => new RuleSourceMatch
            {
                SourceItemId = sm.SourceItemId,
                SourceVariantId = sm.SourceVariantId,
                SourceSku = sm.SourceSku,
                Quantity = sm.Quantity
            }).ToList()
        }).ToList();

        // 1. Check if rule exists to clean up old index
        var existingRuleKey = entity.RowKey;
        var existingRule = await GetRuleEntityAsync(sellerId, existingRuleKey);
        if (existingRule != null)
        {
            await DeleteIndexEntriesAsync(existingRule);
        }

        // 2. Upsert the main rule
        await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);

        // 3. Insert new index entries
        foreach (var component in ruleDto.Components)
        {
            var indexEntity = new StockSourceIndexEntity
            {
                PartitionKey = component.SourceItemId, // Source is PK for Index
                RowKey = entity.TargetItemId,          // Target is RK for Index
                RuleType = entity.RuleType,
                Timestamp = DateTimeOffset.UtcNow
            };
            await _tableClient.UpsertEntityAsync(indexEntity, TableUpdateMode.Replace);
        }
    }

    public async Task DeleteRuleAsync(string sellerId, string targetItemId)
    {
        var rule = await GetRuleEntityAsync(sellerId, targetItemId);
        if (rule != null)
        {
            // 1. Delete index entries
            await DeleteIndexEntriesAsync(rule);

            // 2. Delete main rule
            await _tableClient.DeleteEntityAsync(sellerId, targetItemId);
        }
    }

    public async Task<List<StockRuleDto>> GetRulesBySellerAsync(string sellerId)
    {
        var rules = new List<StockRuleDto>();
        var query = _tableClient.QueryAsync<StockRuleEntity>(filter: $"PartitionKey eq '{sellerId}'");

        await foreach (var rule in query)
        {
            rules.Add(MapToDto(rule));
        }

        return rules;
    }

    public async Task<List<StockRuleDto>> GetAllRulesAsync()
    {
        var rules = new List<StockRuleDto>();
        var query = _tableClient.QueryAsync<StockRuleEntity>();

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
        var query = _tableClient.QueryAsync<StockSourceIndexEntity>(filter: $"PartitionKey eq '{sourceItemId}'");

        await foreach (var index in query)
        {
            indexes.Add(index);
        }

        return indexes;
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
                    // Index PK = Source, RK = Target
                    await _tableClient.DeleteEntityAsync(component.SourceItemId, rule.TargetItemId);
                }
            }
        }
        catch
        {
            // Ignore deserialization errors or missing components during cleanup
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
            Components = string.IsNullOrEmpty(entity.ComponentsJson) 
                ? new List<RuleComponentDto>() 
                : JsonSerializer.Deserialize<List<RuleComponentDto>>(entity.ComponentsJson) ?? new List<RuleComponentDto>(),
            Mappings = entity.Mappings.Select(m => new VariantMappingDto
            {
                TargetVariantId = m.TargetVariantId,
                TargetSku = m.TargetSku,
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
