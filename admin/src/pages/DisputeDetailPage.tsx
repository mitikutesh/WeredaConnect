import { useState, type ReactNode } from "react";
import { useNavigate, useParams, useSearchParams } from "react-router";
import { ArrowLeft, Lock } from "lucide-react";
import { Card } from "@/components/ui/Card";
import { Badge } from "@/components/ui/Badge";
import { DispositionChoiceCard, VerdictMessages } from "@/components/disputes/DisputeVerdict";
import { SplitVerdictCard } from "@/components/disputes/SplitVerdict";
import { RecordedSplitRow } from "@/components/escrows/MoneyLegs";
import { EvidenceError, MessagesTab, PhotosTab, TimelineTab } from "@/components/disputes/EvidenceTabs";
import { useDisputeVerdict } from "@/components/disputes/verdictFlow";
import { TripTrailTab } from "@/components/trail/TripTrailTab";
import { useLocale } from "@/hooks/useLocale";
import { useDisputeEvidence, useEscrowDetail } from "@/hooks/useQuery";
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
    <div className="flex flex-col gap-6 lg:flex-row lg:items-start">
      <div className="flex min-w-0 flex-col gap-6 lg:flex-[3]">{children}</div>
      {aside && <aside className="flex min-w-0 flex-col gap-6 lg:flex-[2]">{aside}</aside>}
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
  const navigate = useNavigate();
  const { data, isLoading, isError, error } = useEscrowDetail(id || "");
  const [searchParams] = useSearchParams();
  const [tab, setTab] = useState<Tab>("trail");
  // FUP-173: the Disputes list's "Decide per leg" link opens the page with the per-leg form shown.
  const [splitOpen, setSplitOpen] = useState(() => searchParams.get("decide") === "split");
  const verdictFlow = useDisputeVerdict(t);
  const evidence = useDisputeEvidence(id || "");

  if (isLoading) {
    return <div className="text-text-muted">{t("common.loading")}</div>;
  }

  const back = (
    <button
      type="button"
      onClick={() => navigate("/disputes")}
      className="mb-4 inline-flex items-center gap-1 text-sm text-text-muted hover:text-text"
    >
      <ArrowLeft size={16} /> {t("common.back")}
    </button>
  );

  if (isError) {
    const code = (error as { code?: string } | null)?.code;
    const key = code ? `errors.${code}` : "common.error";
    const text = t(key);
    return (
      <div>
        {back}
        <div role="alert" className="rounded border border-danger/30 bg-danger/10 px-4 py-2 text-sm text-danger">
          {text !== key ? text : t("common.error")}
        </div>
      </div>
    );
  }

  const escrow = ((data as Row | undefined)?.escrow as Row | undefined) ?? {};
  const dispute = escrow.dispute as Row | undefined;
  const status = String(escrow.status || "");
  const statusText = t(`escrows.${status}`);
  // Admin trip evidence board: the parties as chips, the frozen money with its split.
  const parties = [
    { label: t("transactions.party_buyer"), name: escrow.buyerName, id: escrow.buyerId, tone: "bg-blue-50 text-blue-900" },
    { label: t("transactions.party_farmer"), name: escrow.farmerName, id: escrow.farmerId, tone: "bg-green-50 text-green-900" },
    { label: t("transactions.party_trucker"), name: escrow.truckerName, id: escrow.truckerId, tone: "bg-amber-50 text-amber-900" },
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
          <h3 className="font-semibold">{t("disputes.decide")}</h3>
          <div className="mt-3 flex flex-col gap-2">
            <button
              type="button"
              onClick={() => verdictFlow.submit(String(escrow.escrowId || id || ""), "farmer_wins")}
              disabled={verdictFlow.resolvingId !== null}
              className="rounded-lg bg-success px-3 py-3 text-sm font-semibold text-white hover:opacity-90 disabled:opacity-50"
            >
              {t("disputes.farmerWins")}
            </button>
            <button
              type="button"
              onClick={() => verdictFlow.submit(String(escrow.escrowId || id || ""), "buyer_wins")}
              disabled={verdictFlow.resolvingId !== null}
              className="rounded-lg border border-gray-300 bg-white px-3 py-3 text-sm font-semibold hover:bg-gray-50 disabled:opacity-50"
            >
              {t("disputes.buyerWins")}
            </button>
            {/* ESC-24: the per-leg verdict of ESC-20, only where the server takes a split (a ledger escrow, crop leg held). */}
            {heldLegs(escrow) && (
              <button
                type="button"
                onClick={() => setSplitOpen(true)}
                disabled={verdictFlow.resolvingId !== null || splitOpen}
                className="rounded-lg border border-gray-300 bg-white px-3 py-3 text-sm font-semibold hover:bg-gray-50 disabled:opacity-50"
              >
                {t("legs.decidePerLeg")}
              </button>
            )}
          </div>
          <p className="mt-3 text-xs text-text-muted">{t("disputes.auditNote")}</p>
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
      {back}
      <div className="mb-6 flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <div className="flex flex-wrap items-center gap-3">
            <h2 className="text-2xl font-bold">{t("disputes.detailTitle")}</h2>
            <span className="font-mono text-xs text-text-muted">{String(escrow.escrowId || id || "")}</span>
            {status && (
              <Badge variant={status === "disputed" ? "warning" : "default"}>
                {statusText !== `escrows.${status}` ? statusText : status}
              </Badge>
            )}
          </div>
          {parties.length > 0 && (
            <ul className="mt-3 flex flex-wrap gap-2 text-xs font-semibold">
              {parties.map((party) => (
                <li key={party.label} className={`rounded-md px-2 py-1 ${party.tone}`}>
                  {party.label} · {partyName(party.name, party.id, locale)}
                </li>
              ))}
            </ul>
          )}
        </div>
        {status === "disputed" && (
          <Card className="lg:min-w-[320px]">
            <div className="flex items-start gap-3">
              <Lock size={18} className="mt-1 shrink-0 text-info" aria-hidden="true" />
              <div>
                <span className="text-lg font-bold">
                  {fill(t("disputes.frozen"), { amount: formatETB(Number(escrow.amount) || 0, locale) })}
                </span>
                {split && <p className="mt-1 text-xs text-text-muted">{split}</p>}
              </div>
            </div>
          </Card>
        )}
      </div>

      <Card className="mb-6">
        <dl className="grid grid-cols-1 gap-3 text-sm sm:grid-cols-2">
          <div className="flex justify-between gap-3">
            <dt className="text-text-muted">{t("disputes.reason")}</dt>
            <dd className="text-right">{String(dispute?.reason ?? "")}</dd>
          </div>
          <div className="flex justify-between gap-3">
            <dt className="text-text-muted">{t("disputes.raisedAt")}</dt>
            <dd className="text-right">{dispute?.createdAt ? formatDateTime(String(dispute.createdAt)) : ""}</dd>
          </div>
          <div className="flex justify-between gap-3">
            <dt className="text-text-muted">{t("escrows.buyer")}</dt>
            <dd className="text-right">{partyName(escrow.buyerName, escrow.buyerId, locale)}</dd>
          </div>
          <div className="flex justify-between gap-3">
            <dt className="text-text-muted">{t("escrows.seller")}</dt>
            <dd className="text-right">{partyName(escrow.farmerName, escrow.farmerId, locale)}</dd>
          </div>
          <div className="flex justify-between gap-3">
            <dt className="text-text-muted">{t("escrows.amount")}</dt>
            <dd className="text-right font-medium">{formatETB(Number(escrow.amount) || 0, locale)}</dd>
          </div>
          <div className="flex justify-between gap-3">
            <dt className="text-text-muted">{t("disputes.delivery")}</dt>
            <dd className="text-right font-mono text-xs">
              {String(escrow.deliveryId || "") || t("common.notAvailable")}
            </dd>
          </div>
          <RecordedSplitRow escrow={escrow} />
        </dl>
      </Card>

      <VerdictMessages flow={verdictFlow} />

      <div
        role="tablist"
        aria-label={t("disputes.evidence")}
        className="mb-4 inline-flex gap-1 rounded-lg bg-gray-100 p-1"
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
            className={`rounded-md px-4 py-2 text-sm ${
              tab === value ? "bg-white font-semibold shadow-sm" : "text-text-muted hover:text-text"
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
                <p role="status" className="text-sm text-text-muted">
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
