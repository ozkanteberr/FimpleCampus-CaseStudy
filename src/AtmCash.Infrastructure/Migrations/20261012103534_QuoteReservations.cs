using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtmCash.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class QuoteReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WithdrawalQuotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AtmId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    RequestedAmount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WithdrawalQuotes", x => x.Id);
                    table.CheckConstraint("CK_WithdrawalQuote_Amount", "\"RequestedAmount\" > 0");
                    table.CheckConstraint("CK_WithdrawalQuote_Expiry", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.ForeignKey(
                        name: "FK_WithdrawalQuotes_Atms_AtmId",
                        column: x => x.AtmId,
                        principalTable: "Atms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WithdrawalAlternatives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WithdrawalAlternatives", x => x.Id);
                    table.CheckConstraint("CK_WithdrawalAlternative_Amount", "\"Amount\" > 0");
                    table.ForeignKey(
                        name: "FK_WithdrawalAlternatives_WithdrawalQuotes_QuoteId",
                        column: x => x.QuoteId,
                        principalTable: "WithdrawalQuotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WithdrawalReservations",
                columns: table => new
                {
                    QuoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    CassetteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Denomination = table.Column<int>(type: "integer", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WithdrawalReservations", x => new { x.QuoteId, x.CassetteId, x.Currency, x.Denomination });
                    table.CheckConstraint("CK_WithdrawalReservation_Count", "\"Count\" BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_WithdrawalReservation_Note", "\"Currency\" IN ('TRY','USD','EUR') AND \"Denomination\" IN (20,50,100,500)");
                    table.ForeignKey(
                        name: "FK_WithdrawalReservations_Cassettes_CassetteId",
                        column: x => x.CassetteId,
                        principalTable: "Cassettes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WithdrawalReservations_WithdrawalQuotes_QuoteId",
                        column: x => x.QuoteId,
                        principalTable: "WithdrawalQuotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WithdrawalAllocations",
                columns: table => new
                {
                    AlternativeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CassetteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Denomination = table.Column<int>(type: "integer", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WithdrawalAllocations", x => new { x.AlternativeId, x.CassetteId, x.Currency, x.Denomination });
                    table.CheckConstraint("CK_WithdrawalAllocation_Count", "\"Count\" BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_WithdrawalAllocation_Note", "\"Currency\" IN ('TRY','USD','EUR') AND \"Denomination\" IN (20,50,100,500)");
                    table.ForeignKey(
                        name: "FK_WithdrawalAllocations_Cassettes_CassetteId",
                        column: x => x.CassetteId,
                        principalTable: "Cassettes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WithdrawalAllocations_WithdrawalAlternatives_AlternativeId",
                        column: x => x.AlternativeId,
                        principalTable: "WithdrawalAlternatives",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawalAllocations_CassetteId",
                table: "WithdrawalAllocations",
                column: "CassetteId");

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawalAlternatives_QuoteId_Kind",
                table: "WithdrawalAlternatives",
                columns: new[] { "QuoteId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawalQuotes_AtmId",
                table: "WithdrawalQuotes",
                column: "AtmId");

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawalQuotes_Status_ExpiresAt",
                table: "WithdrawalQuotes",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawalReservations_CassetteId",
                table: "WithdrawalReservations",
                column: "CassetteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WithdrawalAllocations");

            migrationBuilder.DropTable(
                name: "WithdrawalReservations");

            migrationBuilder.DropTable(
                name: "WithdrawalAlternatives");

            migrationBuilder.DropTable(
                name: "WithdrawalQuotes");
        }
    }
}
