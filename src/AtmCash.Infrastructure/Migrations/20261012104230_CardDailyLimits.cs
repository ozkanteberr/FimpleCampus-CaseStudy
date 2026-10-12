using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtmCash.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CardDailyLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CardFingerprint",
                table: "WithdrawalQuotes",
                type: "character varying(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReservedLimitAmount",
                table: "WithdrawalQuotes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "CardDailyUsages",
                columns: table => new
                {
                    CardFingerprint = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    UtcDate = table.Column<DateOnly>(type: "date", nullable: false),
                    UsedAmount = table.Column<int>(type: "integer", nullable: false),
                    ReservedAmount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardDailyUsages", x => new { x.CardFingerprint, x.Currency, x.UtcDate });
                    table.CheckConstraint("CK_CardDailyUsage_Balance", "\"UsedAmount\" >= 0 AND \"ReservedAmount\" >= 0 AND \"UsedAmount\" + \"ReservedAmount\" <= CASE \"Currency\" WHEN 'TRY' THEN 20000 WHEN 'USD' THEN 1000 WHEN 'EUR' THEN 1000 ELSE -1 END");
                    table.CheckConstraint("CK_CardDailyUsage_Fingerprint", "\"CardFingerprint\" ~ '^[0-9a-f]{64}$'");
                });

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawalQuotes_CardFingerprint_Status",
                table: "WithdrawalQuotes",
                columns: new[] { "CardFingerprint", "Status" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_WithdrawalQuote_CardFingerprint",
                table: "WithdrawalQuotes",
                sql: "\"CardFingerprint\" IS NULL OR \"CardFingerprint\" ~ '^[0-9a-f]{64}$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WithdrawalQuote_LimitReservation",
                table: "WithdrawalQuotes",
                sql: "\"ReservedLimitAmount\" >= 0 AND (\"CardFingerprint\" IS NOT NULL OR \"ReservedLimitAmount\" = 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CardDailyUsages");

            migrationBuilder.DropIndex(
                name: "IX_WithdrawalQuotes_CardFingerprint_Status",
                table: "WithdrawalQuotes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WithdrawalQuote_CardFingerprint",
                table: "WithdrawalQuotes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WithdrawalQuote_LimitReservation",
                table: "WithdrawalQuotes");

            migrationBuilder.DropColumn(
                name: "CardFingerprint",
                table: "WithdrawalQuotes");

            migrationBuilder.DropColumn(
                name: "ReservedLimitAmount",
                table: "WithdrawalQuotes");
        }
    }
}
