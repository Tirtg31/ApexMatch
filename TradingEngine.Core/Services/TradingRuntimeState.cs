public sealed class TradingRuntimeState : IDisposable
{
    public SemaphoreSlim TradingGate { get; } = new(1, 1);
    public bool RequiresRecovery { get; set; } = true;
    public void Dispose()
    {
        TradingGate.Dispose();
    }
}