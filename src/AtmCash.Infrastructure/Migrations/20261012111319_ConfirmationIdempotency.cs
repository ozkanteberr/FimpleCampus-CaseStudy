using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtmCash.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConfirmationIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WithdrawalConfirmations",
                columns: table => new
                {
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    QuoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlternativeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResponseJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WithdrawalConfirmations", x => x.IdempotencyKey);
                    table.ForeignKey(
                        name: "FK_WithdrawalConfirmations_WithdrawalQuotes_QuoteId",
                        column: x => x.QuoteId,
                        principalTable: "WithdrawalQuotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawalConfirmations_QuoteId",
                table: "WithdrawalConfirmations",
                column: "QuoteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WithdrawalConfirmations");
        }
    }
}
