import { useState, type ReactNode } from "react";
import { Link, useParams, useSearchParams } from "react-router";
import { Lock } from "lucide-react";
import { Card } from "@/components/ui/Card";
import { Badge } from "@/components/ui/Badge";
import { DispositionChoiceCard, VerdictMessages } from "@/components/disputes/DisputeVerdict";
import { SplitVerdictCard } from "@/components/disputes/SplitVerdict";
import { RecordedSplitRow } from "@/components/escrows/MoneyLegs";
import {
  EvidenceError,
  MessagesTab,
  PhotosTab,
  StampedHandoffEvidence,
  TimelineTab,
} from "@/components/disputes/EvidenceTabs";
import { useDisputeVerdict } from "@/components/disputes/verdictFlow";
import { TripTrailTab } from "@/components/trail/TripTrailTab";
import { useLocale } from "@/hooks/useLocale";
import { useDisputeEvidence, useEscrowDetail } from "@/hooks/useQuery";
import { disputeReasonTitle } from "@/lib/disputes";
import { photosOf } from "@/lib/evidence";
import { formatDateTime, formatETB } from "@/lib/i18n";
import { heldLegs } from "@/lib/legs";
import { fill } from "@/lib/trail";
import { partyName } from "@/lib/user";

type Row = Record<string, unknown>;

/* The evidence tabs, in the order of the admin trip evidence board: LT-13 brings the Trip trail,
 * FRD-14 the Timeline (with the handoffs), the Photos and the Messages. */
const tabs = ["timeline", "trail", "photos", "messages"] as const;
type Tab = (typeof tabs)[number];

const tabLabelKey: Record<Tab, string> = {
  timeline: "evidence.tabTimeline",
  trail: "trail.tab",
  photos: "evidence.tabPhotos",
  messages: "evidence.tabMessages",
};

/** An evidence tab with the page's own cards (Decide) in the right column, as the Trip trail has them. */
function WithAside({ children, aside }: { children: ReactNode; aside: ReactNode }) {
  return (
    <div className="flex flex-col gap-5 lg:flex-row lg:items-start">
      <div className="flex min-w-0 flex-col gap-5 lg:flex-[3_1_560px]">{children}</div>
      {aside && <aside className="flex min-w-0 flex-col gap-5 lg:flex-[1_1_320px]">{aside}</aside>}
    </div>
  );
}

/**
 * LT-13 and FRD-14: the dispute page, reached from the Disputes list. The case and its parties, the
 * evidence tabs (Timeline with the handoffs, Trip trail, Photos, Messages) and Decide beside the
 * evidence, so the admin resolves the dispute from this one screen.
 */
