import { useState } from "react";
import { useResolveDispute } from "@/hooks/useQuery";

/* ESC-24: partial_refund is the per-leg verdict of ESC-20 (action split), on a ledger escrow only. */
export type Verdict = "farmer_wins" | "buyer_wins" | "partial_refund";
export type Disposition = "return_to_farmer" | "delivered_anyway" | "settle_now" | "wait_for_delivery";

/* FUP-16: when the order or delivery cannot follow a verdict as it is, the admin chooses what
 * happens. Each disposition belongs to one verdict (escrow-protocol.md 1.21.0). */
export const dispositionsFor: Record<Verdict, Disposition[]> = {
  buyer_wins: ["return_to_farmer", "delivered_anyway"],
  farmer_wins: ["settle_now", "wait_for_delivery"],
  // The order and delivery follow a split as they follow farmer_wins (the goods were delivered).
  partial_refund: ["settle_now", "wait_for_delivery"],
};

/** The action the server pairs with each verdict (ResolveDisputeRequest). */
const actionFor: Record<Verdict, string> = {
  farmer_wins: "release",
  buyer_wins: "refund",
  partial_refund: "split",
};

/** ESC-24: the amounts of a per-leg verdict (birr, 2 decimals) and the admin's reason, sent as notes. */
export type SplitVerdict = {
  farmerAmount: number;
  truckerAmount: number;
  buyerRefund: number;
  notes?: string;
};

export const REASON_MIN = 10;
export const REASON_MAX = 500;

export type VerdictChoice = { id: string; verdict: Verdict; split?: SplitVerdict };

/**
 * The verdict flow shared by the Disputes list and the dispute page: send the verdict, and when
 * the server answers ADMIN_015 (the order cannot follow it as it is) ask what happens instead.
 */
export function useDisputeVerdict(t: (key: string) => string) {
  const resolveDispute = useResolveDispute();
  const [resolvingId, setResolvingId] = useState<string | null>(null);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [choice, setChoice] = useState<VerdictChoice | null>(null);

  function submit(
    id: string,
    verdict: Verdict,
    picked?: { disposition: Disposition; reason: string },
    split?: SplitVerdict,
  ) {
    setResolvingId(id);
    setErrorMsg(null);
    setNotice(null);
    // Server contract (ResolveDisputeRequest) requires `resolution` + `action`.
    // Map the winner to the outcome (farmer wins → release funds; buyer wins → refund).
    // ESC-24: a per-leg verdict sends its three amounts with action split.
    resolveDispute.mutate(
      {
        id,
        body: {
          resolution: verdict,
          action: actionFor[verdict],
          ...(verdict === "partial_refund" ? (split ?? {}) : {}),
          ...(picked ?? {}),
        },
      },
      {
        onSettled: () => setResolvingId(null),
        onSuccess: () => {
          setChoice(null);
          setNotice(t("disputes.saved"));
        },
        onError: (err: unknown) => {
          const code = (err as { code?: string }).code;
          if (code === "ADMIN_015") {
            // The goods are moving or not delivered yet: the admin chooses what happens.
            setChoice({ id, verdict, split });
            return;
          }
          const key = code ? `errors.${code}` : "errors.actionFailed";
          const msg = t(key);
          setErrorMsg(msg !== key ? msg : t("errors.actionFailed"));
        },
      },
    );
  }

  return {
    submit,
    resolvingId,
    errorMsg,
    notice,
    choice,
    openChoice: (id: string, verdict: Verdict, split?: SplitVerdict) => setChoice({ id, verdict, split }),
    closeChoice: () => setChoice(null),
  };
}

export type DisputeVerdictFlow = ReturnType<typeof useDisputeVerdict>;
