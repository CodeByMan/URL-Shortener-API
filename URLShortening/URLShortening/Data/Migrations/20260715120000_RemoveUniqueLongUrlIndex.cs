using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace URLShortening.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUniqueLongUrlIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Urls_LongUrl",
                table: "Urls");

            migrationBuilder.CreateIndex(
                name: "IX_Urls_LongUrl",
                table: "Urls",
                column: "LongUrl");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Urls_LongUrl",
                table: "Urls");

            migrationBuilder.CreateIndex(
                name: "IX_Urls_LongUrl",
                table: "Urls",
                column: "LongUrl",
                unique: true);
        }
    }
}
