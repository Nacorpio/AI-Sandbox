using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISandbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderPathVariables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PathVariables",
                table: "Providers",
                type: "TEXT",
                maxLength: 2000,
                nullable: false,
                defaultValueSql: "'{}'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PathVariables",
                table: "Providers");
        }
    }
}
