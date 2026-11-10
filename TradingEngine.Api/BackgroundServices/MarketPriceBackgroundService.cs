using TradingEngine.Core.Interfaces.Repositories.MarketData;

namespace TradingEngine.Api.BackgroundServices;

public class MarketPriceBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IMarketPriceStore marketPriceStore;
    private readonly ILogger<MarketPriceBackgroundService> logger;

    public MarketPriceBackgroundService(IServiceScopeFactory scopeFactory, IMarketPriceStore marketPriceStore, ILogger<MarketPriceBackgroundService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.marketPriceStore = marketPriceStore;
        this.logger = logger;
    }
    private async Task RefreshPricesAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var instrumentRepository = scope.ServiceProvider.GetRequiredService<IInstrumentRepository>();
        var marketDataProvider = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();

        var instruments = await instrumentRepository.GetAllAsync(ct);
        var activeInstruments = instruments.Where(i => i.IsActive).ToList();

        foreach (var instrument in activeInstruments)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var updatedPrice = await marketDataProvider.GetCurrentPriceAsync(instrument.InstrumentSymbol, ct);
                if (updatedPrice is not null)
                {
                    marketPriceStore.UpdatePrice(updatedPrice);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                logger.LogWarning(ex, "Finnhub rate limit reached. Ending refresh cycle.");
                break;
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Unable to fetch price for {Symbol}", instrument.InstrumentSymbol);
            }
            catch (System.Text.Json.JsonException ex)
            {
                logger.LogWarning(ex, "Invalid Finnhub response for {Symbol}", instrument.InstrumentSymbol);
            }
        }
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Market Price Background Service started.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));

        try
        {
            // Fetch initial prices immediately on startup
            await RefreshPricesAsync(stoppingToken);

            // Repeat every 60 seconds
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RefreshPricesAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during graceful application shutdown
            logger.LogInformation("Market Price Background Service is stopping.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error in Market Price Background Service.");
            throw;
        }
        finally
        {
            logger.LogInformation("Market Price Background Service stopped.");
        }
    }

}