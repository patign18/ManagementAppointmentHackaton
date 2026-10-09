using System.Security.Cryptography;
using System.Text;
using ManagementAppointment.Models;
using ManagementAppointment.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ManagementAppointment.Data;

public static class DoctorOfficeDemo
{
    public const string DoctorEmail = "doctor@clinica.local";
    private static string Password() => "Pr9!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    public static async Task CreateAsync(IServiceProvider services, string exportPath)
    {
        await DatabaseSetup.InitializeAsync(services);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var clock = scope.ServiceProvider.GetRequiredService<OfficeClock>();
        await using var tx = await db.Database.BeginTransactionAsync();
        await AppointmentRecovery.LockAsync(db, "ma:doctor-office-demo");
        var existing = await db.Patients.CountAsync(x => x.IsDemo && x.DocumentNumber != null && x.DocumentNumber.StartsWith("PR-DEMO-"));
        if (existing == 36) { Console.WriteLine("El modelo de 36 pacientes ya existe. No se duplicaron perfiles ni se cambiaron contraseñas."); return; }
        if (existing != 0) throw new InvalidOperationException("El modelo de demostración está incompleto. No se duplicarán registros.");
        var specialty = await db.Specialties.SingleOrDefaultAsync(x => x.Name == "Medicina general");
        if (specialty == null) { specialty = new Specialty { Name = "Medicina general" }; db.Specialties.Add(specialty); await db.SaveChangesAsync(); }
        var doctorUser = await users.FindByEmailAsync(DoctorEmail);
        var doctorPassword = doctorUser == null ? Password() : "Contraseña existente; no se modificó";
        if (doctorUser == null)
        {
            doctorUser = new ApplicationUser { UserName = DoctorEmail, Email = DoctorEmail, FullName = "Dra. Patricia Rivera", IsActive = true };
            if (!(await users.CreateAsync(doctorUser, doctorPassword)).Succeeded || !(await users.AddToRoleAsync(doctorUser, Roles.Doctor)).Succeeded) throw new InvalidOperationException("No se pudo crear la cuenta médica de demostración.");
        }
        if (!await users.IsInRoleAsync(doctorUser, Roles.Doctor)) throw new InvalidOperationException("El correo de demostración ya pertenece a otra función.");
        var doctor = await db.Doctors.Include(x => x.Schedules).SingleOrDefaultAsync(x => x.UserId == doctorUser.Id);
        if (doctor == null)
        {
            doctor = new Doctor { UserId = doctorUser.Id, FullName = doctorUser.FullName, SpecialtyId = specialty.Id, AutomaticRecovery = true, ConfirmationHours = 48, CanManageAppointments = true, ConsultationMinutes = 30, MinimumRecoveredMinutes = 10 };
            db.Doctors.Add(doctor); await db.SaveChangesAsync();
            foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
            {
                db.DoctorSchedules.Add(new() { DoctorId = doctor.Id, Day = day, Start = new(9, 0), End = new(12, 0) });
                db.DoctorSchedules.Add(new() { DoctorId = doctor.Id, Day = day, Start = new(13, 0), End = new(16, 0) });
            }
            await db.SaveChangesAsync();
        }
        if (await db.Appointments.AnyAsync(x => x.DoctorId == doctor.Id)) throw new InvalidOperationException("La agenda de demostración ya tiene citas. No se mezclarán registros existentes.");
        string[] names = ["Ana Morales Santiago", "Luis Rivera Ortiz", "Carmen Rodríguez Pérez", "José Torres Delgado", "María Hernández Cruz", "Carlos González Vega", "Isabel Martínez León", "Miguel López Ramos", "Elena Pérez Soto", "Javier Sánchez Colón", "Rosa Díaz Fuentes", "Daniel Ortiz Medina", "Lucía Cruz Navarro", "Andrés Vega Silva", "Sofía Ramos Nieves", "Ricardo Santiago Ríos", "Valeria Delgado Molina", "Fernando Medina Lara", "Paola Colón Reyes", "Diego Fuentes Santos", "Gabriela Navarro Ruiz", "Manuel Silva Castro", "Natalia Nieves Acosta", "Roberto Ríos Serrano", "Adriana Molina Flores", "Francisco Lara Méndez", "Camila Reyes Pacheco", "Eduardo Santos Vidal", "Beatriz Ruiz Mercado", "Antonio Castro Rosado", "Daniela Acosta Peña", "Héctor Serrano Cabrera", "Mónica Flores Pimentel", "Alejandro Méndez Ferrer", "Patricia Pacheco Lugo", "Sebastián Vidal Cordero"];
        string[] towns = ["San Juan", "Bayamón", "Caguas", "Ponce", "Mayagüez", "Humacao", "Fajardo", "Arecibo", "Aguadilla", "Dorado", "Toa Baja", "Río Grande"];
        string[] zips = ["00901", "00956", "00725", "00716", "00680", "00791", "00738", "00612", "00603", "00646", "00949", "00745"];
        string[] streets = ["Calle Modelo", "Paseo Demostración", "Calle Jardín Modelo", "Camino Ejemplo", "Calle Vista Modelo", "Paseo Muestra"];
        var csv = new StringBuilder("tipo,codigo,nombre,correo,contrasena_inicial,direccion,municipio,codigo_postal,telefono,nacimiento,estado_inicial,cita_inicial\n");
        csv.AppendLine(string.Join(',', new[] { "Doctor", "DOCTOR-DEMO", doctor.FullName, DoctorEmail, doctorPassword, "Oficina de demostración", "San Juan", "00901", "", "", "Acceso a la oficina", "" }.Select(Csv)));
        var date = clock.Today.AddDays(1); while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(1);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 36; i++)
        {
            var password = Password(); var email = $"paciente{i + 1:00}@example.invalid";
            var user = new ApplicationUser { UserName = email, Email = email, FullName = names[i], IsActive = true };
            if (!(await users.CreateAsync(user, password)).Succeeded || !(await users.AddToRoleAsync(user, Roles.Patient)).Succeeded) throw new InvalidOperationException("No se pudo crear un perfil ficticio; se revirtió el modelo.");
            var patient = new Patient { UserId = user.Id, FullName = names[i], DocumentNumber = $"PR-DEMO-{i + 1:00}", BirthDate = new DateOnly(1958 + i % 40, i % 12 + 1, i % 26 + 1), Email = email, Phone = $"+1 {(i % 2 == 0 ? "787" : "939")} 555-{100 + i:0000}", Address = $"{streets[i % streets.Length]} {101 + i}, Residencial Modelo {i / 12 + 1}", Municipality = towns[i % towns.Length], PostalCode = zips[i % zips.Length], IsDemo = true, AutomaticReassignmentConsent = true, RegisteredByDoctorId = doctor.Id };
            db.Patients.Add(patient); await db.SaveChangesAsync();
            Appointment? appointment = null;
            if (i < 24)
            {
                if (i > 0 && i % 6 == 0) { date = date.AddDays(1); while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(1); }
                var start = clock.Utc(date, new TimeOnly(9, 0).AddMinutes(i % 6 * 30));
                appointment = new Appointment { PatientId = patient.Id, DoctorId = doctor.Id, StartUtc = start, EndUtc = start.AddMinutes(30), Status = i % 3 == 0 ? AppointmentStatus.Pending : AppointmentStatus.Confirmed, AdministrativeNote = "DEMO: cita ficticia para mostrar la gestión de la oficina.", ConfirmationDueUtc = i % 3 == 0 ? AppointmentRecovery.ConfirmationDeadline(start, now, 48, 15) : null };
                db.Appointments.Add(appointment);
                db.AppointmentHistory.Add(new() { Appointment = appointment, ActorId = doctorUser.Id, Action = "Cita de demostración programada por la oficina" });
                await db.SaveChangesAsync();
            }
            db.WaitlistEntries.Add(new() { PatientId = patient.Id, DoctorId = doctor.Id, FromUtc = now.AddMinutes(1), UntilUtc = now.AddDays(60), MinimumNoticeMinutes = 60, AppointmentToAdvanceId = appointment?.Id, CreatedUtc = now.AddSeconds(i) });
            csv.AppendLine(string.Join(',', new[] { "Paciente sin acceso web", patient.DocumentNumber!, patient.FullName, email, password, patient.Address, patient.Municipality, patient.PostalCode, patient.Phone!, patient.BirthDate!.Value.ToString("yyyy-MM-dd"), appointment == null ? "Hold sin cita" : StatusText.Label(appointment.Status), appointment == null ? "" : clock.Local(appointment.StartUtc).ToString("yyyy-MM-dd h.mm tt") }.Select(Csv)));
        }
        db.AuditLogs.Add(new() { ActorId = doctorUser.Id, Action = "Modelo autorizado: 36 perfiles DEMO de Puerto Rico, 24 citas y 12 pacientes en hold" });
        await db.SaveChangesAsync();
        exportPath = Path.GetFullPath(exportPath); Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
        await File.WriteAllTextAsync(exportPath, csv.ToString(), new UTF8Encoding(true));
        await tx.CommitAsync();
        Console.WriteLine("Modelo creado: 36 perfiles ficticios, 24 citas y 12 pacientes en hold sin cita. Los 24 programados también autorizan adelantos. Credenciales iniciales guardadas en el archivo privado indicado.");
    }
}
