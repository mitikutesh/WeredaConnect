import { useState } from "react";
import { Link } from "react-router";
import { Card } from "@/components/ui/Card";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { useListingReports, useReviewListingReports, type ListingReportStatus } from "@/hooks/useQuery";
import { formatDate, formatDateTime } from "@/lib/i18n";
import { partyName } from "@/lib/user";

/* FUP-67 (listing-report-api.md 1.0.0): buyers' reports of crop listings and trips. Buyers see every
 * listing, so they report what no check catches (a phone number in a voice note or a photo, a scam).
 * The reports of one listing are decided together: dismissed (nothing wrong) or actioned (the admin
 * acted, for example suspended the owner on the user page). Reporter ids are for admins only. */

interface ListingReport {
  listingId: string;
  listingType: string;
  ownerId: string;
  reporterId: string;
  reason: string;
  status: string;
  createdAt: string;
  reviewedAt?: string;
  reviewedBy?: string;
  reviewNote?: string;
}

interface ReportedListing {
  listingId: string;
  listingType: string;
  found: boolean;
  status?: string;
  ownerName?: string;
  cropType?: string;
  description?: string;
  audioNoteRef?: string;
  fromWoreda?: string;
  toWoreda?: string;
  departureDate?: string;
}

interface ListingGroup {
  listingId: string;
  listingType: string;
  ownerId: string;
  listing?: ReportedListing;
  reports: ListingReport[];
}

const NOTE_MAX = 500;

const reasonVariant: Record<string, "danger" | "warning" | "default"> = {
  contact_info: "warning",
  scam: "danger",
  misleading: "warning",
  offensive: "danger",
  other: "default",
};

function fill(template: string, values: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (_, k: string) => values[k] ?? "");
}

