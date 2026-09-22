using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class UserAliasMapping : IEntityTypeConfiguration<UserAlias>
{
    public void Configure(EntityTypeBuilder<UserAlias> b)
    {
        b.ToTable("user_aliases");

        b.HasKey(x => x.AliasPublicId);
        b.Property(x => x.AliasPublicId).HasColumnName("alias_public_id").ValueGeneratedNever();

        b.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        b.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Property(x => x.SourceWorkspaceId).HasColumnName("source_workspace_id");
        b.Property(x => x.MergedAt).HasColumnName("merged_at").IsRequired();
    }
}
