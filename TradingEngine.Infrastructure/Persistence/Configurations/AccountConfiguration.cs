using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;

namespace TradingEngine.Infrastructure.Persistence.Configurations
{
    public class AccountConfiguration:IEntityTypeConfiguration<Account>
    {
        public void Configure(EntityTypeBuilder<Account> builder)
        {
            builder.HasKey(a => a.AccountId);
            builder.Property(a => a.AccountId).ValueGeneratedOnAdd();
            builder.Property(a => a.AccountName).IsRequired().HasMaxLength(100);
            builder.Property(a => a.CashBalance).HasPrecision(18, 2);
            builder.Property(a => a.AvailableCash).HasPrecision(18, 2);
            builder.Property(a=>a.ReservedCash).HasPrecision(18,2);
            builder.Property(a => a.IsActive).IsRequired();
            //Concurrency token
            builder.Property(a => a.Version).IsRequired().HasDefaultValue(1L).IsConcurrencyToken();

            //Account->Order - 1:M
            builder.HasMany(a=>a.Orders)
                   .WithOne(o=>o.Account)
                   .HasForeignKey(o => o.AccountId)
                   .OnDelete(DeleteBehavior.Restrict);

            //Account->Position - 1:M
            builder.HasMany(a=>a.Positions)
                   .WithOne(p=>p.Account)
                   .HasForeignKey(p => p.AccountId)
                   .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
