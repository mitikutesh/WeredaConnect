# Troubleshooting

## Common Issues and Solutions

This guide helps you resolve common problems with WeredaConnect.

## Audience
- End users (farmers, transport providers, wholesalers)
- Support staff
- Market coordinators

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
1. **Check phone number**: Make sure it's correct with country code (+251)
2. **Check signal**: Need cellular signal for SMS
3. **Wait 10 minutes**: SMS can be delayed in rural areas with weak coverage
4. **Request new code**: Tap "Resend Code"
5. **Check spam/blocked messages**: Some phones filter SMS
6. **Contact support**: We can verify manually

### Problem: Fayda ID Verification Failing

**Symptoms**: "Verification failed" or "Could not verify identity"

**Solutions**:
1. **Check Fayda ID number**: Ensure you entered it correctly with no extra spaces
2. **Check internet connection**: Fayda verification requires an active connection
3. **Try again later**: The Fayda verification service may be temporarily unavailable
4. **Ensure name matches**: Your registered name must match your Fayda ID record
5. **Contact support**: If verification keeps failing, support can assist with manual verification

### Problem: Forgot PIN

**Solutions**:
1. Tap **"Forgot PIN"** on login screen
2. Enter your registered phone number
3. Receive SMS with reset code
4. Enter code and create new PIN

### Problem: Account Locked

**Symptoms**: "Too many failed login attempts"

**Solutions**:
1. **Wait 30 minutes**: Account auto-unlocks
2. **Reset PIN**: Use "Forgot PIN"
3. **Contact support**: If still locked after 1 hour

---

## Sync Issues

### Problem: Crop Listings Not Syncing

**Symptoms**: Red sync icon, "Pending Sync" indicator on listings

**Solutions**:
1. **Check internet connection**:
   - Open browser, try loading a website
   - Switch between WiFi and mobile data
2. **Check sync settings**:
   - Settings > Sync > Auto-Sync: Enable
   - Settings > Sync > WiFi Only: Disable (if on mobile data)
3. **Manual sync**:
   - Pull down on My Listings screen to refresh
   - Or: Settings > Sync > Sync Now
4. **Check storage**:
   - Settings > Storage > Free up space if needed
5. **Restart app**:
   - Close completely and reopen

### Problem: Delivery Photos Not Uploading

**Symptoms**: Delivery confirmation created but photos show "Uploading..."

**Solutions**:
1. **Check internet speed**: Photos need a stable connection
   - Wait for better signal
   - Connect to WiFi if available
2. **Check photo size**: Large photos take longer on slow networks
   - Settings > Data Saver > Compress Photos: Enable
3. **Check storage**: Need space for upload queue
4. **Retry upload**:
   - Go to the delivery record
   - Tap "Retry Upload" on failed photos

### Problem: Sync Conflicts

**Symptoms**: "Conflict detected" message on crop listings or orders

**Solutions**:
1. **Accept server version**: Tap "Keep Server Version" (recommended if another party updated the order)
2. **Keep local changes**: Tap "Keep My Changes" (use if you updated crop details offline)
3. **Merge manually**: Edit the listing to combine both versions (e.g., updated price from server + updated quantity from your edit)

---

## Performance Issues

### Problem: App is Slow

**Symptoms**: Lag, freezing, slow loading

**Solutions**:
1. **Clear cache**:
   - Settings > Storage > Clear Cache
2. **Reduce cached data**:
   - Settings > Storage > Cached Listings: Reduce limit
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
   - Use 1-2 crop photos instead of 5
   - Enable photo compression
4. **Reduce cache**:
   - Settings > Storage > Cached Listings: Set to "Last 7 Days"

---

## Feature Issues

### Problem: Voice-to-Text Not Working

**Symptoms**: Microphone button unresponsive, or transcription is inaccurate

**Solutions**:
1. **Grant microphone permission**:
   - Phone Settings > Apps > WeredaConnect > Permissions > Microphone > Allow
2. **Check language setting**:
   - WeredaConnect Settings > Language: Select Amharic, Afan Oromo, or English
   - Voice-to-text must match your selected language
3. **Speak clearly**: Hold the phone close and speak at a normal pace
4. **Reduce background noise**: Move to a quieter area (away from market noise, livestock)
5. **Check internet**: Voice recognition may need a connection for first use to download language models
6. **Edit manually**: Tap the transcribed text to correct any errors before posting

### Problem: Location Not Working

**Symptoms**: "Location unavailable" or wrong location on crop listing or delivery

**Solutions**:
1. **Enable location services**:
   - Phone Settings > Location > Enable
2. **Grant permission**:
   - Phone Settings > Apps > WeredaConnect > Permissions > Location > Allow
3. **Wait for GPS lock**: Can take 30-60 seconds outdoors
4. **Move outdoors**: GPS doesn't work well indoors or under dense tree cover
5. **Manual location**: Tap the map and drag the pin to your correct farm or market location

