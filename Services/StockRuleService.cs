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

    public async Task SaveRuleAsync(StockRuleEntity rule)
    {
        // 1. Check if rule exists to clean up old index
        var existingRule = await GetRuleAsync(rule.PartitionKey, rule.RowKey);
        if (existingRule != null)
        {
            await DeleteIndexEntriesAsync(existingRule);
        }

        // 2. Upsert the main rule
        await _tableClient.UpsertEntityAsync(rule, TableUpdateMode.Replace);

        // 3. Insert new index entries
        var components = JsonSerializer.Deserialize<List<RuleComponentDto>>(rule.ComponentsJson) ?? new List<RuleComponentDto>();
        foreach (var component in components)
        {
            var indexEntity = new StockSourceIndexEntity
            {
                PartitionKey = component.SourceItemId, // Source is PK for Index
                RowKey = rule.TargetItemId,            // Target is RK for Index
                RuleType = rule.RuleType,
                Timestamp = DateTimeOffset.UtcNow
            };
            await _tableClient.UpsertEntityAsync(indexEntity, TableUpdateMode.Replace);
        }
    }

    public async Task DeleteRuleAsync(string sellerId, string targetItemId)
    {
        var rule = await GetRuleAsync(sellerId, targetItemId);
        if (rule != null)
        {
            // 1. Delete index entries
            await DeleteIndexEntriesAsync(rule);

            // 2. Delete main rule
            await _tableClient.DeleteEntityAsync(sellerId, targetItemId);
        }
    }

    public async Task<List<StockRuleEntity>> GetRulesBySellerAsync(string sellerId)
    {
        var rules = new List<StockRuleEntity>();
        var query = _tableClient.QueryAsync<StockRuleEntity>(filter: $"PartitionKey eq '{sellerId}'");

        await foreach (var rule in query)
        {
            rules.Add(rule);
        }

        return rules;
    }

    public async Task<List<StockRuleEntity>> GetAllRulesAsync()
    {
        var rules = new List<StockRuleEntity>();
        var query = _tableClient.QueryAsync<StockRuleEntity>();

        await foreach (var rule in query)
        {
            rules.Add(rule);
        }

        return rules;
    }

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

    public async Task<StockRuleEntity?> GetRuleAsync(string sellerId, string targetItemId)
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
}
