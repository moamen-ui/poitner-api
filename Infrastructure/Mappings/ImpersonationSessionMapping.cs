using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

/// <summary>DB-13 §3.2 — operator record, not a BaseEntity, long identity PK (the JWT "imp" claim).</summary>
public class ImpersonationSessionMapping : IEntityTypeConfiguration<ImpersonationSession>
{
    public void Configure(EntityTypeBuilder<ImpersonationSession> builder)
    {
        builder.ToTable("impersonation_sessions");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.OwnerId).HasColumnName("owner_id");
        builder
            .HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(e => e.OwnerId)
            .OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("fk_impersonation_sessions_workspaces_owner_id");

        builder.Property(e => e.OperatorUserId).HasColumnName("operator_user_id").IsRequired();

        builder.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired();

        builder.Property(e => e.StartedAt).HasColumnName("started_at").IsRequired();
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(e => e.EndedAt).HasColumnName("ended_at");
        builder.Property(e => e.EndReason).HasColumnName("end_reason");

        builder
            .Property(e => e.RequestCount)
            .HasColumnName("request_count")
            .IsRequired()
            .HasDefaultValue(0);
        builder.Property(e => e.LastRequestAt).HasColumnName("last_request_at");

        builder
            .HasIndex(e => new { e.OwnerId, e.StartedAt })
            .HasDatabaseName("ix_impersonation_sessions_owner_started")
            .IsDescending(false, true);

        // GLM DB-13 #4: one live session per operator is enforced by the database — the
        // AlreadyActive pre-check in StartAsync is the friendly message, this unique partial index
        // is the race guard (caught as a 23505/Sqlite-19 DbUpdateException in ImpersonationService).
        builder
            .HasIndex(e => e.OperatorUserId)
            .IsUnique()
            .HasFilter("ended_at IS NULL")
            .HasDatabaseName("ux_impersonation_sessions_operator_live");
    }
}