export function DisputeDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t, locale } = useLocale();
  const { data, isLoading, isError, error } = useEscrowDetail(id || "");
  const [searchParams] = useSearchParams();
  const [tab, setTab] = useState<Tab>("trail");
  // FUP-173: the Disputes list's "Decide per leg" link opens the page with the per-leg form shown.
  const [splitOpen, setSplitOpen] = useState(() => searchParams.get("decide") === "split");
  const verdictFlow = useDisputeVerdict(t);
  const evidence = useDisputeEvidence(id || "");

  if (isLoading) {
    return <div className="text-slate">{t("common.loading")}</div>;
  }

  // The board's breadcrumb: "Disputes / <this case>".
  const back = (
    <nav aria-label={t("disputes.breadcrumb")} className="mb-1.5 text-[13px] text-slate">
      <Link
        to="/disputes"
        className="font-semibold text-ink underline decoration-field underline-offset-2 hover:decoration-ink"
      >
        {t("disputes.title")}
      </Link>
      {id ? <span className="font-mono text-xs"> / {id}</span> : null}
    </nav>
  );

  if (isError) {
    const code = (error as { code?: string } | null)?.code;
    const key = code ? `errors.${code}` : "common.error";
    const text = t(key);
    return (
      <div>
        {back}
        <div role="alert" className="rounded-[14px] border border-danger/30 bg-danger-tint px-4 py-3 text-sm text-danger-ink">
          {text !== key ? text : t("common.error")}
        </div>
      </div>
    );
  }

  const escrow = ((data as Row | undefined)?.escrow as Row | undefined) ?? {};
  const dispute = escrow.dispute as Row | undefined;
  const status = String(escrow.status || "");
  const statusText = t(`escrows.${status}`);
  // Page M board: the reason is the heading. A reason with no title (free text, a new code) keeps "Dispute".
  const reasonTitle = disputeReasonTitle(dispute?.reason, t);
  // Admin trip evidence board: the parties as chips, the frozen money with its split.
  const parties = [
    { label: t("transactions.party_buyer"), name: escrow.buyerName, id: escrow.buyerId, tone: "bg-buyer-tint text-buyer" },
    { label: t("transactions.party_farmer"), name: escrow.farmerName, id: escrow.farmerId, tone: "bg-farmer-tint text-farmer" },
    { label: t("transactions.party_trucker"), name: escrow.truckerName, id: escrow.truckerId, tone: "bg-trucker-tint text-trucker" },
  ].filter((party) => party.name || party.id);
  const split = [
    { label: t("transactions.crop"), amount: escrow.farmerPayout },
    { label: t("transactions.party_trucker"), amount: escrow.truckerPayout },
    { label: t("transactions.commission"), amount: escrow.commission },
  ]
    .filter((part) => Number(part.amount) > 0)
    .map((part) => `${part.label} ${formatETB(Number(part.amount), locale)}`)
    .join(" · ");
  const decide =
    status === "disputed" ? (
      <>
        <Card>
          <h3 className="text-[15px] font-extrabold">{t("disputes.decide")}</h3>
          <div className="mt-3 flex flex-col gap-2.5">
            <button
              type="button"
              onClick={() => verdictFlow.submit(String(escrow.escrowId || id || ""), "farmer_wins")}
              disabled={verdictFlow.resolvingId !== null}
              className="btn-ink min-h-12"
            >
              {t("disputes.farmerWins")}
            </button>
            <button
              type="button"
              onClick={() => verdictFlow.submit(String(escrow.escrowId || id || ""), "buyer_wins")}
              disabled={verdictFlow.resolvingId !== null}
              className="btn-outline min-h-12"
            >
              {t("disputes.buyerWins")}
            </button>
            {/* ESC-24: the per-leg verdict of ESC-20, only where the server takes a split (a ledger escrow, crop leg held). */}
            {heldLegs(escrow) && (
              <button
                type="button"
                onClick={() => setSplitOpen(true)}
                disabled={verdictFlow.resolvingId !== null || splitOpen}
                className="btn-outline min-h-12"
              >
                {t("legs.decidePerLeg")}
              </button>
            )}
          </div>
          <p className="mt-3 text-xs text-slate">{t("disputes.auditNote")}</p>
        </Card>
        {splitOpen && !verdictFlow.choice && (
          <SplitVerdictCard
            escrow={escrow}
            flow={verdictFlow}
            t={t}
            locale={locale}
            onClose={() => setSplitOpen(false)}
          />
        )}
        <DispositionChoiceCard flow={verdictFlow} t={t} />
      </>
    ) : null;

  return (
    <div>
      <div className="mb-5 flex flex-col gap-4 lg:flex-row lg:items-end lg:justify-between">
        <div>
          {back}
          {reasonTitle && <p className="mb-0.5 text-[13px] font-bold text-slate">{t("disputes.detailTitle")}</p>}
          <div className="flex flex-wrap items-center gap-3">
            <h2 className="text-[28px] font-extrabold leading-tight tracking-[-0.03em]">
              {reasonTitle ?? t("disputes.detailTitle")}
            </h2>
            <span className="font-mono text-xs text-slate">{String(escrow.escrowId || id || "")}</span>
            {status && (
              <Badge variant={status === "disputed" ? "warning" : "default"}>
                {statusText !== `escrows.${status}` ? statusText : status}
              </Badge>
            )}
          </div>
          {parties.length > 0 && (
            <ul className="mt-2.5 flex flex-wrap gap-2 text-xs font-bold">
              {parties.map((party) => (
                <li key={party.label} className={`inline-flex min-h-7 items-center rounded-lg px-2.5 ${party.tone}`}>
                  {party.label} · {partyName(party.name, party.id, locale)}
                </li>
              ))}
            </ul>
          )}
        </div>
        {status === "disputed" && (
          <div className="rounded-[16px] border border-line bg-ground px-[18px] py-3.5 lg:min-w-[320px]">
            <div className="flex items-center gap-3">
              <Lock size={20} className="shrink-0 text-slate" aria-hidden="true" />
              <div>
                <span className="block text-xl font-extrabold leading-tight">
                  {fill(t("disputes.frozen"), { amount: formatETB(Number(escrow.amount) || 0, locale) })}
                </span>
                {split && <p className="text-xs text-slate">{split}</p>}
              </div>
            </div>
          </div>
        )}
      </div>

      <Card className="mb-5">
        <dl className="grid grid-cols-1 gap-3 text-sm sm:grid-cols-2">
          <div className="flex justify-between gap-3">
            <dt className="text-slate">{t("disputes.reason")}</dt>
            <dd className="text-right">{String(dispute?.reason ?? "")}</dd>
          </div>
          <div className="flex justify-between gap-3">
            <dt className="text-slate">{t("disputes.raisedAt")}</dt>
            <dd className="text-right">{dispute?.createdAt ? formatDateTime(String(dispute.createdAt)) : ""}</dd>
          </div>
          <div className="flex justify-between gap-3">
            <dt className="text-slate">{t("escrows.buyer")}</dt>
            <dd className="text-right">{partyName(escrow.buyerName, escrow.buyerId, locale)}</dd>
          </div>
          <div className="flex justify-between gap-3">
            <dt className="text-slate">{t("escrows.seller")}</dt>
            <dd className="text-right">{partyName(escrow.farmerName, escrow.farmerId, locale)}</dd>
          </div>
          <div className="flex justify-between gap-3">
            <dt className="text-slate">{t("escrows.amount")}</dt>
            <dd className="text-right font-medium">{formatETB(Number(escrow.amount) || 0, locale)}</dd>
          </div>
          <div className="flex justify-between gap-3">
            <dt className="text-slate">{t("disputes.delivery")}</dt>
            <dd className="text-right font-mono text-xs">
              {String(escrow.deliveryId || "") || t("common.notAvailable")}
            </dd>
          </div>
          <RecordedSplitRow escrow={escrow} />
        </dl>
      </Card>

      {/* BUG-6: what the server held about the handoffs when the dispute was opened. */}
      <StampedHandoffEvidence escrow={escrow} evidence={evidence.data} />

      <VerdictMessages flow={verdictFlow} />

      <div
        role="tablist"
        aria-label={t("disputes.evidence")}
        className="mb-5 inline-flex flex-wrap gap-1 rounded-[14px] bg-track p-1"
      >
        {tabs.map((value) => (
          <button
            key={value}
            type="button"
            role="tab"
            id={`dispute-tab-${value}`}
            aria-selected={tab === value}
            aria-controls={`dispute-panel-${value}`}
            onClick={() => setTab(value)}
            className={`min-h-11 rounded-[11px] px-4 text-[13px] ${
              tab === value ? "bg-ground font-bold text-ink" : "font-semibold text-ink-soft hover:text-ink"
            }`}
          >
            {value === "photos" && photosOf(evidence.data).length > 0
              ? fill(t("evidence.tabPhotosCount"), { count: photosOf(evidence.data).length })
              : t(tabLabelKey[value])}
          </button>
        ))}
      </div>

      <div role="tabpanel" id={`dispute-panel-${tab}`} aria-labelledby={`dispute-tab-${tab}`}>
        {/* Admin trip evidence board: Decide in the evidence's right column. */}
        {tab === "trail" && <TripTrailTab deliveryId={String(escrow.deliveryId || "")} aside={decide} />}
        {tab === "messages" && (
          <WithAside aside={decide}>
            <MessagesTab escrowId={String(escrow.escrowId || id || "")} evidence={evidence.data} />
          </WithAside>
        )}
        {(tab === "timeline" || tab === "photos") && (
          <WithAside aside={decide}>
            {evidence.isLoading ? (
              <Card>
                <p role="status" className="text-sm text-slate">
                  {t("evidence.loading")}
                </p>
              </Card>
            ) : evidence.isError || !evidence.data ? (
              <EvidenceError error={evidence.error} fallbackKey="evidence.loadFailed" onRetry={() => evidence.refetch()} />
            ) : tab === "timeline" ? (
              <TimelineTab evidence={evidence.data} />
            ) : (
              <PhotosTab evidence={evidence.data} />
            )}
          </WithAside>
        )}
      </div>
    </div>
  );
}
