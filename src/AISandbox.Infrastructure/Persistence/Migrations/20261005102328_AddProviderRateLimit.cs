using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISandbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderRateLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RateLimit",
                table: "Providers",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RateLimit",
                table: "Providers");
        }
    }
}
