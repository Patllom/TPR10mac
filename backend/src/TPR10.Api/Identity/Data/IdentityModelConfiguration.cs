using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TPR10.Api.Identity.Data;

internal static class IdentityModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var mfaAttempts = model.Entity<MfaAttemptState>();
        mfaAttempts.ToTable("mfa_attempt_states", t => t.HasCheckConstraint("ck_mfa_attempts", "failed_attempts >= 0 AND failed_attempts <= 5"));
        mfaAttempts.HasKey(x => x.UserId);
        UserLink(mfaAttempts);
        var attempts = model.Entity<LoginAttemptWindow>();
        attempts.ToTable("login_attempt_windows", t =>
        {
            t.HasCheckConstraint("ck_login_identifier_hash", "octet_length(identifier_hash) = 32");
            t.HasCheckConstraint("ck_login_attempt_count", "failed_attempts >= 0 AND failed_attempts <= 5");
        });
        attempts.HasKey(x => x.IdentifierHash);
        attempts.HasIndex(x => x.ExpiresAtUtc);
        var user = Table<IdentityUser>(model, "users");
        user.Property(x => x.Username).HasMaxLength(128);
        user.Property(x => x.NormalizedUsername).HasMaxLength(256);
        user.Property(x => x.Email).HasMaxLength(320);
        user.Property(x => x.IsActive).HasDefaultValue(true);
        user.Property(x => x.SecurityVersion).HasDefaultValue(0L).IsConcurrencyToken();
        user.HasIndex(x => x.NormalizedUsername).IsUnique();
        user.ToTable(t => t.HasCheckConstraint("ck_users_security_version", "security_version >= 0"));

        var credential = model.Entity<LocalCredential>();
        credential.ToTable("local_credentials", t => t.HasCheckConstraint("ck_credentials_failures", "failed_attempts >= 0"));
        credential.HasKey(x => x.UserId);
        credential.HasOne<IdentityUser>().WithOne().HasForeignKey<LocalCredential>(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        credential.Property(x => x.PasswordHash).HasMaxLength(512);
        credential.Property(x => x.MustChangePassword).HasDefaultValue(true);
        credential.Property(x => x.FailedAttempts).HasDefaultValue(0);
        credential.Property(x => x.PasswordChangedAtUtc).HasDefaultValueSql("now()");

        var external = Table<ExternalIdentity>(model, "external_identities");
        UserLink(external);
        external.Property(x => x.Provider).HasMaxLength(80);
        external.Property(x => x.Subject).HasMaxLength(256);
        external.HasIndex(x => new { x.Provider, x.Subject }).IsUnique();

        var role = Table<IdentityRole>(model, "roles");
        role.Property(x => x.Name).HasMaxLength(120);
        role.Property(x => x.RoleClass).HasMaxLength(40);
        role.HasIndex(x => x.Name).IsUnique();
        role.ToTable(t => t.HasCheckConstraint("ck_roles_class",
            "role_class IN ('staff','system-administration','approval','accounting','finance-data-access')"));

        var permission = Table<IdentityPermission>(model, "permissions");
        permission.Property(x => x.Capability).HasMaxLength(120);
        permission.HasIndex(x => x.Capability).IsUnique();

        var rolePermission = model.Entity<RolePermission>();
        rolePermission.ToTable("role_permissions");
        rolePermission.HasKey(x => new { x.RoleId, x.PermissionId });
        rolePermission.HasOne<IdentityRole>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        rolePermission.HasOne<IdentityPermission>().WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Restrict);

        var userRole = model.Entity<UserRole>();
        userRole.ToTable("user_roles");
        userRole.HasKey(x => new { x.UserId, x.RoleId });
        UserLink(userRole);
        userRole.HasOne<IdentityRole>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);

        var factor = Table<MfaFactor>(model, "mfa_factors");
        UserLink(factor);
        factor.Property(x => x.ProtectedSecret).HasMaxLength(4096);
        factor.HasIndex(x => x.UserId).IsUnique().HasFilter("revoked_at_utc IS NULL");
        factor.ToTable(t => t.HasCheckConstraint("ck_mfa_step", "last_used_step IS NULL OR last_used_step >= 0"));

        var session = Table<IdentitySession>(model, "sessions");
        UserLink(session);
        Hash(session, "sessions", "TokenHash", "token_hash");
        session.Property(x => x.Stage).HasConversion<string>().HasMaxLength(40);
        session.Property(x => x.SecurityVersion).HasDefaultValue(0L);
        session.ToTable(t =>
        {
            t.HasCheckConstraint("ck_sessions_stage", "stage IN ('PasswordChangeRequired','MfaEnrollmentRequired','MfaChallengeRequired','Active')");
            t.HasCheckConstraint("ck_sessions_expiry", "expires_at_utc > created_at_utc");
            t.HasCheckConstraint("ck_sessions_security_version", "security_version >= 0");
        });
        session.HasIndex(x => x.ExpiresAtUtc);

        var reset = Table<PasswordResetRequest>(model, "password_reset_requests");
        UserLink(reset);
        Hash(reset, "password_reset_requests", "TokenHash", "token_hash");
        reset.ToTable(t => t.HasCheckConstraint("ck_reset_expiry", "expires_at_utc > created_at_utc"));
        reset.HasIndex(x => x.ExpiresAtUtc);

        var preAuth = Table<PreAuthFlow>(model, "pre_auth_flows");
        UserLink(preAuth);
        Hash(preAuth, "pre_auth_flows", "TokenHash", "token_hash");
        preAuth.Property(x => x.Purpose).HasMaxLength(80);
        preAuth.ToTable(t => t.HasCheckConstraint("ck_preauth_expiry", "expires_at_utc > created_at_utc"));
        preAuth.HasIndex(x => x.ExpiresAtUtc);

        var recovery = Table<MfaRecoveryCode>(model, "mfa_recovery_codes");
        UserLink(recovery);
        Hash(recovery, "mfa_recovery_codes", "CodeHash", "code_hash");

        var outbox = Table<IdentityDeliveryOutbox>(model, "identity_delivery_outbox");
        outbox.HasOne<PasswordResetRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        outbox.HasIndex(x => x.RequestId).IsUnique();
        outbox.HasIndex(x => new { x.DeliveredAtUtc, x.NextAttemptAtUtc });
        outbox.Property(x => x.Recipient).HasMaxLength(320);
        outbox.Property(x => x.ProtectedPayload).HasMaxLength(8192);
        outbox.Property(x => x.Attempts).HasDefaultValue(0);
        outbox.ToTable(t => t.HasCheckConstraint("ck_outbox_attempts", "attempts >= 0"));

        foreach (var entity in model.Model.GetEntityTypes().Where(x => x.ClrType.Namespace == typeof(IdentityUser).Namespace))
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
            if (entity.FindProperty("CreatedAtUtc") is not null)
                model.Entity(entity.ClrType).Property("CreatedAtUtc").HasDefaultValueSql("now()");
        }
    }

    private static EntityTypeBuilder<T> Table<T>(ModelBuilder model, string name) where T : class
    {
        var entity = model.Entity<T>();
        entity.ToTable(name);
        entity.HasKey("Id");
        return entity;
    }

    private static void UserLink<T>(EntityTypeBuilder<T> entity) where T : class
        => entity.HasOne<IdentityUser>().WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Restrict);

    private static void Hash<T>(EntityTypeBuilder<T> entity, string table, string property, string column) where T : class
    {
        entity.Property<byte[]>(property).IsRequired();
        entity.HasIndex(property).IsUnique();
        entity.ToTable(table, t => t.HasCheckConstraint("ck_" + table + "_hash_length", "octet_length(" + column + ") = 32"));
    }
}
