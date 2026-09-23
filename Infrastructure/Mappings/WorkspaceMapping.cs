using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class WorkspaceMapping : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> b)
    {
        b.ToTable(
            "workspaces",
            t => t.HasCheckConstraint("ck_workspaces_name_not_blank", "length(btrim(name)) > 0")
        );

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        // The workspace's own name (DB-03, Q3) — never a person's name. Seeded with
        // Workspace.PlaceholderName; renamed by the admin via DB-03b.
        b.Property(x => x.Name).HasColumnName("name").IsRequired().HasMaxLength(120);

        // Audit columns — same shape as every other mapping.
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // DB-17 (F4): demo state moved here from `users` (the users copies were dropped by DB-11e — R2 contract).
        // No check constraint (Opus DB-17 #4/#7) — see the doc-comment on Workspace and DB-17 §3.1
        // for the code-enforced invariants that replace it.
        b.Property(x => x.DemoExpiresAt).HasColumnName("demo_expires_at");
        b.Property(x => x.DemoExtendedAt).HasColumnName("demo_extended_at");
        b.Property(x => x.DemoConvertedAt).HasColumnName("demo_converted_at");
        b.Property(x => x.DemoExpiryWarnedAt).HasColumnName("demo_expiry_warned_at");
        b.Property(x => x.DemoCommentCapOverride).HasColumnName("demo_comment_cap_override");
        b.Property(x => x.DemoTtlHoursOverride).HasColumnName("demo_ttl_hours_override");

        // The sweep's own predicate (DB-17 §3.5) — partial index over live demos only (R4: 2 rows).
        b.HasIndex(x => x.DemoExpiresAt)
            .HasFilter("demo_expires_at IS NOT NULL")
            .HasDatabaseName("ix_workspaces_demo_expires_at");
    }
}
