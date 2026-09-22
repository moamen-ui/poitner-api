using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class NotificationMapping : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.ToTable("notifications");

        // BaseEntity columns
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // Notification columns
        b.Property(x => x.OwnerId).HasColumnName("owner_id");
        b.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(x => x.OwnerId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_notifications_workspaces_owner_id");
        b.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        b.Property(x => x.Type).HasColumnName("type").IsRequired();
        b.Property(x => x.CommentId).HasColumnName("comment_id");
        b.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
        b.Property(x => x.ActorId).HasColumnName("actor_id");
        b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
        b.Property(x => x.ReadAt).HasColumnName("read_at");
        b.Property(x => x.SuggestionId).HasColumnName("suggestion_id");

        b.HasOne(x => x.Comment)
            .WithMany()
            .HasForeignKey(x => x.CommentId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Project)
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Suggestion)
            .WithMany()
            .HasForeignKey(x => x.SuggestionId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.UserId, x.ReadAt });
        b.HasIndex(x => x.CommentId);
        b.HasIndex(x => x.OwnerId);
        b.HasIndex(x => x.SuggestionId);
    }
}
