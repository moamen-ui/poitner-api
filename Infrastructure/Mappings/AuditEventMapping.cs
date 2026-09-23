using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class AuditEventMapping : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at").IsRequired();

        // Operator/analytics table (DB-RULES R8.8): owner_id carries the filter shape but the row
        // is NOT deleted with the workspace — FK SET NULL keeps it as an operator record.
        builder.Property(e => e.OwnerId).HasColumnName("owner_id");
        builder
            .HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(e => e.OwnerId)
            .OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("fk_audit_events_workspaces_owner_id");

        // actor_user_id is a content reference to users.public_id — deliberately NO FK (R14).
        builder.Property(e => e.ActorUserId).HasColumnName("actor_user_id");
        // actor_membership_id is a logical reference (memberships are hard-deleted with the
        // workspace; the audit row survives) — deliberately NO FK.
        builder.Property(e => e.ActorMembershipId).HasColumnName("actor_membership_id");
        builder.Property(e => e.ActorKind).HasColumnName("actor_kind").IsRequired();

        builder.Property(e => e.Action).HasColumnName("action").HasMaxLength(64).IsRequired();
        builder
            .Property(e => e.TargetType)
            .HasColumnName("target_type")
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(e => e.TargetId).HasColumnName("target_id").HasMaxLength(128);

        builder.Property(e => e.Before).ConfigureJsonColumn("before", "'{}'");
        builder.Property(e => e.After).ConfigureJsonColumn("after", "'{}'");

        builder.Property(e => e.RequestId).HasColumnName("request_id").HasMaxLength(64);
        builder.Property(e => e.IpHash).HasColumnName("ip_hash").HasMaxLength(64);
        builder.Property(e => e.UserAgent).HasColumnName("user_agent").HasMaxLength(256);
        // Logical reference to DB-13's impersonation_sessions.id — no FK (DB-13 ships after DB-12).
        builder.Property(e => e.ImpersonationSessionId).HasColumnName("impersonation_session_id");

        builder
            .HasIndex(e => new { e.OwnerId, e.OccurredAt })
            .HasDatabaseName("ix_audit_events_owner_occurred")
            .IsDescending(false, true);
        builder
            .HasIndex(e => new { e.ActorUserId, e.OccurredAt })
            .HasDatabaseName("ix_audit_events_actor_occurred")
            .IsDescending(false, true);
        builder
            .HasIndex(e => new { e.TargetType, e.TargetId })
            .HasDatabaseName("ix_audit_events_target");

        // NOT modelled in EF (no trigger API): migration *_AddAuditEventsAppendOnlyTrigger creates
        // trg_audit_events_append_only / trg_audit_events_no_truncate — do not "repair" the
        // snapshot to match them (DB-12 §3.2, like DB-11a's expression index, R9).
    }
}
