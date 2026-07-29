namespace Sentinel.Domain.Security;

public enum SessionRevocationReason
{
    None = 0,
    UserLogout = 1,
    LogoutAllDevices = 2,
    AdminRevoked = 3,
    PasswordChanged = 4,
    AccountDisabled = 5,
}
