using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradingEngine.Core.Entities;

namespace TradingEngine.Infrastructure.Persistence.Configurations
{
    public class ExecutionConfiguration :
        IEntityTypeConfiguration<Execution>
    {
        public void Configure(
            EntityTypeBuilder<Execution> builder)
        {
            // Primary key
            builder.HasKey(e => e.ExecutionId);

            builder.Property(e => e.ExecutionId)
                .ValueGeneratedOnAdd();

            // Properties
            builder.Property(e => e.ExecutedQuantity)
                .IsRequired();

            builder.Property(e => e.ExecutedPrice)
                .HasPrecision(18, 2);

            builder.Property(e => e.ExecutedAt)
                .IsRequired();

            // Order (1) -> Execution (Many)
            // When the order is the BUY order
            builder.HasOne(e => e.BuyOrder)
                .WithMany(o => o.BuyExecutions)
                .HasForeignKey(e => e.BuyOrderId)
                .OnDelete(DeleteBehavior.Restrict);

            // Order (1) -> Execution (Many)
            // When the order is the SELL order
            builder.HasOne(e => e.SellOrder)
                .WithMany(o => o.SellExecutions)
                .HasForeignKey(e => e.SellOrderId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasIndex(e => e.BuyOrderId);
            builder.HasIndex(e => e.SellOrderId);
        }
    }
}