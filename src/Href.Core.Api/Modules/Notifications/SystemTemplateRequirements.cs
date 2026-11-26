namespace Href.Core.Api.Modules.Notifications;

/// <summary>
/// The placeholders of each system template, which are exactly the variables the Core flows provide when they render it.
/// An update of a system template must keep all of them and cannot add others. Keys are matched exactly; placeholders case-insensitively.
/// </summary>
public static class SystemTemplateRequirements
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Placeholders =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["welcome-user"] = ["UserName", "AppName"],
            ["verify-email"] = ["UserName", "AppName", "VerificationUrl", "ExpiresInHours"],
            ["reset-password"] = ["UserName", "AppName", "ResetUrl", "ExpiresInMinutes"],
            ["user-invitation"] = ["UserName", "AppName", "InvitationUrl", "ExpiresInHours"],
            ["delivery-failed-alert"] = ["OriginalTo", "OriginalSubject", "NotificationId", "Attempts", "LastError"],
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> WorstCase =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["delivery-failed-alert"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OriginalTo"] = new string('a', 254),
                ["OriginalSubject"] = new string('s', 500),
                ["NotificationId"] = "0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b",
                ["Attempts"] = "3",
                ["LastError"] = new string('"', 2000),
            },
        };

    /// <summary>
    /// Gets the largest values the Core provides for a system template whose output must always fit its limits
    /// (the failure alert carries user-controlled text), or returns false when the template has none.
    /// </summary>
    public static bool TryGetWorstCaseVariables(string key, out IReadOnlyDictionary<string, string> variables) =>
        WorstCase.TryGetValue(key, out variables!);

    /// <summary>
    /// Keys of every system template with requirements.
    /// </summary>
    public static IReadOnlyCollection<string> Keys => Placeholders.Keys.ToList();

    /// <summary>
    /// Gets the required placeholders of a system template, or returns false when the key has no requirements.
    /// </summary>
    public static bool TryGet(string key, out IReadOnlyList<string> placeholders) =>
        Placeholders.TryGetValue(key, out placeholders!);
}
