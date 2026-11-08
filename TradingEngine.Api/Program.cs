using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradingEngine.Api.Exceptions;
using TradingEngine.Core.Interfaces.Repositories;
using TradingEngine.Core.Services;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddDbContext<TradingDbContext>(options =>options.UseOracle(builder.Configuration.GetConnectionString("TradingDb"),
                                                oracleOptions => oracleOptions.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion21)));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddSingleton<TradingRuntimeState>();
builder.Services.AddSingleton<OrderMatchingEngine>();
// Repositories
builder.Services.AddScoped<IAccountRepository, AccountRepository>();
builder.Services.AddScoped<IPositionRepository, PositionRepository>();
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<IExecutionRepository, ExecutionRepository>();
builder.Services.AddScoped<IInstrumentRepository, InstrumentRepository>();

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
