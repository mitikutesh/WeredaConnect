import { useParams, useNavigate } from "react-router";
import type { ReactNode } from "react";
import {
  ShoppingCart,
  Truck,
  Wallet,
  MapPin,
  ArrowRight,
  ArrowLeft,
} from "lucide-react";
import { Card } from "@/components/ui/Card";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { useTransactionDetail } from "@/hooks/useQuery";
import { formatETB, formatDateTime } from "@/lib/i18n";
import {
  orderStatusVariant,
  stageVariant,
  escrowStatusVariant,
  deliveryStatusVariant,
  timelineEntityVariant,
  orderStatusLabel,
  stageLabel,
  escrowStatusLabel,
  deliveryStatusLabel,
  timelineActionLabel,
  deriveStage,
} from "@/lib/transaction";

type Row = Record<string, unknown>;

function s(v: unknown): string {
  return v == null ? "" : String(v);
}

/** A label/value row inside a detail card; hidden entirely when value is empty. */
function Field({ label, children }: { label: string; children: ReactNode }) {
  if (children == null || children === "") return null;
  return (
    <div className="flex justify-between gap-3">
      <dt className="text-text-muted shrink-0">{label}</dt>
      <dd className="text-right">{children}</dd>
    </div>
  );
}

const entityIcon: Record<string, ReactNode> = {
  order: <ShoppingCart size={16} />,
  delivery: <Truck size={16} />,
  escrow: <Wallet size={16} />,
};

