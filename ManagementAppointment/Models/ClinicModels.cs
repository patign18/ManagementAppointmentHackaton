using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace ManagementAppointment.Models;

public static class Roles
{
    public const string Admin = "Administrador";
    public const string Reception = "Secretaria";
    public const string Doctor = "Doctor";
    public const string Patient = "Paciente";
    public static readonly string[] All = [Admin, Reception, Doctor, Patient];
    public const string Staff = Admin + "," + Reception + "," + Doctor;
}

public class ApplicationUser : IdentityUser
{
    [MaxLength(120)] public string FullName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool EmailConsent { get; set; }
}

public class Patient
{
    public int Id { get; set; }
    [MaxLength(450)] public string? UserId { get; set; }
    public ApplicationUser? User { get; set; }
    [Required, MaxLength(120)] public string FullName { get; set; } = "";
    [MaxLength(40)] public string? DocumentNumber { get; set; }
    public DateOnly? BirthDate { get; set; }
    [MaxLength(254)] public string? Email { get; set; }
    [MaxLength(30)] public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
    public int? RegisteredByDoctorId { get; set; }
    [MaxLength(200)] public string Address { get; set; } = "";
    [MaxLength(80)] public string Municipality { get; set; } = "";
    [MaxLength(12)] public string PostalCode { get; set; } = "";
    public bool ContactConsent { get; set; }
    public bool AutomaticReassignmentConsent { get; set; }
    public bool IsDemo { get; set; }
}

public class Specialty
{
    public int Id { get; set; }
    [Required, MaxLength(80)] public string Name { get; set; } = "";
}

public class Doctor
{
    public int Id { get; set; }
    [Required, MaxLength(450)] public string UserId { get; set; } = "";
    public ApplicationUser User { get; set; } = null!;
    public int? SpecialtyId { get; set; }
    public Specialty? Specialty { get; set; }
    [Required, MaxLength(120)] public string FullName { get; set; } = "";
    [MaxLength(30)] public string? Phone { get; set; }
    public int ConsultationMinutes { get; set; } = 30;
    public bool IsActive { get; set; } = true;
    public bool CanManageAppointments { get; set; } = true;
    public bool AutomaticRecovery { get; set; }
    public int ConfirmationHours { get; set; } = 48;
    public int ArrivalGraceMinutes { get; set; } = 15;
    public int LastMinuteConfirmationMinutes { get; set; } = 15;
    public int MinimumRecoveredMinutes { get; set; } = 10;
    public List<DoctorSchedule> Schedules { get; set; } = [];
}

public class DoctorSchedule
{
    public int Id { get; set; }
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    public DayOfWeek Day { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
}

public class ScheduleException
{
    public int Id { get; set; }
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    [MaxLength(200)] public string Reason { get; set; } = "";
}

public enum AppointmentStatus { Pending, Confirmed, Waiting, InConsultation, Completed, Cancelled, NoShow }

public class Appointment
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public AppointmentStatus Status { get; set; }
    [MaxLength(300)] public string? AdministrativeNote { get; set; }
    [MaxLength(300)] public string? ChangeRequest { get; set; }
    public DateTime? ArrivalUtc { get; set; }
    public DateTime? ConfirmationDueUtc { get; set; }
    public DateTime? ReminderQueuedUtc { get; set; }
    public int ConfirmationReminderStage { get; set; }
    [Timestamp] public byte[] Version { get; set; } = [];
}

public class AppointmentHistory
{
    public long Id { get; set; }
    public int AppointmentId { get; set; }
    public Appointment Appointment { get; set; } = null!;
    [Required, MaxLength(450)] public string ActorId { get; set; } = "";
    [MaxLength(500)] public string Action { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class ClinicNotification
{
    public long Id { get; set; }
    [Required, MaxLength(450)] public string UserId { get; set; } = "";
    public ApplicationUser User { get; set; } = null!;
    [MaxLength(180)] public string Message { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public bool IsRead { get; set; }
}

public class AuditLog
{
    public long Id { get; set; }
    [MaxLength(450)] public string ActorId { get; set; } = "";
    [MaxLength(150)] public string Action { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public enum WaitlistStatus { Active, Assigned, Withdrawn, Expired }

public class WaitlistEntry
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    public DateTime FromUtc { get; set; }
    public DateTime UntilUtc { get; set; }
    public int MinimumNoticeMinutes { get; set; } = 30;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public WaitlistStatus Status { get; set; }
    public int? AppointmentToAdvanceId { get; set; }
    public Appointment? AppointmentToAdvance { get; set; }
    [Timestamp] public byte[] Version { get; set; } = [];
}

public class ReleasedSlot
{
    public int Id { get; set; }
    public int SourceAppointmentId { get; set; }
    public Appointment SourceAppointment { get; set; } = null!;
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public bool Closed { get; set; }
}

public class SlotAssignment
{
    public int Id { get; set; }
    public int ReleasedSlotId { get; set; }
    public ReleasedSlot ReleasedSlot { get; set; } = null!;
    public int WaitlistEntryId { get; set; }
    public WaitlistEntry WaitlistEntry { get; set; } = null!;
    public int AppointmentId { get; set; }
    public Appointment Appointment { get; set; } = null!;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class ClinicMail
{
    public long Id { get; set; }
    [MaxLength(450)] public string UserId { get; set; } = "";
    [MaxLength(180)] public string Message { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public int Attempts { get; set; }
    public int? PatientId { get; set; }
    public int? AppointmentId { get; set; }
    public bool IncludeResponseLinks { get; set; }
    public bool Suppressed { get; set; }
    public DateTime? AppointmentStartUtc { get; set; }
}

public static class StatusText
{
    public static string Label(AppointmentStatus status) => status switch
    {
        AppointmentStatus.Pending => "Pendiente", AppointmentStatus.Confirmed => "Confirmada",
        AppointmentStatus.Waiting => "En espera", AppointmentStatus.InConsultation => "En consulta",
        AppointmentStatus.Completed => "Completada", AppointmentStatus.Cancelled => "Cancelada",
        _ => "No asistió"
    };
    public static string Css(AppointmentStatus status) => status.ToString().ToLowerInvariant();
}

