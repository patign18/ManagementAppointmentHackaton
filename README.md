# ManagementAppointment · Oficina médica

Gestión de pacientes y citas para doctores y personal de oficina. .NET 10, Blazor Interactive Server, ASP.NET Core Identity y SQL Server. Interfaz en español y horarios de Puerto Rico (`America/Puerto_Rico`).

## Acceso y ejecución

La web es exclusiva para médicos, secretarias y administradores. No existe registro público ni portal de pacientes. Las rutas antiguas `/portal` y `/registro` conducen al login. Las cuentas con rol Paciente no pueden iniciar sesión ni usar los servicios de la oficina, aunque un perfil conserve una contraseña protegida en Identity.

En Visual Studio, detén la ejecución anterior con Shift+F5 antes de recompilar. Selecciona el perfil `https` e inicia con F5. Desde la raíz:

```powershell
dotnet restore --configfile NuGet.Config --packages .packages
dotnet run --project ManagementAppointment --launch-profile https
```

Abre `https://localhost:7246/login`. La conexión `ConnectionStrings:TicketSystem` se obtiene de la configuración, los secretos de usuario o el archivo privado `appsettings.Local.json`. Este archivo está excluido de Git y de publicación. En otra máquina, configura la conexión mediante secretos de usuario o `ConnectionStrings__TicketSystem`.

## Modelo solicitado: 36 pacientes de Puerto Rico

Ya se creó una oficina de demostración con:

- Doctora: **Dra. Patricia Rivera**, correo de acceso `doctor@clinica.local`.
- 36 perfiles ficticios identificados como `PR-DEMO-01` a `PR-DEMO-36`.
- Nombres, nacimiento, dirección, municipio, código postal, teléfono y correo diferentes. Hay perfiles de San Juan, Bayamón, Caguas, Ponce, Mayagüez, Humacao, Fajardo, Arecibo, Aguadilla, Dorado, Toa Baja y Río Grande.
- Contraseñas iniciales distintas, guardadas con hashing de Identity. Estas contraseñas no dan acceso web a los pacientes.
- 24 citas iniciales: 16 confirmadas y 8 pendientes. Se distribuyen desde el siguiente día laborable en cuatro mañanas de consulta.
- 12 pacientes en hold sin cita y 24 pacientes programados que también autorizan adelantos.
- Horarios de lunes a viernes, 09:00–12:00 y 13:00–16:00, con consultas de 30 minutos.
- Recuperación automática activada en la agenda de demostración, confirmación 24 horas antes y tolerancia de llegada de 15 minutos.

Las credenciales de la doctora y los 36 perfiles están en **`LocalDemo/pacientes-puerto-rico.csv`**, un archivo privado excluido de Git y situado fuera del proyecto publicable. La primera fila contiene el acceso de la doctora. No compartas ese archivo al publicar la aplicación. Los nombres y direcciones son ficticios; los correos usan `example.invalid` y los teléfonos son de demostración. Nunca se envían correos a perfiles marcados DEMO.

Para crear el modelo en una instalación nueva, ejecuta una sola vez:

```powershell
dotnet run --project ManagementAppointment --no-launch-profile -- --clinic-demo ../LocalDemo/pacientes-puerto-rico.csv
```

La ruta es relativa al directorio desde el que se ejecuta el proceso. Si corres desde la raíz del repositorio, utiliza `LocalDemo/pacientes-puerto-rico.csv`. El comando no duplica un modelo existente ni cambia contraseñas previas. Los datos de otros usuarios y pacientes se preservan.

## Flujo del doctor

1. **Resumen:** número de pacientes, citas futuras, confirmaciones pendientes y disponibilidad en hold. Se actualiza cada 15 segundos.
2. **Pacientes:** perfiles, direcciones, contacto, próxima cita e historial. El doctor puede programar una cita o agregar el paciente a hold desde su registro. Los doctores ven pacientes de su consulta; administración y recepción ven la oficina completa.
3. **Citas y calendario:** programar, confirmar respuestas, registrar llegadas, cancelar con motivo, reprogramar e iniciar o finalizar atención.
4. **Agenda de la oficina:** publicar intervalos semanales, bloquear ausencias y cambiar las reglas de recuperación de la agenda propia.
5. **Hold y reasignaciones:** el personal registra disponibilidad, tiempo de traslado y, opcionalmente, una cita posterior que pueda adelantarse. La autorización de reasignación y contacto se registra en el perfil del paciente.

La lista de hold no es la sala de espera física. **Sala de espera** muestra pacientes cuya llegada fue registrada. Si alguien se retira después de llegar, el personal utiliza **Registrar abandono y liberar espacio** e indica el motivo.

## Reasignación automática

Mientras la aplicación está ejecutándose, un proceso revisa la agenda cada 30 segundos:

- Envía recordatorios de confirmación durante las 24 horas anteriores al vencimiento; con la política inicial, el plazo vence 24 horas antes de la cita.
- Cancela reservas pendientes cuando vence su confirmación. Las reservas creadas dentro del plazo reciben una tolerancia configurable, sin superar el inicio.
- Marca ausencia cuando se supera la tolerancia de llegada sin que se haya registrado llegada. No interrumpe citas en sala de espera o en consulta.
- Recupera espacios de cancelaciones, ausencias, abandonos registrados y reprogramaciones.
- Busca pacientes activos en la cola del mismo médico, por orden de entrada, respetando su disponibilidad, autorización y aviso mínimo de traslado.
- Si el paciente tiene una cita posterior elegible, adelanta esa cita y libera su horario anterior. No duplica su cita.
- Si estaba en hold sin cita, crea una cita confirmada y retira su entrada activa de la cola.
- Conserva el historial de cada movimiento y registra la relación entre la cita original, el espacio liberado y la nueva asignación.
- Solo aprovecha el tiempo restante de una cita ya empezada si alcanza para el traslado y el mínimo de atención configurado. Si no hay candidatos, el espacio permanece libre.

