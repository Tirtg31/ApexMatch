using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TradingEngine.Core.Interfaces
{
    public interface IVersionedEntity
    {
        public long Version { get; set; }
    }
}
