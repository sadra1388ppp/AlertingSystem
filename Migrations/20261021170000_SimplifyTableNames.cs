using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlertingSystem.Migrations;

[DbContext(typeof(AlertingSystem.Services.AlertingDbContext))]
[Migration("20261021170000_SimplifyTableNames")]
public sealed class SimplifyTableNames : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameTable(name: "Incidents", newName: "incident_records");
        migrationBuilder.RenameTable(name: "AlertHistory", newName: "checks");
        migrationBuilder.RenameTable(name: "AppMetadata", newName: "settings");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameTable(name: "settings", newName: "AppMetadata");
        migrationBuilder.RenameTable(name: "checks", newName: "AlertHistory");
        migrationBuilder.RenameTable(name: "incident_records", newName: "Incidents");
    }
}
