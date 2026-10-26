using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CameraMonitor.Api.Infrastructure.Db.Migrations
{
    /// <inheritdoc />
    public partial class SegmentSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Segments",
                type: "TEXT",
                nullable: false,
                defaultValue: "live");

            migrationBuilder.CreateIndex(
                name: "IX_Segments_StartUtc",
                table: "Segments",
                column: "StartUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Segments_StartUtc",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Segments");
        }
    }
}
