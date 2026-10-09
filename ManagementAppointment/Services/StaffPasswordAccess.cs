using System.Text;
using ManagementAppointment.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace ManagementAppointment.Services;

public static class StaffPasswordAccess
{
    public static async Task<string> CreateLinkAsync(UserManager<ApplicationUser> users, ApplicationUser user, IConfiguration configuration)
    {
        if (!user.IsActive || !(await users.GetRolesAsync(user)).Any(r => r is Roles.Admin or Roles.Doctor or Roles.Reception))
            throw new ClinicException("El enlace de contraseña requiere una cuenta activa de personal médico.");
        var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GeneratePasswordResetTokenAsync(user)));
        return QueryHelpers.AddQueryString((configuration["Office:PublicUrl"] ?? "https://localhost:7246").TrimEnd('/') + "/restablecer", new Dictionary<string, string?> { ["email"] = user.Email, ["token"] = token });
    }
}
