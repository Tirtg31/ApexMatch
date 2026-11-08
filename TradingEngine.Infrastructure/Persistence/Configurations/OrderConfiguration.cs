using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradingEngine.Core.Entities;

namespace TradingEngine.Infrastructure.Persistence.Configurations
{
    public class OrderConfiguration : IEntityTypeConfiguration<Order>
    {
        public void Configure(EntityTypeBuilder<Order> builder)
        {
            // Primary key
            builder.HasKey(o => o.OrderId);

            builder.Property(o => o.OrderId)
                .ValueGeneratedOnAdd();

            // Properties
            builder.Property(o => o.Side)
                .IsRequired();

            builder.Property(o => o.OrderType)
                .IsRequired();

            builder.Property(o => o.Quantity)
                .IsRequired();

            builder.Property(o => o.LimitPrice)
                .HasPrecision(18, 2);

            builder.Property(o => o.FilledQuantity)
                .IsRequired()
                .HasDefaultValue(0);

            builder.Property(o => o.Status)
                .IsRequired();

            builder.Property(o => o.CreatedAt)
                .IsRequired();

            builder.Property(o => o.Version)
                    .IsRequired()
                    .IsConcurrencyToken();

            // Account -> Order is already configured
            // in AccountConfiguration.

            // TradeInstrument -> Order is configured
            // in TradeInstrumentConfiguration.

            // Index to retrieve orders for an instrument
            builder.HasIndex(o => o.InstrumentId);

            // Index to retrieve orders for an account
            builder.HasIndex(o => o.AccountId);
        }
    }
}