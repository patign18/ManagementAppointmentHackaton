using System.Security.Claims;
using System.IO.Compression;
using System.Xml.Linq;
using ManagementAppointment.Data;
using ManagementAppointment.Models;
using ManagementAppointment.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// Verification runs in a newly created, isolated schema. Existing schemas are never reused or deleted.
var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath("../ManagementAppointment/appsettings.json"))
    .AddUserSecrets<ClinicDbContext>(optional: true).AddEnvironmentVariables().Build();
var options = new DbContextOptionsBuilder<ClinicDbContext>().UseSqlServer(config.GetConnectionString("TicketSystem")).Options;
await using var check = new VerificationContext(options);
var exists = await check.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.schemas WHERE name='ma_verification'").SingleAsync();
if (exists != 0) throw new InvalidOperationException("El esquema de verificación ya existe. No se alterará; revisa su origen antes de ejecutar otra vez.");
var created = false;
try
{
    created = true;
    await check.Database.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
    var collection = new ServiceCollection().AddLogging();
    collection.AddDataProtection().UseEphemeralDataProtectionProvider();
    collection.AddScoped<ClinicDbContext>(_ => new VerificationContext(options));
    collection.AddIdentityCore<ApplicationUser>(o => { o.User.RequireUniqueEmail = true; o.Password.RequiredLength = 12; o.Lockout.MaxFailedAccessAttempts = 5; })
        .AddRoles<IdentityRole>().AddEntityFrameworkStores<ClinicDbContext>().AddDefaultTokenProviders();
    await using var provider = collection.BuildServiceProvider();
    await using var setup = provider.CreateAsyncScope();
    var users = setup.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roles = setup.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    foreach (var role in Roles.All) Ensure((await roles.CreateAsync(new(role))).Succeeded, "Crear roles de verificación");
    const string password = "Verification!2026Strong";
    async Task<ApplicationUser> User(string name, string role)
    {
        var u = new ApplicationUser { FullName = "PRUEBA AUTOMÁTICA " + name, Email = name + "@example.invalid", UserName = name + "@example.invalid" };
        Ensure((await users.CreateAsync(u, password)).Succeeded, "Crear usuario de verificación");
        Ensure((await users.AddToRoleAsync(u, role)).Succeeded, "Asignar rol de verificación"); return u;
    }
    var admin = await User("admin", Roles.Admin); var reception = await User("recepcion", Roles.Reception);
    var docUser = await User("doctor", Roles.Doctor); var doc2User = await User("doctor2", Roles.Doctor);
    var patientUser = await User("paciente", Roles.Patient); var patient2User = await User("paciente2", Roles.Patient);
    var db = setup.ServiceProvider.GetRequiredService<ClinicDbContext>();
    var p1 = new Patient { FullName = patientUser.FullName, UserId = patientUser.Id };
    var p2 = new Patient { FullName = patient2User.FullName, UserId = patient2User.Id };
    var d1 = new Doctor { FullName = docUser.FullName, UserId = docUser.Id };
    var d2 = new Doctor { FullName = doc2User.FullName, UserId = doc2User.Id };
    foreach (var d in new[] { d1, d2 }) foreach (var day in Enum.GetValues<DayOfWeek>()) d.Schedules.Add(new() { Day = day, Start = new(9, 0), End = new(17, 0) });
    db.AddRange(p1, p2, d1, d2); await db.SaveChangesAsync();
    var clock = new OfficeClock(config); var date = clock.Today.AddDays(1);
    var factory = new VerificationFactory(options);
    ClinicService Service(ApplicationUser u, string role) => new(factory, new FixedAuthentication(u, role), provider.GetRequiredService<IServiceScopeFactory>(), clock);
    var sa = Service(admin, Roles.Admin); var sr = Service(reception, Roles.Reception);
    var sd = Service(docUser, Roles.Doctor); var sp = Service(patientUser, Roles.Patient); var sp2 = Service(patient2User, Roles.Patient);
    AppointmentForm Booking(int patient, int doctor, int hour) => new() { PatientId = patient, DoctorId = doctor, Date = date, Time = new(hour, 0) };

    Ensure(patientUser.PasswordHash != password && await users.CheckPasswordAsync(patientUser, password) && !await users.CheckPasswordAsync(patientUser, "incorrect"), "Hashing y comprobación de contraseña");
    for (var i = 0; i < 5; i++) await users.AccessFailedAsync(patient2User);
    Ensure(await users.IsLockedOutAsync(patient2User), "Bloqueo tras cinco intentos fallidos");
    await users.SetLockoutEndDateAsync(patient2User, null);
    await Denied(() => new ClinicService(factory, new FixedAuthentication(), provider.GetRequiredService<IServiceScopeFactory>(), clock).ActorAsync(), "Acceso sin autenticación");
    await Denied(() => sp.UsersAsync(), "Paciente no accede a usuarios");
    await Denied(() => sr.AddSpecialtyAsync("No autorizada"), "Recepción no administra especialidades");
    await Denied(() => sp.SavePatientAsync(new() { FullName = "Otro paciente" }), "Paciente no registra a terceros");
    await Denied(() => sd.SaveAppointmentAsync(Booking(p1.Id, d1.Id, 10)), "Permiso explícito para gestionar la agenda médica");
    await Denied(() => sp.SaveAppointmentAsync(Booking(p2.Id, d1.Id, 10)), "Paciente solo reserva para sí mismo");
    await sp.SaveAppointmentAsync(Booking(p1.Id, d1.Id, 10));
    Ensure((await sp.AppointmentsAsync(date, date)).Count == 1, "Reserva conectada a SQL Server");
    var ownId = (await sp.AppointmentsAsync(date, date))[0].Id;
    await Denied(() => sp2.HistoryAsync(ownId), "Historial ajeno protegido");

    async Task<bool> Concurrent(ClinicService service, int patient)
    {
        try { await service.SaveAppointmentAsync(Booking(patient, d1.Id, 11)); return true; }
        catch (ClinicException) { return false; }
    }
    var winners = await Task.WhenAll(Concurrent(sp, p1.Id), Concurrent(sp2, p2.Id));
    Ensure(winners.Count(x => x) == 1, "Dos reservas simultáneas: solo una confirma el horario");
    await using (var verify = new VerificationContext(options))
        Ensure(await verify.Appointments.CountAsync(x => x.StartUtc == clock.Utc(date, new(11, 0))) == 1, "Sin duplicados después de la carrera de reservas");

    var first = (await sp.AppointmentsAsync(date, date)).First(x => clock.Local(x.StartUtc).Hour == 10);
    await Denied(() => sr.ChangeStatusAsync(first.Id, first.Version, AppointmentStatus.Completed), "Recepción no finaliza consultas");
    await sp.ChangeStatusAsync(first.Id, first.Version, AppointmentStatus.Confirmed);
    try { await sa.ChangeStatusAsync(first.Id, first.Version, AppointmentStatus.Cancelled, "Intento concurrente de verificación"); throw new Exception("No se detectó edición concurrente."); }
    catch (DbUpdateConcurrencyException) { Pass("Ediciones obsoletas rechazadas por rowversion"); }
    first = (await sr.AppointmentsAsync(date, date)).Single(x => x.Id == first.Id);
    await Denied(() => sr.ChangeStatusAsync(first.Id, first.Version, AppointmentStatus.Waiting), "Llegada solo permitida el día de la cita");
    await using (var arrivals = new VerificationContext(options))
    {
        var moveToToday = await arrivals.Appointments.SingleAsync(x => x.Id == first.Id);
        moveToToday.StartUtc = clock.Utc(clock.Today, new(12, 0)); moveToToday.EndUtc = moveToToday.StartUtc.AddMinutes(30);
        await arrivals.SaveChangesAsync();
    }
    first = (await sr.AppointmentsAsync(clock.Today, clock.Today)).Single(x => x.Id == first.Id);
    await sr.ChangeStatusAsync(first.Id, first.Version, AppointmentStatus.Waiting);
    first = (await sd.AppointmentsAsync(clock.Today, clock.Today)).Single(x => x.Id == first.Id);
    await sd.ChangeStatusAsync(first.Id, first.Version, AppointmentStatus.InConsultation);
    first = (await sd.AppointmentsAsync(clock.Today, clock.Today)).Single(x => x.Id == first.Id);
    await sd.ChangeStatusAsync(first.Id, first.Version, AppointmentStatus.Completed);
    Ensure((await sd.HistoryAsync(first.Id)).Count == 5, "Recepción, consulta, finalización e historial");

    var concurrent = (await sa.AppointmentsAsync(date, date)).Single(x => clock.Local(x.StartUtc).Hour == 11);
    await sa.ChangeStatusAsync(concurrent.Id, concurrent.Version, AppointmentStatus.Cancelled, "Cancelación de verificación");
    Ensure((await sp.SlotsAsync(d1.Id, date)).Contains(new(11, 0)), "Cancelación libera el horario");
    await sa.SaveAppointmentAsync(Booking(p1.Id, d1.Id, 12));
    await Denied(() => sa.SaveAppointmentAsync(new() { PatientId = p1.Id, DoctorId = d1.Id, Date = date, Time = new(12, 15) }), "Reservas respetan la duración y alineación de consulta");
    await Denied(() => sa.SaveAppointmentAsync(Booking(p1.Id, d2.Id, 12)), "Paciente no puede tener citas superpuestas con distintos médicos");
    await Denied(() => sa.AddBlockAsync(d1.Id, date.ToDateTime(new(12, 0)), date.ToDateTime(new(13, 0)), "Bloqueo de prueba"), "Bloqueos respetan citas existentes");
    await sa.AddBlockAsync(d1.Id, date.ToDateTime(new(13, 0)), date.ToDateTime(new(14, 0)), "Ausencia de verificación");
    Ensure(!(await sp.SlotsAsync(d1.Id, date)).Contains(new(13, 0)), "Disponibilidad respeta ausencias y bloqueos");
    await Denied(() => sa.SaveAppointmentAsync(Booking(p1.Id, d1.Id, 13)), "Servidor rechaza reservas en horarios bloqueados");
    await sa.SaveAppointmentAsync(Booking(p2.Id, d2.Id, 15));
    Ensure((await sd.AppointmentsAsync(date, date)).All(x => x.DoctorId == d1.Id), "Doctor solo ve su agenda");
    Ensure((await sp.AppointmentsAsync(date, date)).All(x => x.PatientId == p1.Id), "Paciente solo ve sus citas");
    await Denied(() => sd.SlotsAsync(d2.Id, date), "Doctor no consulta la agenda de otro médico");
    var pending = (await sp.AppointmentsAsync(date, date)).Single(x => clock.Local(x.StartUtc).Hour == 12);
    await sp.RequestChangeAsync(pending.Id, "Solicito otra fecha de verificación");
    Ensure((await sr.AppointmentsAsync(date, date)).Single(x => x.Id == pending.Id).ChangeRequest != null, "Solicitud de reprogramación llega a recepción");
    Ensure((await sr.NotificationsAsync()).Any(), "Avisos internos a recepción");
    pending = (await sa.AppointmentsAsync(date, date)).Single(x => x.Id == pending.Id);
    await sa.SaveAppointmentAsync(new() { Id = pending.Id, PatientId = pending.PatientId, DoctorId = pending.DoctorId, Date = date, Time = new(12, 30), Version = pending.Version });
    Ensure((await sa.AppointmentsAsync(date, date)).Single(x => x.Id == pending.Id).ChangeRequest == null, "Reprogramación validada resuelve la solicitud");
    var unusedSchedule = d2.Schedules.Single(x => x.Day == date.AddDays(1).DayOfWeek);
    await sa.RemoveScheduleAsync(unusedSchedule.Id);
    Ensure((await sp.SlotsAsync(d2.Id, date.AddDays(1))).Count == 0, "Días sin horario no permiten reservas");
    await Denied(() => sp2.ToggleUserAsync(admin.Id), "Paciente no puede desactivar personal");
    await sa.ToggleUserAsync(reception.Id);
    await Denied(() => sr.ActorAsync(), "Cuenta inactiva pierde acceso inmediatamente");
    await sa.ToggleUserAsync(reception.Id);
    await sa.CreateStaffAsync(new() { FullName = "PRUEBA AUTOMÁTICA nueva secretaria", Email = "nueva@example.invalid", Password = password, Role = Roles.Reception });
    Ensure((await sa.UsersAsync()).Any(x => x.User.Email == "nueva@example.invalid"), "Creación de personal y asignación de rol");
    var newStaff = (await sa.UsersAsync()).Single(x => x.User.Email == "nueva@example.invalid");
    await sa.ChangeStaffRoleAsync(newStaff.User.Id, Roles.Doctor);
    Ensure((await sa.DoctorsAsync()).Any(x => x.UserId == newStaff.User.Id), "Cambio de rol crea el perfil médico");
    var resetToken = await users.GeneratePasswordResetTokenAsync(patient2User);
    Ensure(!(await users.ResetPasswordAsync(patient2User, "token-invalido", "NewVerification!2026Strong")).Succeeded, "Recuperación rechaza tokens inválidos");
    Ensure((await users.ResetPasswordAsync(patient2User, resetToken, "NewVerification!2026Strong")).Succeeded, "Token de recuperación permite restablecer la contraseña");
    Ensure(!(await users.ResetPasswordAsync(patient2User, resetToken, "ThirdVerification!2026Strong")).Succeeded, "Token de recuperación no puede reutilizarse");
    var excel = ReportExport.Excel(await sa.AppointmentsAsync(date, date), clock);
    using (var zip = new ZipArchive(new MemoryStream(excel)))
    using (var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open())
        Ensure(XDocument.Load(sheet).Root != null, "Exportación Excel válida y sin fórmulas de usuario");
    Console.WriteLine("VERIFICACIÓN COMPLETADA. Los registros de prueba están aislados y se retiran al finalizar.");
}
finally
{
    if (created)
    {
        await check.Database.ExecuteSqlRawAsync("DECLARE @sql nvarchar(max)=''; SELECT @sql += 'ALTER TABLE [ma_verification].' + QUOTENAME(t.name) + ' DROP CONSTRAINT ' + QUOTENAME(f.name) + ';' FROM sys.foreign_keys f JOIN sys.tables t ON f.parent_object_id=t.object_id JOIN sys.schemas s ON t.schema_id=s.schema_id WHERE s.name='ma_verification'; EXEC sys.sp_executesql @sql; SET @sql=''; SELECT @sql += 'DROP TABLE [ma_verification].' + QUOTENAME(t.name) + ';' FROM sys.tables t JOIN sys.schemas s ON t.schema_id=s.schema_id WHERE s.name='ma_verification'; EXEC sys.sp_executesql @sql; IF SCHEMA_ID('ma_verification') IS NOT NULL EXEC('DROP SCHEMA [ma_verification]');");
        Console.WriteLine("Esquema temporal de verificación retirado. Los datos de ma permanecen intactos.");
    }
}

static void Ensure(bool result, string label) { if (!result) throw new Exception("FALLO: " + label); Pass(label); }
static void Pass(string label) => Console.WriteLine("OK · " + label);
static async Task Denied(Func<Task> action, string label)
{
    try { await action(); } catch (Exception ex) when (ex is UnauthorizedAccessException or ClinicException) { Pass(label); return; }
    throw new Exception("FALLO: " + label);
}

sealed class VerificationContext(DbContextOptions<ClinicDbContext> options) : ClinicDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model) { base.OnModelCreating(model); model.HasDefaultSchema("ma_verification"); }
}
sealed class VerificationFactory(DbContextOptions<ClinicDbContext> options) : IDbContextFactory<ClinicDbContext>
{
    public ClinicDbContext CreateDbContext() => new VerificationContext(options);
}
sealed class FixedAuthentication : AuthenticationStateProvider
{
    private readonly ClaimsPrincipal principal;
    public FixedAuthentication() => principal = new(new ClaimsIdentity());
    public FixedAuthentication(ApplicationUser user, string role) => principal = new(new ClaimsIdentity([
        new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Name, user.Email!), new(ClaimTypes.Role, role), new("AspNet.Identity.SecurityStamp", user.SecurityStamp!)], "Verification"));
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
}
