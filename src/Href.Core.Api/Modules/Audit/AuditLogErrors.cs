using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Audit;

public static class AuditLogErrors
{
    public static readonly Error InvalidPage = Error.Validation(
        "AuditLogs.InvalidPage",
        "The page must be 1 or greater.");

    public static readonly Error InvalidPageSize = Error.Validation(
        "AuditLogs.InvalidPageSize",
        "The page size must be between 1 and 200.");

    public static readonly Error InvalidDate = Error.Validation(
        "AuditLogs.InvalidDate",
        "The 'from' and 'to' dates must be ISO 8601 (yyyy-MM-dd, optionally followed by 'T' and a time and a 'Z' or '+hh:mm' offset; without an offset the time is UTC).");

    public static readonly Error InvalidRange = Error.Validation(
        "AuditLogs.InvalidRange",
        "The 'from' date must be earlier than or equal to the 'to' date.");

    public static readonly Error InvalidActorType = Error.Validation(
        "AuditLogs.InvalidActorType",
        "The actor type must be 'User', 'Application', 'System' or 'Anonymous'.");

    public static readonly Error InvalidFilter = Error.Validation(
        "AuditLogs.InvalidFilter",
        "Text filters must be a single line without control characters and no longer than their column.");
}
