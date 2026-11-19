INSERT INTO email_templates (id, key, name, description, subject, html_body, text_body, is_system, created_at_utc, updated_at_utc)
VALUES
    (
        gen_random_uuid(),
        'welcome-user',
        'Bienvenida de usuario',
        'Correo de bienvenida que se envía cuando se crea una cuenta de usuario. Variables: UserName, AppName.',
        'Te damos la bienvenida a {{AppName}}',
        $$<!DOCTYPE html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Te damos la bienvenida a {{AppName}}</title>
</head>
<body style="margin:0;padding:0;background-color:#f3f4f6;font-family:Arial,Helvetica,sans-serif;">
  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background-color:#f3f4f6;">
    <tr>
      <td align="center" style="padding:24px 12px;">
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:600px;background-color:#ffffff;border-radius:8px;">
          <tr>
            <td style="padding:32px 24px;">
              <p style="margin:0 0 16px 0;font-size:16px;line-height:24px;color:#374151;">Hola {{UserName}},</p>
              <p style="margin:0 0 16px 0;font-size:16px;line-height:24px;color:#374151;">Te damos la bienvenida a <strong>{{AppName}}</strong>. Tu cuenta ya está lista para usarse.</p>
              <p style="margin:0 0 16px 0;font-size:16px;line-height:24px;color:#374151;">Si no creaste esta cuenta, podés ignorar este correo.</p>
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>$$,
        $$Hola {{UserName}},

Te damos la bienvenida a {{AppName}}. Tu cuenta ya está lista para usarse.

Si no creaste esta cuenta, podés ignorar este correo.$$,
        TRUE,
        now(),
        now()
    ),
    (
        gen_random_uuid(),
        'verify-email',
        'Verificación de correo electrónico',
        'Correo con el enlace para verificar la dirección de correo de una cuenta. Variables: UserName, AppName, VerificationUrl, ExpiresInHours.',
        'Verificá tu correo electrónico en {{AppName}}',
        $$<!DOCTYPE html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Verificá tu correo electrónico en {{AppName}}</title>
</head>
<body style="margin:0;padding:0;background-color:#f3f4f6;font-family:Arial,Helvetica,sans-serif;">
  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background-color:#f3f4f6;">
    <tr>
      <td align="center" style="padding:24px 12px;">
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:600px;background-color:#ffffff;border-radius:8px;">
          <tr>
            <td style="padding:32px 24px;">
              <p style="margin:0 0 16px 0;font-size:16px;line-height:24px;color:#374151;">Hola {{UserName}},</p>
              <p style="margin:0 0 16px 0;font-size:16px;line-height:24px;color:#374151;">Para terminar de configurar tu cuenta en <strong>{{AppName}}</strong>, verificá tu correo electrónico.</p>
              <p style="margin:0 0 16px 0;font-size:16px;line-height:24px;color:#374151;">Este enlace vence en {{ExpiresInHours}} horas.</p>
              <p style="margin:24px 0;text-align:center;"><a href="{{VerificationUrl}}" style="display:inline-block;padding:12px 24px;background-color:#2563eb;color:#ffffff;text-decoration:none;border-radius:6px;font-size:16px;font-weight:bold;">Verificar correo</a></p>
              <p style="margin:0 0 16px 0;font-size:14px;line-height:20px;color:#6b7280;">Si el botón no funciona, copiá y pegá este enlace en tu navegador. Si no creaste esta cuenta, podés ignorar este correo.</p>
              <p style="margin:0 0 16px 0;font-size:14px;line-height:20px;color:#6b7280;word-break:break-all;">{{VerificationUrl}}</p>
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>$$,
        $$Hola {{UserName}},

Para terminar de configurar tu cuenta en {{AppName}}, verificá tu correo electrónico abriendo este enlace:

{{VerificationUrl}}

Este enlace vence en {{ExpiresInHours}} horas.

Si no creaste esta cuenta, podés ignorar este correo.$$,
        TRUE,
        now(),
        now()
    ),
    (
        gen_random_uuid(),
        'reset-password',
        'Restablecimiento de contraseña',
        'Correo con el enlace para restablecer la contraseña de una cuenta. Variables: UserName, AppName, ResetUrl, ExpiresInMinutes.',
        'Restablecé tu contraseña de {{AppName}}',
        $$<!DOCTYPE html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Restablecé tu contraseña de {{AppName}}</title>
</head>
<body style="margin:0;padding:0;background-color:#f3f4f6;font-family:Arial,Helvetica,sans-serif;">
  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background-color:#f3f4f6;">
    <tr>
      <td align="center" style="padding:24px 12px;">
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:600px;background-color:#ffffff;border-radius:8px;">
          <tr>
            <td style="padding:32px 24px;">
              <p style="margin:0 0 16px 0;font-size:16px;line-height:24px;color:#374151;">Hola {{UserName}},</p>
              <p style="margin:0 0 16px 0;font-size:16px;line-height:24px;color:#374151;">Recibimos una solicitud para restablecer la contraseña de tu cuenta en <strong>{{AppName}}</strong>.</p>
              <p style="margin:0 0 16px 0;font-size:16px;line-height:24px;color:#374151;">Este enlace vence en {{ExpiresInMinutes}} minutos.</p>
              <p style="margin:24px 0;text-align:center;"><a href="{{ResetUrl}}" style="display:inline-block;padding:12px 24px;background-color:#2563eb;color:#ffffff;text-decoration:none;border-radius:6px;font-size:16px;font-weight:bold;">Restablecer contraseña</a></p>
              <p style="margin:0 0 16px 0;font-size:14px;line-height:20px;color:#6b7280;">Si el botón no funciona, copiá y pegá este enlace en tu navegador. Si no solicitaste el cambio, podés ignorar este correo: tu contraseña actual no se modifica.</p>
              <p style="margin:0 0 16px 0;font-size:14px;line-height:20px;color:#6b7280;word-break:break-all;">{{ResetUrl}}</p>
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>$$,
        $$Hola {{UserName}},

Recibimos una solicitud para restablecer la contraseña de tu cuenta en {{AppName}}. Para continuar, abrí este enlace:

{{ResetUrl}}

Este enlace vence en {{ExpiresInMinutes}} minutos.

Si no solicitaste el cambio, podés ignorar este correo: tu contraseña actual no se modifica.$$,
        TRUE,
        now(),
        now()
    ),
    (
        gen_random_uuid(),
        'delivery-failed-alert',
        'Alerta de entrega fallida',
        'Alerta operativa interna que se envía al dueño de la aplicación cuando una notificación agota sus reintentos. Variables: OriginalTo, OriginalSubject, NotificationId, Attempts, LastError.',
        'Falló la entrega de la notificación {{NotificationId}}',
        $$<!DOCTYPE html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Falló la entrega de un correo</title>
</head>
<body style="margin:0;padding:0;background-color:#f3f4f6;font-family:Arial,Helvetica,sans-serif;">
  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background-color:#f3f4f6;">
    <tr>
      <td align="center" style="padding:24px 12px;">
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:600px;background-color:#ffffff;border-radius:8px;">
          <tr>
            <td style="padding:32px 24px;">
              <p style="margin:0 0 16px 0;font-size:16px;line-height:24px;color:#374151;">Una notificación no pudo entregarse después de agotar los reintentos.</p>
              <p style="margin:0 0 12px 0;font-size:15px;line-height:22px;color:#374151;"><strong>Destinatario original:</strong> {{OriginalTo}}</p>
              <p style="margin:0 0 12px 0;font-size:15px;line-height:22px;color:#374151;"><strong>Asunto original:</strong> {{OriginalSubject}}</p>
              <p style="margin:0 0 12px 0;font-size:15px;line-height:22px;color:#374151;"><strong>Id de notificación:</strong> {{NotificationId}}</p>
              <p style="margin:0 0 12px 0;font-size:15px;line-height:22px;color:#374151;"><strong>Intentos:</strong> {{Attempts}}</p>
              <p style="margin:0 0 12px 0;font-size:15px;line-height:22px;color:#374151;white-space:pre-wrap;word-break:break-word;"><strong>Último error:</strong> {{LastError}}</p>
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>$$,
        $$Una notificación no pudo entregarse después de agotar los reintentos.

Destinatario original: {{OriginalTo}}
Asunto original: {{OriginalSubject}}
Id de notificación: {{NotificationId}}
Intentos: {{Attempts}}
Último error: {{LastError}}$$,
        TRUE,
        now(),
        now()
    )
ON CONFLICT (key) DO NOTHING;
