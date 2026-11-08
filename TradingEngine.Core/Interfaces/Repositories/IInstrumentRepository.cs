using TradingEngine.Core.Entities;

public interface IInstrumentRepository
{
    Task<List<TradeInstrument>> GetAllAsync(CancellationToken ct = default);
    Task<TradeInstrument?> GetByIdAsync(int instrumentId, CancellationToken ct = default);
}