import { describe, expect, it } from "vitest";

import am from "@/i18n/am.json";
import en from "@/i18n/en.json";
import om from "@/i18n/om.json";
import { t, type Locale } from "@/lib/i18n";

import {
  CONDUCT_RESULTS,
  CONDUCT_STEPS,
  LEFT_OUT_REASONS,
  NOT_COUNTED_REASONS,
  conductResultLabel,
  conductResultVariant,
  conductStepLabel,
  leftOutReasonLabel,
  notCountedReasonLabel,
} from "./conduct";

/* GCR-07: the labels of the conduct stamp on the transaction view, and the strings they read. */

const tEn = (key: string) => t(key, "en");

describe("conduct labels", () => {
  it("names the steps, results and reasons the server sends", () => {
    expect(conductStepLabel("pickup_fix", tEn)).toBe("GPS fix at pickup");
    expect(conductStepLabel("fair_disputes", tEn)).toBe("No dispute lost");
    expect(conductResultLabel("left_out", tEn)).toBe("Left out");
    expect(leftOutReasonLabel("no_signal", tEn)).toBe("No GPS signal in time");
    expect(notCountedReasonLabel("pair_cap", tEn)).toBe("Already 2 counted orders with the same party within 30 days");
  });

  it("reads a value it has no label for as the raw value", () => {
    expect(conductStepLabel("farm_point_measured", tEn)).toBe("farm_point_measured");
    expect(conductResultLabel("partial", tEn)).toBe("partial");
    expect(leftOutReasonLabel("flag_off", tEn)).toBe("flag_off");
    expect(notCountedReasonLabel("new_rule", tEn)).toBe("new_rule");
  });

  it("gives each result its badge", () => {
    expect(conductResultVariant("earned")).toBe("success");
    expect(conductResultVariant("missed")).toBe("warning");
    expect(conductResultVariant("left_out")).toBe("default");
    expect(conductResultVariant("unknown")).toBe("default");
  });
});

describe("conduct strings exist in every locale", () => {
  const locales: Locale[] = ["en", "am", "om"];
  const sent = [
    ...CONDUCT_STEPS.map((s) => `conduct.step_${s}`),
    ...CONDUCT_RESULTS.map((r) => `conduct.result_${r}`),
    ...LEFT_OUT_REASONS.map((r) => `conduct.reason_${r}`),
    ...NOT_COUNTED_REASONS.map((r) => `conduct.notCounted_${r}`),
  ];

  for (const locale of locales) {
    it(`${locale} has every conduct key and labels every value the server sends`, () => {
      const keys = [...Object.keys(en.conduct).map((k) => `conduct.${k}`), ...sent];
      expect(keys.filter((key) => t(key, locale) === key || t(key, locale).trim() === "")).toEqual([]);
    });
  }

  it("has the same conduct keys in en, am and om", () => {
    const sorted = (messages: { conduct: Record<string, string> }) => Object.keys(messages.conduct).sort();
    expect(sorted(am)).toEqual(sorted(en));
    expect(sorted(om)).toEqual(sorted(en));
  });

  it("keeps every placeholder the card fills in", () => {
    const placeholders = (text: string) => (text.match(/\{\w+\}/g) ?? []).sort();
    for (const key of Object.keys(en.conduct)) {
      for (const locale of locales) {
        expect([key, locale, placeholders(t(`conduct.${key}`, locale))]).toEqual([
          key,
          locale,
          placeholders(t(`conduct.${key}`, "en")),
        ]);
      }
    }
    expect(t("conduct.score", "en")).toContain("{earned}");
    expect(t("conduct.score", "en")).toContain("{offered}");
    expect(t("conduct.stepsOf", "en")).toContain("{party}");
  });
});
