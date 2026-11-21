using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlertingSystem.Backend.Migrations;

[DbContext(typeof(AlertingSystem.Backend.Services.AlertingDbContext))]
[Migration("20261021173000_RenameChecksToAlertHistory")]
public sealed class RenameChecksToAlertHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameTable(
            name: "checks",
            newName: "AlertHistory");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameTable(
            name: "AlertHistory",
            newName: "checks");
    }
}
