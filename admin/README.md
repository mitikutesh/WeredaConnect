# WeredaConnect Admin Dashboard

## First-Time Setup (dev)

Two one-time steps before the dashboard is usable:

**1. Create `admin/.env`** (never committed) with the dev values:

```bash
VITE_API_BASE_URL=https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com
VITE_COGNITO_ADMIN_USER_POOL_ID=eu-north-1_hSYkEt6nd
VITE_COGNITO_ADMIN_CLIENT_ID=4dntti20l1if7jfutdskjdkrmm
VITE_COGNITO_REGION=eu-north-1
```

Without this, login fails with a generic "Service temporarily unavailable"
before any credential check (Vite bakes these in at build time).

**2. Seed the admin user** — no admin exists by default. From CloudShell
(Console → `eu-north-1` → CloudShell → Actions → Upload file →
`scripts/seed-admin-user.sh`) or any shell with AWS credentials:

```bash
ADMIN_USER_POOL_ID=eu-north-1_hSYkEt6nd ADMIN_CLIENT_ID=4dntti20l1if7jfutdskjdkrmm bash seed-admin-user.sh
```

Idempotent; also resets a forgotten password. Then log in with
`+251900100009` / `AdminPass1dev`. Full details: `.ai/test-users.md`.

If the AdminUserPool is ever replaced, the IDs above change — re-read them from
the ApiStack outputs `AdminUserPoolId` / `AdminClientId` and update this file,
`.ai/test-users.md`, and the GitHub secrets (`COGNITO_ADMIN_USER_POOL_ID`,
`COGNITO_ADMIN_CLIENT_ID`) used by `deploy-admin.yml`.

## Environment Variables

Create a local `.env` (never committed) with:

| Variable | Source | Notes |
|----------|--------|-------|
| `VITE_API_BASE_URL` | API Gateway URL | dev: `https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com` |
| `VITE_COGNITO_ADMIN_USER_POOL_ID` | `AdminUserPoolId` CloudFormation output | The **admin** pool — NOT the main `UserPoolId` |
| `VITE_COGNITO_ADMIN_CLIENT_ID` | `AdminClientId` CloudFormation output | App client on the admin pool |
| `VITE_COGNITO_REGION` | Stack region | dev: `eu-north-1` |

CI/CD (`.github/workflows/deploy-admin.yml`) supplies these from GitHub Actions
secrets. **Before merging changes that use `VITE_COGNITO_ADMIN_USER_POOL_ID`,
create the `COGNITO_ADMIN_USER_POOL_ID` secret** (value = the `AdminUserPoolId`
stack output) in each deployment environment. The older `COGNITO_USER_POOL_ID`
secret holds the main user pool ID and is no longer used by this app.

---

# React + TypeScript + Vite

This template provides a minimal setup to get React working in Vite with HMR and some ESLint rules.

Currently, two official plugins are available:

- [@vitejs/plugin-react](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react) uses [Babel](https://babeljs.io/) (or [oxc](https://oxc.rs) when used in [rolldown-vite](https://vite.dev/guide/rolldown)) for Fast Refresh
- [@vitejs/plugin-react-swc](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react-swc) uses [SWC](https://swc.rs/) for Fast Refresh

## React Compiler

The React Compiler is not enabled on this template because of its impact on dev & build performances. To add it, see [this documentation](https://react.dev/learn/react-compiler/installation).

## Expanding the ESLint configuration

If you are developing a production application, we recommend updating the configuration to enable type-aware lint rules:

```js
export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      // Other configs...

      // Remove tseslint.configs.recommended and replace with this
      tseslint.configs.recommendedTypeChecked,
      // Alternatively, use this for stricter rules
      tseslint.configs.strictTypeChecked,
      // Optionally, add this for stylistic rules
      tseslint.configs.stylisticTypeChecked,

      // Other configs...
    ],
    languageOptions: {
      parserOptions: {
        project: ['./tsconfig.node.json', './tsconfig.app.json'],
        tsconfigRootDir: import.meta.dirname,
      },
      // other options...
    },
  },
])
```

You can also install [eslint-plugin-react-x](https://github.com/Rel1cx/eslint-react/tree/main/packages/plugins/eslint-plugin-react-x) and [eslint-plugin-react-dom](https://github.com/Rel1cx/eslint-react/tree/main/packages/plugins/eslint-plugin-react-dom) for React-specific lint rules:

```js
// eslint.config.js
import reactX from 'eslint-plugin-react-x'
import reactDom from 'eslint-plugin-react-dom'

export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      // Other configs...
      // Enable lint rules for React
      reactX.configs['recommended-typescript'],
      // Enable lint rules for React DOM
      reactDom.configs.recommended,
    ],
    languageOptions: {
      parserOptions: {
        project: ['./tsconfig.node.json', './tsconfig.app.json'],
        tsconfigRootDir: import.meta.dirname,
      },
      // other options...
    },
  },
])
```
