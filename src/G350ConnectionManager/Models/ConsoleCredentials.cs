namespace G350ConnectionManager.Models;

/// <summary>
/// The account used to reach the console over SSH and SFTP.
///
/// Every console service needs the same credentials, and ArkOS ships a single
/// default account for the user data partition. Keeping the values in one place
/// means a console whose password was changed can be supported by pointing this
/// type at a setting, instead of editing four unrelated constants.
///
/// The defaults are the ArkOS account, which is what the console-side script
/// expects and what the documentation tells users to enter in an external SFTP
/// client.
/// </summary>
public static class ConsoleCredentials
{
    /// <summary>ArkOS user account.</summary>
    public const string DefaultUser = "ark";

    /// <summary>ArkOS default password.</summary>
    public const string DefaultPassword = "ark";

    /// <summary>
    /// Account name used for SSH and SFTP. Defaults to <see cref="DefaultUser"/>
    /// and is replaced when settings carry a different value.
    /// </summary>
    public static string User { get; private set; } = DefaultUser;

    /// <summary>
    /// Password used for SSH, SFTP and the console's <c>sudo</c> prompt. Defaults
    /// to <see cref="DefaultPassword"/> and is replaced when settings carry a
    /// different value.
    /// </summary>
    public static string Password { get; private set; } = DefaultPassword;

    /// <summary>
    /// Applies credentials from settings. Blank values keep the ArkOS defaults,
    /// so an existing settings file needs no migration.
    /// </summary>
    public static void Apply(string? user, string? password)
    {
        User = string.IsNullOrWhiteSpace(user) ? DefaultUser : user.Trim();
        Password = string.IsNullOrEmpty(password) ? DefaultPassword : password;
    }
}
