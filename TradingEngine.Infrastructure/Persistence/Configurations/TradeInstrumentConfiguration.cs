using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradingEngine.Core.Entities;

namespace TradingEngine.Infrastructure.Persistence.Configurations
{
    public class TradeInstrumentConfiguration :
        IEntityTypeConfiguration<TradeInstrument>
    {
        public void Configure(
            EntityTypeBuilder<TradeInstrument> builder)
        {
            // Primary key
            builder.HasKey(t => t.InstrumentId);

            builder.Property(t => t.InstrumentId)
                .ValueGeneratedOnAdd();

            // Properties
            builder.Property(t => t.InstrumentName)
                .IsRequired()
                .HasMaxLength(200);

            builder.Property(t => t.InstrumentSymbol)
                .IsRequired()
                .HasMaxLength(20);

            builder.Property(t => t.IsActive)
                .IsRequired();

            // Each stock symbol must be unique
            builder.HasIndex(t => t.InstrumentSymbol)
                .IsUnique();

            // TradeInstrument (1) -> Order (Many)
            builder.HasMany(t => t.Orders)
                .WithOne(o => o.TradeInstrument)
                .HasForeignKey(o => o.InstrumentId)
                .OnDelete(DeleteBehavior.Restrict);

            // TradeInstrument (1) -> Position (Many)
            builder.HasMany(t => t.Positions)
                .WithOne(p => p.TradeInstrument)
                .HasForeignKey(p => p.InstrumentId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}