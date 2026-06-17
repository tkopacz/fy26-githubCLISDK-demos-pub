// FIXME: Brak paginacji w punkcie końcowym GetAll
// FIXME: Brak walidacji danych wejściowych w UpdateStock
[ApiController, Route("api/[controller]")]
public class InventoryController : ControllerBase
{
    private readonly IInventoryRepository _repo;
    public InventoryController(IInventoryRepository repo) => _repo = repo;

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _repo.GetAllAsync()); // FIXME: zwraca WSZYSTKIE elementy, bez paginacji

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(int id) =>
        Ok(await _repo.GetByIdAsync(id));

    [HttpPut("{id}/stock")]
    public async Task<IActionResult> UpdateStock(int id, [FromBody] int quantity)
    {
        // TODO: zwalidować quantity >= 0
        await _repo.UpdateStockAsync(id, quantity);
        return NoContent();
    }
}
