using ManagementAppointment.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace ManagementAppointment.Data;

public static class DatabaseSetup
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var tables = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.tables t JOIN sys.schemas s ON t.schema_id=s.schema_id WHERE s.name='ma'").SingleAsync();
        if (tables == 0) await db.Database.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        else
        {
            await UpgradeRecoveryAsync(db);
            // Existing application schemas are never rebuilt automatically.
            var existing = await db.Database.SqlQueryRaw<string>("SELECT t.name AS [Value] FROM sys.tables t JOIN sys.schemas s ON t.schema_id=s.schema_id WHERE s.name='ma'").ToListAsync();
            var required = db.Model.GetEntityTypes().Select(x => x.GetTableName()).Where(x => x != null).Distinct();
            if (required.Any(x => !existing.Contains(x!))) throw new InvalidOperationException("El esquema ma está incompleto y requiere revisión antes de inicializar. No se reconstruirán tablas existentes.");
        }
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in Roles.All)
            if (!await roles.RoleExistsAsync(role))
            {
                var result = await roles.CreateAsync(new IdentityRole(role));
                if (!result.Succeeded) throw new InvalidOperationException("No se pudieron crear los roles de la aplicación.");
            }
    }

    private static async Task UpgradeRecoveryAsync(ClinicDbContext db)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='ma:schema-upgrade', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @r < 0 THROW 51000, 'No se pudo bloquear la actualización.', 1;");
        using var stream = typeof(DatabaseSetup).Assembly.GetManifestResourceStream("ManagementAppointment.Data.RecoveryUpgrade.sql")
            ?? throw new InvalidOperationException("No se encontró el script de actualización.");
        using var reader = new StreamReader(stream);
        await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync());
        await transaction.CommitAsync();
    }

    public static async Task AccountInfoAsync(IServiceProvider services, string email)
    {
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);
        if (user == null) { Console.WriteLine("No existe una cuenta con ese correo."); return; }
        Console.WriteLine($"Rol: {string.Join(", ", await users.GetRolesAsync(user))}. Activa: {user.IsActive}. Bloqueada: {await users.IsLockedOutAsync(user)}.");
    }

    public static async Task ChangeStaffAccessAsync(IServiceProvider services, string previousEmail, string email)
    {
        var password = Environment.GetEnvironmentVariable("MA_ACCOUNT_PASSWORD") ?? throw new InvalidOperationException("Define MA_ACCOUNT_PASSWORD para cambiar la contraseña.");
        if (!new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email)) throw new InvalidOperationException("El correo no es válido.");
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var user = await users.FindByEmailAsync(previousEmail) ?? throw new InvalidOperationException("Cuenta no encontrada.");
        if (!user.IsActive || !(await users.GetRolesAsync(user)).Any(r => r is Roles.Admin or Roles.Doctor or Roles.Reception)) throw new InvalidOperationException("Se requiere una cuenta activa de personal.");
        var occupied = await users.FindByEmailAsync(email);
        if (occupied != null && occupied.Id != user.Id) throw new InvalidOperationException("El nuevo correo ya está en uso.");
        if (!(await users.SetEmailAsync(user, email)).Succeeded || !(await users.SetUserNameAsync(user, email)).Succeeded) throw new InvalidOperationException("No se pudo cambiar el correo de acceso.");
        var token = await users.GeneratePasswordResetTokenAsync(user);
        if (!(await users.ResetPasswordAsync(user, token, password)).Succeeded) throw new InvalidOperationException("La contraseña no cumple los requisitos.");
        if (!(await users.SetLockoutEndDateAsync(user, null)).Succeeded || !(await users.ResetAccessFailedCountAsync(user)).Succeeded) throw new InvalidOperationException("No se pudo actualizar el estado de acceso.");
        db.AuditLogs.Add(new() { ActorId = "mantenimiento-local", Action = $"Credenciales de personal actualizadas: {user.Id}" });
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        Console.WriteLine("Correo y contraseña actualizados en la misma cuenta. Se preservaron la agenda y los pacientes.");
    }

    public static async Task PasswordLinkAsync(IServiceProvider services, string email, string path)
    {
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email) ?? throw new InvalidOperationException("La cuenta no existe. Crea primero su acceso de personal.");
        var link = await ManagementAppointment.Services.StaffPasswordAccess.CreateLinkAsync(users, user, scope.ServiceProvider.GetRequiredService<IConfiguration>());
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, $"CREAR O CAMBIAR CONTRASEÑA SIN CORREO\n\nCuenta: {email}\n\nAbre este enlace después de reiniciar la aplicación:\n{link}\n\nEs válido por 2 horas y se invalida al guardar la nueva contraseña.\nElige tu contraseña en la página; no hace falta configurar SMTP.\n");
        Console.WriteLine("Enlace privado guardado. Válido por 2 horas; no se envió correo.");
    }

    public static async Task CreateAdminAsync(IServiceProvider services, string email, string password)
    {
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        if (await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where r.Name == Roles.Admin select ur).AnyAsync())
            throw new InvalidOperationException("Ya existe una cuenta administrativa. Usa administración para nuevas cuentas.");
        var user = new ApplicationUser { UserName = email, Email = email, FullName = "Administración" };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded) throw new InvalidOperationException("El correo o la contraseña no cumple los requisitos. Usa 12 caracteres, mayúscula, minúscula, número y símbolo.");
        result = await users.AddToRoleAsync(user, Roles.Admin);
        if (!result.Succeeded) { await users.DeleteAsync(user); throw new InvalidOperationException("No se pudo asignar el rol inicial."); }
    }
}
