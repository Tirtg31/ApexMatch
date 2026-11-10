using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradingEngine.Api.BackgroundServices;
using TradingEngine.Api.Exceptions;
using TradingEngine.Core.Interfaces.Repositories;
using TradingEngine.Core.Interfaces.Repositories.MarketData;
using TradingEngine.Core.Services;
using TradingEngine.Infrastructure.MarketData;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;

var builder = WebApplication.CreateBuilder(args);

//Register HttpClient
builder.Services.AddHttpClient<IMarketDataProvider,FinnhubMarketDataProvider>((sp, client) =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var apiKey = configuration["Finnhub:ApiKey"];

    if (string.IsNullOrWhiteSpace(apiKey))
        throw new InvalidOperationException("Finnhub API key is missing.");

    client.BaseAddress = new Uri("https://finnhub.io/api/v1/");
    client.DefaultRequestHeaders.Add("X-Finnhub-Token", apiKey);
    client.Timeout = TimeSpan.FromSeconds(10);
});

// Add services to the container.
builder.Services.AddDbContext<TradingDbContext>(options =>options.UseOracle(builder.Configuration.GetConnectionString("TradingDb"),
                                                oracleOptions => oracleOptions.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion21)));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddSingleton<TradingRuntimeState>();
builder.Services.AddSingleton<OrderMatchingEngine>();

//Background service
builder.Services.AddHostedService<MarketPriceBackgroundService>();

// Repositories
builder.Services.AddScoped<IAccountRepository, AccountRepository>();
builder.Services.AddScoped<IPositionRepository, PositionRepository>();
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<IExecutionRepository, ExecutionRepository>();
builder.Services.AddScoped<IInstrumentRepository, InstrumentRepository>();
builder.Services.AddSingleton<IMarketPriceStore, MarketPriceStore>();

// Unit of Work
builder.Services.AddScoped<IUnitOfWork, EfUnitOfWork>();

// Business Services
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<PositionService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<SettlementService>();


builder.Services.AddScoped<TradingCoordinator>(sp =>
    new TradingCoordinator(
        sp.GetRequiredService<SettlementService>(),
        sp.GetRequiredService<OrderMatchingEngine>(),
        sp.GetRequiredService<OrderService>(),
        sp.GetRequiredService<IOrderRepository>(),
        sp.GetRequiredService<AccountService>(),
        sp.GetRequiredService<TradingRuntimeState>()
    ));

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseExceptionHandler();
// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();
app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var coordinator = scope.ServiceProvider
        .GetRequiredService<TradingCoordinator>();

    await coordinator.RecoverOrderBookAsync();
}

app.Run();
