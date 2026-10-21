using System.Globalization;
using AlertingSystem.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AlertingSystem.Services;

public sealed class AlertingDbContext : DbContext
{
    private static readonly ValueConverter<DateTime, string> DateTimeToStringConverter = new(
        value => value.ToString("O", CultureInfo.InvariantCulture),
        value => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private readonly string _connectionString;
    private readonly bool _useSimplifiedIncidentTable;
    private readonly bool _useSimplifiedHistoryTable;
    private readonly bool _useSimplifiedMetadataTable;

    public AlertingDbContext(
        string connectionString,
        bool useSimplifiedIncidentTable = false,
        bool useSimplifiedHistoryTable = false,
        bool useSimplifiedMetadataTable = false)
    {
        _connectionString = connectionString;
        _useSimplifiedIncidentTable = useSimplifiedIncidentTable;
        _useSimplifiedHistoryTable = useSimplifiedHistoryTable;
        _useSimplifiedMetadataTable = useSimplifiedMetadataTable;
    }

    internal bool UsesSimplifiedIncidentTable => _useSimplifiedIncidentTable;
    internal bool UsesSimplifiedHistoryTable => _useSimplifiedHistoryTable;
    internal bool UsesSimplifiedMetadataTable => _useSimplifiedMetadataTable;

    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<CheckHistoryEntry> CheckHistory => Set<CheckHistoryEntry>();
    public DbSet<AppMetadata> AppMetadata => Set<AppMetadata>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder
                .UseMySql(
                    _connectionString,
                    new MariaDbServerVersion(new Version(10, 11, 0)))
                .ReplaceService<IModelCacheKeyFactory, AlertingDbContextModelCacheKeyFactory>();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Incident>(entity =>
        {
            entity.ToTable(_useSimplifiedIncidentTable ? "incident_records" : "Incidents");
            entity.HasKey(item => item.IncidentNumber);
            entity.Property(item => item.IncidentNumber).HasMaxLength(100).HasColumnType("varchar(100)");
            entity.Property(item => item.EndpointUrl).HasColumnType("text");
            entity.Property(item => item.Title).HasMaxLength(512).HasColumnType("varchar(512)");
            entity.Property(item => item.Details).HasColumnType("text");
            entity.Property(item => item.ResolutionNote).HasColumnType("text");
            entity.Property(item => item.Severity).HasColumnType("int");
            entity.Property(item => item.Status).HasColumnType("int");
            entity.Property(item => item.FirstOccurredAt)
                .HasColumnType("varchar(40)")
                .HasConversion(DateTimeToStringConverter);
            entity.Property(item => item.LastOccurredAt)
                .HasColumnType("varchar(40)")
                .HasConversion(DateTimeToStringConverter);
            entity.Property(item => item.ResolvedAt)
                .HasColumnType("varchar(40)")
                .HasConversion(DateTimeToStringConverter);
            entity.Property(item => item.OccurrenceCount).HasColumnType("int");
            entity.HasIndex(item => new { item.Status, item.LastOccurredAt })
                .HasDatabaseName("IX_Incidents_Status_LastOccurredAt");

            entity.Ignore(item => item.SeverityDisplay);
            entity.Ignore(item => item.StatusDisplay);
            entity.Ignore(item => item.SeverityBrush);
            entity.Ignore(item => item.StatusBrush);
            entity.Ignore(item => item.ActivityDisplay);
        });

        modelBuilder.Entity<CheckHistoryEntry>(entity =>
        {
            entity.ToTable(_useSimplifiedHistoryTable ? "checks" : "AlertHistory");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedOnAdd();
            entity.Property(item => item.Timestamp)
                .HasColumnName("CheckedAt")
                .HasColumnType("varchar(40)")
                .HasConversion(DateTimeToStringConverter);
            entity.Property(item => item.Endpoint).HasColumnType("text");
            entity.Property(item => item.Result).HasMaxLength(255).HasColumnType("varchar(255)");
            entity.Property(item => item.Details).HasColumnType("text");
            entity.Property(item => item.ResponseTimeMs).HasColumnType("bigint");
            entity.HasIndex(item => item.Timestamp).HasDatabaseName("IX_AlertHistory_CheckedAt");

            entity.Ignore(item => item.TimestampDisplay);
            entity.Ignore(item => item.ResponseTimeDisplay);
            entity.Ignore(item => item.ResultBrush);
            entity.Ignore(item => item.DisplayText);
        });

        modelBuilder.Entity<AppMetadata>(entity =>
        {
            entity.ToTable(_useSimplifiedMetadataTable ? "settings" : "AppMetadata");
            entity.HasKey(item => item.MetadataKey);
            entity.Property(item => item.MetadataKey).HasMaxLength(100).HasColumnType("varchar(100)");
            entity.Property(item => item.MetadataValue).HasMaxLength(100).HasColumnType("varchar(100)");
        });
    }
}

public sealed class AlertingDbContextModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        if (context is AlertingDbContext alertingContext)
        {
            return (
                context.GetType(),
                alertingContext.UsesSimplifiedIncidentTable,
                alertingContext.UsesSimplifiedHistoryTable,
                alertingContext.UsesSimplifiedMetadataTable,
                designTime);
        }

        return (context.GetType(), designTime);
    }
}

public sealed class AppMetadata
{
    public string MetadataKey { get; set; } = string.Empty;
    public string MetadataValue { get; set; } = string.Empty;
}
