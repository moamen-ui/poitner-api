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
    }
}
