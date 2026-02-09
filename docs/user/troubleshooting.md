# Troubleshooting

## Common Issues and Solutions

This guide helps you resolve common problems with WeredaConnect.

## Audience
- End users
- Support staff
- Community moderators

---

## Installation Issues

### Problem: Can't Download from Play Store

**Symptoms**: "Your device isn't compatible" or "Not available in your country"

**Solutions**:
1. **Check Android version**: Requires Android 8.0+
   - Go to Settings > About Phone > Android Version
   - If below 8.0, device is not supported
2. **Check storage**: Requires 100MB free space
   - Go to Settings > Storage
   - Clear cache or delete unused apps
3. **Check region**: May not be available in all countries yet
   - Contact support for APK download link

### Problem: Installation Failed

**Symptoms**: "App not installed" error

**Solutions**:
1. **Enable unknown sources** (if installing APK):
   - Settings > Security > Unknown Sources > Enable
2. **Clear Play Store cache**:
   - Settings > Apps > Google Play Store > Clear Cache
3. **Restart phone** and try again
4. **Uninstall old version** if updating

---

## Login & Account Issues

### Problem: Can't Receive SMS Verification Code

**Symptoms**: No SMS received after 5 minutes

**Solutions**:
1. **Check phone number**: Make sure it's correct (include country code)
2. **Check signal**: Need cellular signal for SMS
3. **Wait 10 minutes**: SMS can be delayed
4. **Request new code**: Tap "Resend Code"
5. **Check spam/blocked messages**: Some phones filter SMS
6. **Contact support**: We can verify manually

### Problem: Forgot Password

**Solutions**:
1. Tap **"Forgot Password"** on login screen
2. Enter your phone number
3. Receive SMS with reset code
4. Enter code and create new password

### Problem: Account Locked

**Symptoms**: "Too many failed login attempts"

**Solutions**:
1. **Wait 30 minutes**: Account auto-unlocks
2. **Reset password**: Use "Forgot Password"
3. **Contact support**: If still locked after 1 hour

---

## Sync Issues

### Problem: Reports Not Syncing

**Symptoms**: Red sync icon, "Pending Sync" indicator

**Solutions**:
1. **Check internet connection**:
   - Open browser, try loading a website
   - Switch between WiFi and mobile data
2. **Check sync settings**:
   - Settings > Sync > Auto-Sync: Enable
   - Settings > Sync > WiFi Only: Disable (if on mobile data)
3. **Manual sync**:
   - Pull down on Reports screen to refresh
   - Or: Settings > Sync > Sync Now
4. **Check storage**:
   - Settings > Storage > Free up space if needed
5. **Restart app**:
   - Close completely and reopen

### Problem: Photos Not Uploading

**Symptoms**: Report created but photos show "Uploading..."

**Solutions**:
1. **Check internet speed**: Photos need stable connection
   - Wait for better signal
   - Connect to WiFi
2. **Check photo size**: Large photos take longer
   - Settings > Data Saver > Compress Photos: Enable
3. **Check storage**: Need space for upload queue
4. **Retry upload**:
   - Go to report
   - Tap "Retry Upload" on failed photos

### Problem: Sync Conflicts

**Symptoms**: "Conflict detected" message

**Solutions**:
1. **Accept server version**: Tap "Keep Server Version"
2. **Keep local changes**: Tap "Keep My Changes" (may lose server updates)
3. **Merge manually**: Edit report to combine both versions

---

## Performance Issues

### Problem: App is Slow

**Symptoms**: Lag, freezing, slow loading

**Solutions**:
1. **Clear cache**:
   - Settings > Storage > Clear Cache
2. **Reduce cached data**:
   - Settings > Storage > Cached Reports: Reduce limit
3. **Close other apps**: Free up RAM
4. **Restart phone**: Clears memory
5. **Update app**: Check Play Store for updates
6. **Reinstall app**: Last resort (will lose offline data)

### Problem: App Crashes

**Symptoms**: App closes unexpectedly

**Solutions**:
1. **Update app**: Check for latest version
2. **Clear cache**: Settings > Storage > Clear Cache
3. **Restart phone**
4. **Check storage**: Need at least 100MB free
5. **Report crash**: Settings > Help > Report Bug
   - Include: What you were doing when it crashed

