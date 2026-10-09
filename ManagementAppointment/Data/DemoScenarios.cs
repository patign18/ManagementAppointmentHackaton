using System.Data;
using ManagementAppointment.Models;
using ManagementAppointment.Services;
using Microsoft.EntityFrameworkCore;

namespace ManagementAppointment.Data;

public static class DemoScenarios
{
    public static async Task ReportAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<OfficeClock>();
        var rows = await db.SlotAssignments.AsNoTracking()
            .Where(x => x.ReleasedSlot.SourceAppointment.Patient.IsDemo &&
                x.ReleasedSlot.SourceAppointment.Status == AppointmentStatus.Cancelled)
            .OrderByDescending(x => x.CreatedUtc)
            .Select(x => new { Source = x.ReleasedSlot.SourceAppointmentId, Original = x.ReleasedSlot.SourceAppointment.Patient.FullName,
                Recipient = x.WaitlistEntry.Patient.FullName, Target = x.AppointmentId, Start = x.ReleasedSlot.StartUtc }).Take(20).ToListAsync();
        foreach (var row in rows)
        {
            var reason = await db.AppointmentHistory.Where(x => x.AppointmentId == row.Source && x.Action.StartsWith("Liberación automática:"))
                .OrderByDescending(x => x.CreatedUtc).Select(x => x.Action).FirstOrDefaultAsync();
            Console.WriteLine($"Cita #{row.Source}: {row.Original} | {clock.Local(row.Start):dd/MM/yyyy h.mm tt} | {reason ?? "Cancelación registrada por la oficina"} | Reasignada a {row.Recipient}, cita #{row.Target}.");
        }
        if (rows.Count == 0) Console.WriteLine("No hay cancelaciones DEMO con reasignación registrada.");
    }

    public static async Task CreateAsync(IServiceProvider services)
    {
        await DatabaseSetup.InitializeAsync(services);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<OfficeClock>();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await AppointmentRecovery.LockAsync(db, "ma:recovery");
        var doctor = await db.Doctors.Include(x => x.Schedules).SingleAsync(x => x.User.Email == DoctorOfficeDemo.DoctorEmail);
        var patients = await db.Patients.Where(x => x.IsDemo && x.RegisteredByDoctorId == doctor.Id && x.IsActive).OrderBy(x => x.DocumentNumber).ToListAsync();
        if (patients.Count < 28) throw new InvalidOperationException("Se necesitan los perfiles DEMO de la oficina.");
        doctor.AutomaticRecovery = true;
        doctor.ConfirmationHours = 48;
        var now = DateTime.UtcNow;
        var existing = await db.Appointments.Where(x => x.DoctorId == doctor.Id).ToListAsync();
        var created = 0;
        async Task Add(string key, AppointmentStatus status, int patientIndex, bool historical, bool expired = false, bool today = false)
        {
            var note = $"DEMO-CASOS:{key}";
            if (existing.Any(x => x.AdministrativeNote == note)) return;
            var patient = patients[patientIndex];
            DateTime start = default;
            for (var offset = today ? 0 : historical ? -7 : 1; offset < (today ? 1 : historical ? 0 : 45) && start == default; offset++)
            {
                var day = clock.Today.AddDays(offset);
                foreach (var schedule in doctor.Schedules.Where(x => x.Day == day.DayOfWeek))
                {
                    for (var time = schedule.Start; time.AddMinutes(30) <= schedule.End; time = time.AddMinutes(30))
                    {
                        var candidate = clock.Utc(day, time);
                        if (existing.Any(x => x.StartUtc < candidate.AddMinutes(30) && x.EndUtc > candidate && (x.DoctorId == doctor.Id || x.PatientId == patient.Id))) continue;
                        start = candidate; break;
                    }
                    if (start != default) break;
                }
            }
            if (start == default) throw new InvalidOperationException("No hay espacio para el escenario DEMO.");
            var appointment = new Appointment
            {
                DoctorId = doctor.Id, PatientId = patient.Id, StartUtc = start, EndUtc = start.AddMinutes(30),
                Status = status, AdministrativeNote = note,
                ArrivalUtc = status is AppointmentStatus.Waiting or AppointmentStatus.InConsultation or AppointmentStatus.Completed ? start : null,
                ConfirmationDueUtc = status == AppointmentStatus.Pending ? (expired ? now.AddMinutes(-1) : AppointmentRecovery.ConfirmationDeadline(start, now, 48, 15)) : null
            };
            db.Appointments.Add(appointment); existing.Add(appointment); created++;
            db.AppointmentHistory.Add(new() { Appointment = appointment, ActorId = "demo", Action = $"Escenario ficticio: {key}. Estado inicial: {StatusText.Label(status)}." });
            await db.SaveChangesAsync();
        }
        for (var i = 0; i < 2; i++)
        {
            await Add($"pendiente-{i}", AppointmentStatus.Pending, i, false);
            await Add($"confirmada-{i}", AppointmentStatus.Confirmed, 2 + i, false);
            await Add($"en-espera-{i}", AppointmentStatus.Waiting, 4 + i, true, today: true);
            await Add($"en-consulta-{i}", AppointmentStatus.InConsultation, 6 + i, true, today: true);
            await Add($"completada-{i}", AppointmentStatus.Completed, 8 + i, true);
            await Add($"cancelada-{i}", AppointmentStatus.Cancelled, 10 + i, false);
            await Add($"no-asistio-{i}", AppointmentStatus.NoShow, 12 + i, true);
            await Add($"plazo-vencido-{i}", AppointmentStatus.Pending, 14 + i, false, true);
        }
        // Eligible demo patients allow the real recovery worker to demonstrate direct allocation.
        for (var i = 24; i < 28; i++)
        {
            var patient = patients[i];
            patient.AutomaticReassignmentConsent = true;
            var entry = await db.WaitlistEntries.FirstOrDefaultAsync(x => x.PatientId == patient.Id && x.DoctorId == doctor.Id && x.Status == WaitlistStatus.Active);
            if (entry == null) db.WaitlistEntries.Add(new() { PatientId = patient.Id, DoctorId = doctor.Id, FromUtc = now, UntilUtc = now.AddDays(60), MinimumNoticeMinutes = 60 });
            else { entry.FromUtc = now; entry.UntilUtc = now.AddDays(60); }
        }
        await db.SaveChangesAsync(); await tx.CommitAsync();
        Console.WriteLine($"Escenarios DEMO creados: {created}. Se conservan los 36 pacientes y las citas existentes.");
        await scope.ServiceProvider.GetRequiredService<AppointmentRecovery>().ProcessAsync(CancellationToken.None);
        Console.WriteLine("Se ejecutó el proceso de recuperación: consulta Hold y reasignaciones y la actividad de las citas.");
    }
}
