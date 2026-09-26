using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;

namespace WeredaConnect.Server.Services;

/// <summary>
/// Sends transactional SMS through Amazon SNS. Disabled (returns false without
/// calling SNS) when SMS_ENABLED is "false", which dev uses while the SNS
/// account is sandboxed; the OTP dev bypass covers that environment instead.
/// </summary>
public sealed class SnsSmsSender : ISmsSender
{
    private readonly IAmazonSimpleNotificationService _sns;
    private readonly bool _enabled;

    public SnsSmsSender(IAmazonSimpleNotificationService sns, bool enabled)
    {
        _sns = sns ?? throw new ArgumentNullException(nameof(sns));
        _enabled = enabled;
    }

    public static bool IsEnabledByEnvironment() =>
        !string.Equals(Environment.GetEnvironmentVariable("SMS_ENABLED"), "false", StringComparison.OrdinalIgnoreCase);

    public async Task<bool> SendAsync(string phoneNumber, string message)
    {
        if (!_enabled)
        {
            return false;
        }

        try
        {
            await _sns.PublishAsync(new PublishRequest
            {
                PhoneNumber = phoneNumber,
                Message = message,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    {
                        "AWS.SNS.SMS.SMSType",
                        new MessageAttributeValue { DataType = "String", StringValue = "Transactional" }
                    }
                }
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
