using System.ComponentModel.DataAnnotations;

namespace ManagementAppointment.Models;

public class PatientForm
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Escribe el nombre completo."), StringLength(120, ErrorMessage = "Usa hasta 120 caracteres.")] public string FullName { get; set; } = "";
    [StringLength(40, ErrorMessage = "Usa hasta 40 caracteres.")] public string? DocumentNumber { get; set; }
    public DateOnly? BirthDate { get; set; }
    [EmailAddress(ErrorMessage = "Revisa el correo electrónico."), StringLength(254, ErrorMessage = "Usa hasta 254 caracteres.")] public string? Email { get; set; }
    [Phone(ErrorMessage = "Revisa el teléfono."), StringLength(30, ErrorMessage = "Usa hasta 30 caracteres.")] public string? Phone { get; set; }
    [StringLength(200)] public string Address { get; set; } = "";
    [StringLength(80)] public string Municipality { get; set; } = "";
    [StringLength(12)] public string PostalCode { get; set; } = "";
    public bool ContactConsent { get; set; }
    public bool AutomaticReassignmentConsent { get; set; }
}

public class StaffForm
{
    [Required(ErrorMessage = "Completa este campo."), StringLength(120, ErrorMessage = "Usa hasta 120 caracteres.")] public string FullName { get; set; } = "";
    [Required(ErrorMessage = "Completa este campo."), EmailAddress(ErrorMessage = "Revisa el correo electrónico."), StringLength(254, ErrorMessage = "Usa hasta 254 caracteres.")] public string Email { get; set; } = "";
    [Required(ErrorMessage = "Completa este campo."), MinLength(12, ErrorMessage = "Usa una contraseña de al menos 12 caracteres."), StringLength(128, ErrorMessage = "Usa hasta 128 caracteres.")] public string Password { get; set; } = "";
    [Required(ErrorMessage = "Completa este campo.")] public string Role { get; set; } = Roles.Reception;
    public int? SpecialtyId { get; set; }
}

public class AppointmentForm
{
    public int Id { get; set; }
    [Range(1, int.MaxValue, ErrorMessage = "Selecciona un paciente.")] public int PatientId { get; set; }
    [Range(1, int.MaxValue, ErrorMessage = "Selecciona un médico.")] public int DoctorId { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly Time { get; set; }
    [StringLength(300, ErrorMessage = "Usa hasta 300 caracteres.")] public string? Note { get; set; }
    public byte[] Version { get; set; } = [];
}

public class DoctorForm
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Completa este campo."), StringLength(120, ErrorMessage = "Usa hasta 120 caracteres.")] public string FullName { get; set; } = "";
    [Phone(ErrorMessage = "Revisa el teléfono."), StringLength(30, ErrorMessage = "Usa hasta 30 caracteres.")] public string? Phone { get; set; }
    public int? SpecialtyId { get; set; }
    [Range(10, 180, ErrorMessage = "La consulta debe durar entre 10 y 180 minutos.")] public int ConsultationMinutes { get; set; } = 30;
    public bool CanManageAppointments { get; set; }
}

public class ProfileForm
{
    [Required(ErrorMessage = "Completa este campo."), StringLength(120, ErrorMessage = "Usa hasta 120 caracteres.")] public string FullName { get; set; } = "";
    [Phone(ErrorMessage = "Revisa el teléfono."), StringLength(30, ErrorMessage = "Usa hasta 30 caracteres.")] public string? Phone { get; set; }
    public bool EmailConsent { get; set; }
}

