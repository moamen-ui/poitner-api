using Microsoft.EntityFrameworkCore;
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
        b.HasOne(x => x.Comment).WithMany(c => c.Replies).HasForeignKey(x => x.CommentId).OnDelete(DeleteBehavior.Cascade);
    }
}
