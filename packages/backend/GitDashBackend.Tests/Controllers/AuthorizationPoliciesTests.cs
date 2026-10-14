using System.Security.Claims;
using GitDashBackend.Configurations;
using Microsoft.Extensions.Configuration;

namespace GitDashBackend.Tests.Controllers
{
    // R1: general listings need their own permission
    public class AuthorizationPoliciesTests
    {
        private static ClaimsPrincipal Session(string login) =>
            new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, login) }, "Test"));

        [Fact]
        public void IsAdmin_IsFalse_WhenNoAdminsConfigured()
        {
            var admins = AuthorizationPolicies.ReadAdminLogins(new ConfigurationBuilder().Build());

            Assert.Empty(admins);
            Assert.False(AuthorizationPolicies.IsAdmin(Session("ana"), admins));
        }

        [Fact]
        public void IsAdmin_MatchesConfiguredLogin_IgnoringCaseAndEmptyEntries()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Admin:Logins:0"] = "Ana",
                    ["Admin:Logins:1"] = ""
                })
                .Build();

            var admins = AuthorizationPolicies.ReadAdminLogins(config);

            Assert.Single(admins);
            Assert.True(AuthorizationPolicies.IsAdmin(Session("ana"), admins));
            Assert.False(AuthorizationPolicies.IsAdmin(Session("bruno"), admins));
        }

        [Fact]
        public void IsAdmin_IsFalse_WithoutLoginClaim()
        {
            var admins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ana" };

            Assert.False(AuthorizationPolicies.IsAdmin(new ClaimsPrincipal(new ClaimsIdentity()), admins));
        }
    }
}
