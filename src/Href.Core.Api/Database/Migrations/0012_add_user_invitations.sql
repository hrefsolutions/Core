ALTER TABLE user_tokens DROP CONSTRAINT ck_user_tokens_purpose;

ALTER TABLE user_tokens ADD CONSTRAINT ck_user_tokens_purpose
    CHECK (purpose IN ('EmailVerification', 'PasswordReset', 'Invitation'));

INSERT INTO email_templates (id, key, name, description, subject, html_body, text_body, is_system, created_at_utc, updated_at_utc)
VALUES
    (
        gen_random_uuid(),
        'user-invitation',
        'Invitación de usuario',
        'Correo con el enlace para que un usuario invitado por un administrador elija su contraseña. Variables: UserName, AppName, InvitationUrl, ExpiresInHours.',
        'Te invitaron a {{AppName}}',
        $$<!DOCTYPE html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Te invitaron a {{AppName}}</title>
</head>
<body style="margin:0;padding:0;background-color:#f3f4f6;font-family:Arial,Helvetica,sans-serif;">
  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background-color:#f3f4f6;">
    <tr>
      <td align="center" style="padding:24px 12px;">
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:600px;background-color:#ffffff;border-radius:8px;">
          <tr>
            <td style="padding:32px 24px;">
              <p style="margin:0 0 16px 0;font-size:16px;line-height:24px;color:#374151;">Hola {{UserName}},</p>
              <p style="margin:0 0 16px 0;font-size:16px;line-height:24px;color:#374151;">Te crearon una cuenta en <strong>{{AppName}}</strong>. Para activarla, elegí tu contraseña.</p>
              <p style="margin:0 0 16px 0;font-size:16px;line-height:24px;color:#374151;">Este enlace vence en {{ExpiresInHours}} horas y se puede usar una sola vez.</p>
              <p style="margin:24px 0;text-align:center;"><a href="{{InvitationUrl}}" style="display:inline-block;padding:12px 24px;background-color:#2563eb;color:#ffffff;text-decoration:none;border-radius:6px;font-size:16px;font-weight:bold;">Activar mi cuenta</a></p>
              <p style="margin:0 0 16px 0;font-size:14px;line-height:20px;color:#6b7280;">Si el botón no funciona, copiá y pegá este enlace en tu navegador. Si no esperabas esta invitación, podés ignorar este correo.</p>
              <p style="margin:0 0 16px 0;font-size:14px;line-height:20px;color:#6b7280;word-break:break-all;">{{InvitationUrl}}</p>
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>$$,
        $$Hola {{UserName}},

Te crearon una cuenta en {{AppName}}. Para activarla, elegí tu contraseña abriendo este enlace:

{{InvitationUrl}}

Este enlace vence en {{ExpiresInHours}} horas y se puede usar una sola vez.

Si no esperabas esta invitación, podés ignorar este correo.$$,
        TRUE,
        now(),
        now()
    )
ON CONFLICT (key) DO NOTHING;
