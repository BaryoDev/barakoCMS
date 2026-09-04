namespace barakoCMS.Models;

/// <summary>
/// The system-wide capabilities a <see cref="Role"/> can hold in <see cref="Role.SystemCapabilities"/>,
/// and the rule for whether a role's granted set satisfies a required one. Modelled on
/// <see cref="ApiKeyScopes"/>, which already carries both the known set and the satisfies rule.
/// </summary>
/// <remarks>
/// A capability names an administrative surface, not an HTTP verb. It is what an administrative
/// endpoint asks for instead of a role name, so a role created through <c>POST /api/roles</c> can be
/// granted access without a code change, and a role called "Editor" gains nothing by its name.
///
/// The vocabulary is deliberately short. It covers the surfaces migrated so far and grows one area
/// at a time, because the role gates it replaces are not uniform and a name invented ahead of the
/// migration would encode the wrong grant. <c>GET /api/settings</c> is <c>Roles("SuperAdmin", "Admin")</c>
/// while <c>PUT /api/settings/email</c> is <c>Roles("SuperAdmin")</c>; a single <c>manage_settings</c>
/// defined now would have to pick one of those, and picking the wider one hands every Admin the
/// email configuration. Users split the same way, which is why it is three names below and not one.
/// See issues #272 and #443.
/// </remarks>
public static class SystemCapabilities
{
    /// <summary>Satisfies every capability, including ones added after the role was written.</summary>
    public const string All = "*";

    /// <summary>Create, read, update and delete roles.</summary>
    public const string ManageRoles = "manage_roles";

    /// <summary>List, create and update tenants.</summary>
    public const string ManageTenants = "manage_tenants";

    /// <summary>List and change who belongs to a tenant and with which roles.</summary>
    public const string ManageTenantMembers = "manage_tenant_members";

    /// <summary>
    /// List user accounts and reset another user's password. Narrower than the two below on
    /// purpose: these were <c>Roles("SuperAdmin")</c>, and Admin never reached them.
    /// </summary>
    public const string ManageUsers = "manage_users";

    /// <summary>Assign and remove a user's roles and groups.</summary>
    public const string ManageUserMembership = "manage_user_membership";

    /// <summary>Create, read, update and delete user groups, and change who is in them.</summary>
    public const string ManageUserGroups = "manage_user_groups";

    /// <summary>Issue, list and revoke API keys.</summary>
    public const string ManageApiKeys = "manage_api_keys";

    /// <summary>
    /// Read the audit log. Named for reading rather than managing because the surface is one GET:
    /// entries are append-only and the chain is tamper-evident, so there is nothing to manage.
    /// </summary>
    public const string ViewAuditLog = "view_audit_log";

    /// <summary>
    /// List and write system settings, and read the email settings summary. Everything Admin could
    /// already reach under <c>/api/settings</c>.
    /// </summary>
    public const string ManageSettings = "manage_settings";

    /// <summary>
    /// Change where the system's email comes from, and send a test through it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ManageSettings"/> rather than folded into it, because the two gates
    /// it replaces were deliberately different: reading the summary was Admin and SuperAdmin, and
    /// writing was SuperAdmin alone. Changing the sending identity redirects every password reset
    /// and every verification token in the deployment, which is a takeover rather than an
    /// administrative tweak, and it is exactly the change a compromised admin account makes. One
    /// <c>manage_settings</c> covering both would hand that to every Admin.
    /// </remarks>
    public const string ManageEmailSettings = "manage_email_settings";

    /// <summary>
    /// List and create content types, and rebuild an event-sourced type's read model.
    /// </summary>
    public const string ManageContentTypes = "manage_content_types";

    /// <summary>
    /// Decide what an anonymous caller can read: turn public delivery on or off for a type, and set
    /// a field's sensitivity, which is what determines whether the field is scrubbed on the way out.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="ManageContentTypes"/> even though both gates were the same role pair,
    /// for the reason API keys and the audit log are split: the jobs are different. Designing a
    /// schema is modelling work. Deciding what leaves the building to an anonymous caller is a
    /// disclosure decision, and a role that should do the first without the second is an ordinary
    /// thing to want. One name makes it unexpressible.
    ///
    /// Both go to Admin's defaults, because Admin reached all five routes already and this migration
    /// does not narrow anything.
    /// </remarks>
    public const string ManagePublicDelivery = "manage_public_delivery";

    /// <summary>
    /// Read which modules this instance registered.
    /// </summary>
    /// <remarks>
    /// Two fields per module and nothing else, so this is closer to reading the audit log than to
    /// managing anything, and it is named for reading.
    /// </remarks>
    public const string ViewModules = "view_modules";

