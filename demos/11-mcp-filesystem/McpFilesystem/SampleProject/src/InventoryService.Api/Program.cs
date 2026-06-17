// TODO: Dodać middleware uwierzytelniania
// TODO: Dodać ograniczanie liczby żądań (rate limiting)
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddScoped<IInventoryRepository, SqlInventoryRepository>();

var app = builder.Build();
app.MapControllers();
app.Run();
