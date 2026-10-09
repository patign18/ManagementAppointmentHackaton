using ManagementAppointment.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace ManagementAppointment.Services;

public sealed class IdentityRevalidationProvider(ILoggerFactory loggerFactory, IServiceScopeFactory scopes,
    IOptions<IdentityOptions> options) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);
    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await manager.GetUserAsync(state.User);
        return user is { IsActive: true } && (await manager.GetRolesAsync(user)).Any(r => r is Roles.Admin or Roles.Reception or Roles.Doctor)
            && state.User.FindFirst(options.Value.ClaimsIdentity.SecurityStampClaimType)?.Value == await manager.GetSecurityStampAsync(user);
    }
}