La inscripción en hold registra autorización previa para la asignación directa. Las citas recuperadas quedan confirmadas por esa autorización. El personal registra llegadas a tiempo: el sistema no puede detectar por sí solo la presencia o salida física de una persona.

## Avisos externos y respuestas sin cuenta

Para establecer o recuperar la contraseña de personal sin SMTP, administración utiliza **Usuarios y acceso → Crear / cambiar contraseña** y entrega el enlace privado a la persona. El formulario también permite pegar el correo y código del enlace desde **Crear o cambiar mi contraseña** en el login. Los enlaces vencen en 2 horas y se invalidan al guardar la contraseña; el restablecimiento elimina el bloqueo por intentos fallidos. También se puede generar un enlace desde el equipo local con `--clinic-password-link correo ruta.txt`. Guarda la salida dentro de `LocalDemo`, que está excluido de Git, y comparte el enlace de manera privada.

SMTP está deshabilitado en la configuración actual. La demostración funciona y registra los cambios en el panel; los perfiles DEMO nunca reciben mensajes. La asignación a pacientes reales requiere correo habilitado, correo registrado y autorización de contacto y reasignación.

Configura en secretos de usuario o variables de entorno:

- `Mail:Enabled=true`
- `Mail:Host`, `Mail:Port`, `Mail:Username`, `Mail:Password`, `Mail:From`
- `Office:PublicUrl`: URL HTTPS accesible para los destinatarios, no localhost.

Los correos se guardan en una cola persistente. Se reintentan hasta 5 veces con al menos 5 minutos entre intentos. Se descartan mensajes obsoletos, contactos sin autorización y avisos de horarios que ya cambiaron. Los fallos de entrega se registran sin exponer credenciales.

El correo permite confirmar o cancelar con un enlace protegido y con vencimiento. Ese enlace muestra únicamente la acción de respuesta: no es un portal, no permite registrarse ni muestra el listado de pacientes. Abrir un enlace no modifica la cita; hace falta enviar el formulario. Las respuestas actualizan directamente la agenda del doctor y disparan la recuperación cuando corresponde.

No está configurado un proveedor de SMS.

## Actualización de base

```powershell
dotnet run --project ManagementAppointment --no-launch-profile -- --clinic-upgrade
```

El comando aplica el script incremental de `ma`, agrega campos y tablas, y mantiene los roles. No elimina pacientes, citas ni usuarios. El arranque normal no modifica el esquema ni vuelve a generar datos ficticios. Una base nueva se prepara con `--clinic-init` y la primera administración con `Initialize-Admin.ps1`.

Las operaciones de reserva y reasignación comparten transacciones y bloqueos SQL por doctor, día y paciente. La automatización utiliza un bloqueo adicional entre instancias. `rowversion` detecta cambios hechos desde pantallas antiguas. Las fechas se guardan en UTC y se muestran en la zona de la oficina.

El proyecto `ManagementAppointment.Verification` conserva escenarios históricos del antiguo portal y necesita adaptarse antes de usarse para validar este flujo de oficina. No se ejecutó durante este cambio.


## Tres avisos automáticos
Avisos 7, 5 y 3 días antes; vencimiento 2 días antes para conceder 24 horas tras el último. Confirmar detiene los avisos; cancelar libera inmediatamente. Las reservas tardías reciben el margen de último momento. Tras una interrupción se registra solo la etapa vigente. SMTP debe estar habilitado para enviar a pacientes reales; los perfiles demo no reciben correos.

## Casos de demostración por estado
El comando `--clinic-demo-scenarios` agrega 16 citas identificadas con DEMO-CASOS, usando los perfiles ficticios existentes: dos pendientes, confirmadas, en espera, en consulta, completadas, canceladas y ausencias, más dos pendientes con plazo vencido. Espera y consulta se sitúan en la fecha de carga; completadas y ausencias son históricas. Las cancelaciones futuras y plazos vencidos se entregan al proceso real de recuperación. En el calendario usa Todos y la vista Día para ver más de tres citas. La actividad conserva el estado inicial y la liberación/asignación posterior. Una ausencia histórica no se reasigna porque el espacio ya pasó. El generador no duplica las citas etiquetadas en ejecuciones posteriores. Las fechas del conjunto permanecen como registro de la demostración y no se desplazan cada día.
## Filtros y trazabilidad en calendario
Las etiquetas de estado inferiores filtran el periodo visible. Pulsar otra vez la misma etiqueta o Ver todas restaura todos los estados. Cada etiqueta muestra el total del periodo considerando médico y especialidad. En vista mensual filtrada se muestran todas las coincidencias del día. El detalle de una cancelación muestra su liberación automática y la asignación persistida: paciente destinatario, número de cita, espacio original y fecha de asignación. Si no existe asignación registrada, informa que aún no la hay. `--clinic-demo-report` consulta los ejemplos cancelados y su destino sin modificar datos.
## Plantilla de configuración y credenciales
Comparte `ManagementAppointment/appsettings.Template.json`: contiene la estructura de configuración y campos vacíos para SQL y SMTP. Para una instalación nueva, copia la plantilla como `appsettings.json` dentro del proyecto y completa la configuración en esa copia, en secretos de usuario o en variables de entorno. La plantilla no se carga automáticamente. Los archivos `appsettings*.json` están excluidos de Git excepto `appsettings.Template.json`; la configuración local existente se conserva. Si un archivo privado ya fue versionado, `.gitignore` no lo retira del índice ni del historial: hay que retirarlo del índice y cambiar cualquier credencial que se haya publicado.