### Problem: QR Code Scanner Issues

**Symptoms**: Camera won't open, QR code not recognized, or "Invalid QR code"

**Solutions**:
1. **Grant camera permission**:
   - Phone Settings > Apps > WeredaConnect > Permissions > Camera > Allow
2. **Clean camera lens**: Wipe dust or dirt from the phone camera
3. **Ensure proper lighting**: QR codes are hard to scan in low light
4. **Hold steady**: Keep the phone still and position the QR code within the scanner frame
5. **Check QR code condition**: If the printed QR code is damaged, wet, or faded, request a new delivery token
6. **Restart app**: Close and reopen if scanner freezes

### Problem: Escrow Payment Issues

**Symptoms**: "Payment failed", funds not released, or Telebirr transaction not completing

**Solutions**:
1. **Check Telebirr account balance**: Ensure you have sufficient funds
2. **Check Telebirr app**: Make sure Telebirr is installed, updated, and logged in
3. **Verify phone number**: Your WeredaConnect account phone number must match your Telebirr number
4. **Check internet connection**: Payment transactions require an active connection
5. **Wait for confirmation**: Escrow payments may take 1-2 minutes to process
6. **Don't retry immediately**: If a payment appears stuck, wait 5 minutes before retrying to avoid duplicate charges
7. **Delivery not confirmed yet?**: Escrow funds are released only after the buyer scans the delivery QR code
8. **Contact support**: For stuck or disputed payments, include the transaction ID from your payment history

### Problem: Can't Upload Crop Photos

**Symptoms**: "Failed to upload" error when adding photos to a crop listing

**Solutions**:
1. **Check permissions**:
   - Phone Settings > Apps > WeredaConnect > Permissions > Storage > Allow
   - Phone Settings > Apps > WeredaConnect > Permissions > Camera > Allow
2. **Check photo size**: Max 10MB per photo
   - Enable compression: Settings > Data Saver > Compress Photos
3. **Check format**: Supports JPG, PNG only
4. **Check internet**: Need stable connection for upload (or photos will queue for later sync)
5. **Reduce number of photos**: Try uploading 1-2 photos at a time on slow connections

---

## Offline Mode Issues

### Problem: "Offline" When Internet is Available

**Symptoms**: Red sync icon despite having internet

**Solutions**:
1. **Check internet**: Open browser, load a website
2. **Restart app**: Close and reopen
3. **Toggle airplane mode**: On, wait 5 seconds, off
4. **Check server status**: May be under maintenance (rare)
   - Check SMS announcements or social media for updates

### Problem: Can't Access Crop Listings Offline

**Symptoms**: "Not available offline" message when browsing listings

**Solutions**:
1. **Browse listings while online**: They will cache automatically for offline access
2. **Increase cache size**:
   - Settings > Storage > Cached Listings: Increase limit
3. **Manual cache**: Open specific listings you need while connected

### Problem: Can't Create Listings or Request Transport Offline

**Symptoms**: Features seem disabled or show warnings in offline mode

**Solutions**:
1. **Listings work offline**: You can create new crop listings offline; they sync when connection returns
2. **Transport requests queue offline**: Requests are saved locally and submitted when you reconnect
3. **Check pending queue**: Settings > Sync > Pending Items shows your queued actions
4. **Don't duplicate**: If you created a listing offline, don't create it again online; it will sync automatically

---

## Error Messages

### "Network Error"
- **Cause**: No internet connection
- **Solution**: Connect to WiFi or mobile data. Your data is safe offline and will sync later.

### "Server Error"
- **Cause**: Backend issue (rare)
- **Solution**: Wait 5 minutes and retry. Contact support if it persists.

### "Invalid Credentials"
- **Cause**: Wrong phone number or PIN
- **Solution**: Check your credentials or reset your PIN

### "Fayda Verification Failed"
- **Cause**: Fayda ID could not be verified (incorrect ID, service unavailable, or name mismatch)
- **Solution**: Double-check your Fayda ID number and try again. Contact support if the issue persists.

### "Permission Denied"
- **Cause**: Missing app permissions
- **Solution**: Grant permissions in phone settings (camera, microphone, location, storage)

### "Storage Full"
- **Cause**: Not enough space on device
- **Solution**: Delete unused apps/files or reduce cache size

### "Sync Conflict"
- **Cause**: Listing or order changed on server while you were editing offline
- **Solution**: Choose server version or keep local changes

### "Payment Failed"
- **Cause**: Telebirr transaction could not be completed
- **Solution**: Check your Telebirr balance and connection. Wait 5 minutes before retrying. Contact support with your transaction ID if it persists.

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
- Connect with other farmers, transporters, and buyers
- [Coming Soon]

---

## Related Documentation

- **User Guide** - How to use WeredaConnect [Coming Soon]
- **Features** - Complete feature list [Coming Soon]

---

**Last Updated**: 2026-02-09
**Version**: 1.0.0
**Support**: support@weredaconnect.com
