using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class ReplyMapping : IEntityTypeConfiguration<Reply>
{
    public void Configure(EntityTypeBuilder<Reply> b)
    {
        b.ToTable("replies");

        // BaseEntity columns
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // Reply-specific columns
        b.Property(x => x.CommentId).HasColumnName("comment_id");
        b.Property(x => x.AuthorId).HasColumnName("author_id");
        b.Property(x => x.Body).HasColumnName("body").IsRequired();
        b.Property(x => x.OwnerId).HasColumnName("owner_id");
        b.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(x => x.OwnerId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_replies_workspaces_owner_id");
        b.HasIndex(x => x.OwnerId);
        b.HasOne(x => x.Comment)
            .WithMany(c => c.Replies)
            .HasForeignKey(x => x.CommentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Advisory payload/secret flags, computed on write (R2-06). The jsonb list mirrors
        // PlanMapping.FeatureBullets: a value-comparer so EF tracks element-level mutations.
        b.Property(x => x.HasPayloadFlag)
            .HasColumnName("has_payload_flag")
            .HasDefaultValue(false);
        b.Property(x => x.PayloadFlags).ConfigureJsonStringList("payload_flags");

        // Structured AI attribution — see Reply.AiTool/AiModel doc comments.
        b.Property(x => x.AiTool).HasColumnName("ai_tool").HasMaxLength(64);
        b.Property(x => x.AiModel).HasColumnName("ai_model").HasMaxLength(64);
    }
}
