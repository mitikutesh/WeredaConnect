import type { ReactNode } from "react";
import { useLocale } from "@/hooks/useLocale";

interface Column<T> {
  key: string;
  header: string;
  render?: (row: T) => ReactNode;
}

interface DataTableProps<T> {
  columns: Column<T>[];
  data: T[];
  onRowClick?: (row: T) => void;
  isLoading?: boolean;
}

export function DataTable<T extends Record<string, unknown>>({
  columns,
  data,
  onRowClick,
  isLoading,
}: DataTableProps<T>) {
  const { t } = useLocale();

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-12 text-text-muted">
        {t("common.loading")}
      </div>
    );
  }

  if (data.length === 0) {
    return (
      <div className="flex items-center justify-center py-12 text-text-muted">
        {t("common.noData")}
      </div>
    );
  }

  return (
    <>
      {/* Desktop table view */}
      <div className="hidden md:block overflow-x-auto">
        <table className="w-full text-left text-sm">
          <thead>
            <tr className="border-b border-gray-200">
              {columns.map((col) => (
                <th
                  key={col.key}
                  className="px-4 py-3 font-medium text-text-muted"
                >
                  {col.header}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {data.map((row, i) => (
              <tr
                key={i}
                onClick={() => onRowClick?.(row)}
                className={`border-b border-gray-100 ${onRowClick ? "cursor-pointer hover:bg-gray-50" : ""}`}
              >
                {columns.map((col) => (
                  <td key={col.key} className="px-4 py-3">
                    {col.render
                      ? col.render(row)
                      : String(row[col.key] ?? "")}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* Mobile card view */}
      <div className="md:hidden space-y-3">
        {data.map((row, i) => (
          <div
            key={i}
            onClick={() => onRowClick?.(row)}
            className={`rounded-lg border border-gray-200 bg-white p-4 shadow-sm ${onRowClick ? "cursor-pointer active:bg-gray-50" : ""}`}
          >
            {columns.map((col) => (
              <div
                key={col.key}
                className="flex justify-between gap-2 py-1 text-sm"
              >
                <span className="font-medium text-text-muted shrink-0">
                  {col.header}
                </span>
                <span className="text-right">
                  {col.render
                    ? col.render(row)
                    : String(row[col.key] ?? "")}
                </span>
              </div>
            ))}
          </div>
        ))}
      </div>
    </>
  );
}
