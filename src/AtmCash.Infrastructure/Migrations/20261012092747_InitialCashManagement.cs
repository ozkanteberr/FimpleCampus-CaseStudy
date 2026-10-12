using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AtmCash.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCashManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Atms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Atms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExchangeRates",
                columns: table => new
                {
                    Currency = table.Column<string>(type: "text", nullable: false),
                    TryRate = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRates", x => x.Currency);
                });

            migrationBuilder.CreateTable(
                name: "Outbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AtmId = table.Column<Guid>(type: "uuid", nullable: false),
                    CassetteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Cassettes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AtmId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: true),
                    Denomination = table.Column<int>(type: "integer", nullable: true),
                    LowCash = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cassettes", x => x.Id);
                    table.CheckConstraint("CK_Cassette_Number", "\"Number\" BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_Cassettes_Atms_AtmId",
                        column: x => x.AtmId,
                        principalTable: "Atms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Movements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CassetteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Denomination = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    CountDelta = table.Column<int>(type: "integer", nullable: false),
                    ReservedDelta = table.Column<int>(type: "integer", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    User = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Movements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Movements_Cassettes_CassetteId",
                        column: x => x.CassetteId,
                        principalTable: "Cassettes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Stocks",
                columns: table => new
                {
                    CassetteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Denomination = table.Column<int>(type: "integer", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    Reserved = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stocks", x => new { x.CassetteId, x.Currency, x.Denomination });
                    table.CheckConstraint("CK_Stock_Count", "\"Count\" BETWEEN 0 AND 100 AND \"Reserved\" BETWEEN 0 AND \"Count\"");
                    table.CheckConstraint("CK_Stock_Note", "\"Currency\" IN ('TRY','USD','EUR') AND \"Denomination\" IN (20,50,100,500)");
                    table.ForeignKey(
                        name: "FK_Stocks_Cassettes_CassetteId",
                        column: x => x.CassetteId,
                        principalTable: "Cassettes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "ExchangeRates",
                columns: new[] { "Currency", "TryRate" },
                values: new object[,]
                {
                    { "EUR", 45m },
                    { "TRY", 1m },
                    { "USD", 40m }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Cassettes_AtmId_Number",
                table: "Cassettes",
                columns: new[] { "AtmId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Movements_CassetteId_CreatedAt",
                table: "Movements",
                columns: new[] { "CassetteId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_SentAt",
                table: "Outbox",
                column: "SentAt");

            migrationBuilder.Sql("""
                CREATE FUNCTION reject_movement_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Movement records are immutable' USING ERRCODE = '23514';
                END;
                $$;
                CREATE TRIGGER movements_no_update_delete
                    BEFORE UPDATE OR DELETE ON "Movements"
                    FOR EACH ROW EXECUTE FUNCTION reject_movement_mutation();
                CREATE TRIGGER movements_no_truncate
                    BEFORE TRUNCATE ON "Movements"
                    FOR EACH STATEMENT EXECUTE FUNCTION reject_movement_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExchangeRates");

            migrationBuilder.DropTable(
                name: "Movements");

            migrationBuilder.Sql("DROP FUNCTION reject_movement_mutation();");

            migrationBuilder.DropTable(
                name: "Outbox");

            migrationBuilder.DropTable(
                name: "Stocks");

            migrationBuilder.DropTable(
                name: "Cassettes");

            migrationBuilder.DropTable(
                name: "Atms");
        }
    }
}
