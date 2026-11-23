using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventering.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnvandareKravTvafaktor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireTotp",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequireTotp",
                table: "Users");
        }
    }
}
