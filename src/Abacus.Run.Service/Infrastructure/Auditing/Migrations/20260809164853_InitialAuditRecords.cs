using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abacus.Run.Service.Infrastructure.Auditing.Migrations
{
    /// <inheritdoc />
    public partial class InitialAuditRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditRecords",
                columns: table => new
                {
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WorkflowName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WorkflowVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RootKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RootKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AttributesJson = table.Column<string>(type: "TEXT", nullable: false),
                    OpenedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ClosedUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRecords", x => x.InstanceId);
                });

            migrationBuilder.CreateTable(
                name: "AuditRecordEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SectionKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRecordEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditRecordEntries_AuditRecords_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "AuditRecords",
                        principalColumn: "InstanceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecordEntries_InstanceId_SectionKind_Key",
                table: "AuditRecordEntries",
                columns: new[] { "InstanceId", "SectionKind", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_WorkflowName_RootKey",
                table: "AuditRecords",
                columns: new[] { "WorkflowName", "RootKey" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_WorkflowName_Status",
                table: "AuditRecords",
                columns: new[] { "WorkflowName", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditRecordEntries");

            migrationBuilder.DropTable(
                name: "AuditRecords");
        }
    }
}