    /// <summary>
    /// Read the monitoring surface: per-check health detail, the Kubernetes cluster view and the
    /// metrics summary.
    /// </summary>
    /// <remarks>
    /// One name for all three routes, and named for reading because there is nothing here to write.
    /// Each is a GET reporting what the host and the cluster are doing, and watching the dashboard
    /// is one job rather than three. They are not merged into <see cref="ManageSettings"/> either:
    /// these disclose infrastructure detail (node names, image versions, replica counts) that a
    /// settings screen never shows, and the gate they replace was its own.
    /// </remarks>
    public const string ViewMonitoring = "view_monitoring";

    /// <summary>List, create and delete URL redirects, and import a batch of them.</summary>
    /// <remarks>
    /// One name for the four routes. The import writes exactly what the single create writes, only
    /// more of it, so a role allowed to add one redirect at a time and not fifty would be a rate
    /// limit dressed up as an authorisation decision.
    /// </remarks>
    public const string ManageRedirects = "manage_redirects";

    /// <summary>
    /// List, read, save and delete saved queries, and preview the rows one returns.
    /// </summary>
    /// <remarks>
    /// One name, preview included, which is the split deliberately not taken. The preview does
    /// disclose content rows, and it does not consult the per-role content permissions, so it is a
    /// real read of data the caller may hold no <c>Read</c> rule for. It stays here anyway because
    /// whoever can save a definition can already point one at any content type and attach it to a
    /// request, which sends those same rows to a third party; showing them to the author instead is
    /// strictly less. Withholding the preview would leave the person writing the query with
    /// production as the only way to learn what it selects.
    ///
    /// What bounds the disclosure is <c>QueryRunner</c>, not this gate: a query may only filter,
    /// sort and project fields whose sensitivity is <c>Public</c>, and that is re-checked on every
    /// run rather than only when the query was saved.
    /// </remarks>
    public const string ManageQueries = "manage_queries";

    /// <summary>
    /// List, read, save and delete request definitions, and dry-run one against a content item.
    /// </summary>
    /// <remarks>
    /// One name, dry run included, and unlike the query preview there is nothing to weigh: a
    /// request definition holds no credential by construction. The connector holds those and the
    /// sender attaches them after the message is composed, so the dry run has nothing to redact and
    /// shows the author the finished call without making it.
    /// </remarks>
    public const string ManageRequests = "manage_requests";

    /// <summary>
    /// Read connector configuration: where a connector points, how it authenticates, and the names
    /// of the secrets it holds. Never a secret's value, which no endpoint returns.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="ManageConnectors"/>. The argument is in that summary, since the write
    /// half is the half that carries the risk.
    /// </remarks>
    public const string ViewConnectors = "view_connectors";

    /// <summary>
    /// Create, update and delete connectors, and probe one. This is the capability that writes a
    /// third party's credentials into the system and the one that spends them.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="ViewConnectors"/> even though both halves gated on the same role pair,
    /// and the argument is the one the surface itself already makes. A connector is the only
    /// document in core holding somebody else's credentials. The read routes return the
    /// configuration and <c>SecretKeys</c>, which are names; the write routes take secret values,
    /// and the probe sends them to the configured base URL and records what came back. So writing
    /// is credential handling twice over: it is where a token enters the system, and where a base
    /// URL can be repointed, which redirects every request built on that connector to a new
    /// destination without touching any request definition.
    ///
    /// It could have gone the other way, on the grounds that nothing about a connector is harmless:
    /// the list alone tells a reader which third parties this deployment talks to. That is true,
    /// and it is why reading is gated too, at its own name. It is not a reason to make the person
    /// who answers "where does the invoicing connector point" hold the grant that can repoint it.
    ///
    /// Both names go to Admin's defaults, because Admin reached all six routes already.
    /// </remarks>
    public const string ManageConnectors = "manage_connectors";

    /// <summary>
    /// Author workflows: list and create them, read the registered actions and the template
    /// variables, validate a definition and dry-run one.
    /// </summary>
    /// <remarks>
    /// The dry run belongs here rather than in a name of its own. It executes nothing: it resolves
    /// the templates and reports what each action would have done. Splitting it out would leave the
    /// person who just wrote the workflow with running it in production as the only way to see what
    /// it does, which is the opposite of what the endpoint is for.
    /// </remarks>
    public const string ManageWorkflows = "manage_workflows";

