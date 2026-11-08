using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using TradingEngine.Core.Entities;
using TradingEngine.Core.Interfaces.Repositories;

namespace TradingEngine.Core.Services
{
    public class PositionService
    {
        private readonly Dictionary<(int accountId, int instrumentId), Position> Positions = new();
        private readonly object positionLock = new();
        private int _nextPositionId = 1;

        private readonly IPositionRepository positionRepository;
        private readonly IUnitOfWork unitOfWork;
        public PositionService()
        {
            // Used by existing in-memory unit tests
        }
        public PositionService(IPositionRepository positionRepository, IUnitOfWork unitOfWork)
        {
            this.positionRepository = positionRepository;
            this.unitOfWork = unitOfWork;
        }

        private static Position ClonePosition(Position position)
        {
            return new Position
            {
                PositionId = position.PositionId,
                AccountId = position.AccountId,
                InstrumentId = position.InstrumentId,
                Quantity = position.Quantity,
                ReservedQuantity = position.ReservedQuantity,
                AverageCost = position.AverageCost
            };
        }
        public void AddPosition(int accountId, int instrumentId, int quantity, decimal price)
        {
            lock (positionLock)
            {
                if (accountId <= 0) throw new ArgumentException(nameof(accountId), "Account ID must be positive.");
                if (instrumentId <= 0) throw new ArgumentException("Invalid instrument");
                if (quantity <= 0) throw new ArgumentException("Quantity should be greater than 0");
                if (price <= 0) throw new ArgumentException("Price should be greater than 0");

                if (!Positions.TryGetValue((accountId, instrumentId), out Position? position)) // ITS A NEW POSITION
                {
                    Position newPos = new Position
                    {
                        PositionId = _nextPositionId++,
                        AccountId = accountId,
                        InstrumentId = instrumentId,
                        Quantity = quantity,
                        ReservedQuantity = 0,
                        AverageCost = price
                    };
                    Positions[(accountId, instrumentId)] = newPos;
                }
                else
                {
                    decimal totalCost = (position.AverageCost * position.Quantity) + (price * quantity);
                    position.Quantity += quantity;
                    position.AverageCost = totalCost / position.Quantity;
                }
            }
        }
        public void ReducePosition(int accountId, int instrumentId, int quantity)
        {
            lock (positionLock)
            {
                if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId), "Account ID must be positive.");
                if (instrumentId <= 0) throw new ArgumentException("Invalid instrument");
                if (quantity <= 0) throw new ArgumentException("Quantity should be greater than 0");

                if (!Positions.TryGetValue((accountId, instrumentId), out Position? position))
                    throw new ArgumentException("Position does not exist");

                if (position.AvailableQuantity < quantity)
                    throw new ArgumentException("Provided quantity exceeds available quantity");

