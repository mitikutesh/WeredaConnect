import { describe, expect, it } from "vitest";

import am from "@/i18n/am.json";
import en from "@/i18n/en.json";
import om from "@/i18n/om.json";
import { t, type Locale } from "@/lib/i18n";

import {
  canExcuse,
  canUphold,
  dayOffset,
  decisionPath,
  endOfDayIso,
  formProblem,
  isSystemRow,
  maxUntilDays,
  needsUntil,
  type ReputationEventRow,
} from "./reputation";

/* ESC-32: the rules the reputation panel and the appeals queue share, and the strings they read. */

// 12 Nov 2026, 10:00 on the clock of whoever runs the tests: the rules follow the admin's own day.
const TODAY = new Date(2026, 10, 12, 10, 0, 0);

function event(overrides: Partial<ReputationEventRow> = {}): ReputationEventRow {
  return {
    eventId: "CANCEL#o1",
    code: "LATE_CANCEL",
    status: "active",
    points: 4,
    appeal: { status: "none" },
    ...overrides,
  };
}

describe("which events an admin can decide", () => {
  it("excuses an active penalty, appealed or not", () => {
    expect(canExcuse(event())).toBe(true);
    expect(canExcuse(event({ appeal: { status: "pending" } }))).toBe(true);
    expect(canExcuse(event({ code: "DELIVERY_OVERDUE", appeal: { status: "review" } }))).toBe(true);
  });

  it("does not excuse an event that is excused, expired, upheld, not a penalty or a system row", () => {
    expect(canExcuse(event({ status: "excused", appeal: { status: "excused" } }))).toBe(false);
    expect(canExcuse(event({ status: "expired" }))).toBe(false);
    expect(canExcuse(event({ appeal: { status: "upheld" } }))).toBe(false);
    expect(canExcuse(event({ code: "ORDER_COMPLETED" }))).toBe(false);
    expect(canExcuse(event({ code: "CANCELLED_TRANSPORT_FAILED" }))).toBe(false);
    expect(canExcuse(event({ code: "RESTRICTION", appeal: undefined }))).toBe(false);
  });

  it("upholds only an open appeal or review", () => {
    expect(canUphold(event({ appeal: { status: "pending" } }))).toBe(true);
    expect(canUphold(event({ appeal: { status: "review" } }))).toBe(true);
    expect(canUphold(event())).toBe(false);
    expect(canUphold(event({ appeal: { status: "upheld" } }))).toBe(false);
    expect(canUphold(event({ code: "TIER_CHANGED", appeal: undefined }))).toBe(false);
  });

  it("tells a system row by its missing appeal", () => {
    expect(isSystemRow(event())).toBe(false);
    expect(isSystemRow(event({ code: "ADMIN_TAG", appeal: undefined }))).toBe(true);
  });
});

describe("the reason and the end date of a reputation write", () => {
  it("needs a reason of 3 to 500 characters after trimming", () => {
    expect(formProblem("  no  ", "", false, 365, TODAY)).toBe("reason");
    expect(formProblem("x".repeat(501), "", false, 365, TODAY)).toBe("reason");
    expect(formProblem("yes", "", false, 365, TODAY)).toBeNull();
    expect(formProblem("x".repeat(500), "", false, 365, TODAY)).toBeNull();
  });

  it("needs an end date when the action takes one, after today and inside the limit", () => {
    expect(formProblem("A reason.", "", true, 90, TODAY)).toBe("until");
    expect(formProblem("A reason.", "2026-11-11", true, 90, TODAY)).toBe("until");
    expect(formProblem("A reason.", "not a date", true, 90, TODAY)).toBe("until");
    expect(formProblem("A reason.", "2026-11-13", true, 90, TODAY)).toBeNull();
    // 90 days after 12 Nov 10:00 is 10 Feb 10:00: the 9th still ends inside it, the 10th does not.
    expect(formProblem("A reason.", "2027-02-09", true, 90, TODAY)).toBeNull();
    expect(formProblem("A reason.", "2027-02-10", true, 90, TODAY)).toBe("until");
  });

  it("lets an optional end date stay empty but checks one that is given", () => {
    expect(formProblem("A reason.", "", false, 365, TODAY)).toBeNull();
    expect(formProblem("A reason.", "2026-01-01", false, 365, TODAY)).toBe("until");
  });

  it("knows which actions take an end date and how far it may be", () => {
    expect(needsUntil("tag")).toBe(true);
    expect(needsUntil("untag")).toBe(true);
    expect(needsUntil("restrict")).toBe(true);
    expect(needsUntil("lift")).toBe(false);
    expect(maxUntilDays("tag")).toBe(90);
    expect(maxUntilDays("untag")).toBe(90);
    expect(maxUntilDays("restrict")).toBe(365);
    expect(maxUntilDays("uphold")).toBe(365);
  });

  it("sends the end of the chosen day on the admin's clock and bounds the date field", () => {
    const end = new Date(endOfDayIso("2026-12-01"));
    expect([end.getFullYear(), end.getMonth(), end.getDate(), end.getHours(), end.getMinutes()]).toEqual([
      2026, 11, 1, 23, 59,
    ]);
    expect(endOfDayIso("2026-12-01")).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.000Z$/);
    expect(endOfDayIso("soon")).toBe("");
    expect(dayOffset(TODAY, 1)).toBe("2026-11-13");
    expect(dayOffset(TODAY, 89)).toBe("2027-02-09");
  });

  it("encodes the event id in the decision path", () => {
    expect(decisionPath("farmer 1", "CANCEL#ord-7f3a")).toBe(
      "/admin/users/farmer%201/reputation/events/CANCEL%23ord-7f3a/decision",
    );
  });
});