### Problem: High Data Usage

**Symptoms**: Using more data than expected

**Solutions**:
1. **Enable data saver**:
   - Settings > Data Saver > Enable
   - Compresses photos, reduces sync frequency
2. **WiFi only sync**:
   - Settings > Sync > WiFi Only: Enable
3. **Limit photo uploads**:
   - Use 1-2 photos instead of 5
   - Enable photo compression
4. **Reduce cache**:
   - Settings > Storage > Cached Reports: Set to "Last 7 Days"

---

## Feature Issues

### Problem: Location Not Working

**Symptoms**: "Location unavailable" or wrong location

**Solutions**:
1. **Enable location services**:
   - Phone Settings > Location > Enable
2. **Grant permission**:
   - Phone Settings > Apps > WeredaConnect > Permissions > Location > Allow
3. **Wait for GPS lock**: Can take 30-60 seconds outdoors
4. **Move outdoors**: GPS doesn't work well indoors
5. **Manual location**: Tap map and drag pin to correct location

### Problem: Can't Upload Photos

**Symptoms**: "Failed to upload" error

**Solutions**:
1. **Check permissions**:
   - Phone Settings > Apps > WeredaConnect > Permissions > Storage > Allow
2. **Check photo size**: Max 10MB per photo
   - Enable compression: Settings > Data Saver > Compress Photos
3. **Check format**: Supports JPG, PNG only
4. **Check internet**: Need stable connection for upload

### Problem: Notifications Not Working

**Symptoms**: Not receiving notifications

**Solutions**:
1. **Enable notifications**:
   - Phone Settings > Apps > WeredaConnect > Notifications > Enable
2. **Check app settings**:
   - WeredaConnect Settings > Notifications > Enable desired types
3. **Disable battery optimization**:
   - Phone Settings > Battery > Battery Optimization > WeredaConnect > Don't Optimize
4. **Check Do Not Disturb**: Disable or allow WeredaConnect

### Problem: Can't Change Language

**Symptoms**: Language setting doesn't change UI

**Solutions**:
1. **Restart app**: Close completely and reopen
2. **Clear cache**: Settings > Storage > Clear Cache
3. **Reinstall app**: Last resort

---

## Offline Mode Issues

### Problem: "Offline" When Internet is Available

**Symptoms**: Red sync icon despite having internet

**Solutions**:
1. **Check internet**: Open browser, load website
2. **Restart app**: Close and reopen
3. **Toggle airplane mode**: On, wait 5 seconds, off
4. **Check server status**: May be maintenance (rare)
   - Check social media for announcements

### Problem: Can't Access Reports Offline

**Symptoms**: "Not available offline" message

**Solutions**:
1. **Load reports while online**: They'll cache for offline use
2. **Increase cache size**:
   - Settings > Storage > Cached Reports: Increase limit
3. **Manual cache**: Open reports you want offline while connected

---

## Error Messages

### "Network Error"
- **Cause**: No internet connection
- **Solution**: Connect to WiFi or mobile data

### "Server Error"
- **Cause**: Backend issue (rare)
- **Solution**: Wait 5 minutes and retry. Contact support if persists.

### "Invalid Credentials"
- **Cause**: Wrong phone number or password
- **Solution**: Check credentials or reset password

### "Permission Denied"
- **Cause**: Missing app permissions
- **Solution**: Grant permissions in phone settings

### "Storage Full"
- **Cause**: Not enough space on device
- **Solution**: Delete unused apps/files or reduce cache size

### "Sync Conflict"
- **Cause**: Report changed on server while editing offline
- **Solution**: Choose server version or keep local changes

---

## Getting More Help

### In-App Support
1. Menu > Help & Support
2. Browse FAQs
3. Contact Support

### Contact Information
- **Email**: support@weredaconnect.com
- **Response Time**: 24-48 hours
- **Include**:
  - Phone model and Android version
  - App version (Settings > About)
  - Screenshot of error (if applicable)
  - Steps to reproduce issue

### Community Forum
- Ask questions
- Share solutions
- Connect with other users
- [Coming Soon]

---

## Related Documentation

- **[User Guide](./user-guide.md)** - How to use WeredaConnect
- **[Features](./features.md)** - Complete feature list

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0  
**Support**: support@weredaconnect.com

