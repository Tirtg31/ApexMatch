using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    AccountId = table.Column<int>(type: "NUMBER(10)", nullable: false)
                        .Annotation("Oracle:Identity", "START WITH 1 INCREMENT BY 1"),
                    AccountName = table.Column<string>(type: "NVARCHAR2(100)", maxLength: 100, nullable: false),
                    CashBalance = table.Column<decimal>(type: "DECIMAL(18,2)", precision: 18, scale: 2, nullable: false),
                    ReservedCash = table.Column<decimal>(type: "DECIMAL(18,2)", precision: 18, scale: 2, nullable: false),
                    AvailableCash = table.Column<decimal>(type: "DECIMAL(18,2)", precision: 18, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "NUMBER(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.AccountId);
                });

            migrationBuilder.CreateTable(
                name: "TradeInstruments",
                columns: table => new
                {
                    InstrumentId = table.Column<int>(type: "NUMBER(10)", nullable: false)
                        .Annotation("Oracle:Identity", "START WITH 1 INCREMENT BY 1"),
                    InstrumentName = table.Column<string>(type: "NVARCHAR2(200)", maxLength: 200, nullable: false),
                    InstrumentSymbol = table.Column<string>(type: "NVARCHAR2(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "NUMBER(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeInstruments", x => x.InstrumentId);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    OrderId = table.Column<int>(type: "NUMBER(10)", nullable: false)
                        .Annotation("Oracle:Identity", "START WITH 1 INCREMENT BY 1"),
                    AccountId = table.Column<int>(type: "NUMBER(10)", nullable: false),
                    InstrumentId = table.Column<int>(type: "NUMBER(10)", nullable: false),
                    Side = table.Column<int>(type: "NUMBER(10)", nullable: false),
                    OrderType = table.Column<int>(type: "NUMBER(10)", nullable: false),
                    Quantity = table.Column<int>(type: "NUMBER(10)", nullable: false),
                    LimitPrice = table.Column<decimal>(type: "DECIMAL(18,2)", precision: 18, scale: 2, nullable: true),
                    FilledQuantity = table.Column<int>(type: "NUMBER(10)", nullable: false, defaultValue: 0),
                    Status = table.Column<int>(type: "NUMBER(10)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TIMESTAMP(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.OrderId);
                    table.ForeignKey(
                        name: "FK_Orders_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Orders_TradeInstruments_InstrumentId",
                        column: x => x.InstrumentId,
                        principalTable: "TradeInstruments",
                        principalColumn: "InstrumentId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Positions",
                columns: table => new
                {
                    PositionId = table.Column<int>(type: "NUMBER(10)", nullable: false)
                        .Annotation("Oracle:Identity", "START WITH 1 INCREMENT BY 1"),
                    AccountId = table.Column<int>(type: "NUMBER(10)", nullable: false),
                    InstrumentId = table.Column<int>(type: "NUMBER(10)", nullable: false),
                    Quantity = table.Column<int>(type: "NUMBER(10)", nullable: false),
                    ReservedQuantity = table.Column<int>(type: "NUMBER(10)", nullable: false, defaultValue: 0),
                    AverageCost = table.Column<decimal>(type: "DECIMAL(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Positions", x => x.PositionId);
                    table.ForeignKey(
                        name: "FK_Positions_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Positions_TradeInstruments_InstrumentId",
                        column: x => x.InstrumentId,
                        principalTable: "TradeInstruments",
                        principalColumn: "InstrumentId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Executions",
                columns: table => new
                {
                    ExecutionId = table.Column<int>(type: "NUMBER(10)", nullable: false)
                        .Annotation("Oracle:Identity", "START WITH 1 INCREMENT BY 1"),
                    BuyOrderId = table.Column<int>(type: "NUMBER(10)", nullable: false),
                    SellOrderId = table.Column<int>(type: "NUMBER(10)", nullable: false),
                    ExecutedQuantity = table.Column<int>(type: "NUMBER(10)", nullable: false),
                    ExecutedPrice = table.Column<decimal>(type: "DECIMAL(18,2)", precision: 18, scale: 2, nullable: false),
                    ExecutedAt = table.Column<DateTime>(type: "TIMESTAMP(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Executions", x => x.ExecutionId);
                    table.ForeignKey(
                        name: "FK_Executions_Orders_BuyOrderId",
                        column: x => x.BuyOrderId,
                        principalTable: "Orders",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Executions_Orders_SellOrderId",
                        column: x => x.SellOrderId,
                        principalTable: "Orders",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Executions_BuyOrderId",
                table: "Executions",
                column: "BuyOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Executions_SellOrderId",
                table: "Executions",
                column: "SellOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_AccountId",
                table: "Orders",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_InstrumentId",
                table: "Orders",
                column: "InstrumentId");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_AccountId_InstrumentId",
                table: "Positions",
                columns: new[] { "AccountId", "InstrumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Positions_InstrumentId",
                table: "Positions",
                column: "InstrumentId");

            migrationBuilder.CreateIndex(
                name: "IX_TradeInstruments_InstrumentSymbol",
                table: "TradeInstruments",
                column: "InstrumentSymbol",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Executions");

            migrationBuilder.DropTable(
                name: "Positions");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "TradeInstruments");
        }
    }
}
