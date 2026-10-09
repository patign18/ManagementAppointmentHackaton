using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using ManagementAppointment.Data;
using ManagementAppointment.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ManagementAppointment.Services;

public record ClinicActor(string Id, string Role, int? PatientId, int? DoctorId, string FullName)
{
    public bool IsStaff => Role is Roles.Admin or Roles.Reception;
}

public partial class ClinicService(IDbContextFactory<ClinicDbContext> factory, AuthenticationStateProvider authentication,
    IServiceScopeFactory scopes, OfficeClock clock)
{
    private static void Validate(object form)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(form, new ValidationContext(form), results, true))
            throw new ClinicException(results.First().ErrorMessage ?? "Revisa los datos del formulario.");
    }

    public async Task<ClinicActor> ActorAsync()
    {
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException("Inicia sesión para continuar.");
        await using var db = await factory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.IsActive)
            ?? throw new UnauthorizedAccessException("La cuenta no está disponible.");
        if (principal.FindFirstValue("AspNet.Identity.SecurityStamp") != user.SecurityStamp) throw new UnauthorizedAccessException("La sesión venció. Inicia sesión de nuevo.");
        var role = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == id select r.Name).SingleAsync();
        if (role is not (Roles.Admin or Roles.Reception or Roles.Doctor)) throw new UnauthorizedAccessException("El acceso a la oficina está reservado al personal médico.");
        var patient = await db.Patients.Where(x => x.UserId == id && x.IsActive).Select(x => (int?)x.Id).SingleOrDefaultAsync();
        var doctor = await db.Doctors.Where(x => x.UserId == id && x.IsActive).Select(x => (int?)x.Id).SingleOrDefaultAsync();
        if (role == Roles.Doctor && doctor == null) throw new UnauthorizedAccessException("La consulta médica está desactivada.");
        return new(id, role!, patient, doctor, user.FullName);
    }

    private static void Staff(ClinicActor a) { if (!a.IsStaff) throw new UnauthorizedAccessException("Esta acción requiere permiso de recepción o administración."); }
    private static void Admin(ClinicActor a) { if (a.Role != Roles.Admin) throw new UnauthorizedAccessException("Esta acción requiere permiso de administración."); }
    private static IQueryable<Appointment> Scope(IQueryable<Appointment> query, ClinicActor a) => a.Role switch
    {
        Roles.Admin or Roles.Reception => query,
        Roles.Doctor => query.Where(x => x.DoctorId == a.DoctorId),
        Roles.Patient => query.Where(x => x.PatientId == a.PatientId),
        _ => query.Where(x => false)
    };

    public async Task<List<Appointment>> AppointmentsAsync(DateOnly start, DateOnly end)
    {
        if (end < start || end.DayNumber - start.DayNumber > 366) throw new ClinicException("Consulta un periodo de hasta un año.");
        var a = await ActorAsync();
        await using var db = await factory.CreateDbContextAsync();
        var from = clock.Utc(start, TimeOnly.MinValue); var until = clock.Utc(end.AddDays(1), TimeOnly.MinValue);
        return await Scope(db.Appointments, a).AsNoTracking().Include(x => x.Patient).Include(x => x.Doctor).ThenInclude(x => x.Specialty)
            .Where(x => x.StartUtc >= from && x.StartUtc < until).OrderBy(x => x.StartUtc).ToListAsync();
    }

    public async Task<List<Patient>> PatientsAsync()
    {
        var a = await ActorAsync(); await using var db = await factory.CreateDbContextAsync();
        var q = db.Patients.AsNoTracking();
        if (a.Role == Roles.Patient) q = q.Where(x => x.Id == a.PatientId);
        else if (a.Role == Roles.Doctor) q = q.Where(x => x.RegisteredByDoctorId == a.DoctorId || db.Appointments.Any(v => v.PatientId == x.Id && v.DoctorId == a.DoctorId) || db.WaitlistEntries.Any(w => w.PatientId == x.Id && w.DoctorId == a.DoctorId && w.Status == WaitlistStatus.Active));
        else Staff(a);
        return await q.OrderBy(x => x.FullName).ToListAsync();
    }

    public async Task<List<Doctor>> DoctorsAsync()
    {
        var a = await ActorAsync(); await using var db = await factory.CreateDbContextAsync();
        var q = db.Doctors.AsNoTracking().Include(x => x.Specialty).Include(x => x.Schedules).AsQueryable();
        if (a.Role == Roles.Patient) q = q.Where(x => x.IsActive && x.User.IsActive);
        if (a.Role == Roles.Doctor) q = q.Where(x => x.Id == a.DoctorId);
        return await q.OrderBy(x => x.FullName).ToListAsync();
    }

    public async Task<List<Specialty>> SpecialtiesAsync()
    {
        await ActorAsync(); await using var db = await factory.CreateDbContextAsync();
        return await db.Specialties.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
    }

    public async Task AddSpecialtyAsync(string name)
    {
        var a = await ActorAsync(); Admin(a); name = name.Trim();
        if (name.Length is < 2 or > 80) throw new ClinicException("La especialidad debe tener entre 2 y 80 caracteres.");
        await using var db = await factory.CreateDbContextAsync();
        if (await db.Specialties.AnyAsync(x => x.Name == name)) throw new ClinicException("La especialidad ya está registrada.");
        db.Specialties.Add(new() { Name = name }); Audit(db, a, "Especialidad creada"); await db.SaveChangesAsync();
    }

    public async Task SavePatientAsync(PatientForm form)
    {
        Validate(form); var a = await ActorAsync(); if (a.Role != Roles.Doctor) Staff(a); else if (a.DoctorId == null) throw new UnauthorizedAccessException(); await using var db = await factory.CreateDbContextAsync();
        if (a.Role == Roles.Doctor && form.Id != 0 && !await db.Patients.AnyAsync(x => x.Id == form.Id && x.RegisteredByDoctorId == a.DoctorId)) throw new UnauthorizedAccessException("Solo puedes editar pacientes registrados por ti.");
        var doc = string.IsNullOrWhiteSpace(form.DocumentNumber) ? null : form.DocumentNumber.Trim();
        if (form.BirthDate > clock.Today) throw new ClinicException("La fecha de nacimiento no puede estar en el futuro.");
        if (doc != null && await db.Patients.AnyAsync(x => x.Id != form.Id && x.DocumentNumber == doc))
            throw new ClinicException("Ya existe un paciente con este documento. Busca su registro antes de continuar.");
        if (form.Id == 0 && await db.Patients.AnyAsync(x => x.FullName == form.FullName.Trim() && x.Email == form.Email && x.BirthDate == form.BirthDate))
            throw new ClinicException("Existe un posible duplicado con estos datos. Revisa la lista de pacientes.");
        var patient = form.Id == 0 ? new Patient() : await db.Patients.FindAsync(form.Id) ?? throw new ClinicException("Paciente no encontrado.");
        patient.FullName = form.FullName.Trim(); patient.DocumentNumber = doc; patient.BirthDate = form.BirthDate;
        patient.Email = form.Email?.Trim(); patient.Phone = form.Phone?.Trim();
        patient.Address = form.Address.Trim(); patient.Municipality = form.Municipality.Trim(); patient.PostalCode = form.PostalCode.Trim();
        patient.ContactConsent = form.ContactConsent; patient.AutomaticReassignmentConsent = form.AutomaticReassignmentConsent;
        if (form.Id == 0) { patient.RegisteredByDoctorId = a.DoctorId; db.Patients.Add(patient); }
        Audit(db, a, $"Paciente {(form.Id == 0 ? "creado" : "actualizado")} #{form.Id}"); await db.SaveChangesAsync();
    }

    public async Task SaveDoctorAsync(DoctorForm form)
    {
        Validate(form); var a = await ActorAsync(); ManageAgenda(a, form.Id); await using var db = await factory.CreateDbContextAsync();
        var d = await db.Doctors.FindAsync(form.Id) ?? throw new ClinicException("Médico no encontrado.");
        if (d.AutomaticRecovery && form.ConsultationMinutes < d.MinimumRecoveredMinutes) throw new ClinicException("La duración debe permitir el mínimo de atención configurado para espacios recuperados.");
        if (form.SpecialtyId.HasValue && !await db.Specialties.AnyAsync(x => x.Id == form.SpecialtyId)) throw new ClinicException("Especialidad no válida.");
        d.FullName = form.FullName.Trim(); d.Phone = form.Phone; d.SpecialtyId = form.SpecialtyId;
        d.ConsultationMinutes = form.ConsultationMinutes; d.CanManageAppointments = form.CanManageAppointments;
        Audit(db, a, $"Médico actualizado #{d.Id}"); await db.SaveChangesAsync();
    }

    public async Task<List<Appointment>> PatientHistoryAsync(int patientId)
    {
        var a = await ActorAsync(); await using var db = await factory.CreateDbContextAsync();
        return await Scope(db.Appointments, a).AsNoTracking().Include(x => x.Patient).Include(x => x.Doctor).ThenInclude(x => x.Specialty)
            .Where(x => x.PatientId == patientId).OrderByDescending(x => x.StartUtc).Take(100).ToListAsync();
    }

    public async Task TogglePatientAsync(int id)
    {
        var a = await ActorAsync(); Staff(a); await using var db = await factory.CreateDbContextAsync();
        var p = await db.Patients.FindAsync(id) ?? throw new ClinicException("Paciente no encontrado.");
        if (p.IsActive && await db.Appointments.AnyAsync(x => x.PatientId == id && x.StartUtc > DateTime.UtcNow && x.Status != AppointmentStatus.Cancelled && x.Status != AppointmentStatus.NoShow))
            throw new ClinicException("El paciente tiene citas futuras. Resuélvelas antes de desactivarlo.");
        p.IsActive = !p.IsActive; Audit(db, a, $"Paciente #{id}: {(p.IsActive ? "activado" : "desactivado")}"); await db.SaveChangesAsync();
    }

    public async Task<List<TimeOnly>> SlotsAsync(int doctorId, DateOnly date, int excludeId = 0)
    {
        var a = await ActorAsync(); await using var db = await factory.CreateDbContextAsync();
        if (a.Role == Roles.Doctor && doctorId != a.DoctorId) throw new UnauthorizedAccessException();
        if (excludeId != 0 && !await Scope(db.Appointments, a).AnyAsync(x => x.Id == excludeId)) throw new UnauthorizedAccessException();
        var doctor = await db.Doctors.Include(x => x.Schedules).SingleOrDefaultAsync(x => x.Id == doctorId && x.IsActive && x.User.IsActive);
        if (doctor == null || date < clock.Today || date > clock.Today.AddDays(180)) return [];
        var from = clock.Utc(date, TimeOnly.MinValue); var to = clock.Utc(date.AddDays(1), TimeOnly.MinValue);
        var busy = await db.Appointments.Where(x => x.Id != excludeId && x.DoctorId == doctorId && x.StartUtc < to && x.EndUtc > from && x.Status != AppointmentStatus.Cancelled && x.Status != AppointmentStatus.NoShow).ToListAsync();
        var blocks = await db.ScheduleExceptions.Where(x => x.DoctorId == doctorId && x.StartUtc < to && x.EndUtc > from).ToListAsync();
        var result = new List<TimeOnly>();
        foreach (var schedule in doctor.Schedules.Where(x => x.Day == date.DayOfWeek))
            for (var time = schedule.Start; time.AddMinutes(doctor.ConsultationMinutes) > time && time.AddMinutes(doctor.ConsultationMinutes) <= schedule.End; time = time.AddMinutes(doctor.ConsultationMinutes))
            {
                var start = clock.Utc(date, time); var end = start.AddMinutes(doctor.ConsultationMinutes);
                if (start > DateTime.UtcNow && !busy.Any(x => x.StartUtc < end && x.EndUtc > start) && !blocks.Any(x => x.StartUtc < end && x.EndUtc > start)) result.Add(time);
            }
        return result.Distinct().OrderBy(x => x).ToList();
    }

    private async Task LockAsync(ClinicDbContext db, string resource)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @result < 0 THROW 51000, 'No se pudo obtener el bloqueo de agenda.', 1;");
    }

    public async Task SaveAppointmentAsync(AppointmentForm form)
    {
        Validate(form); var a = await ActorAsync(); await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var d = await db.Doctors.Include(x => x.Schedules).SingleOrDefaultAsync(x => x.Id == form.DoctorId && x.IsActive && x.User.IsActive)
            ?? throw new ClinicException("El médico no está disponible.");
        if (a.Role == Roles.Doctor && d.Id != a.DoctorId) throw new UnauthorizedAccessException("Solo puedes administrar tu agenda.");
        if (a.Role == Roles.Patient && (form.PatientId != a.PatientId || form.Id != 0)) throw new UnauthorizedAccessException("Puedes reservar tus citas y solicitar cambios a recepción.");
        var patient = await db.Patients.SingleOrDefaultAsync(x => x.Id == form.PatientId && x.IsActive) ?? throw new ClinicException("Paciente no disponible.");
        if (a.Role == Roles.Doctor && patient.RegisteredByDoctorId != a.DoctorId && !await db.Appointments.AnyAsync(x => x.PatientId == patient.Id && x.DoctorId == a.DoctorId) && !await db.WaitlistEntries.AnyAsync(x => x.PatientId == patient.Id && x.DoctorId == a.DoctorId && x.Status == WaitlistStatus.Active))
            throw new UnauthorizedAccessException("Solo puedes gestionar pacientes ya asignados a tu consulta.");
        var start = clock.Utc(form.Date, form.Time); var end = start.AddMinutes(d.ConsultationMinutes);
        if (start <= DateTime.UtcNow || form.Date > clock.Today.AddDays(180)) throw new ClinicException("Selecciona un horario futuro dentro de los próximos seis meses.");
        var valid = d.Schedules.Any(x => x.Day == form.Date.DayOfWeek && form.Time >= x.Start && form.Time.AddMinutes(d.ConsultationMinutes) > form.Time && form.Time.AddMinutes(d.ConsultationMinutes) <= x.End && (form.Time.ToTimeSpan() - x.Start.ToTimeSpan()).TotalMinutes % d.ConsultationMinutes == 0);
        if (!valid) throw new ClinicException("El horario está fuera de la agenda del médico.");
        await LockAsync(db, $"{db.Model.GetDefaultSchema()}:doctor:{d.Id}:{form.Date:yyyyMMdd}");
        await LockAsync(db, $"{db.Model.GetDefaultSchema()}:patient:{patient.Id}");
        if (await db.ScheduleExceptions.AnyAsync(x => x.DoctorId == d.Id && x.StartUtc < end && x.EndUtc > start)) throw new ClinicException("El médico tiene un bloqueo en ese horario.");
        if (await db.Appointments.AnyAsync(x => x.Id != form.Id && (x.DoctorId == d.Id || x.PatientId == patient.Id) && x.StartUtc < end && x.EndUtc > start && x.Status != AppointmentStatus.Cancelled && x.Status != AppointmentStatus.NoShow))
            throw new ClinicException("Ese horario acaba de ocuparse o el paciente tiene otra cita. Selecciona otro horario.");
        var appointment = form.Id == 0 ? new Appointment() : await Scope(db.Appointments, a).SingleOrDefaultAsync(x => x.Id == form.Id) ?? throw new UnauthorizedAccessException();
        if (form.Id != 0)
        {
            if (appointment.Status is not (AppointmentStatus.Pending or AppointmentStatus.Confirmed)) throw new ClinicException("Solo se pueden reprogramar citas pendientes o confirmadas.");
            if (appointment.PatientId != patient.Id) throw new ClinicException("El paciente de una cita existente no puede cambiarse.");
            db.Entry(appointment).Property(x => x.Version).OriginalValue = form.Version;
            if ((appointment.StartUtc != start || appointment.DoctorId != d.Id) && appointment.EndUtc > DateTime.UtcNow &&
                await db.Doctors.AnyAsync(x => x.Id == appointment.DoctorId && x.AutomaticRecovery) &&
                !await db.ReleasedSlots.AnyAsync(x => x.SourceAppointmentId == appointment.Id && x.DoctorId == appointment.DoctorId && x.StartUtc == appointment.StartUtc && x.EndUtc == appointment.EndUtc))
                db.ReleasedSlots.Add(new() { SourceAppointmentId = appointment.Id, DoctorId = appointment.DoctorId, StartUtc = appointment.StartUtc, EndUtc = appointment.EndUtc });
        }
        appointment.PatientId = patient.Id; appointment.DoctorId = d.Id; appointment.StartUtc = start; appointment.EndUtc = end;
        appointment.Status = AppointmentStatus.Pending; appointment.AdministrativeNote = a.Role == Roles.Patient ? null : form.Note?.Trim(); appointment.ChangeRequest = null;
        appointment.ConfirmationDueUtc = d.AutomaticRecovery ? AppointmentRecovery.ConfirmationDeadline(start, DateTime.UtcNow, d.ConfirmationHours, d.LastMinuteConfirmationMinutes) : null;
        appointment.ReminderQueuedUtc = null; appointment.ConfirmationReminderStage = 0;
        if (form.Id == 0) db.Appointments.Add(appointment);
        db.AppointmentHistory.Add(new() { Appointment = appointment, ActorId = a.Id, Action = form.Id == 0 ? "Cita creada" : $"Reprogramada para {form.Date:yyyy-MM-dd} {form.Time:h.mm tt}" });
        Audit(db, a, form.Id == 0 ? "Cita creada" : $"Cita reprogramada #{form.Id}");
        Notify(db, [d.UserId], form.Id == 0 ? "Nueva cita programada por la oficina. Consulta tu agenda." : "Una cita fue reprogramada. Consulta tu agenda.");
        await db.SaveChangesAsync();
        AppointmentRecovery.PatientNotice(db, patient, $"La oficina {(form.Id == 0 ? "programó" : "reprogramó")} tu cita para {clock.Local(start):dd/MM h.mm tt}. Confirma antes del plazo indicado en el correo.", appointment.Id, true);
        if (d.AutomaticRecovery && appointment.ConfirmationDueUtc <= DateTime.UtcNow.AddHours(24)) appointment.ReminderQueuedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(); await transaction.CommitAsync();
    }

    public async Task ChangeStatusAsync(int id, byte[] version, AppointmentStatus status, string? reason = null)
    {
        var a = await ActorAsync(); await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await LockAsync(db, $"{db.Model.GetDefaultSchema()}:recovery");
        var item = await Scope(db.Appointments, a).Include(x => x.Patient).Include(x => x.Doctor).SingleOrDefaultAsync(x => x.Id == id) ?? throw new UnauthorizedAccessException();
        var old = item.Status;
        var allowed = status switch
        {
            AppointmentStatus.Confirmed => old == AppointmentStatus.Pending && (a.IsStaff || a.Role is Roles.Patient or Roles.Doctor),
            AppointmentStatus.Waiting => old is AppointmentStatus.Pending or AppointmentStatus.Confirmed && (a.IsStaff || a.Role == Roles.Doctor),
            AppointmentStatus.InConsultation => old == AppointmentStatus.Waiting && a.Role is Roles.Doctor or Roles.Admin,
            AppointmentStatus.Completed => old == AppointmentStatus.InConsultation && a.Role is Roles.Doctor or Roles.Admin,
            AppointmentStatus.Cancelled =>
                (old is AppointmentStatus.Pending or AppointmentStatus.Confirmed && a.Role is Roles.Admin or Roles.Reception or Roles.Patient or Roles.Doctor) ||
                (old == AppointmentStatus.Waiting && a.Role is Roles.Admin or Roles.Reception or Roles.Doctor) ||
                (old == AppointmentStatus.InConsultation && a.Role is Roles.Admin or Roles.Doctor),
            AppointmentStatus.NoShow => old is AppointmentStatus.Pending or AppointmentStatus.Confirmed && a.Role is Roles.Admin or Roles.Reception or Roles.Doctor && item.EndUtc < DateTime.UtcNow,
            _ => false
        };
        if (!allowed) throw new ClinicException("La acción no está permitida para este estado o tu rol.");
        if (status == AppointmentStatus.Confirmed && item.ConfirmationDueUtc <= DateTime.UtcNow) throw new ClinicException("El plazo de confirmación terminó. Actualiza tu agenda para consultar las alternativas.");
        if (status == AppointmentStatus.Waiting && DateOnly.FromDateTime(clock.Local(item.StartUtc)) != clock.Today) throw new ClinicException("La llegada solo puede registrarse el día de la cita.");
        if (a.Role == Roles.Patient && item.StartUtc <= DateTime.UtcNow) throw new ClinicException("Contacta a recepción para gestionar una cita que ya comenzó.");
        if (status == AppointmentStatus.Cancelled && (string.IsNullOrWhiteSpace(reason) || reason.Length > 300)) throw new ClinicException("Indica un motivo administrativo de hasta 300 caracteres.");
        db.Entry(item).Property(x => x.Version).OriginalValue = version;
        item.Status = status; if (status == AppointmentStatus.Waiting) item.ArrivalUtc = DateTime.UtcNow;
        db.AppointmentHistory.Add(new() { AppointmentId = id, ActorId = a.Id, Action = $"{StatusText.Label(old)} → {StatusText.Label(status)}{(status == AppointmentStatus.Cancelled ? ": " + (old is AppointmentStatus.Waiting or AppointmentStatus.InConsultation ? "Atención interrumpida o abandono registrado: " : "") + reason : "")}" });
        Audit(db, a, $"Estado de cita #{id}: {status}");
        Notify(db, [item.Doctor.UserId], $"El estado de una cita cambió a {StatusText.Label(status).ToLowerInvariant()}. Consulta tu agenda.");
        if (status is AppointmentStatus.Cancelled or AppointmentStatus.Confirmed) AppointmentRecovery.PatientNotice(db, item.Patient, $"La oficina registró tu cita del {clock.Local(item.StartUtc):dd/MM h.mm tt} como {StatusText.Label(status).ToLowerInvariant()}.", item.Id);
        await db.SaveChangesAsync(); await transaction.CommitAsync();
    }

    public async Task RequestChangeAsync(int id, string request)
    {
        var a = await ActorAsync(); if (a.Role != Roles.Patient) throw new UnauthorizedAccessException();
        if (string.IsNullOrWhiteSpace(request) || request.Length > 300) throw new ClinicException("Indica la fecha o el horario que prefieres, hasta 300 caracteres.");
        await using var db = await factory.CreateDbContextAsync();
        var item = await Scope(db.Appointments, a).SingleOrDefaultAsync(x => x.Id == id) ?? throw new UnauthorizedAccessException();
        if (item.StartUtc <= DateTime.UtcNow || item.Status is not (AppointmentStatus.Pending or AppointmentStatus.Confirmed)) throw new ClinicException("Esta cita ya no admite solicitudes de cambio.");
        item.ChangeRequest = request.Trim(); db.AppointmentHistory.Add(new() { AppointmentId = id, ActorId = a.Id, Action = "Solicitud de reprogramación recibida" });
        var staff = await (from u in db.Users join ur in db.UserRoles on u.Id equals ur.UserId join r in db.Roles on ur.RoleId equals r.Id where u.IsActive && (r.Name == Roles.Admin || r.Name == Roles.Reception) select u.Id).ToListAsync();
        Notify(db, staff, "Hay una solicitud de reprogramación por revisar en Citas."); await db.SaveChangesAsync();
    }

    public async Task<List<AppointmentHistory>> HistoryAsync(int id)
    {
        var a = await ActorAsync(); await using var db = await factory.CreateDbContextAsync();
        if (!await Scope(db.Appointments, a).AnyAsync(x => x.Id == id)) throw new UnauthorizedAccessException();
        return await db.AppointmentHistory.AsNoTracking().Where(x => x.AppointmentId == id).OrderByDescending(x => x.CreatedUtc).Take(50).ToListAsync();
    }

    public async Task AddScheduleAsync(int doctorId, DayOfWeek day, TimeOnly start, TimeOnly end)
    {
        var a = await ActorAsync(); ManageAgenda(a, doctorId);
        if (!Enum.IsDefined(day) || start >= end) throw new ClinicException("Revisa el día y las horas del horario.");
        await using var db = await factory.CreateDbContextAsync(); await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        if (!await db.Doctors.AnyAsync(x => x.Id == doctorId)) throw new ClinicException("Médico no encontrado.");
        if (await db.DoctorSchedules.AnyAsync(x => x.DoctorId == doctorId && x.Day == day && x.Start < end && x.End > start)) throw new ClinicException("Este horario se superpone con uno existente.");
        db.DoctorSchedules.Add(new() { DoctorId = doctorId, Day = day, Start = start, End = end });
        Audit(db, a, $"Horario agregado al médico #{doctorId}"); await db.SaveChangesAsync(); await transaction.CommitAsync();
    }

    public async Task RemoveScheduleAsync(int id)
    {
        var a = await ActorAsync(); await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var schedule = await db.DoctorSchedules.FindAsync(id) ?? throw new ClinicException("Horario no encontrado.");
        ManageAgenda(a, schedule.DoctorId);
        var future = await db.Appointments.Where(x => x.DoctorId == schedule.DoctorId && x.StartUtc > DateTime.UtcNow && x.Status != AppointmentStatus.Cancelled && x.Status != AppointmentStatus.NoShow).ToListAsync();
        if (future.Any(x => clock.Local(x.StartUtc).DayOfWeek == schedule.Day && TimeOnly.FromDateTime(clock.Local(x.StartUtc)) >= schedule.Start && TimeOnly.FromDateTime(clock.Local(x.StartUtc)) < schedule.End))
            throw new ClinicException("Hay citas futuras en este horario. Reprográmalas antes de quitarlo.");
        db.DoctorSchedules.Remove(schedule); Audit(db, a, $"Horario retirado #{id}"); await db.SaveChangesAsync(); await transaction.CommitAsync();
    }

    public async Task AddBlockAsync(int doctorId, DateTime localStart, DateTime localEnd, string reason)
    {
        var a = await ActorAsync(); ManageAgenda(a, doctorId); var start = clock.Utc(localStart); var end = clock.Utc(localEnd);
        if (start >= end || end < DateTime.UtcNow || string.IsNullOrWhiteSpace(reason) || reason.Length > 200) throw new ClinicException("Indica un periodo válido y un motivo de hasta 200 caracteres.");
        await using var db = await factory.CreateDbContextAsync(); await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        if (!await db.Doctors.AnyAsync(x => x.Id == doctorId)) throw new ClinicException("Médico no encontrado.");
        if (await db.Appointments.AnyAsync(x => x.DoctorId == doctorId && x.StartUtc < end && x.EndUtc > start && x.Status != AppointmentStatus.Cancelled && x.Status != AppointmentStatus.NoShow)) throw new ClinicException("Hay citas en ese periodo. Reprográmalas antes de bloquearlo.");
        db.ScheduleExceptions.Add(new() { DoctorId = doctorId, StartUtc = start, EndUtc = end, Reason = reason.Trim() });
        Audit(db, a, $"Bloqueo agregado al médico #{doctorId}"); await db.SaveChangesAsync(); await transaction.CommitAsync();
    }

    public async Task<List<ScheduleException>> BlocksAsync(int doctorId)
    {
        var a = await ActorAsync(); if (a.Role == Roles.Patient || (a.Role == Roles.Doctor && a.DoctorId != doctorId)) throw new UnauthorizedAccessException();
        await using var db = await factory.CreateDbContextAsync(); return await db.ScheduleExceptions.AsNoTracking().Where(x => x.DoctorId == doctorId && x.EndUtc >= DateTime.UtcNow).OrderBy(x => x.StartUtc).ToListAsync();
    }

    public async Task RemoveBlockAsync(int id)
    {
        var a = await ActorAsync(); await using var db = await factory.CreateDbContextAsync();
        var block = await db.ScheduleExceptions.FindAsync(id) ?? throw new ClinicException("Bloqueo no encontrado.");
        ManageAgenda(a, block.DoctorId);
        db.ScheduleExceptions.Remove(block); Audit(db, a, $"Bloqueo retirado #{id}"); await db.SaveChangesAsync();
    }

    public async Task<List<(ApplicationUser User, string Role)>> UsersAsync()
    {
        var a = await ActorAsync(); Admin(a); await using var db = await factory.CreateDbContextAsync();
        var rows = await (from u in db.Users.AsNoTracking() join ur in db.UserRoles on u.Id equals ur.UserId join r in db.Roles on ur.RoleId equals r.Id where r.Name != Roles.Patient orderby u.FullName select new { User = u, Role = r.Name }).ToListAsync();
        return rows.Select(x => (x.User, x.Role!)).ToList();
    }

    public async Task<string> PasswordSetupLinkAsync(string id)
    {
        var actor = await ActorAsync(); Admin(actor);
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(id) ?? throw new ClinicException("Cuenta no encontrada.");
        var link = await StaffPasswordAccess.CreateLinkAsync(users, user, scope.ServiceProvider.GetRequiredService<IConfiguration>());
        await using var db = await factory.CreateDbContextAsync();
        Audit(db, actor, $"Enlace privado de contraseña emitido para {id}"); await db.SaveChangesAsync();
        return link;
    }

    public async Task CreateStaffAsync(StaffForm form)
    {
        Validate(form); var a = await ActorAsync(); Admin(a);
        if (form.Role is not (Roles.Admin or Roles.Reception or Roles.Doctor)) throw new ClinicException("Selecciona un rol de personal válido.");
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (form.SpecialtyId.HasValue && !await db.Specialties.AnyAsync(x => x.Id == form.SpecialtyId)) throw new ClinicException("Especialidad no válida.");
        var user = new ApplicationUser { UserName = form.Email.Trim(), Email = form.Email.Trim(), FullName = form.FullName.Trim() };
        var result = await manager.CreateAsync(user, form.Password);
        if (!result.Succeeded) throw new ClinicException("No se pudo crear la cuenta. Revisa si el correo ya existe y usa una contraseña de 12 caracteres con mayúscula, minúscula, número y símbolo.");
        result = await manager.AddToRoleAsync(user, form.Role);
        if (!result.Succeeded) throw new ClinicException("No se pudo asignar el rol.");
        if (form.Role == Roles.Doctor) db.Doctors.Add(new() { UserId = user.Id, FullName = user.FullName, SpecialtyId = form.SpecialtyId });
        Audit(db, a, $"Cuenta de personal creada: {user.Id}"); await db.SaveChangesAsync(); await transaction.CommitAsync();
    }

    public async Task ToggleUserAsync(string id)
    {
        var a = await ActorAsync(); Admin(a); if (id == a.Id) throw new ClinicException("No puedes desactivar tu propia cuenta.");
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(); var user = await manager.FindByIdAsync(id) ?? throw new ClinicException("Cuenta no encontrada.");
        if (user.IsActive && await db.Appointments.AnyAsync(x => (x.Doctor.UserId == id || x.Patient.UserId == id) && x.StartUtc > DateTime.UtcNow && x.Status != AppointmentStatus.Cancelled && x.Status != AppointmentStatus.NoShow)) throw new ClinicException("La cuenta tiene citas futuras. Resuélvelas antes de desactivarla.");
        if (user.IsActive && await manager.IsInRoleAsync(user, Roles.Admin) && await ActiveAdminsAsync(db) <= 1) throw new ClinicException("Debe mantenerse al menos una cuenta administrativa activa.");
        user.IsActive = !user.IsActive;
        var result = await manager.UpdateAsync(user); if (!result.Succeeded) throw new ClinicException("No se pudo actualizar la cuenta.");
        result = await manager.UpdateSecurityStampAsync(user); if (!result.Succeeded) throw new ClinicException("No se pudo invalidar la sesión.");
        Audit(db, a, $"Cuenta {id}: {(user.IsActive ? "activada" : "desactivada")}"); await db.SaveChangesAsync(); await transaction.CommitAsync();
    }

    private static Task<int> ActiveAdminsAsync(ClinicDbContext db) => (from u in db.Users join ur in db.UserRoles on u.Id equals ur.UserId join r in db.Roles on ur.RoleId equals r.Id where u.IsActive && r.Name == Roles.Admin select u).CountAsync();

    public async Task ChangeStaffRoleAsync(string id, string role)
    {
        var a = await ActorAsync(); Admin(a);
        if (a.Id == id || role is not (Roles.Admin or Roles.Reception or Roles.Doctor)) throw new ClinicException("Selecciona otra cuenta de personal y un rol válido.");
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await manager.FindByIdAsync(id) ?? throw new ClinicException("Cuenta no encontrada.");
        var previous = (await manager.GetRolesAsync(user)).Single();
        if (previous == Roles.Patient) throw new ClinicException("Las cuentas de paciente conservan su acceso personal. Crea una cuenta de personal independiente.");
        if (previous == role) return;
        if (previous == Roles.Admin && user.IsActive && await ActiveAdminsAsync(db) <= 1) throw new ClinicException("Debe mantenerse al menos una cuenta administrativa activa.");
        if (previous == Roles.Doctor && await db.Appointments.AnyAsync(x => x.Doctor.UserId == id && x.StartUtc > DateTime.UtcNow && x.Status != AppointmentStatus.Cancelled && x.Status != AppointmentStatus.NoShow)) throw new ClinicException("Reprograma las citas futuras antes de cambiar el rol del médico.");
        if (!(await manager.RemoveFromRoleAsync(user, previous)).Succeeded || !(await manager.AddToRoleAsync(user, role)).Succeeded) throw new ClinicException("No se pudo actualizar el rol.");
        var doctor = await db.Doctors.SingleOrDefaultAsync(x => x.UserId == id);
        if (role == Roles.Doctor) { if (doctor == null) db.Doctors.Add(new() { UserId = id, FullName = user.FullName }); else doctor.IsActive = true; }
        else if (doctor != null) doctor.IsActive = false;
        if (!(await manager.UpdateSecurityStampAsync(user)).Succeeded) throw new ClinicException("No se pudo renovar la seguridad de la cuenta.");
        Audit(db, a, $"Rol de cuenta {id}: {previous} → {role}"); await db.SaveChangesAsync(); await transaction.CommitAsync();
    }

    public async Task<ApplicationUser> ProfileAsync()
    {
        var a = await ActorAsync(); await using var db = await factory.CreateDbContextAsync(); return await db.Users.AsNoTracking().SingleAsync(x => x.Id == a.Id);
    }

    public async Task SaveProfileAsync(ProfileForm form)
    {
        Validate(form); var a = await ActorAsync(); await using var db = await factory.CreateDbContextAsync();
        var user = await db.Users.SingleAsync(x => x.Id == a.Id); user.FullName = form.FullName.Trim(); user.PhoneNumber = form.Phone; user.EmailConsent = form.EmailConsent;
        var patient = await db.Patients.SingleOrDefaultAsync(x => x.UserId == a.Id); if (patient != null) { patient.FullName = user.FullName; patient.Phone = form.Phone; }
        var doctor = await db.Doctors.SingleOrDefaultAsync(x => x.UserId == a.Id); if (doctor != null) { doctor.FullName = user.FullName; doctor.Phone = form.Phone; }
        Audit(db, a, "Perfil actualizado"); await db.SaveChangesAsync();
    }

    public async Task<List<ClinicNotification>> NotificationsAsync()
    {
        var a = await ActorAsync(); await using var db = await factory.CreateDbContextAsync(); return await db.Notifications.AsNoTracking().Where(x => x.UserId == a.Id).OrderByDescending(x => x.CreatedUtc).Take(30).ToListAsync();
    }

    public async Task ReadNotificationsAsync()
    {
        var a = await ActorAsync(); await using var db = await factory.CreateDbContextAsync(); await db.Notifications.Where(x => x.UserId == a.Id && !x.IsRead).ExecuteUpdateAsync(x => x.SetProperty(v => v.IsRead, true));
    }

    private static void Audit(ClinicDbContext db, ClinicActor a, string action) => db.AuditLogs.Add(new() { ActorId = a.Id, Action = action });
    private static void Notify(ClinicDbContext db, IEnumerable<string?> users, string message)
    {
        foreach (var id in users.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()) db.Notifications.Add(new() { UserId = id!, Message = message });
    }
}



