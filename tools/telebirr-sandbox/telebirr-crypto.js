"use strict";
/**
 * Shared Telebirr crypto helpers for the local sandbox simulator.
 *
 * Mirrors the signing scheme of the OgBek/Telebirr SDK and the real Telebirr
 * "SuperApp"/H5 gateway so the C# `TelebirrSandboxGateway` (which must interop
 * with this simulator) and the eventual live sandbox share ONE canonicalization.
 *
 * Canonical string (what gets signed / verified):
 *   1. Flatten top-level fields AND the nested `biz_content` object into one map.
 *   2. Drop excluded keys: sign, sign_type, header, refund_info, openType, raw_request,
 *      wallet_reference_data (Ethio Telecom's demo list).
 *   3. Drop null/undefined values; keep empty strings; booleans -> "1"/"0".
 *   4. Sort keys ascending (string sort).
 *   5. Join as `key=value&key2=value2` (NO url-encoding).
 *
 * Signature: RSA over SHA-256. Default padding PSS (saltLength 32, MGF1-SHA256)
 * to match the SDK/live Telebirr; PKCS1 selectable via TELEBIRR_SIGN_PADDING=pkcs1.
 * Output is base64.
 *
 * NOTE (Tier 3): the exact canonicalization of nested objects is the one point
 * to confirm against the Demo_Project sample / portal before hitting the live
 * sandbox. Both sides here agree, so the local flow is faithful regardless.
 */
const crypto = require("crypto");

const EXCLUDED_KEYS = new Set([
  "sign",
  "sign_type",
  "header",
  "refund_info",
  "openType",
  "raw_request",
  "wallet_reference_data",
]);

/** Flatten top-level + biz_content into a single flat map of string values. */
function flatten(obj) {
  const out = {};
  const add = (key, value) => {
    if (value === null || value === undefined) return;
    if (EXCLUDED_KEYS.has(key)) return;
    if (typeof value === "boolean") {
      out[key] = value ? "1" : "0";
    } else if (typeof value === "object") {
      // Nested object (e.g. biz_content): merge its fields up one level.
      for (const [k, v] of Object.entries(value)) add(k, v);
    } else {
      out[key] = String(value);
    }
  };
  for (const [k, v] of Object.entries(obj)) add(k, v);
  return out;
}

/** Build the canonical `key=value&...` string that gets signed/verified. */
function canonicalString(obj) {
  const flat = flatten(obj);
  return Object.keys(flat)
    .sort()
    .map((k) => `${k}=${flat[k]}`)
    .join("&");
}

function signPadding() {
  const mode = (process.env.TELEBIRR_SIGN_PADDING || "pss").toLowerCase();
  if (mode === "pkcs1") {
    return { padding: crypto.constants.RSA_PKCS1_PADDING };
  }
  return {
    padding: crypto.constants.RSA_PKCS1_PSS_PADDING,
    saltLength: 32,
  };
}

/** Sign an arbitrary object; returns base64 signature over its canonical string. */
function signObject(obj, privateKeyPem) {
  const data = canonicalString(obj);
  return signString(data, privateKeyPem);
}

function signString(data, privateKeyPem) {
  const signer = crypto.createSign("RSA-SHA256");
  signer.update(data, "utf8");
  signer.end();
  return signer.sign({ key: privateKeyPem, ...signPadding() }, "base64");
}

/** Verify base64 signature over the canonical string of `obj`. */
function verifyObject(obj, signatureB64, publicKeyPem) {
  return verifyString(canonicalString(obj), signatureB64, publicKeyPem);
}

function verifyString(data, signatureB64, publicKeyPem) {
  try {
    const verifier = crypto.createVerify("RSA-SHA256");
    verifier.update(data, "utf8");
    verifier.end();
    return verifier.verify(
      { key: publicKeyPem, ...signPadding() },
      Buffer.from(signatureB64, "base64"),
    );
  } catch {
    return false;
  }
}

/** Sign the 5 H5-URL fields (appid, merch_code, nonce_str, prepay_id, timestamp). */
function signH5(fields, privateKeyPem) {
  return signObject(fields, privateKeyPem);
}

function generateKeyPair() {
  return crypto.generateKeyPairSync("rsa", {
    modulusLength: 2048,
    publicKeyEncoding: { type: "spki", format: "pem" },
    privateKeyEncoding: { type: "pkcs8", format: "pem" },
  });
}

module.exports = {
  EXCLUDED_KEYS,
  canonicalString,
  signObject,
  signString,
  verifyObject,
  verifyString,
  signH5,
  generateKeyPair,
};
