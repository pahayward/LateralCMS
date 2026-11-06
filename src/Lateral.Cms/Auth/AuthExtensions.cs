using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Auth;

public static class AuthExtensions
{
    public static IServiceCollection AddCmsAuth(this IServiceCollection services)
    {
        services.AddOptions<AuthOptions>()
            .BindConfiguration(AuthOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();

        services.AddSingleton<CredentialStore>();
        services.AddSingleton<AuthFailureThrottle>();

        services.AddAuthentication(BasicAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(BasicAuthenticationHandler.SchemeName, null);

        services.AddAuthorizationBuilder()
            // Deny by default: every endpoint needs a signed-in caller unless it says otherwise.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.CmsIngest, policy => policy.RequireRole(Roles.CmsIngest))
            .AddPolicy(Policies.ApiRead, policy => policy.RequireRole(Roles.User, Roles.Admin))
            .AddPolicy(Policies.AdminOnly, policy => policy.RequireRole(Roles.Admin))
            .AddPolicy(Policies.BatchStatus, policy => policy.RequireRole(Roles.CmsIngest, Roles.Admin));

        return services;
    }
}
