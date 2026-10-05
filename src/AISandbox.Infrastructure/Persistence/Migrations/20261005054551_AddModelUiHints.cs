using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISandbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddModelUiHints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UiHints",
                table: "ModelDefinitions",
                type: "TEXT",
                maxLength: 20000,
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UiHints",
                table: "ModelDefinitions");
        }
    }
}
