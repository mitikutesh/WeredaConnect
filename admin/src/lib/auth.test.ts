import { beforeEach, describe, expect, it, vi } from "vitest";

// Fake Cognito SDK. Each test scripts what authenticateUser and the
// follow-up calls do by assigning the behaviour hooks below.
type Callbacks = Record<string, (...args: unknown[]) => void>;

const sdk = vi.hoisted(() => {
  type Cb = Record<string, (...args: unknown[]) => void>;
  const noop = () => {};
  return {
    authenticate: noop as (cb: Cb) => void,
    sendMFACode: noop as (code: string, cb: Cb, type?: string) => void,
    associate: noop as (cb: Cb) => void,
    verify: noop as (code: string, name: string, cb: Cb) => void,
    newPassword: noop as (pw: string, cb: Cb) => void,
    selectMfa: noop as (answer: string, cb: Cb) => void,
    groups: ["admin"] as string[],
    signOut: vi.fn(),
    flowType: "",
  };
});

function fakeSession(groups: string[]) {
  return {
    getIdToken: () => ({
      decodePayload: () => ({ "cognito:groups": groups, phone_number: "+251900000001" }),
      getJwtToken: () => "id-token",
    }),
    getAccessToken: () => ({ getJwtToken: () => "access-token" }),
    isValid: () => true,
  };
}

vi.mock("amazon-cognito-identity-js", () => {
  class CognitoUserPool {
    getCurrentUser() {
      return null;
    }
  }
  class AuthenticationDetails {}
  class CognitoUser {
    setAuthenticationFlowType(t: string) {
      sdk.flowType = t;
    }
    authenticateUser(_d: unknown, cb: Callbacks) {
      sdk.authenticate(cb);
    }
    sendMFACode(code: string, cb: Callbacks, type?: string) {
      sdk.sendMFACode(code, cb, type);
    }
    associateSoftwareToken(cb: Callbacks) {
      sdk.associate(cb);
    }
    verifySoftwareToken(code: string, name: string, cb: Callbacks) {
      sdk.verify(code, name, cb);
    }
    completeNewPasswordChallenge(pw: string, _attrs: unknown, cb: Callbacks) {
      sdk.newPassword(pw, cb);
    }
    sendMFASelectionAnswer(answer: string, cb: Callbacks) {
      sdk.selectMfa(answer, cb);
    }
    signOut() {
      sdk.signOut();
    }
  }
  return { CognitoUserPool, CognitoUser, AuthenticationDetails };
});

import * as auth from "./auth";

const PHONE = "+251900000001";

