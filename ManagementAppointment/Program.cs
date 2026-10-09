using ManagementAppointment.Components;
using ManagementAppointment.Data;
using ManagementAppointment.Models;
using ManagementAppointment.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Threading.RateLimiting;

var maintenance = args.FirstOrDefault()?.StartsWith("--clinic-", StringComparison.Ordinal) == true;
var builder = WebApplication.CreateBuilder(maintenance ? [] : args);
// La ejecución directa puede no usar el perfil Development de Visual Studio.
// Recuperar la conexión local solo cuando no hay una conexión configurada.
if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("TicketSystem")))
    builder.Configuration.AddUserSecrets<ClinicDbContext>(optional: true);
if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("TicketSystem")))
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
var connectionString = builder.Configuration.GetConnectionString("TicketSystem");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Falta ConnectionStrings:TicketSystem. Configura la conexión en los secretos de usuario del proyecto o en la variable ConnectionStrings__TicketSystem.");
var officeCulture = (CultureInfo)CultureInfo.GetCultureInfo("es-PR").Clone();
officeCulture.DateTimeFormat.AMDesignator = "am";
officeCulture.DateTimeFormat.PMDesignator = "pm";
CultureInfo.DefaultThreadCurrentCulture = officeCulture;
CultureInfo.DefaultThreadCurrentUICulture = officeCulture;

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddDbContextFactory<ClinicDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.Password.RequiredLength = 12;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddEntityFrameworkStores<ClinicDbContext>().AddDefaultTokenProviders();
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(2));
builder.Services.AddDataProtection().SetApplicationName("ManagementAppointment");
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login"; options.AccessDeniedPath = "/acceso-denegado";
    options.Cookie.HttpOnly = true; options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax; options.ExpireTimeSpan = TimeSpan.FromHours(8); options.SlidingExpiration = true;
});
builder.Services.AddAuthorization(options => options.AddPolicy("ClinicStaff", policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Admin, Roles.Reception, Roles.Doctor)));
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidationProvider>();
builder.Services.AddScoped<ClinicService>();
builder.Services.AddSingleton<OfficeClock>();
builder.Services.AddTransient<AccountMailer>();
builder.Services.AddScoped<AppointmentRecovery>();
builder.Services.AddScoped<ClinicMailDispatcher>();
builder.Services.AddScoped<AppointmentResponseService>();
if (!maintenance) builder.Services.AddHostedService<AppointmentRecoveryWorker>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("accounts", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();

if (maintenance)
{
    if (args[0] == "--clinic-demo-report") { await DemoScenarios.ReportAsync(app.Services); return; }
    if (args[0] == "--clinic-demo-scenarios") { await DemoScenarios.CreateAsync(app.Services); return; }
    if (args[0] == "--clinic-change-access" && args.Length == 3) { await DatabaseSetup.ChangeStaffAccessAsync(app.Services, args[1], args[2]); return; }
    if (args[0] == "--clinic-account-info" && args.Length == 2) { await DatabaseSetup.AccountInfoAsync(app.Services, args[1]); return; }
    if (args[0] == "--clinic-password-link" && args.Length == 3) { await DatabaseSetup.PasswordLinkAsync(app.Services, args[1], args[2]); return; }
    if (args[0] == "--clinic-demo" && args.Length == 2) { await DoctorOfficeDemo.CreateAsync(app.Services, args[1]); return; }
    if (args[0] is "--clinic-init" or "--clinic-upgrade") { await DatabaseSetup.InitializeAsync(app.Services); Console.WriteLine("Esquema ma y roles actualizados sin eliminar datos."); }
    else if (args[0] == "--clinic-admin" && args.Length == 2)
    {
        var password = Environment.GetEnvironmentVariable("MA_ADMIN_PASSWORD") ?? throw new InvalidOperationException("Define MA_ADMIN_PASSWORD para crear la primera cuenta.");
        await DatabaseSetup.CreateAdminAsync(app.Services, args[1], password); Console.WriteLine("Primera cuenta administrativa creada.");
    }
    else throw new InvalidOperationException("Usa --clinic-init, --clinic-upgrade, --clinic-admin correo o --clinic-demo ruta.csv.");
    return;
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapAccountEndpoints();
app.MapAppointmentResponseEndpoints();
app.MapGet("/portal", () => Results.LocalRedirect("/login"));
app.MapGet("/registro", () => Results.LocalRedirect("/login"));

app.Run();
