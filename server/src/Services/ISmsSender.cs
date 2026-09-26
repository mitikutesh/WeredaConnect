namespace WeredaConnect.Server.Services;

/// <summary>
/// Transactional SMS delivery (OTP codes, security notices). Best effort: a
/// false return means the message was not sent; callers log and continue.
/// </summary>
public interface ISmsSender
{
    Task<bool> SendAsync(string phoneNumber, string message);
}
