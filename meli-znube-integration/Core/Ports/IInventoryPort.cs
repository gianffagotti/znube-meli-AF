namespace meli_znube_integration.Core.Ports;

public interface IInventoryPort
{
    Task<int> GetAvailableStockAsync(string sku);
}
