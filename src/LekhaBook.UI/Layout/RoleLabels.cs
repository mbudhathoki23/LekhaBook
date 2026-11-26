namespace LekhaBook.UI.Layout;

/// <summary>
/// Role names sent by the API, as resource keys. Show them with <c>@Loc[RoleLabels.For(role)]</c>.
/// </summary>
public static class RoleLabels
{
    /// <summary>Roles staff can be given (Owner can't be assigned). These are API role keys, not display text.</summary>
    public static readonly string[] Assignable = ["Partner", "Manager", "Accountant", "EntryPerson"];

    /// <summary>The resource key for a role's name, e.g. "Role.EntryPerson".</summary>
    public static string For(string role) => "Role." + role;

    /// <summary>The resource key for a one-line description of a role's access (empty for unknown roles).</summary>
    public static string Describe(string role) => role switch
    {
        "Owner" => "Role.Owner.Description",
        "Partner" => "Role.Partner.Description",
        "Manager" => "Role.Manager.Description",
        "Accountant" => "Role.Accountant.Description",
        "EntryPerson" => "Role.EntryPerson.Description",
        _ => string.Empty,
    };
}
