using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CameraMonitor.Api.Infrastructure.Db.Migrations
{
    /// <inheritdoc />
    public partial class Events : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CameraId = table.Column<string>(type: "TEXT", nullable: false),
                    LensId = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SnapshotPath = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_CameraId_StartedUtc",
                table: "Events",
                columns: new[] { "CameraId", "StartedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Events");
        }
    }
}
