# Registration User Flow

## Overview

Multi-step registration flow with Fayda eKYC identity verification.
Farmers have 3 steps (skip role details), Truckers/Buyers have 4 steps.

## Main Flow

```mermaid
flowchart TD
    Start([User opens app]) --> Login[Login Screen]
    Login -->|Tap Register| R0

    subgraph REG["Registration Flow"]
        R0["Step 0: Role Selection"]
        R1["Step 1: Fayda eKYC Verification"]
        R2["Step 2: Role Details"]
        R3["Step 3: Personal Info"]

        R0 -->|Select Farmer| R1
        R0 -->|Select Trucker| R1
        R0 -->|Select Buyer| R1

        R1 -->|Verified / Skip| RoleCheck{Role?}
        RoleCheck -->|Farmer| R3
        RoleCheck -->|Trucker| R2T["Driving License Photo"]
        RoleCheck -->|Buyer| R2B["Buyer Type Selection"]

        R2T --> R3
        R2B --> R3

        R3 -->|Tap Sign Up| Validate{Form valid?}
        Validate -->|No| R3
        Validate -->|Yes| Submit[Submit Registration]
    end

    Submit -->|POST /auth/register| Server{Server Response}
    Server -->|Success| Cache[Cache tokens & profile locally]
    Cache --> Home([Main App Screen])
    Server -->|Error| ErrSnack[Show error snackbar]
    ErrSnack --> R3

    Login -->|Back| Start
```

## Step 0: Role Selection

```mermaid
flowchart TD
    Enter([Enter Step 0]) --> Show[Display 3 role cards]
    Show --> F["Farmer\n'Lists crops for sale'"]
    Show --> T["Trucker\n'Provides transport services'"]
    Show --> B["Buyer\n'Purchases crops'"]

    F -->|Tap| Selected[Card highlighted green + checkmark]
    T -->|Tap| Selected
    B -->|Tap| Selected

    Selected -->|Continue enabled| Next([Step 1: Fayda])
```

## Step 1: Fayda eKYC Verification (OIDC)

```mermaid
flowchart TD
    Enter([Enter Step 1]) --> Pending[Show 'Verify with Fayda' button]

    Pending -->|Tap Verify| ConfigCheck{Fayda\nconfigured?}
    ConfigCheck -->|No| ConfigErr[Show config error]
    ConfigCheck -->|Yes| GenState[Generate CSRF state]

    GenState --> BuildURL[Build OIDC authorize URL\nscope: openid profile\nacr: biometrics]
    BuildURL --> Launch{Launch browser?}
    Launch -->|Failed| Unavailable[Show 'Fayda unavailable']
    Launch -->|Success| Browser[External browser opens\nFayda login page]

    Browser --> FaydaAuth[User authenticates\nBiometric + OTP]
    FaydaAuth -->|Success| Callback["Deep link callback\nweredaconnect://fayda-callback\n?code=ABC&state=XYZ"]
    FaydaAuth -->|Denied / Error| ErrCallback["Callback with\nerror param"]

    Callback --> StateCheck{State matches?}
    StateCheck -->|No CSRF match| Rejected[Show verification failed]
    StateCheck -->|Yes| Exchange["Exchange code\nPOST /auth/fayda-verify"]

    Exchange -->|Success| Verified["Verified!\nShow green checkmark\nPre-fill name"]
    Exchange -->|Error| ExErr[Show error + retry]

    ErrCallback --> Rejected

    Browser -->|User closes browser\n2s timeout| Timeout[Reset to pending\nShow error]

    Verified -->|Continue| Next([Next Step])
    Pending -->|Skip / Continue| Next

    Unavailable --> Pending
    Rejected --> Pending
    ExErr --> Pending
    Timeout --> Pending
```

## Step 2: Role Details (Trucker / Buyer only)

```mermaid
flowchart TD
    Enter([Enter Step 2]) --> RoleCheck{Role?}

    RoleCheck -->|Farmer| Skip["SKIPPED\n(goes directly to Step 3)"]

    RoleCheck -->|Trucker| License[Show camera capture UI]
    License -->|Tap Scan License| Camera[Open camera]
    Camera --> Photo[Capture & compress photo\n1200x800 max, quality 80]
    Photo --> Preview[Show thumbnail + checkmark]
    Preview -->|Continue| Next([Step 3])
    License -->|Continue without| Next

    RoleCheck -->|Buyer| BuyerType[Show buyer type cards]
    BuyerType --> Private["Private Buyer\n(person icon)"]
    BuyerType --> Company["Company Buyer\n(business icon)"]
    Private -->|Select| Next
    Company -->|Select| CompanyName[Enter company name]
    CompanyName -->|Continue| Next
```

## Step 3: Personal Info & Submit

```mermaid
flowchart TD
    Enter([Enter Step 3]) --> Form[Show form fields]

    Form --> Phone["Phone Number\n+251XXXXXXXXX\n(required)"]
    Form --> Name["Full Name\n(pre-filled from Fayda if verified)\n(required)"]
    Form --> Woreda["Woreda / District\n(required)"]
    Form --> Pass["Password\n8+ chars, lowercase + digit\n(required)"]
    Form --> Confirm["Confirm Password\n(must match)\n(required)"]

    Phone --> SignUp[Tap 'Sign Up']
    Name --> SignUp
    Woreda --> SignUp
    Pass --> SignUp
    Confirm --> SignUp

    SignUp --> V{Validate form}
    V -->|Phone invalid| ErrPhone["'+251' + 9 digits required"]
    V -->|Name empty| ErrName[Field required]
    V -->|Woreda empty| ErrWoreda[Field required]
    V -->|Password weak| ErrPass[Show requirements]
    V -->|Passwords mismatch| ErrMatch[Passwords do not match]
    V -->|All valid| Dispatch["Dispatch AuthRegisterRequested\nto BLoC"]

    ErrPhone --> Form
    ErrName --> Form
    ErrWoreda --> Form
    ErrPass --> Form
    ErrMatch --> Form

    Dispatch --> Loading[Show spinner on button]
    Loading --> API["POST /auth/register\n{phone, password, name,\nrole, woreda, language,\nfaydaToken}"]

    API -->|201 Created| Success["Receive tokens + user profile\nCache in secure storage + Isar"]
    Success --> Auth([Authenticated - Main App])

    API -->|Phone taken| E1[phoneAlreadyRegistered]
    API -->|Rate limited| E2[authRateLimitExceeded]
    API -->|Fayda invalid| E3[faydaVerificationFailed]
    API -->|Fayda linked| E4[faydaAlreadyLinked]
    API -->|Network error| E5[authNetworkError]

    E1 --> Snackbar[Show localized error snackbar\n5s duration, floating]
    E2 --> Snackbar
    E3 --> Snackbar
    E4 --> Snackbar
    E5 --> Snackbar
    Snackbar --> Form
```

## Complete Path Summary

```mermaid
flowchart LR
    subgraph Farmer["Farmer Path (3 steps)"]
        F0[Role Selection] --> F1[Fayda Verify] --> F3[Personal Info]
    end

    subgraph Trucker["Trucker Path (4 steps)"]
        T0[Role Selection] --> T1[Fayda Verify] --> T2[Driving License] --> T3[Personal Info]
    end

    subgraph Buyer["Buyer Path (4 steps)"]
        B0[Role Selection] --> B1[Fayda Verify] --> B2[Buyer Type] --> B3[Personal Info]
    end
```
