using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Billing.Features.RatePlans;

/// <summary>Maps <see cref="AccountRatePlan"/> onto <c>billing.account_rate_plans</c>.</summary>
public sealed class AccountRatePlanConfiguration : IEntityTypeConfiguration<AccountRatePlan>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AccountRatePlan> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("account_rate_plans");

        builder.HasKey(assignment => assignment.Id).HasName("pk_account_rate_plans");

        builder.Property(assignment => assignment.Id).HasColumnName("id").ValueGeneratedNever();

        // No foreign key: the account is a row in the Customers schema. The id arrives from
        // IServiceAccountDirectory, which has already proved it exists.
        builder.Property(assignment => assignment.ServiceAccountId).HasColumnName("service_account_id");

        // A code, never a plan version id — which version applies is decided per bill, from the
        // period being billed. No foreign key to rate_plans for that reason: the target of the
        // reference is a set of rows, not one.
        builder.Property(assignment => assignment.RatePlanCode)
            .HasColumnName("rate_plan_code")
            .HasMaxLength(RatePlan.CodeLength)
            .IsRequired();

        builder.Property(assignment => assignment.AssignedAt).HasColumnName("assigned_at");
        builder.Property(assignment => assignment.ChangedAt).HasColumnName("changed_at");

        builder.Property(assignment => assignment.ActorId)
            .HasColumnName("actor_id")
            .HasMaxLength(RegistryActor.MaxLength)
            .IsRequired();

        builder.Property(assignment => assignment.ActorName)
            .HasColumnName("actor_name")
            .HasMaxLength(RegistryActor.MaxLength);

        // ONE TARIFF PER ACCOUNT, as a database fact. An account billed on two tariffs is two bills
        // for one period, and which one the customer owes would be decided by whichever row the
        // query happened to return first. Unfiltered and not nullable — an account with no tariff of
        // its own has no row at all, which is what makes the default a fallback rather than a value
        // somebody has to remember to write.
        builder.HasIndex(assignment => assignment.ServiceAccountId)
            .HasDatabaseName("ux_account_rate_plans_account")
            .IsUnique();
    }
}
