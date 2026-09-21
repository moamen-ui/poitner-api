using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class WorkspaceSettingMapping : IEntityTypeConfiguration<WorkspaceSetting>
{
    public void Configure(EntityTypeBuilder<WorkspaceSetting> b)
    {
        b.ToTable("workspace_settings");

        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // Nullable only for strict-own filter compatibility; the service never writes null
        // (super admins are refused before the write — see CommentFieldService).
        b.Property(x => x.OwnerId).HasColumnName("owner_id");
        // One opaque jsonb value (converter, deliberately NOT OwnsMany().ToJson(): owned JSON
        // collections need a key and make whole-list replacement awkward).
        b.Property(x => x.CommentFieldDefinitions).ConfigureJsonColumn("comment_field_definitions", "'[]'");

        // One live row per workspace: unique among non-deleted rows; NULLS NOT DISTINCT so even a
        // hypothetical null-owner row could only ever exist once (belt-and-braces, Postgres 15).
        b.HasIndex(x => x.OwnerId).IsUnique().HasFilter("deleted_at IS NULL").AreNullsDistinct(false);
    }
}
