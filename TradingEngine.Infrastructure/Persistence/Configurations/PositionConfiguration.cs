using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradingEngine.Core.Entities;

namespace TradingEngine.Infrastructure.Persistence.Configurations
{
    public class PositionConfiguration : IEntityTypeConfiguration<Position>
    {
        public void Configure(EntityTypeBuilder<Position> builder)
        {
            // Primary key
            builder.HasKey(p => p.PositionId);

            builder.Property(p => p.PositionId)
                .ValueGeneratedOnAdd();

            // Properties
            builder.Property(p => p.Quantity)
                .IsRequired();

            builder.Property(p => p.ReservedQuantity)
                .IsRequired()
                .HasDefaultValue(0);

            builder.Property(p => p.AverageCost)
                .HasPrecision(18, 2);

            builder.Property(p => p.Version)
                .IsRequired()
                .HasDefaultValue(1L)
                .IsConcurrencyToken();

            // This is a calculated C# property.
            // Do not store it in the database.
            builder.Ignore(p => p.AvailableQuantity);

            // One position per account and instrument
            builder.HasIndex(p => new
            {
                p.AccountId,
                p.InstrumentId
            }).IsUnique();

            // Account -> Position is configured
            // in AccountConfiguration.

            // TradeInstrument -> Position is configured
            // in TradeInstrumentConfiguration.
        }
    }
}