using ManagementAppointment.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ManagementAppointment.Components.Shared;

public abstract class ClinicPageBase : ComponentBase
{
    [Inject] protected ClinicService Clinic { get; set; } = null!;
    [Inject] protected OfficeClock Clock { get; set; } = null!;
    [Inject] protected ILogger<ClinicPageBase> Logger { get; set; } = null!;
    protected string? Error;
    protected string? Success;
    protected bool Busy;
    protected bool Loading = true;
    protected async Task Run(Func<Task> action, string? success = null)
    {
        if (Busy) return;
        Busy = true; Error = null; Success = null;
        try { await action(); Success = success; }
        catch (DbUpdateConcurrencyException) { Error = "Otra persona actualizó este registro. Recarga los datos antes de continuar."; }
        catch (UnauthorizedAccessException) { Error = "No tienes permiso para realizar esta acción. Vuelve a iniciar sesión si tu acceso cambió."; }
        catch (ClinicException ex) { Error = ex.Message; }
        catch (InvalidOperationException) { Error = "No pudimos cargar la información. Actualiza la página o contacta a la administración."; Logger.LogWarning("Operación de aplicación fallida."); }
        catch (Exception ex) when (ex is SqlException or DbUpdateException)
        {
            Error = "No pudimos completar la operación. Revisa la conexión y vuelve a cargar los datos.";
            Logger.LogWarning("Operación de datos fallida: {Type}", ex.GetType().Name);
        }
        finally { Busy = false; Loading = false; }
    }
}
