import { useState } from "react";
import { useLocale } from "@/hooks/useLocale";

/* ESC-32: the photo a farmer or trucker sent with an appeal. It is private: the server gives the
 * admin a pre-signed link that lives 10 minutes, never a public address. A link that has run out
 * (the page stayed open) shows a hint to reload instead of a broken image. */

export function AppealPhoto({ url }: { url?: string }) {
  const { t } = useLocale();
  const [failed, setFailed] = useState(false);

  if (!url || failed) {
    return <p className="text-xs text-warning">{t("reputation.photoExpired")}</p>;
  }

  return (
    <a href={url} target="_blank" rel="noopener noreferrer" className="inline-block">
      <img
        src={url}
        alt={t("reputation.photoAlt")}
        loading="lazy"
        referrerPolicy="no-referrer"
        onError={() => setFailed(true)}
        className="max-h-40 rounded border border-gray-200"
      />
      <span className="mt-1 block text-xs text-primary hover:underline">{t("reputation.viewPhoto")}</span>
    </a>
  );
}
