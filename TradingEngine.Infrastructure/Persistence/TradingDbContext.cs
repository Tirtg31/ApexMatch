using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Interfaces;

namespace TradingEngine.Infrastructure.Persistence
{
    public class TradingDbContext:DbContext
    {
        public TradingDbContext(DbContextOptions<TradingDbContext> options):base(options) { }
        public DbSet<Account> Accounts {  get; set; }
        public DbSet<Execution> Executions {  get; set; }
        public DbSet<Order> Orders { get; set; }
        public DbSet<Position> Positions {  get; set; }
        public DbSet<TradeInstrument>TradeInstruments { get; set; }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyConfigurationsFromAssembly(typeof(TradingDbContext).Assembly);
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            UpdateVersions();
            return base.SaveChangesAsync(cancellationToken);
        }

        private void UpdateVersions()
        {
            ChangeTracker.DetectChanges();
            foreach (var entry in ChangeTracker.Entries<IVersionedEntity>())
            {
                if (entry.State == EntityState.Modified)
                {
                    var version = entry.Property(e => e.Version);
                    version.CurrentValue = checked(version.OriginalValue + 1);
                }
            }
        }


    }
}
