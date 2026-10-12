using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtmCash.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReconciliationCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Reconciliations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AtmId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    TotalAbsoluteTryDifference = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    PhysicalCountsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reconciliations", x => x.Id);
                    table.CheckConstraint("CK_Reconciliation_Total", "\"TotalAbsoluteTryDifference\" >= 0");
                    table.ForeignKey(
                        name: "FK_Reconciliations_Atms_AtmId",
                        column: x => x.AtmId,
                        principalTable: "Atms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationLines",
                columns: table => new
                {
                    ReconciliationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CassetteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Denomination = table.Column<int>(type: "integer", nullable: false),
                    ExpectedCount = table.Column<int>(type: "integer", nullable: false),
                    ActualCount = table.Column<int>(type: "integer", nullable: false),
                    DeltaCount = table.Column<int>(type: "integer", nullable: false),
                    TryRateSnapshot = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    AbsoluteTryDifference = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationLines", x => new { x.ReconciliationId, x.CassetteId, x.Currency, x.Denomination });
                    table.CheckConstraint("CK_ReconciliationLine_Counts", "\"ExpectedCount\" BETWEEN 0 AND 100 AND \"ActualCount\" BETWEEN 0 AND 100 AND \"DeltaCount\" = \"ActualCount\" - \"ExpectedCount\" AND \"TryRateSnapshot\" > 0 AND \"AbsoluteTryDifference\" >= 0");
                    table.ForeignKey(
                        name: "FK_ReconciliationLines_Cassettes_CassetteId",
                        column: x => x.CassetteId,
                        principalTable: "Cassettes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReconciliationLines_Reconciliations_ReconciliationId",
                        column: x => x.ReconciliationId,
                        principalTable: "Reconciliations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationLines_CassetteId",
                table: "ReconciliationLines",
                column: "CassetteId");

            migrationBuilder.CreateIndex(
                name: "IX_Reconciliations_AtmId_Status",
                table: "Reconciliations",
                columns: new[] { "AtmId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReconciliationLines");

            migrationBuilder.DropTable(
                name: "Reconciliations");
        }
    }
}
