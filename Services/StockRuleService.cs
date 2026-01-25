using Azure.Data.Tables;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using Microsoft.Extensions.Configuration;

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

    public async Task UpsertRuleAsync(StockRuleEntity rule)
    {
        await _tableClient.UpsertEntityAsync(rule, TableUpdateMode.Replace);
    }

    public async Task DeleteRuleAsync(string motherUpid, string childUpid)
    {
        var query = _tableClient.QueryAsync<StockRuleEntity>(filter: $"MotherItemId eq '{motherUpid}' and ChildItemId eq '{childUpid}'");

        await foreach (var rule in query)
        {
            await _tableClient.DeleteEntityAsync(rule.PartitionKey, rule.RowKey);
        }
    }

    public async Task<List<StockRuleEntity>> GetRulesByMotherUpidAsync(string motherUpid)
    {
        var rules = new List<StockRuleEntity>();
        var query = _tableClient.QueryAsync<StockRuleEntity>(filter: $"PartitionKey eq '{motherUpid}'");

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

    public async Task DeleteRulesByMotherItemIdAsync(string motherItemId)
    {
        var query = _tableClient.QueryAsync<StockRuleEntity>(filter: $"MotherItemId eq '{motherItemId}'");

        await foreach (var rule in query)
        {
            await _tableClient.DeleteEntityAsync(rule.PartitionKey, rule.RowKey);
        }
    }
}
