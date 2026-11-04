using System;
using ExpenseHub.Core.Domain;
using ExpenseHub.Core.Reports;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ExpenseHub.Core.Data;

/// <summary>Contexto único compartilhado pela aplicação web e pelo worker.</summary>
internal sealed class ExpenseHubDbContext : IdentityDbContext<IdentityUser>
{
    public ExpenseHubDbContext(DbContextOptions<ExpenseHubDbContext> options)
        : base(options)
    {
    }

    public DbSet<Expense> Expenses => Set<Expense>();

    public DbSet<ExpenseCategory> ExpenseCategories => Set<ExpenseCategory>();

    public DbSet<ExpenseHistory> ExpenseHistory => Set<ExpenseHistory>();

    public DbSet<PaymentRecord> PaymentRecords => Set<PaymentRecord>();

    public DbSet<ReportRequest> ReportRequests => Set<ReportRequest>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.OnModelCreating(builder);

        builder.Entity<ExpenseCategory>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).HasMaxLength(100).IsRequired();
            entity.HasData(
                new ExpenseCategory { Id = 1, Name = "Transporte" },
                new ExpenseCategory { Id = 2, Name = "Alimentação" },
                new ExpenseCategory { Id = 3, Name = "Hospedagem" },
                new ExpenseCategory { Id = 4, Name = "Material" },
                new ExpenseCategory { Id = 5, Name = "Outros" });
        });

        builder.Entity<Expense>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OwnerId).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.RejectionReason).HasMaxLength(500);
            entity.Property(e => e.Version).IsConcurrencyToken();
            entity.HasOne(e => e.Category).WithMany().HasForeignKey(e => e.CategoryId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<IdentityUser>().WithMany().HasForeignKey(e => e.OwnerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.OwnerId, e.ExpenseDate });
            entity.HasIndex(e => e.Status);
        });

        builder.Entity<ExpenseHistory>(entity =>
        {
            entity.HasKey(h => h.Id);
            entity.Property(h => h.Action).HasMaxLength(30).IsRequired();
            entity.Property(h => h.FromStatus).HasConversion<string>().HasMaxLength(20);
            entity.Property(h => h.ToStatus).HasConversion<string>().HasMaxLength(20);
            entity.Property(h => h.Justification).HasMaxLength(500);
            entity.HasOne<Expense>().WithMany().HasForeignKey(h => h.ExpenseId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(h => h.ExpenseId);
        });

        builder.Entity<PaymentRecord>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Amount).HasPrecision(18, 2);
            entity.HasOne<Expense>().WithMany().HasForeignKey(p => p.ExpenseId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(p => p.ExpenseId).IsUnique();
        });

        builder.Entity<ReportRequest>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.RequesterId).IsRequired();
            entity.Property(r => r.Scope).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.ErrorCode).HasMaxLength(100);
            entity.Property(r => r.ErrorMessage).HasMaxLength(500);
            entity.HasOne<IdentityUser>().WithMany().HasForeignKey(r => r.RequesterId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(r => new { r.RequesterId, r.CreatedAt });
        });
    }

    private sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcDateTimeConverter()
            : base(v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
        {
        }
    }

    private sealed class NullableUtcDateTimeConverter : ValueConverter<DateTime?, DateTime?>
    {
        public NullableUtcDateTimeConverter()
            : base(
                v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : v.Value.ToUniversalTime()) : v,
                v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v)
        {
        }
    }
}