export function ListingReportsPage() {
  const { t, locale } = useLocale();
  const [tab, setTab] = useState<ListingReportStatus>("open");
  const { data, isLoading, isError, hasNextPage, fetchNextPage, isFetchingNextPage } = useListingReports(tab);
  const review = useReviewListingReports();
  const [notes, setNotes] = useState<Record<string, string>>({});
  const [submitting, setSubmitting] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);

  const pages = (data?.pages ?? []) as { reports?: ListingReport[]; listings?: ReportedListing[] }[];
  const listings = new Map<string, ReportedListing>();
  for (const page of pages) {
    for (const listing of page.listings ?? []) listings.set(`${listing.listingType}:${listing.listingId}`, listing);
  }

  // One group per listing, in the order the queue gives them; a retried page cannot double a report.
  const groups: ListingGroup[] = [];
  const byKey = new Map<string, ListingGroup>();
  const seen = new Set<string>();
  for (const page of pages) {
    for (const report of page.reports ?? []) {
      const reportKey = `${report.listingId}:${report.reporterId}`;
      if (seen.has(reportKey)) continue;
      seen.add(reportKey);
      const key = `${report.listingType}:${report.listingId}`;
      let group = byKey.get(key);
      if (!group) {
        group = {
          listingId: report.listingId,
          listingType: report.listingType,
          ownerId: report.ownerId,
          listing: listings.get(key),
          reports: [],
        };
        byKey.set(key, group);
        groups.push(group);
      }
      group.reports.push(report);
    }
  }

  function label(key: string, fallback: string): string {
    const text = t(key);
    return text === key ? fallback : text;
  }

  function changeTab(next: ListingReportStatus) {
    setTab(next);
    setNotice(null);
    setErrorMsg(null);
  }

  function decide(group: ListingGroup, decision: "dismissed" | "actioned") {
    const note = (notes[group.listingId] ?? "").trim();
    setSubmitting(group.listingId);
    setNotice(null);
    setErrorMsg(null);
    review.mutate(
      { listingId: group.listingId, decision, note: note || undefined },
      {
        onSuccess: (result) => {
          const count = Number(result?.reviewed ?? 0);
          setNotice(
            count === 0
              ? t("listingReports.alreadyDecided")
              : fill(t(decision === "dismissed" ? "listingReports.dismissedNotice" : "listingReports.actionedNotice"), {
                  count: String(count),
                }),
          );
          setNotes((prev) => {
            const next = { ...prev };
            delete next[group.listingId];
            return next;
          });
        },
        onError: (err: unknown) => {
          const code = (err as { code?: string }).code;
          setErrorMsg(code ? label(`errors.${code}`, t("errors.actionFailed")) : t("errors.actionFailed"));
        },
        onSettled: () => setSubmitting(null),
      },
    );
  }

  function listingSummary(group: ListingGroup) {
    const listing = group.listing;
    if (!listing || !listing.found) {
      return <p className="text-sm text-warning">{t("listingReports.listingGone")}</p>;
    }
    return (
      <div className="space-y-1 text-sm">
        {group.listingType === "crop" ? (
          <p className="font-semibold">{listing.cropType ?? group.listingId}</p>
        ) : (
          <p className="font-semibold">
            {fill(t("listingReports.route"), { from: listing.fromWoreda ?? "", to: listing.toWoreda ?? "" })}
            {listing.departureDate && (
              <span className="ml-2 font-normal text-text-muted">
                {fill(t("listingReports.departs"), { date: formatDate(listing.departureDate) })}
              </span>
            )}
          </p>
        )}
        {listing.status && (
          <p className="text-text-muted">{fill(t("listingReports.listingStatus"), { status: listing.status })}</p>
        )}
        {listing.description && <p className="max-w-3xl whitespace-pre-line">{listing.description}</p>}
        {listing.audioNoteRef && (
          <p className="text-warning">{fill(t("listingReports.audioNote"), { ref: listing.audioNoteRef })}</p>
        )}
      </div>
    );
  }

  const tabButton = (value: ListingReportStatus, text: string) => (
    <button
      type="button"
      aria-pressed={tab === value}
      onClick={() => changeTab(value)}
      className={`rounded px-3 py-1 text-sm ${
        tab === value ? "bg-primary text-white" : "border border-gray-300 hover:bg-gray-50"
      }`}
    >
      {text}
    </button>
  );

  let body;
  if (isLoading) {
    body = (
      <Card>
        <div className="flex items-center justify-center py-12 text-text-muted">{t("common.loading")}</div>
      </Card>
    );
  } else if (groups.length === 0) {
    body = isError ? null : (
      <Card>
        <div className="flex items-center justify-center py-12 text-text-muted">
          {t(tab === "open" ? "listingReports.emptyOpen" : "listingReports.emptyDecided")}
        </div>
      </Card>
    );
  } else {
    body = groups.map((group) => {
      const busy = submitting === group.listingId;
      const ownerLabel = partyName(group.listing?.ownerName, group.ownerId, locale);
      return (
        <section
          key={`${group.listingType}:${group.listingId}`}
          aria-label={`${t(`listingReports.${group.listingType}`)} ${group.listingId}`}
          className="mb-4"
        >
          <Card>
            <div className="mb-3 flex flex-wrap items-start justify-between gap-2">
              <div className="space-y-1">
                <div className="flex flex-wrap items-center gap-2">
                  <Badge variant="info">{label(`listingReports.${group.listingType}`, group.listingType)}</Badge>
                  <span className="text-xs text-text-muted">{group.listingId}</span>
                  <Badge variant={group.reports.length > 1 ? "danger" : "warning"}>
                    {fill(t("listingReports.reportCount"), { count: String(group.reports.length) })}
                  </Badge>
                </div>
                <p className="text-sm">
                  {t("listingReports.owner")}:{" "}
                  <Link to={`/users/${group.ownerId}`} className="font-medium text-primary hover:underline">
                    {ownerLabel}
                  </Link>
                </p>
              </div>
            </div>
            {listingSummary(group)}
            <ul className="mt-3 divide-y divide-gray-100 border-t border-gray-100 text-sm">
              {group.reports.map((report) => (
                <li key={report.reporterId} className="flex flex-wrap items-center gap-x-3 gap-y-1 py-2">
                  <Badge variant={reasonVariant[report.reason] ?? "default"}>
                    {label(`listingReports.reason_${report.reason}`, report.reason)}
                  </Badge>
                  <span>
                    {t("listingReports.reporter")}:{" "}
                    <Link to={`/users/${report.reporterId}`} className="text-primary hover:underline">
                      {report.reporterId}
                    </Link>
                  </span>
                  <span className="text-text-muted">{formatDateTime(report.createdAt)}</span>
                  {report.status !== "open" && (
                    <span className="text-text-muted">
                      {fill(t("listingReports.decided"), {
                        status: label(`listingReports.status_${report.status}`, report.status),
                        date: report.reviewedAt ? formatDateTime(report.reviewedAt) : "",
                      })}
                      {report.reviewNote ? `: ${report.reviewNote}` : ""}
                    </span>
                  )}
                </li>
              ))}
            </ul>
            {tab === "open" && (
              <div className="mt-3 flex flex-col gap-2 border-t border-gray-100 pt-3 md:flex-row md:items-start">
                <textarea
                  value={notes[group.listingId] ?? ""}
                  onChange={(e) => setNotes((prev) => ({ ...prev, [group.listingId]: e.target.value }))}
                  aria-label={t("listingReports.note")}
                  placeholder={t("listingReports.note")}
                  maxLength={NOTE_MAX}
                  rows={2}
                  className="w-full rounded border border-gray-300 px-2 py-1 text-sm focus:border-primary focus:outline-none md:max-w-md"
                />
                <div className="flex gap-2">
                  <button
                    type="button"
                    onClick={() => decide(group, "dismissed")}
                    disabled={busy}
                    className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50 disabled:opacity-50"
                  >
                    {t("listingReports.dismiss")}
                  </button>
                  <button
                    type="button"
                    onClick={() => decide(group, "actioned")}
                    disabled={busy}
                    className="rounded bg-primary px-3 py-1 text-sm text-white hover:opacity-90 disabled:opacity-50"
                  >
                    {t("listingReports.markActioned")}
                  </button>
                </div>
              </div>
            )}
          </Card>
        </section>
      );
    });
  }

  return (
    <div>
      <h2 className="text-xl font-bold">{t("listingReports.title")}</h2>
      <p className="mb-4 mt-1 max-w-3xl text-sm text-text-muted">{t("listingReports.subtitle")}</p>
      <div className="mb-4 flex flex-wrap gap-2">
        {tabButton("open", t("listingReports.tab_open"))}
        {tabButton("dismissed", t("listingReports.tab_dismissed"))}
        {tabButton("actioned", t("listingReports.tab_actioned"))}
      </div>
      {errorMsg && (
        <div role="alert" className="mb-4 rounded border border-danger/30 bg-danger/10 px-4 py-2 text-sm text-danger">
          {errorMsg}
        </div>
      )}
      {isError && (
        <div role="alert" className="mb-4 rounded border border-danger/30 bg-danger/10 px-4 py-2 text-sm text-danger">
          {t("listingReports.loadFailed")}
        </div>
      )}
      {notice && (
        <div role="status" className="mb-4 rounded border border-success/30 bg-success/10 px-4 py-2 text-sm text-success">
          {notice}
        </div>
      )}
      {body}
      {hasNextPage && (
        <div className="flex justify-center p-4">
          <button
            type="button"
            onClick={() => fetchNextPage()}
            disabled={isFetchingNextPage}
            className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50 disabled:opacity-50"
          >
            {t("listingReports.loadMore")}
          </button>
        </div>
      )}
    </div>
  );
}