                position.Quantity -= quantity;
                if (position.Quantity == 0)
                    Positions.Remove((accountId, instrumentId));
            }
        }
        public void ReserveQuantity(int accountId, int instrumentId, int quantity)
        {
            lock (positionLock)
            {
                if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId), "Account ID must be positive.");
                if (instrumentId <= 0) throw new ArgumentException("Invalid instrument");
                if (quantity <= 0) throw new ArgumentException("Quantity should be greater than 0");

                if (!Positions.TryGetValue((accountId, instrumentId), out Position? position))
                    throw new ArgumentException("Position does not exist");
                if (position.AvailableQuantity < quantity)
                    throw new ArgumentException("Insufficient stocks");

                position.ReservedQuantity += quantity;
            }
        }
        public void ReleaseReservedQuantity(int accountId, int instrumentId, int quantity)
        {
            lock (positionLock)
            {
                if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId), "Account ID must be positive.");
                if (instrumentId <= 0) throw new ArgumentException("Invalid instrument");
                if (quantity <= 0) throw new ArgumentException("Quantity should be greater than 0");

                if (!Positions.TryGetValue((accountId, instrumentId), out Position? position))
                    throw new ArgumentException("Position does not exist");
                if (position.ReservedQuantity < quantity)
                    throw new InvalidOperationException("Quantity exceeds reserved shares.");

                position.ReservedQuantity -= quantity;
            }
        }
        public void SettleSellPosition(int accountId, int instrumentId, int quantity)
        {
            lock (positionLock)
            {
                if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId), "Account ID must be positive.");
                if (instrumentId <= 0) throw new ArgumentException("Invalid instrument");
                if (quantity <= 0) throw new ArgumentException("Quantity should be greater than 0");

                if (!Positions.TryGetValue((accountId, instrumentId), out Position? position))
                    throw new ArgumentException("Position does not exist");

                if (position.ReservedQuantity < quantity)
                    throw new InvalidOperationException("Insufficient reserved shares for settlement.");

                position.ReservedQuantity -= quantity;
                position.Quantity -= quantity;

                if (position.Quantity == 0)
                    Positions.Remove((accountId, instrumentId));
            }
        }

        public Position? GetPosition(int accountId, int instrumentId)
        {
            lock (positionLock)
            {
                if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId), "Account ID must be positive.");
                if (instrumentId <= 0) throw new ArgumentException("Invalid instrument");

                if (!Positions.TryGetValue((accountId, instrumentId), out Position? position))
                    return null;

                return ClonePosition(position);
            }
        }
        public Dictionary<int, Position> GetPositionsByAccount(int accountId)
        {
            lock (positionLock)
            {
                if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId), "Account ID must be positive.");

                Dictionary<int, Position> allPositions = Positions.Where(p => p.Key.accountId == accountId)
                                                                  .ToDictionary(p => p.Key.instrumentId, p => ClonePosition(p.Value));
                return allPositions;
            }
        }
        public int GetAvailableQuantity(int accountId, int instrumentId)
        {
            lock (positionLock)
            {
                if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId), "Account ID must be positive.");
                if (instrumentId <= 0) throw new ArgumentException("Invalid instrument");
                if (!Positions.TryGetValue((accountId, instrumentId), out Position? position))
                    throw new ArgumentException("Position does not exist");
                return position.AvailableQuantity;
            }
        }

        public async Task AddPositionAsync(int accountId, int instrumentId, int quantity, decimal price, CancellationToken ct=default)
        {
            if (accountId <= 0) throw new ArgumentException(nameof(accountId), "Account ID must be positive.");
            if (instrumentId <= 0) throw new ArgumentException("Invalid instrument");
            if (quantity <= 0) throw new ArgumentException("Quantity should be greater than 0");
            if (price <= 0) throw new ArgumentException("Price should be greater than 0");
            await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                Position? position=await GetPositionAsync(accountId, instrumentId, token);
                if(position==null)
                {
                    Position newPos = new Position
                    {
                        AccountId = accountId,
                        InstrumentId = instrumentId,
                        Quantity = quantity,
                        ReservedQuantity = 0,
                        AverageCost = price
                    };
                    await positionRepository.AddAsync(newPos, token); 
                }
                else
                {
                    decimal totalCost = (position.AverageCost * position.Quantity) + (price * quantity);
                    int newQuantity = checked(position.Quantity + quantity);
                    position.AverageCost = totalCost / newQuantity;
                    position.Quantity = newQuantity;
                }
                return true;
            },ct);
        }
        public async Task<Position?>GetPositionAsync(int accountId, int instrumentId, CancellationToken ct = default)
        {
            if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId), "Account ID must be positive.");
            if (instrumentId <= 0) throw new ArgumentException("Invalid instrument");
            return await positionRepository.GetByAccountAndInstrumentAsync(accountId, instrumentId, ct);
        }
        public async Task ReserveQuantityAsync(int accountId, int instrumentId, int quantity, CancellationToken ct=default)
        {
            if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId), "Account ID must be positive.");
            if (instrumentId <= 0) throw new ArgumentException("Invalid instrument");
            if (quantity <= 0) throw new ArgumentException("Quantity should be greater than 0");
            await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                Position? position = await GetPositionAsync(accountId, instrumentId, token);
                if(position==null) throw new ArgumentException("Position does not exist");
                if(position.AvailableQuantity<quantity) throw new ArgumentException("Insufficient stocks");
                position.ReservedQuantity += quantity;
                return true;
            },ct);
        }
        public async Task ReserveQuantityWithoutCommitAsync(int accountId, int instrumentId, int quantity, CancellationToken ct = default)
        {
            if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId), "Account ID must be positive.");
            if (instrumentId <= 0) throw new ArgumentException("Invalid instrument");
            if (quantity <= 0) throw new ArgumentException("Quantity should be greater than 0");
            
            Position? position = await GetPositionAsync(accountId, instrumentId, ct);
            if (position == null) throw new ArgumentException("Position does not exist");
            if (position.AvailableQuantity < quantity) throw new ArgumentException("Insufficient stocks");
            position.ReservedQuantity += quantity;
        }
        public async Task ReducePositionAsync(int accountId, int instrumentId, int quantity, CancellationToken ct = default)
        {
            if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId), "Account ID must be positive.");
            if (instrumentId <= 0) throw new ArgumentException("Invalid instrument");
            if (quantity <= 0) throw new ArgumentException("Quantity should be greater than 0");
            await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                Position? position = await GetPositionAsync(accountId, instrumentId, token);
                if (position == null) throw new ArgumentException("Position does not exist");
                if (position.AvailableQuantity < quantity)
                    throw new ArgumentException("Provided quantity exceeds available quantity");
                position.Quantity -= quantity;
                return true;
            },ct);
        }
        public async Task IncreasePositionAsync(int accountId, int instrumentId, int quantity, decimal price, CancellationToken ct = default)
        {
            await AddPositionAsync(accountId, instrumentId, quantity, price, ct);
        }

        public async Task IncreasePositionWithoutCommitAsync(int accountId, int instrumentId, int quantity, decimal price, CancellationToken ct = default)
        {
            if (accountId <= 0 || instrumentId <= 0 ||
                quantity <= 0 || price <= 0)
                throw new ArgumentException("Invalid position details.");

            Position? position = await GetPositionAsync(
                accountId, instrumentId, ct);

            if (position == null)
            {
                Position newPosition = new Position
                {
                    AccountId = accountId,
                    InstrumentId = instrumentId,
                    Quantity = quantity,
                    ReservedQuantity = 0,
                    AverageCost = price
                };

                await positionRepository.AddAsync(newPosition, ct);
            }
            else
            {
                if (position.Quantity < 0 ||
                    position.ReservedQuantity < 0 ||
                    position.ReservedQuantity > position.Quantity)
                    throw new InvalidOperationException(
                        "Invalid existing position.");

                int newQuantity = checked(
                    position.Quantity + quantity);

                decimal totalCost = checked(
                    position.AverageCost * position.Quantity
                    + price * quantity);

                position.AverageCost = totalCost / newQuantity;
                position.Quantity = newQuantity;
            }
        }
        public async Task SettleSellPositionWithoutCommitAsync(int accountId, int instrumentId, int quantity, CancellationToken ct = default)
        {
            if (accountId <= 0 || instrumentId <= 0 || quantity <= 0)
                throw new ArgumentException("Invalid position details.");

            Position position = await GetPositionAsync(
                accountId, instrumentId, ct)
                ?? throw new InvalidOperationException(
                    "Seller position not found.");

            if (position.Quantity < quantity ||
                position.ReservedQuantity < quantity)
                throw new InvalidOperationException(
                    "Insufficient reserved shares.");

            position.Quantity -= quantity;
            position.ReservedQuantity -= quantity;
        }

        public async Task ReleaseReservedQuantityAsync(int accountId, int instrumentId, int quantity, CancellationToken ct = default)
        {
            if (accountId <= 0)
                throw new ArgumentOutOfRangeException(nameof(accountId));

            if (instrumentId <= 0)
                throw new ArgumentOutOfRangeException(nameof(instrumentId));

            if (quantity <= 0)
                throw new ArgumentException("Quantity must be positive", nameof(quantity));

            await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                Position? position = await GetPositionAsync(accountId, instrumentId, token);

                if (position == null)
                    throw new ArgumentException("Position does not exist");

                if (position.ReservedQuantity < quantity)
                    throw new InvalidOperationException("Insufficient reserved quantity");

                position.ReservedQuantity -= quantity;

                return true;
            }, ct);
        }
        public async Task ReleaseReservedQuantityWithoutCommitAsync(int accountId, int instrumentId, int quantity, CancellationToken ct = default)
        {
            if (accountId <= 0)
                throw new ArgumentOutOfRangeException(nameof(accountId));
            if (instrumentId <= 0)
                throw new ArgumentOutOfRangeException(nameof(instrumentId));
            if (quantity <= 0)
                throw new ArgumentException("Quantity must be positive", nameof(quantity));

            Position? position = await GetPositionAsync(accountId, instrumentId, ct);

            if (position == null)
                throw new ArgumentException("Position does not exist");
            if (position.ReservedQuantity < quantity)
                throw new InvalidOperationException("Insufficient reserved quantity");
            position.ReservedQuantity -= quantity;
        }
    }
}