describe("reputation strings exist in every locale", () => {
  const locales: Locale[] = ["en", "am", "om"];
  const keys = [
    ...Object.keys(en.reputation).map((k) => `reputation.${k}`),
    "nav.reputationAppeals",
    "errors.ADMIN_001",
    "errors.REPUTATION_VALIDATION",
    "errors.REPUTATION_NOT_FOUND",
    "errors.REPUTATION_EVENT_NOT_FOUND",
    "errors.REPUTATION_APPEAL_CLOSED",
    "errors.REPUTATION_APPEAL_CHANGED",
    "errors.REPUTATION_EVENT_DECIDED",
    "errors.REPUTATION_NOT_RESTRICTED",
    "errors.REPUTATION_ALREADY_RESTRICTED",
    "errors.REPUTATION_UNAVAILABLE",
    "errors.REPUTATION_SERVER_ERROR",
  ];

  for (const locale of locales) {
    it(`${locale} has every reputation key, the nav label and the error codes`, () => {
      const missing = keys.filter((key) => t(key, locale) === key || t(key, locale).trim() === "");
      expect(missing).toEqual([]);
    });
  }

  it("has the same reputation keys in en, am and om", () => {
    const sorted = (messages: { reputation: Record<string, string> }) => Object.keys(messages.reputation).sort();
    expect(sorted(am)).toEqual(sorted(en));
    expect(sorted(om)).toEqual(sorted(en));
  });

  it("keeps every placeholder the pages fill in", () => {
    const placeholders = (text: string) => (text.match(/\{\w+\}/g) ?? []).sort();
    for (const key of Object.keys(en.reputation)) {
      for (const locale of locales) {
        expect([key, locale, placeholders(t(`reputation.${key}`, locale))]).toEqual([
          key,
          locale,
          placeholders(t(`reputation.${key}`, "en")),
        ]);
      }
    }
  });

  it("labels every event code, zero reason and status the server sends", () => {
    const codes = [
      "DECLINED_REQUEST",
      "RESPONSE_EXPIRED",
      "CANCELLED_AFTER_ACCEPT",
      "LATE_CANCEL",
      "TRIP_CANCELLED_WITH_BOOKINGS",
      "NO_SHOW",
      "DELIVERY_OVERDUE",
      "CANCELLED_TRANSPORT_FAILED",
      "ORDER_COMPLETED",
      "RESTRICTION",
      "ADMIN_TAG",
      "ADMIN_UNTAG",
      "TIER_CHANGED",
    ].map((c) => `reputation.code_${c}`);
    const zero = [
      "suspended",
      "restricted",
      "short_window",
      "pair_cap",
      "daily_cap",
      "request_cap",
      "not_reachable",
      "timeout_penalties_off",
      "no_fault",
    ].map((z) => `reputation.zero_${z}`);
    const statuses = ["active", "expired", "excused", "ended", "lifted", "replaced"].map((s) => `reputation.status_${s}`);
    const others = [
      ...["good", "warning", "low", "restricted"].map((x) => `reputation.tier_${x}`),
      ...["points", "delivery_overdue", "admin"].map((x) => `reputation.cause_${x}`),
      ...["pending", "review", "excused", "upheld"].map((x) => `reputation.appeal_${x}`),
      ...["tag", "untag", "restrict", "lift"].flatMap((x) => [
        `reputation.action_${x}`,
        `reputation.help_${x}`,
        `reputation.done_${x}`,
      ]),
      ...["excuse", "uphold"].flatMap((x) => [`reputation.title_${x}`, `reputation.help_${x}`, `reputation.done_${x}`]),
    ];
    const missing = [...codes, ...zero, ...statuses, ...others].filter((key) => t(key, "en") === key);
    expect(missing).toEqual([]);
  });
});
