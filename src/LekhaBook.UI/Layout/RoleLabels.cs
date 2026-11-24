namespace LekhaBook.UI.Layout;

/// <summary>Friendly names for role keys sent by the API.</summary>
public static class RoleLabels
{
    /// <summary>Roles staff can be given (Owner can't be assigned).</summary>
    public static readonly string[] Assignable = ["Partner", "Manager", "Accountant", "EntryPerson"];

    public static string For(string role) => role switch
    {
        "EntryPerson" => "Entry Person",
        _ => role,
    };

    public static string Describe(string role) => role switch
    {
        "Owner" => "Full access, including deleting the company.",
        "Partner" => "Everything except deleting the company.",
        "Manager" => "Add and edit entries and parties; no deleting, staff or settings.",
        "Accountant" => "All books, reports and settings; no staff management.",
        "EntryPerson" => "Adds entries dated today and can fix their own entries the same day.",
        _ => string.Empty,
    };
}
