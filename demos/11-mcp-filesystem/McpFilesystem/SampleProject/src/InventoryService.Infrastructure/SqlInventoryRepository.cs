// TODO: Dodać konfigurację puli połączeń
// TODO: Dodać politykę ponawiania dla przejściowych błędów
public class SqlInventoryRepository : IInventoryRepository
{
    private readonly string _connectionString;

    public SqlInventoryRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing 'Default' connection string");
    }

    public Task<IEnumerable<InventoryItem>> GetAllAsync()
    {
        // TODO: Zaimplementować za pomocą Dapper lub EF Core
        throw new NotImplementedException();
    }

    public Task<InventoryItem?> GetByIdAsync(int id)
    {
        throw new NotImplementedException();
    }

    public Task UpdateStockAsync(int id, int quantity)
    {
        throw new NotImplementedException();
    }
}