    /// <summary>
    /// Read workflow runs, one run's detail, and a workflow's execution history.
    /// </summary>
    /// <remarks>
    /// <c>GET /api/workflows/{id}/debug</c> is in here rather than with <see cref="ManageWorkflows"/>
    /// because what it returns is history: the execution log of what already ran, which is the run
    /// list seen from the other end. It is read with the runs.
    /// </remarks>
    public const string ViewWorkflowRuns = "view_workflow_runs";

    /// <summary>Queue a failed action of a workflow run to be attempted again.</summary>
    /// <remarks>
    /// Split from <see cref="ViewWorkflowRuns"/> because retrying is not reading. The runner picks
    /// the attempt up and the action happens for real: the mail is sent, the third party is called.
    /// "Did the notification go out?" is the ordinary support question and answering it needs the
    /// run list and nothing else, so the role that answers it must not also be able to fire the
    /// action a second time. The endpoint already refuses an attempt that succeeded, for this
    /// reason at a smaller scale; the capability is the same judgement at the surface.
    /// </remarks>
    public const string RetryWorkflowActions = "retry_workflow_actions";

    /// <summary>Roll a content item back to an earlier version.</summary>
    /// <remarks>
    /// Its own name rather than one shared with <see cref="EraseContent"/>, because the two gates
    /// being replaced differ: rollback was <c>Roles("SuperAdmin", "Admin")</c> and the erasure was
    /// <c>Roles("SuperAdmin")</c>. One name would have to pick one of those, and picking the wider
    /// one hands every Admin an irreversible delete. They are not the same operation either: a
    /// rollback writes a new version and destroys nothing, so every state it passes through is
    /// still there afterwards.
    /// </remarks>
    public const string RollbackContent = "rollback_content";

    /// <summary>Erase a content item and its history irrecoverably.</summary>
    /// <remarks>
    /// Deliberately absent from Admin's defaults: Admin was never in <c>Roles("SuperAdmin")</c> on
    /// this route, and this is the one operation in the product that destroys the audit trail's own
    /// subject matter. Granting it to Admin here is exactly the widening issue #443 warns against,
    /// and nothing undoes the result.
    /// </remarks>
    public const string EraseContent = "erase_content";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        All, ManageRoles, ManageTenants, ManageTenantMembers, ManageUsers, ManageUserMembership, ManageUserGroups,
        ManageApiKeys, ViewAuditLog, ManageSettings, ManageEmailSettings, ManageContentTypes, ManagePublicDelivery,
        ViewMonitoring, ManageRedirects, ManageQueries, ManageRequests, ViewConnectors, ManageConnectors,
        ManageWorkflows, ViewWorkflowRuns, RetryWorkflowActions, RollbackContent, EraseContent, ViewModules,
    };

    public static bool IsKnown(string capability) =>
        !string.IsNullOrWhiteSpace(capability) && Known.Contains(capability);

    /// <summary>Does a role's granted capability set satisfy a required capability? <c>*</c> satisfies everything.</summary>
    public static bool Satisfies(IEnumerable<string> granted, string required) =>
        granted.Any(c => c == All || string.Equals(c, required, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] None = [];
    private static readonly string[] Everything = [All];
    private static readonly string[] AdminDefaults =
    [
        ManageTenantMembers, ManageUserMembership, ManageUserGroups, ManageApiKeys, ViewAuditLog,
        ManageSettings, ManageContentTypes, ManagePublicDelivery,
        // Every gate migrated below named Admin, so all of these preserve access rather than
        // granting it. EraseContent is the one exception in the whole of #443 and is absent on
        // purpose: DELETE /api/contents/{id}/erase was Roles("SuperAdmin").
        ViewMonitoring, ManageRedirects, ManageQueries, ManageRequests, ViewConnectors,
        ManageConnectors, ManageWorkflows, ViewWorkflowRuns, RetryWorkflowActions, RollbackContent,
        // Modules: GET /api/modules was Roles("SuperAdmin", "Admin"), so Admin read it already.
        ViewModules,
    ];

    /// <summary>
    /// The capabilities a seeded system role starts with, chosen to match what that role could
    /// already reach before capabilities existed. Keyed on name because the role gates being
    /// replaced were keyed on name.
    /// </summary>
    /// <remarks>
    /// Admin gets only the surfaces Admin could already reach: it was never in the
    /// <c>Roles("SuperAdmin")</c> gates on roles, tenants, the user list or the password reset, so
    /// it does not get those here. A role the seeder does not create gets nothing, which is the
    /// point of the issue: a name grants no access on its own.
    /// </remarks>
    public static IReadOnlyList<string> DefaultsFor(string roleName) => roleName switch
    {
        "SuperAdmin" => Everything,
        "Admin" => AdminDefaults,
        _ => None,
    };
}