describe("admin login with MFA challenges", () => {
  beforeEach(() => {
    vi.stubEnv("VITE_COGNITO_ADMIN_USER_POOL_ID", "eu-north-1_pool");
    vi.stubEnv("VITE_COGNITO_ADMIN_CLIENT_ID", "client");
    sdk.groups = ["admin"];
    sdk.signOut.mockReset();
    sdk.authenticate = (cb) => cb.onSuccess(fakeSession(sdk.groups));
    sdk.sendMFACode = (_c, cb) => cb.onSuccess(fakeSession(sdk.groups));
    sdk.associate = (cb) => cb.associateSecretCode("SECRET234");
    sdk.verify = (_c, _n, cb) => cb.onSuccess(fakeSession(sdk.groups));
    sdk.newPassword = (_p, cb) => cb.onSuccess(fakeSession(sdk.groups));
    sdk.selectMfa = (_a, cb) => cb.totpRequired("SOFTWARE_TOKEN_MFA", {});
    auth.logout();
  });

  it("keeps USER_PASSWORD_AUTH and signs in an admin without challenges", async () => {
    await expect(auth.login(PHONE, "pw")).resolves.toEqual({ status: "success" });
    expect(sdk.flowType).toBe("USER_PASSWORD_AUTH");
    expect(auth.isAdmin()).toBe(true);
  });

  it("rejects a non-admin even after a successful TOTP code", async () => {
    sdk.groups = ["farmer"];
    sdk.authenticate = (cb) => cb.totpRequired("SOFTWARE_TOKEN_MFA", {});
    await auth.login(PHONE, "pw");
    await expect(auth.submitTotp("123456")).resolves.toEqual({
      status: "error",
      error: "notAdmin",
    });
    expect(sdk.signOut).toHaveBeenCalled();
    expect(auth.getIdToken()).toBeNull();
  });

  it("returns totpRequired and answers with SOFTWARE_TOKEN_MFA", async () => {
    sdk.authenticate = (cb) => cb.totpRequired("SOFTWARE_TOKEN_MFA", {});
    const sent: Array<[string, string | undefined]> = [];
    sdk.sendMFACode = (c, cb, type) => {
      sent.push([c, type]);
      cb.onSuccess(fakeSession(sdk.groups));
    };

    await expect(auth.login(PHONE, "pw")).resolves.toEqual({
      status: "totpRequired",
      mfaType: "totp",
    });
    expect(auth.hasPendingChallenge()).toBe(true);
    await expect(auth.submitTotp(" 123456 ")).resolves.toEqual({ status: "success" });
    expect(sent).toEqual([["123456", "SOFTWARE_TOKEN_MFA"]]);
    expect(auth.hasPendingChallenge()).toBe(false);
  });

  it("treats SMS MFA like TOTP but answers with SMS_MFA", async () => {
    sdk.authenticate = (cb) => cb.mfaRequired("SMS_MFA", {});
    let type: string | undefined;
    sdk.sendMFACode = (_c, cb, t) => {
      type = t;
      cb.onSuccess(fakeSession(sdk.groups));
    };
    await expect(auth.login(PHONE, "pw")).resolves.toEqual({
      status: "totpRequired",
      mfaType: "sms",
    });
    await auth.submitTotp("654321");
    expect(type).toBe("SMS_MFA");
  });

  it("maps a wrong code to invalidCode and allows a retry", async () => {
    sdk.authenticate = (cb) => cb.totpRequired("SOFTWARE_TOKEN_MFA", {});
    sdk.sendMFACode = (_c, cb) => cb.onFailure({ code: "CodeMismatchException" });
    await auth.login(PHONE, "pw");
    await expect(auth.submitTotp("000000")).resolves.toEqual({
      status: "error",
      error: "invalidCode",
    });
    expect(auth.hasPendingChallenge()).toBe(true);

    sdk.sendMFACode = (_c, cb) => cb.onSuccess(fakeSession(sdk.groups));
    await expect(auth.submitTotp("123456")).resolves.toEqual({ status: "success" });
  });

  it("maps ExpiredCodeException and an expired session", async () => {
    sdk.authenticate = (cb) => cb.totpRequired("SOFTWARE_TOKEN_MFA", {});
    await auth.login(PHONE, "pw");
    sdk.sendMFACode = (_c, cb) => cb.onFailure({ code: "ExpiredCodeException" });
    await expect(auth.submitTotp("111111")).resolves.toEqual({
      status: "error",
      error: "codeExpired",
    });
    sdk.sendMFACode = (_c, cb) => cb.onFailure({ code: "NotAuthorizedException" });
    await expect(auth.submitTotp("222222")).resolves.toEqual({
      status: "error",
      error: "sessionExpired",
    });
    expect(auth.hasPendingChallenge()).toBe(false);
  });

  it("rejects a malformed code without calling Cognito", async () => {
    sdk.authenticate = (cb) => cb.totpRequired("SOFTWARE_TOKEN_MFA", {});
    const spy = vi.fn();
    sdk.sendMFACode = spy;
    await auth.login(PHONE, "pw");
    await expect(auth.submitTotp("12ab")).resolves.toEqual({
      status: "error",
      error: "invalidCode",
    });
    expect(spy).not.toHaveBeenCalled();
  });

  it("returns sessionExpired when no challenge is pending", async () => {
    await expect(auth.submitTotp("123456")).resolves.toEqual({
      status: "error",
      error: "sessionExpired",
    });
  });

  it("associates a software token on mfaSetup and builds the otpauth URL", async () => {
    sdk.authenticate = (cb) => cb.mfaSetup("MFA_SETUP", {});
    const result = await auth.login(PHONE, "pw");
    expect(result.status).toBe("mfaSetupRequired");
    if (result.status !== "mfaSetupRequired") return;
    expect(result.secretCode).toBe("SECRET234");
    const url = new URL(result.otpauthUrl);
    expect(url.protocol).toBe("otpauth:");
    expect(result.otpauthUrl.startsWith(
      "otpauth://totp/WeredaConnect%20Admin:%2B251900000001?",
    )).toBe(true);
    expect(url.searchParams.get("secret")).toBe("SECRET234");
    expect(url.searchParams.get("issuer")).toBe("WeredaConnect Admin");
    expect(result.otpauthUrl).toContain("issuer=WeredaConnect%20Admin");
  });

  it("completes MFA setup by verifying the token and continuing to success", async () => {
    sdk.authenticate = (cb) => cb.mfaSetup("MFA_SETUP", {});
    let args: [string, string] | null = null;
    sdk.verify = (c, n, cb) => {
      args = [c, n];
      cb.onSuccess(fakeSession(sdk.groups));
    };
    await auth.login(PHONE, "pw");
    await expect(auth.completeMfaSetup("123456", "Pixel")).resolves.toEqual({
      status: "success",
    });
    expect(args).toEqual(["123456", "Pixel"]);
    expect(auth.isAdmin()).toBe(true);
  });

  it("maps a wrong setup code (EnableSoftwareTokenMFAException) to invalidCode", async () => {
    sdk.authenticate = (cb) => cb.mfaSetup("MFA_SETUP", {});
    sdk.verify = (_c, _n, cb) => cb.onFailure({ code: "EnableSoftwareTokenMFAException" });
    await auth.login(PHONE, "pw");
    await expect(auth.completeMfaSetup("000000", "")).resolves.toEqual({
      status: "error",
      error: "invalidCode",
    });
  });

  it("handles newPasswordRequired and can chain into MFA setup", async () => {
    sdk.authenticate = (cb) => cb.newPasswordRequired({}, []);
    sdk.newPassword = (_p, cb) => cb.mfaSetup("MFA_SETUP", {});
    await expect(auth.login(PHONE, "temp")).resolves.toEqual({
      status: "newPasswordRequired",
    });
    const next = await auth.completeNewPassword("N3w-Password!");
    expect(next.status).toBe("mfaSetupRequired");
  });

  it("maps InvalidPasswordException on the new password step", async () => {
    sdk.authenticate = (cb) => cb.newPasswordRequired({}, []);
    sdk.newPassword = (_p, cb) => cb.onFailure({ code: "InvalidPasswordException" });
    await auth.login(PHONE, "temp");
    await expect(auth.completeNewPassword("short")).resolves.toEqual({
      status: "error",
      error: "passwordPolicy",
    });
  });

  it("picks TOTP when Cognito asks to select an MFA type", async () => {
    let answer = "";
    sdk.authenticate = (cb) => cb.selectMFAType("SELECT_MFA_TYPE", {});
    sdk.selectMfa = (a, cb) => {
      answer = a;
      cb.totpRequired("SOFTWARE_TOKEN_MFA", {});
    };
    await expect(auth.login(PHONE, "pw")).resolves.toEqual({
      status: "totpRequired",
      mfaType: "totp",
    });
    expect(answer).toBe("SOFTWARE_TOKEN_MFA");
  });

  it("keeps invalidCredentials for a bad password", async () => {
    sdk.authenticate = (cb) => cb.onFailure({ code: "NotAuthorizedException" });
    await expect(auth.login(PHONE, "bad")).resolves.toEqual({
      status: "error",
      error: "invalidCredentials",
    });
  });
});
