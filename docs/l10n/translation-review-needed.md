# Translation Review Needed - Afan Oromo (om)

## Status
**Created**: 2026-02-14
**Priority**: HIGH
**Affects**: 33 authentication-related strings in `/mobile/lib/shared/l10n/app_om.arb`

## Background

During Sprint 3 code quality review, the Afan Oromo translations were flagged as potentially being Google Translate quality rather than culturally appropriate Ethiopian Oromo linguistic conventions.

## Critical Terms Requiring Native Review

### User Roles (Agricultural Context)

| English | Current Oromo | Status | Notes |
|---------|---------------|--------|-------|
| `farmer` | Qonnaan Bultaa | NEEDS_REVIEW | Verify this is the term Ethiopian Oromo farmers self-identify with |
| `trucker` | Konkolaataa Fe'aa | NEEDS_REVIEW | Verify transport/logistics terminology used in rural Ethiopia |
| `buyer` | Bitaa | NEEDS_REVIEW | Confirm this represents wholesale/market buyers, not general shoppers |

**Context**: These roles are core to the WeredaConnect platform. They must use terms that rural Ethiopian Oromo speakers actually use in agricultural/market contexts.

### Technical Terms

| English | Current Oromo | Status | Notes |
|---------|---------------|--------|-------|
| `password` | Jecha Icciitii | NEEDS_REVIEW | Is this the common term for password in Oromo tech contexts? |
| `passwordRequirements` | Jecha icciitiin yoo xiqqaate qubeelee 6 ta'uu qaba | NEEDS_REVIEW | Verify this phrasing is natural for password requirements |
| `enterPhoneNumber` | Lakkoofsa Bilbilaa galchi (implied) | NEEDS_REVIEW | Check phone number terminology |
| `offlineMode` | Haalata Offline | NEEDS_REVIEW | Is "Offline" kept as loanword or should it be translated? |

### Authentication Flow

| English | Current Oromo | Status | Notes |
|---------|---------------|--------|-------|
| `register` | Galmaa'i | NEEDS_REVIEW | Correct verb form for registration? |
| `login` | Seeni | NEEDS_REVIEW | Natural term for logging in? |
| `logout` | Ba'i | NEEDS_REVIEW | Natural term for logging out? |
| `verifyOtp` | Mirkaneessi | NEEDS_REVIEW | Is "mirkaneessi" the right term for OTP verification? |

### Error Messages

| English | Current Oromo | Status | Notes |
|---------|---------------|--------|-------|
| `invalidCredentials` | Lakkoofsi bilbilaa ykn jecha icciitii sirrii miti | NEEDS_REVIEW | Natural phrasing for error messages? |
| `phoneAlreadyRegistered` | Lakkoofsi bilbilaa kun duraanuu galmaa'eera | NEEDS_REVIEW | Grammatically correct? |
| `otpExpired` | Koodiin mirkaneessaa darbeeera | NEEDS_REVIEW | "Darbeeera" means expired/passed? |

## Geographic Terms (Keep as Proper Nouns)

These should NOT be translated:
- `wereda` → "Aanaa" (correctly translated, as it's a common noun meaning "district")
- `kebele` → "Gandaa" (correctly translated, as it's a common noun meaning "neighborhood")

## Validation Criteria

For each translation to pass review, it must meet:

1. **Linguistic accuracy**: Grammatically correct Afan Oromo
2. **Cultural appropriateness**: Uses terms rural Ethiopian Oromo speakers actually use
3. **Agricultural context**: Terminology reflects farming/market/logistics domains
4. **Natural phrasing**: Not word-for-word translations, but contextually adapted

## Recommended Validation Process

### 1. Community Validation (Ideal)
- Share translations with native Afan Oromo speakers from rural Ethiopia
- Specifically target users in agricultural/logistics sectors
- Test with users in target regions (e.g., Oromia Region)

### 2. Expert Review (Minimum)
- Consult with professional Afan Oromo translators
- Prioritize translators with agricultural/rural development experience
- Cross-reference with existing agricultural extension materials in Oromo

### 3. User Testing
- Beta test app with Oromo-speaking farmers
- Observe which terms cause confusion
- Iterate based on feedback

## Current Status: BLOCKED_ON_NATIVE_REVIEW

The current translations are functional but may not be optimal. Deployment can proceed, but translations should be flagged for improvement in next iteration.

## Action Items

- [ ] Find native Afan Oromo speaker with agricultural background
- [ ] Schedule translation review session
- [ ] Update `app_om.arb` with validated translations
- [ ] Document any terminology decisions in this file
- [ ] Re-test with Oromo-speaking users

## Resources

- See `/docs/local_reality/README.md` for Ethiopian context
- Afan Oromo language resources: https://en.wikipedia.org/wiki/Oromo_language
- Ethiopian agricultural terminology references: [TBD]

## Notes

**Why this matters**: According to `/docs/local_reality/README.md`:
> "Afan Oromo (Afaan Oromoo): ~40M speakers"

This is the largest language group in Ethiopia. Poor translations will alienate our primary user base.

**Current approach**: Translations use standard Oromo spelling and grammar, but need verification that terminology matches real-world usage in rural agricultural contexts, not just dictionary definitions.

---

**Last Updated**: 2026-02-14
**Reviewer**: Code Quality Fix Agent (automated check)
**Next Review**: [Pending native speaker assignment]
