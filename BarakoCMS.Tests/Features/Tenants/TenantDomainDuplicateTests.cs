using barakoCMS.Infrastructure.Multitenancy;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>Which unique violations are a domain clash (#1280).</summary>
public class TenantDomainDuplicateTests
{
    private static PostgresException Violation(string sqlState, string table) =>
        new("duplicate key", "ERROR", "ERROR", sqlState, "", "", 0, 0, "", "", "public", table, "", "", "pkey", "", "", "");

    [Fact]
    public void A_unique_violation_on_the_claims_table_is_a_domain_clash()
    {
        TenantDomainClaims.IsDuplicate(Violation("23505", TenantDomainClaims.Table)).Should().BeTrue();
        TenantDomainClaims.IsDuplicate(new Exception("wrapped", Violation("23505", TenantDomainClaims.Table))).Should().BeTrue();
    }

    [Fact]
    public void A_unique_violation_on_another_table_is_not()
    {
        TenantDomainClaims.IsDuplicate(Violation("23505", "mt_doc_tenant")).Should().BeFalse("a duplicate tenant handle is not a domain clash");
        TenantDomainClaims.IsDuplicate(new Exception("wrapped", Violation("23505", "mt_doc_tenant"))).Should().BeFalse();
    }

    [Fact]
    public void Another_error_class_is_not()
    {
        TenantDomainClaims.IsDuplicate(Violation("23503", TenantDomainClaims.Table)).Should().BeFalse();
        TenantDomainClaims.IsDuplicate(new InvalidOperationException()).Should().BeFalse();
    }
}
