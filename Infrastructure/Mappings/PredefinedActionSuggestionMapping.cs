using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class PredefinedActionSuggestionMapping
    : IEntityTypeConfiguration<PredefinedActionSuggestion>
{
    public void Configure(EntityTypeBuilder<PredefinedActionSuggestion> b)
    {
        b.ToTable("predefined_action_suggestions");

        // BaseEntity columns
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // Suggestion-specific columns
        b.Property(x => x.OwnerId).HasColumnName("owner_id"); // = target project's owner; nullable per owner model
        b.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(x => x.OwnerId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_predefined_action_suggestions_workspaces_owner_id");
        b.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
        b.Property(x => x.Text).HasColumnName("text").IsRequired().HasMaxLength(256);
        b.Property(x => x.Prompt).HasColumnName("prompt").HasColumnType("text").IsRequired();
        // Enum stored as int (existing convention).
        b.Property(x => x.Status)
            .HasColumnName("status")
            .HasDefaultValue(Pointer.Domain.Enums.SuggestionStatus.Pending);
        b.Property(x => x.ReviewedBy).HasColumnName("reviewed_by");
        b.Property(x => x.ReviewedAt).HasColumnName("reviewed_at");
        b.Property(x => x.AdminFeedback).HasColumnName("admin_feedback").HasColumnType("text");

        // Admin review-queue lookup by tenant + status.
        b.HasIndex(x => new { x.OwnerId, x.Status });
    }
}