export function TransactionDetailPage() {
  const { orderId } = useParams<{ orderId: string }>();
  const { t, locale } = useLocale();
  const navigate = useNavigate();
  const { data, isLoading } = useTransactionDetail(orderId || "");

  if (isLoading) {
    return <div className="text-text-muted">{t("common.loading")}</div>;
  }

  const resp = (data as Row) || {};
  const order = (resp.order as Row) || {};
  const escrow = resp.escrow as Row | null;
  const delivery = resp.delivery as Row | null;
  const timeline = (resp.timeline as Row[]) || [];

  const status = s(order.status);
  const stage = deriveStage(status);

  const gps = (v: unknown): string => {
    const p = v as Row | undefined;
    if (!p || p.latitude == null || p.longitude == null) return "";
    return `${Number(p.latitude).toFixed(5)}, ${Number(p.longitude).toFixed(5)}`;
  };

  return (
    <div>
      <button
        onClick={() => navigate("/transactions")}
        className="mb-4 inline-flex items-center gap-1 text-sm text-text-muted hover:text-text"
      >
        <ArrowLeft size={16} /> {t("common.back")}
      </button>

      <div className="mb-6 flex flex-wrap items-center gap-3">
        <h2 className="text-xl font-bold">{t("transactions.detail")}</h2>
        <span className="font-mono text-xs text-text-muted">{s(order.orderId)}</span>
        <Badge variant={stageVariant[stage] || "default"}>{stageLabel(stage, t)}</Badge>
        <Badge variant={orderStatusVariant[status] || "default"}>
          {orderStatusLabel(status, t)}
        </Badge>
      </div>

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        {/* Parties */}
        <Card>
          <h3 className="mb-4 font-semibold">{t("transactions.parties")}</h3>
          <dl className="space-y-3 text-sm">
            <Field label={t("transactions.buyer")}>
              {s(order.buyerName) || s(order.buyerId)}
            </Field>
            <Field label={t("transactions.buyerPhone")}>{s(delivery?.buyerPhone)}</Field>
            <Field label={t("transactions.seller")}>
              {s(order.farmerName) || s(order.farmerId)}
            </Field>
            <Field label={t("transactions.sellerPhone")}>{s(delivery?.farmerPhone)}</Field>
            <Field label={t("transactions.trucker")}>
              {s(order.truckerName) || s(order.truckerId) || t("transactions.noTrucker")}
            </Field>
            <Field label={t("transactions.truckerPhone")}>{s(delivery?.truckerPhone)}</Field>
            <Field label={t("transactions.crop")}>{s(order.cropType)}</Field>
            <Field label={t("transactions.quantity")}>
              {order.quantity != null ? `${Number(order.quantity)} kg` : ""}
            </Field>
            <Field label={t("transactions.createdAt")}>
              {order.createdAt ? formatDateTime(s(order.createdAt)) : ""}
            </Field>
          </dl>
        </Card>

        {/* Payment & Escrow */}
        <Card>
          <h3 className="mb-4 font-semibold">{t("transactions.payment")}</h3>
          {escrow ? (
            <dl className="space-y-3 text-sm">
              <Field label={t("transactions.escrowStatus")}>
                <Badge variant={escrowStatusVariant[s(escrow.status)] || "default"}>
                  {escrowStatusLabel(s(escrow.status), t)}
                </Badge>
              </Field>
              <Field label={t("transactions.amount")}>
                {formatETB(Number(escrow.amount) || 0, locale)}
              </Field>
              <Field label={t("transactions.commission")}>
                {formatETB(Number(escrow.commission) || 0, locale)}
              </Field>
              <Field label={t("transactions.farmerPayout")}>
                {formatETB(Number(escrow.farmerPayout) || 0, locale)}
              </Field>
              {escrow.truckerPayout != null && (
                <Field label={t("transactions.truckerPayout")}>
                  {formatETB(Number(escrow.truckerPayout) || 0, locale)}
                </Field>
              )}
              <Field label={t("transactions.paymentMethod")}>{s(order.paymentMethod)}</Field>
              <Field label={t("transactions.cbeRef")}>{s(order.cbeTransactionId)}</Field>
              <Field label={t("transactions.telebirrRef")}>{s(escrow.telebirrRef)}</Field>
              <Field label={t("transactions.releasedAt")}>
                {escrow.releasedAt ? formatDateTime(s(escrow.releasedAt)) : ""}
              </Field>
              <Field label={t("transactions.refundedAt")}>
                {escrow.refundedAt ? formatDateTime(s(escrow.refundedAt)) : ""}
              </Field>
              <Field label={t("transactions.completedAt")}>
                {escrow.completedAt ? formatDateTime(s(escrow.completedAt)) : ""}
              </Field>
            </dl>
          ) : (
            <div className="space-y-3 text-sm">
              <Field label={t("transactions.paymentMethod")}>{s(order.paymentMethod)}</Field>
              <Field label={t("transactions.cbeRef")}>{s(order.cbeTransactionId)}</Field>
              <Field label={t("transactions.orderTotal")}>
                {formatETB(Number(order.orderTotal) || 0, locale)}
              </Field>
              <p className="text-text-muted">{t("transactions.noEscrow")}</p>
            </div>
          )}
        </Card>

        {/* Delivery */}
        <Card>
          <h3 className="mb-4 font-semibold">{t("transactions.delivery")}</h3>
          {delivery ? (
            <dl className="space-y-3 text-sm">
              <Field label={t("transactions.deliveryStatus")}>
                <Badge variant={deliveryStatusVariant[s(delivery.status)] || "default"}>
                  {deliveryStatusLabel(s(delivery.status), t)}
                </Badge>
              </Field>
              <Field label={t("transactions.pickupLocation")}>{gps(delivery.gpsPickup)}</Field>
              <Field label={t("transactions.pickedUpAt")}>
                {delivery.pickedUpAt ? formatDateTime(s(delivery.pickedUpAt)) : ""}
              </Field>
              <Field label={t("transactions.dropoffLocation")}>{gps(delivery.gpsDropoff)}</Field>
              <Field label={t("transactions.confirmedAt")}>
                {delivery.confirmedAt ? formatDateTime(s(delivery.confirmedAt)) : ""}
              </Field>
            </dl>
          ) : (
            <p className="text-sm text-text-muted">{t("transactions.noDelivery")}</p>
          )}
        </Card>

        {/* Timeline */}
        <Card>
          <h3 className="mb-4 font-semibold">{t("transactions.timeline")}</h3>
          {timeline.length === 0 ? (
            <p className="text-sm text-text-muted">{t("common.noData")}</p>
          ) : (
            <ol className="space-y-4">
              {timeline.map((ev, i) => {
                const entity = s(ev.entity);
                const from = s(ev.fromState);
                const to = s(ev.toState);
                const loc = gps(ev.location);
                return (
                  <li key={i} className="flex gap-3">
                    <div className="mt-0.5 text-text-muted">
                      {entityIcon[entity] || <ArrowRight size={16} />}
                    </div>
                    <div className="min-w-0 flex-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className="text-sm font-medium">
                          {timelineActionLabel(s(ev.action), t)}
                        </span>
                        <Badge variant={timelineEntityVariant[entity] || "default"}>
                          {t(`transactions.entity_${entity}`) === `transactions.entity_${entity}`
                            ? entity
                            : t(`transactions.entity_${entity}`)}
                        </Badge>
                      </div>
                      <p className="text-xs text-text-muted">
                        {ev.timestamp ? formatDateTime(s(ev.timestamp)) : ""}
                        {ev.actor ? ` · ${s(ev.actor)}` : ""}
                      </p>
                      {from && to ? (
                        <p className="text-xs text-text-muted">
                          {from} <ArrowRight size={10} className="inline" /> {to}
                        </p>
                      ) : null}
                      {loc ? (
                        <p className="flex items-center gap-1 text-xs text-text-muted">
                          <MapPin size={10} /> {loc}
                        </p>
                      ) : null}
                      {ev.telebirrRef ? (
                        <p className="font-mono text-xs text-text-muted">
                          {s(ev.telebirrRef)}
                        </p>
                      ) : null}
                      {ev.ipAddress ? (
                        <p className="text-xs text-text-muted">
                          {t("transactions.ip")} {s(ev.ipAddress)}
                        </p>
                      ) : null}
                      {ev.detail ? (
                        <p className="text-xs text-text-muted">{s(ev.detail)}</p>
                      ) : null}
                    </div>
                  </li>
                );
              })}
            </ol>
          )}
        </Card>
      </div>
    </div>
  );
}
