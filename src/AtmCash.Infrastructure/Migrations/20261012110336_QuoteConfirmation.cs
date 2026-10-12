using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtmCash.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class QuoteConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConfirmedAt",
                table: "WithdrawalQuotes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SelectedAlternativeId",
                table: "WithdrawalQuotes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_WithdrawalQuote_Confirmation",
                table: "WithdrawalQuotes",
                sql: "\"Status\" <> 'Confirmed' OR (\"SelectedAlternativeId\" IS NOT NULL AND \"ConfirmedAt\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_WithdrawalQuote_Confirmation",
                table: "WithdrawalQuotes");

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                table: "WithdrawalQuotes");

            migrationBuilder.DropColumn(
                name: "SelectedAlternativeId",
                table: "WithdrawalQuotes");
        }
    }
}
