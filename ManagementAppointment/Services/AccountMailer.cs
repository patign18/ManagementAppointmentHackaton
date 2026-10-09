using System.Net;
using System.Net.Mail;

namespace ManagementAppointment.Services;

public class AccountMailer(IConfiguration configuration, ILogger<AccountMailer> logger)
{
    public bool Enabled => configuration.GetValue<bool>("Mail:Enabled");
    public async Task SendRecoveryAsync(string recipient, string link)
    {
        if (!Enabled) return;
        try
        {
            using var client = new SmtpClient(configuration["Mail:Host"], configuration.GetValue("Mail:Port", 587))
            {
                EnableSsl = true, Credentials = new NetworkCredential(configuration["Mail:Username"], configuration["Mail:Password"])
            };
            using var message = new MailMessage(configuration["Mail:From"]!, recipient)
            {
                Subject = "Recupera tu acceso a ManagementAppointment",
                Body = $"Solicitaste restablecer tu contraseña. Abre este enlace en las próximas 2 horas:\n{link}\n\nSi no hiciste esta solicitud, puedes ignorar el mensaje."
            };
            await client.SendMailAsync(message);
        }
        catch (Exception ex) when (ex is SmtpException or ArgumentException or InvalidOperationException)
        {
            logger.LogWarning("No fue posible entregar el correo de recuperación. Revisa la configuración del servicio de correo.");
        }
    }
}
