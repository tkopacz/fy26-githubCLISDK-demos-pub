public interface IInventoryRepository
{
    Task<IEnumerable<InventoryItem>> GetAllAsync();
    Task<InventoryItem?> GetByIdAsync(int id);
    Task UpdateStockAsync(int id, int quantity);
}

public record InventoryItem(int Id, string Sku, string Name, int StockQuantity, decimal Price);
