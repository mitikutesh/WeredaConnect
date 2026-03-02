# 🌍 Local Reality - Ethiopian Context

## Purpose
This directory documents the **real-world constraints and cultural context** of Ethiopian users. Every technical decision must be validated against these realities.

---

## Infrastructure Constraints

### Network Connectivity
- **Primary**: 3G (HSPA+), speeds 1-5 Mbps
- **Coverage**: Intermittent in rural areas
- **Reliability**: Frequent disconnections, high latency (200-500ms)
- **Cost**: ~$0.50-1.00 per GB (expensive relative to income)

**Implications**:
- ✅ Offline-first is mandatory, not optional
- ✅ Compress all payloads (gzip/brotli)
- ✅ Minimize API calls, batch operations
- ✅ Show data usage estimates to users

### Device Landscape
- **Common devices**: Tecno, Infinix, Samsung A-series
- **RAM**: 2-4GB typical
- **Storage**: 16-32GB (often nearly full)
- **Android version**: 8.0-12.0 (API 26-31)

**Implications**:
- ✅ Target Android 8+ (API 26)
- ✅ Optimize memory usage (<100MB app footprint)
- ✅ Keep APK size <50MB
- ✅ Test on low-end emulators (2GB RAM)

### Power & Battery
- **Electricity**: Unreliable in rural areas
- **Battery health**: Often degraded (old devices)
- **Charging**: May only charge once per day

**Implications**:
- ✅ Minimize background sync
- ✅ Use WorkManager for deferred tasks
- ✅ Optimize battery usage (no constant polling)
- ✅ Show battery impact in settings

---

## Cultural Context

### Languages
1. **Amharic** (አማርኛ): Official language, ~32M speakers
2. **Afan Oromo** (Afaan Oromoo): ~40M speakers
3. **English**: Urban educated population

**Implications**:
- ✅ All three languages must be supported
- ✅ Amharic is right-to-left aware (though written left-to-right)
- ✅ Use culturally appropriate fonts (Noto Sans Ethiopic)
- ✅ Avoid direct translations—adapt for context

### Terminology Considerations

#### Agricultural Terms
- **Gullit** (ጉልት): Local market/trading post
- **Kebele** (ቀበሌ): Smallest administrative unit (neighborhood)
- **Wereda** (ወረዳ): District (our app's namesake)
- **Woreda** (alternate spelling)

**Implications**:
- ✅ Use local terms, not English equivalents
- ✅ Validate translations with rural users
- ✅ Avoid urban-centric language

#### Agricultural & Market Context
- **Crop seasons**: Meher (main), Belg (short rains)
- **Market days**: Weekly markets (Gullit) in rural areas
- **Trust networks**: Built through personal relationships, cooperatives
- **Middlemen problem**: High commissions (30-40%) reduce farmer income
- **Transport challenges**: Truck drivers travel with empty loads after delivery

**Implications**:
- ✅ Design for farmer-trucker-buyer workflows
- ✅ Support cooperative/group listings
- ✅ Allow offline sharing (Bluetooth, QR codes)
- ✅ Build trust through transparency (escrow, delivery verification)
- ✅ Use local market terminology (Gullit, Kebele, Wereda)

---

## User Behavior Patterns

### Literacy & Digital Literacy
- **Literacy rate**: ~52% nationally, lower in rural areas
- **Digital literacy**: Low, especially for older users
- **Smartphone experience**: Often first smartphone

**Implications**:
- ✅ Use icons and images, not just text
- ✅ Provide voice input/output options
- ✅ Simple, linear workflows (avoid complex navigation)
- ✅ Onboarding tutorials with visuals

### Data Consciousness
- **Users track data usage closely** (it's expensive)
- **Prefer offline features** to save money
- **Suspicious of apps that use too much data**

**Implications**:
- ✅ Show data usage per feature
- ✅ Allow users to control sync frequency
- ✅ Provide "data saver" mode
- ✅ Warn before large downloads

### Privacy & Security
- **Concerns**: Government surveillance, data misuse
- **Trust**: Low for new apps, especially those handling financial transactions
- **Sharing**: Devices often shared among family

**Implications**:
- ✅ End-to-end encryption for sensitive data
- ✅ Clear privacy policy in local languages
- ✅ Allow anonymous usage where possible
- ✅ Support multiple user profiles on one device

---

## Regulatory Environment

### Data Privacy
- **Proclamation 1284/2023** establishes data protection framework for Fayda digital ID
- **Disclosing resident data without consent** is punishable by up to 8 years imprisonment
- **Government access**: Possible under national security laws
- **NBE mandate**: VeriFayda 2.0 mandatory for digital financial transactions >10,000 ETB since Jan 2026

**Implications**:
- ✅ Minimize data collection (data minimization principle)
- ✅ Do NOT store biometric data — Fayda handles all biometrics
- ✅ Store only Fayda ID number and verification status
- ✅ Provide data export/deletion features
- ✅ Register as Relying Party with NIDP for Fayda OIDC API access

### Content Moderation
- **Hate speech laws**: Enforced, especially ethnic/religious content
- **Political content**: Sensitive, especially during elections
- **Misinformation**: Growing concern

**Implications**:
- ✅ Implement content moderation for user-generated content
- ✅ Allow reporting of harmful content
- ✅ Verify crop listing accuracy and prevent fraudulent listings
- ✅ Avoid political endorsements

---

## Economic Context

### Income Levels
- **Rural**: ~$500-1000/year per capita
- **Urban**: ~$1500-3000/year per capita
- **Smartphone cost**: $50-150 (significant investment)

**Implications**:
- ✅ Free app, no paywalls
- ✅ Minimize data costs (see above)
- ✅ Optimize for low-end devices (see above)
- ✅ Consider ad-free model (ads use data)

### Payment Methods
- **Cash-based economy**: Still dominant
- **Mobile money**: Growing — **Telebirr** (52.5M+ users), M-Pesa, HelloCash
- **Bank accounts**: CBE has most rural coverage; EthSwitch enables inter-bank transfers
- **Agricultural transactions**: Typically 50K-500K+ ETB — exceeds Telebirr balance limits

**Implications**:
- ✅ CBE bank transfer is the primary payment integration (escrow for deliveries)
- ✅ Direct bank transfer handles agricultural-scale amounts without mobile money limits
- ✅ Farmers/truckers can have accounts at any Ethiopian bank
- ✅ Consider Telebirr as supplementary channel for smaller transactions

---

## Testing Checklist

Before releasing any feature, validate:

- [ ] Works offline (disconnect network during test)
- [ ] Tested on 2GB RAM Android 8 emulator
- [ ] APK size increase <5MB
- [ ] Data usage <1MB per typical user session
- [ ] Localized in Amharic, Afan Oromo, English
- [ ] Icons/images used for key actions (not just text)
- [ ] Battery usage <5% per hour of active use
- [ ] Privacy policy updated if collecting new data
- [ ] Content moderation in place for UGC
- [ ] Tested with shared device scenario (multiple users)

---

## Resources

### Fonts
- **Noto Sans Ethiopic**: Google Fonts, supports Amharic/Oromo
- **Abyssinica SIL**: Alternative, open-source

### Translation Services
- **Community validation**: Essential for accuracy
- **Avoid**: Google Translate alone (often inaccurate for context)

### Testing Devices
- **Recommended**: Tecno Spark 7 (2GB RAM, Android 11)
- **Emulator**: Android 8.0, 2GB RAM, 480x800 screen

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0  
**Maintained by**: Solo developer (validate with local users)

